using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Ipc;
using MyPowerTools.Platform.Abstractions;
using ScreenEase.MyPowerTools;

var pipeName = GetOption(args, "--pipe") ?? "screenease.core";
var heartbeatFile = GetOption(args, "--heartbeat-file");
var intervalMs = int.TryParse(GetOption(args, "--interval-ms"), out var iv) ? iv : 1000;
var logicalOnly = args.Contains("--logical-only", StringComparer.OrdinalIgnoreCase);
var toolDataRoot = Environment.GetEnvironmentVariable("MPT_TOOL_DATA_ROOT");
if (string.IsNullOrWhiteSpace(toolDataRoot))
{
    toolDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyPowerTools",
        "state",
        "tools",
        "screenease");
}

var cacheRoot = Path.Combine(toolDataRoot, "cache");
var logRoot = Path.Combine(toolDataRoot, "logs");
Directory.CreateDirectory(toolDataRoot);
Directory.CreateDirectory(cacheRoot);
Directory.CreateDirectory(logRoot);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var module = logicalOnly
    ? new ScreenEaseModule(new UnsupportedDisplayService("verification", "Logical-only service verification."))
    : new ScreenEaseModule();
var initialized = await module.InitializeAsync(
    new ModuleContext(
        HostVersion: "screenease-service/0.2.0",
        ProtocolVersion: "1.0",
        PackageId: "screenease",
        ModuleId: "screenease",
        DataDirectory: toolDataRoot,
        CacheDirectory: cacheRoot,
        LogDirectory: logRoot,
        Platform: OperatingSystem.IsWindows() ? "windows" : "portable",
        GrantedCapabilities: []),
    cts.Token);
if (!initialized.Ok)
{
    Console.Error.WriteLine("ScreenEase.Service failed to initialize the ScreenEase runtime.");
    return 2;
}

var pid = Environment.ProcessId;
Console.WriteLine($"ScreenEase.Service active pid={pid} pipe={pipeName} data={toolDataRoot}");
var settingsRevision = new SettingsRevisionStore(Path.Combine(toolDataRoot, "settings.revision"));
var pipeTask = Task.Run(() => ServePipeAsync(pipeName, module, settingsRevision, cts.Token));

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var line = $"heartbeat pid={pid} ts={DateTimeOffset.UtcNow:O}";
        Console.WriteLine(line);
        if (!string.IsNullOrWhiteSpace(heartbeatFile))
        {
            try
            {
                await AppendHeartbeatAsync(heartbeatFile, line, cts.Token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"ScreenEase.Service heartbeat write failed: {exception.Message}");
            }
        }

        // macOS readiness/liveness uses the control pipe. A repeating heartbeat
        // does no useful work and wakes an otherwise completely idle service.
        await Task.Delay(OperatingSystem.IsMacOS() ? Timeout.Infinite : intervalMs, cts.Token);
    }
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
}
finally
{
    cts.Cancel();
    try { await pipeTask; } catch (OperationCanceledException) { }
    await module.DisposeAsync(CancellationToken.None);
}

Console.WriteLine($"ScreenEase.Service stopped pid={pid}");
return 0;

static async Task AppendHeartbeatAsync(string path, string line, CancellationToken cancellationToken)
{
    const long maxHeartbeatBytes = 4L * 1024 * 1024;
    var parent = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
    var current = new FileInfo(path);
    if (current.Exists && current.Length >= maxHeartbeatBytes)
    {
        File.Move(path, path + ".1", overwrite: true);
    }

    await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
}

static async Task ServePipeAsync(
    string name,
    ScreenEaseModule module,
    SettingsRevisionStore settingsRevision,
    CancellationToken cancellationToken)
{
    // Keep accepting while existing clients are served. On Unix, disposing the
    // last server instance also drops queued connections from concurrent page reads.
    using var commandGate = new SemaphoreSlim(1, 1);
    var clients = new List<Task>();
    var listener = MptNamedPipePolicy.CreateServer(name);
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await listener.WaitForConnectionAsync(cancellationToken);
            var connected = listener;
            // Establish the replacement before the connected instance can close.
            listener = MptNamedPipePolicy.CreateServer(name);
            clients.RemoveAll(task => task.IsCompleted);
            clients.Add(ServeClientAsync(connected));
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    finally
    {
        await listener.DisposeAsync();
        await Task.WhenAll(clients);
    }

    async Task ServeClientAsync(System.IO.Pipes.NamedPipeServerStream server)
    {
        await using (server)
        {
            try
            {
                while (server.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    using var request = await ReadFramedMessageAsync(server, cancellationToken);
                    if (request is null) break;
                    await commandGate.WaitAsync(cancellationToken);
                    try
                    {
                        var response = await HandleRequestAsync(module, settingsRevision, request.RootElement, cancellationToken);
                        await WriteFramedMessageAsync(server, response, cancellationToken);
                    }
                    finally { commandGate.Release(); }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"ScreenEase.Service pipe error: {exception.Message}");
            }
        }
    }
}

