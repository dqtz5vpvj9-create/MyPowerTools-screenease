using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace ScreenEase.MyPowerTools;

/// <summary>
/// Runner-facing ScreenEase module. It keeps command discovery and hotkey registration in the
/// host process while all mutable state, Gamma writes, overlays and timers live in
/// ScreenEase.Service. Runner disposal only releases this proxy.
/// </summary>
public sealed class ScreenEaseServiceProxyModule : IMptModule
{
    private readonly ScreenEaseModule _contractSource = new();
    private ScreenEaseServicePipeClient? _service;
    private ModuleContext? _context;

    public string Id => "screenease";
    public string PackageId => "screenease";
    public Version Version => new(0, 2, 0);

    public async ValueTask<InitializeResult> InitializeAsync(
        ModuleContext context,
        CancellationToken cancellationToken)
    {
        _context = context;
        var units = context.TryGetCapability<IServiceUnitClientFactory>("service.units", out var factory)
            ? factory.ForTool(Id)
            : new NullServiceUnitClient(Id);
        _service = new ScreenEaseServicePipeClient(units);
        try
        {
            var settings = await _service.GetSettingsAsync(cancellationToken);
            await SyncHotkeysAsync(settings.Values, cancellationToken);
        }
        catch
        {
            // Runtime discovery remains available while ServiceManager is starting. Product calls
            // surface the concrete connection error and can be retried after the unit recovers.
        }

        return new InitializeResult(
            true,
            context.ProtocolVersion,
            ["status", "commands", "settings", "logs", "dashboardCard", "detailPage"]);
    }

    public async ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Service.GetModuleStatusAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            return new ModuleStatusSnapshot(
                Id,
                "degraded",
                $"ScreenEase Service unavailable: {exception.Message}",
                DateTimeOffset.UtcNow,
                [new HealthCheckSnapshot("service-unit", "ScreenEase Service", false, exception.Message)],
                0);
        }
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
        => _contractSource.ListCommandsAsync(cancellationToken);

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
        => await Service.ExecuteAsync(request, cancellationToken);

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
        EventCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
        => _contractSource.GetSettingsSchemaAsync(cancellationToken);

    public async ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Service.GetSettingsAsync(cancellationToken);
        }
        catch
        {
            return new SettingsSnapshotDocument(Id, 0, new JsonObject(), DateTimeOffset.UtcNow);
        }
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(
        SettingsPatch patch,
        CancellationToken cancellationToken)
        => _contractSource.ValidateSettingsAsync(patch, cancellationToken);

    public async ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(
        SettingsSnapshotDocument snapshot,
        CancellationToken cancellationToken)
    {
        var current = await Service.GetSettingsAsync(cancellationToken);
        var updated = await Service.UpdateSettingsAsync(current.Revision, snapshot.Values, cancellationToken);
        await SyncHotkeysAsync(updated.Values, cancellationToken);
        return updated;
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
        => _contractSource.ListSurfacesAsync(cancellationToken);

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        _service = null;
        _context = null;
        return ValueTask.CompletedTask;
    }

    private ScreenEaseServicePipeClient Service =>
        _service ?? throw new InvalidOperationException("ScreenEase proxy has not been initialized.");

    private async Task SyncHotkeysAsync(JsonObject values, CancellationToken cancellationToken)
    {
        if (_context?.TryGetCapability<IModuleHotkeyConfigurationService>("runtime.hotkeys", out var hotkeys) != true)
        {
            return;
        }

        var configured = values["hotkeys"] as JsonArray;
        var bindings = (configured ?? [])
            .OfType<JsonObject>()
            .Select(item => new ModuleHotkeyConfiguration(
                ReadString(item, "id"),
                ReadString(item, "gesture"),
                ReadBool(item, "enabled")))
            .Where(item => item.Id.Length > 0 && item.Gesture.Length > 0)
            .ToArray();
        await hotkeys.ApplyAsync(bindings, cancellationToken);
    }

    private static string ReadString(JsonObject source, string key)
    {
        try { return source[key]?.GetValue<string>() ?? ""; }
        catch (InvalidOperationException) { return ""; }
    }

    private static bool ReadBool(JsonObject source, string key)
    {
        try { return source[key]?.GetValue<bool>() ?? false; }
        catch (InvalidOperationException) { return false; }
    }
}

