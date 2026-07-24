using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;

namespace ScreenEase.Surface.Services;

public sealed class ShellCommandExecutionService
{
    public const string UnitId = "screenease.service";
    private const string DefaultPipeName = "screenease.core";
    private readonly IServiceUnitClient _serviceUnits;

    public ShellCommandExecutionService(IServiceUnitClient serviceUnits)
    {
        _serviceUnits = serviceUnits;
    }

    public Task<ShellCommandExecutionResult> ExecuteAsync(
        string commandId,
        JsonObject? args = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(Guid.NewGuid().ToString("N"), commandId, args, cancellationToken);

    public async Task<ShellCommandExecutionResult> ExecuteAsync(
        string invocationId,
        string commandId,
        JsonObject? args = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(new
        {
            command = "execute",
            invocationId,
            commandId,
            args = args ?? new JsonObject()
        }, cancellationToken);
        var data = response.RootElement.GetProperty("data");
        var error = data.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object
            ? ReadString(errorElement, "message", "")
            : "";
        var result = new ScreenEaseCommandResponse(
            ReadString(data, "state", "failed"),
            ReadString(data, "output", ""),
            error);
        return new ShellCommandExecutionResult(
            $"{result.State}: {result.Summary}",
            result,
            string.Equals(result.State, "permission-required", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ScreenEaseSettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(new { command = "getSettings" }, cancellationToken);
        return ParseSettings(response.RootElement.GetProperty("data"));
    }

    public async Task<ScreenEaseSettingsSnapshot> UpdateSettingsAsync(
        ulong expectedRevision,
        JsonObject patch,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(new
        {
            command = "updateSettings",
            expectedRevision,
            patch
        }, cancellationToken);
        return ParseSettings(response.RootElement.GetProperty("data"));
    }

    public async Task<IReadOnlyList<MptToolLogEntry>> TailLogsAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = new List<MptToolLogEntry>();
        await foreach (var entry in _serviceUnits.TailLogsAsync(UnitId, cancellationToken))
        {
            entries.Add(entry);
        }
        return entries;
    }

    private async Task<JsonDocument> SendAsync(object request, CancellationToken cancellationToken)
    {
        var unit = await EnsureRunningAsync(cancellationToken);
        var readiness = unit.Readiness;
        var address = readiness?.Address;
        var pipeName = string.Equals(readiness?.Kind, "pipe", StringComparison.OrdinalIgnoreCase) &&
                       !string.IsNullOrWhiteSpace(address)
            ? address
            : DefaultPipeName;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
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
        var units = await _serviceUnits.ListAsync(cancellationToken);
        var unit = units.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, UnitId, StringComparison.OrdinalIgnoreCase));
        if (unit is null)
        {
            await _serviceUnits.ReloadAsync(cancellationToken);
            units = await _serviceUnits.ListAsync(cancellationToken);
            unit = units.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, UnitId, StringComparison.OrdinalIgnoreCase));
        }

        if (unit is null)
        {
            throw new InvalidOperationException($"Service Unit '{UnitId}' is not installed.");
        }

        if (unit.State is not ServiceUnitState.Active and not ServiceUnitState.Degraded)
        {
            unit = await _serviceUnits.StartAsync(UnitId, cancellationToken);
        }

        if (unit.State is not ServiceUnitState.Active and not ServiceUnitState.Degraded)
        {
            throw new InvalidOperationException(unit.LastError ?? "ScreenEase Service failed to start.");
        }
        return unit;
    }

    private static ScreenEaseSettingsSnapshot ParseSettings(JsonElement data)
    {
        var values = data.TryGetProperty("values", out var valuesElement) && valuesElement.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(valuesElement.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var revision = data.TryGetProperty("revision", out var revisionElement) && revisionElement.TryGetUInt64(out var value)
            ? value
            : 0;
        return new ScreenEaseSettingsSnapshot(revision, values);
    }

    private static string ReadString(JsonElement element, string name, string fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

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

public sealed record ShellCommandExecutionResult(
    string StatusText,
    ScreenEaseCommandResponse Response,
    bool RequiresPermissionPrompt);

public sealed record ScreenEaseCommandResponse(string State, string Summary, string ErrorMessage);

public sealed record ScreenEaseSettingsSnapshot(ulong Revision, JsonObject Values);