static async Task<object> HandleRequestAsync(
    ScreenEaseModule module,
    SettingsRevisionStore settingsRevision,
    JsonElement request,
    CancellationToken cancellationToken)
{
    var command = ReadString(request, "command", "state");
    try
    {
        object data = command switch
        {
            "ping" => new { pong = true },
            "state" or "get_state" => new { pid = Environment.ProcessId, state = "active" },
            "moduleStatus" => await module.GetStatusAsync(cancellationToken),
            "listCommands" => await module.ListCommandsAsync(cancellationToken),
            "execute" => await ExecuteAsync(module, request, cancellationToken),
            "getSettings" => (await module.GetSettingsAsync(cancellationToken)) with
            {
                Revision = settingsRevision.Current
            },
            "updateSettings" => await UpdateSettingsAsync(module, settingsRevision, request, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown command '{command}'.")
        };
        return new { ok = true, command, data };
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"ScreenEase.Service command '{command}' failed: {exception}");
        return new { ok = false, command, error = exception.Message };
    }
}

static async Task<CommandExecutionResult> ExecuteAsync(
    ScreenEaseModule module,
    JsonElement request,
    CancellationToken cancellationToken)
{
    var invocationId = ReadString(request, "invocationId", Guid.NewGuid().ToString("N"));
    var commandId = ReadString(request, "commandId", "");
    var commandArgs = request.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object
        ? JsonNode.Parse(argsElement.GetRawText()) as JsonObject ?? new JsonObject()
        : new JsonObject();
    return await module.ExecuteCommandAsync(
        new CommandRequest(invocationId, commandId, commandArgs),
        cancellationToken);
}

static async Task<SettingsSnapshotDocument> UpdateSettingsAsync(
    ScreenEaseModule module,
    SettingsRevisionStore settingsRevision,
    JsonElement request,
    CancellationToken cancellationToken)
{
    var current = (await module.GetSettingsAsync(cancellationToken)) with
    {
        Revision = settingsRevision.Current
    };
    var expectedRevision = request.TryGetProperty("expectedRevision", out var revisionElement) && revisionElement.TryGetUInt64(out var revision)
        ? revision
        : current.Revision;
    if (expectedRevision != current.Revision)
    {
        throw new InvalidOperationException(
            $"Settings revision conflict: expected {expectedRevision}, current {current.Revision}.");
    }

    var patch = request.TryGetProperty("patch", out var patchElement) && patchElement.ValueKind == JsonValueKind.Object
        ? JsonNode.Parse(patchElement.GetRawText()) as JsonObject ?? new JsonObject()
        : new JsonObject();
    var validation = await module.ValidateSettingsAsync(
        new SettingsPatch("screenease", expectedRevision, patch),
        cancellationToken);
    if (!validation.Ok)
    {
        throw new InvalidOperationException(string.Join("; ", validation.Messages));
    }

    var nextRevision = checked(current.Revision + 1);
    var updated = await module.ApplySettingsAsync(
        new SettingsSnapshotDocument(
            "screenease",
            nextRevision,
            patch,
            DateTimeOffset.UtcNow),
        cancellationToken);
    settingsRevision.Commit(nextRevision);
    return updated with { Revision = nextRevision };
}

static async Task<JsonDocument?> ReadFramedMessageAsync(Stream stream, CancellationToken cancellationToken)
{
    var header = new byte[4];
    if (!await ReadExactlyOrEofAsync(stream, header, cancellationToken)) return null;

    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length <= 0 || length > 4 * 1024 * 1024)
    {
        throw new InvalidDataException($"Invalid message length {length}.");
    }

    var payload = new byte[length];
    await ReadExactlyAsync(stream, payload, cancellationToken);
    return JsonDocument.Parse(payload);
}

static async Task WriteFramedMessageAsync(Stream stream, object message, CancellationToken cancellationToken)
{
    var json = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    });
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
    await stream.WriteAsync(header, cancellationToken);
    await stream.WriteAsync(json, cancellationToken);
    await stream.FlushAsync(cancellationToken);
}

static async Task<bool> ReadExactlyOrEofAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
        if (read == 0)
        {
            if (offset == 0) return false;
            throw new EndOfStreamException();
        }
        offset += read;
    }
    return true;
}

static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
{
    if (!await ReadExactlyOrEofAsync(stream, buffer, cancellationToken))
    {
        throw new EndOfStreamException();
    }
}

static string ReadString(JsonElement element, string name, string fallback) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? fallback
        : fallback;

static string? GetOption(string[] values, string name)
{
    for (var index = 0; index < values.Length - 1; index++)
    {
        if (string.Equals(values[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return values[index + 1];
        }
    }
    return null;
}

internal sealed class SettingsRevisionStore
{
    private readonly string _path;

    public SettingsRevisionStore(string path)
    {
        _path = path;
        Current = Read(path);
    }

    public ulong Current { get; private set; }

    public void Commit(ulong revision)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(temporary, _path, overwrite: true);
        Current = revision;
    }

    private static ulong Read(string path)
    {
        if (File.Exists(path) &&
            ulong.TryParse(File.ReadAllText(path), out var revision) &&
            revision > 0)
        {
            return revision;
        }
        return 1;
    }
}