internal sealed class ScreenEaseServicePipeClient
{
    private const string UnitId = "screenease.service";
    private const string DefaultPipeName = "screenease.core";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly IServiceUnitClient _units;

    public ScreenEaseServicePipeClient(IServiceUnitClient units)
    {
        _units = units;
    }

    public async Task<ModuleStatusSnapshot> GetModuleStatusAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new { command = "moduleStatus" }, cancellationToken);
        return JsonSerializer.Deserialize<ModuleStatusSnapshot>(
                   response.RootElement.GetProperty("data").GetRawText(),
                   JsonOptions)
               ?? throw new InvalidDataException("ScreenEase Service returned no module status.");
    }

    public async Task<CommandExecutionResult> ExecuteAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new
        {
            command = "execute",
            invocationId = request.InvocationId,
            commandId = request.CommandId,
            args = request.Args
        }, cancellationToken);
        return JsonSerializer.Deserialize<CommandExecutionResult>(
                   response.RootElement.GetProperty("data").GetRawText(),
                   JsonOptions)
               ?? throw new InvalidDataException("ScreenEase Service returned no command result.");
    }

    public async Task<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new { command = "getSettings" }, cancellationToken);
        return ParseSettings(response.RootElement.GetProperty("data"));
    }

    public async Task<SettingsSnapshotDocument> UpdateSettingsAsync(
        ulong expectedRevision,
        JsonObject patch,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new
        {
            command = "updateSettings",
            expectedRevision,
            patch
        }, cancellationToken);
        return ParseSettings(response.RootElement.GetProperty("data"));
    }

    private async Task<JsonDocument> SendAsync(object request, CancellationToken cancellationToken)
    {
        var unit = await EnsureRunningAsync(cancellationToken);
        var address = unit.Readiness?.Address;
        var pipeName = string.Equals(unit.Readiness?.Kind, "pipe", StringComparison.OrdinalIgnoreCase) &&
                       !string.IsNullOrWhiteSpace(address)
            ? address
            : DefaultPipeName;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, timeout.Token);
        await pipe.WriteAsync(payload, timeout.Token);
        await pipe.FlushAsync(timeout.Token);

        await ReadExactlyAsync(pipe, header, timeout.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException($"ScreenEase Service returned invalid message length {length}.");
        }
        var responsePayload = new byte[length];
        await ReadExactlyAsync(pipe, responsePayload, timeout.Token);
        var response = JsonDocument.Parse(responsePayload);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = response.RootElement.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;
            response.Dispose();
            throw new InvalidOperationException(error ?? "ScreenEase Service request failed.");
        }
        return response;
    }

    private async Task<ServiceUnitSnapshot> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        var units = await _units.ListAsync(cancellationToken);
        var unit = units.FirstOrDefault(item => string.Equals(item.Id, UnitId, StringComparison.OrdinalIgnoreCase));
        if (unit is null)
        {
            await _units.ReloadAsync(cancellationToken);
            units = await _units.ListAsync(cancellationToken);
            unit = units.FirstOrDefault(item => string.Equals(item.Id, UnitId, StringComparison.OrdinalIgnoreCase));
        }
        if (unit is null) throw new InvalidOperationException($"Service Unit '{UnitId}' is unavailable.");
        if (unit.State is not ServiceUnitState.Active and not ServiceUnitState.Degraded)
        {
            unit = await _units.StartAsync(UnitId, cancellationToken);
        }
        if (unit.State is not ServiceUnitState.Active and not ServiceUnitState.Degraded)
        {
            throw new InvalidOperationException(unit.LastError ?? "ScreenEase Service failed to start.");
        }
        return unit;
    }

    private static SettingsSnapshotDocument ParseSettings(JsonElement data)
    {
        var values = data.TryGetProperty("values", out var valuesElement) && valuesElement.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(valuesElement.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var revision = data.TryGetProperty("revision", out var revisionElement) && revisionElement.TryGetUInt64(out var value)
            ? value
            : 0;
        var updatedAt = data.TryGetProperty("updatedAt", out var updatedElement) &&
                        updatedElement.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(updatedElement.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
        return new SettingsSnapshotDocument("screenease", revision, values, updatedAt);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
