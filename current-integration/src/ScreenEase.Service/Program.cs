using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// ScreenEase Service Unit — supervised by MyPowerTools.ServiceManager.
//
// Role: own a long-running process whose life is independent of the Shell and Runner, so that
// eye-care state survives Shell/Runner restarts. It exposes a named-pipe readiness probe on
// `screenease.core` (matching the upstream ScreenEase.CoreService pipe name) and answers the
// length-prefixed binary-JSON `ping` command the upstream protocol defines, so a ServiceManager
// readiness probe and future command proxying use the expected wire format.
//
// This initial cut focuses on proving the supervised-unit lifecycle (start/stop/restart/
// re-adoption across ServiceManager restarts) with a real process. The eye-care gamma/overlay
// engine continues to be driven by the in-proc ScreenEaseModule until a later change routes
// hardware writes through this service.

var pipeName = GetOption(args, "--pipe") ?? "screenease.core";
var heartbeatFile = GetOption(args, "--heartbeat-file");
var instanceToken = GetOption(args, "--instance-token") ?? "";
var intervalMs = int.TryParse(GetOption(args, "--interval-ms"), out var iv) ? iv : 1000;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var pid = Environment.ProcessId;
Console.WriteLine($"ScreenEase.Service starting pid={pid} pipe={pipeName} token={instanceToken}");

var pipeCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
_ = Task.Run(() => ServeReadinessPipe(pipeName, pipeCts.Token));

// Heartbeat loop: proves the unit is alive both to stdout (captured by UnitLogStore) and to an
// optional external file the gate can inspect.
try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var line = $"heartbeat pid={pid} ts={DateTimeOffset.UtcNow:O}";
        Console.WriteLine(line);
        if (!string.IsNullOrEmpty(heartbeatFile))
        {
            try
            {
                await File.AppendAllTextAsync(heartbeatFile, line + Environment.NewLine, cts.Token);
            }
            catch
            {
                // heartbeat file is best-effort
            }
        }

        try
        {
            await Task.Delay(intervalMs, cts.Token);
        }
        catch (TaskCanceledException)
        {
            break;
        }
    }
}
catch (OperationCanceledException)
{
    // expected on stop
}

Console.WriteLine($"ScreenEase.Service stopping pid={pid}");
return 0;

// Named-pipe readiness server. Speaks the ScreenEase native-messaging wire format:
// each message = 4-byte little-endian length + UTF-8 JSON payload. Responds to the
// canonical `ping` command (and aliases) with the upstream NativeHostResponse shape.
static async Task ServeReadinessPipe(string name, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        NamedPipeServerStream? server = null;
        try
        {
            server = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(cancellationToken);

            using var reader = new StreamReader(stream: server, leaveOpen: true);
            using var writer = new StreamWriter(stream: server, leaveOpen: true);

            // The upstream server supports multiple framed messages per connection.
            while (server.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var request = await ReadFramedMessageAsync(server, cancellationToken);
                if (request is null)
                {
                    break; // client closed
                }

                var command = ExtractCommand(request);
                object? data = command switch
                {
                    "ping" => new { pong = true },
                    "state" or "get_state" => new { pid = Environment.ProcessId, state = "active" },
                    _ => null
                };

                var ok = data is not null;
                var response = new
                {
                    ok,
                    command,
                    data,
                    error = ok ? null : $"Unknown command '{command}'."
                };

                await WriteFramedMessageAsync(server, response, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            // a single failed connection must not kill the readiness server
            try { Console.Error.WriteLine($"ScreenEase.Service pipe error: {ex.Message}"); } catch { }
        }
        finally
        {
            server?.Dispose();
        }
    }
}

// Chromium-native-messaging framing: 4-byte LE length header + UTF-8 JSON body.
static async Task<JsonDocument?> ReadFramedMessageAsync(Stream stream, CancellationToken cancellationToken)
{
    var header = new byte[4];
    var read = 0;
    while (read < 4)
    {
        var n = await stream.ReadAsync(header.AsMemory(read, 4 - read), cancellationToken);
        if (n == 0)
        {
            return read == 0 ? null : throw new EndOfStreamException();
        }

        read += n;
    }

    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length <= 0 || length > 1024 * 1024)
    {
        throw new InvalidDataException($"Invalid message length {length}");
    }

    var payload = new byte[length];
    read = 0;
    while (read < length)
    {
        var n = await stream.ReadAsync(payload.AsMemory(read, length - read), cancellationToken);
        if (n == 0)
        {
            throw new EndOfStreamException();
        }

        read += n;
    }

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

// Upstream priority: "command" -> "type" -> "action" -> default "state".
static string ExtractCommand(JsonDocument doc)
{
    foreach (var key in new[] { "command", "type", "action" })
    {
        if (doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
        {
            return el.GetString()?.Trim().ToLowerInvariant() ?? "state";
        }
    }

    return "state";
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}
