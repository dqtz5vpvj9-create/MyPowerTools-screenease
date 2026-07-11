using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenEase.Core;

public sealed record NativeHostResponse(
    bool Ok,
    string Command,
    object? Data,
    string? Error = null);

public static class NativeMessageCodec
{
    public const int MaximumMessageBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static NativeMessageCodec()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static async Task<JsonDocument?> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await ReadExactOrEndAsync(input, header, cancellationToken);
        if (read == 0)
        {
            return null;
        }

        if (read != header.Length)
        {
            throw new InvalidDataException("Native message length header is incomplete.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaximumMessageBytes)
        {
            throw new InvalidDataException($"Native message length {length} is outside the allowed range.");
        }

        var payload = new byte[length];
        var payloadRead = await ReadExactOrEndAsync(input, payload, cancellationToken);
        if (payloadRead != length)
        {
            throw new InvalidDataException("Native message payload is incomplete.");
        }

        return JsonDocument.Parse(payload);
    }

    public static async Task WriteAsync(
        Stream output,
        object message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException($"Native response length {payload.Length} is outside the allowed range.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(payload, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    public static byte[] PackForTest(object message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var packed = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(0, 4), payload.Length);
        payload.CopyTo(packed.AsSpan(4));
        return packed;
    }

    private static async Task<int> ReadExactOrEndAsync(
        Stream input,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }

        return total;
    }
}

public sealed class NativeCommandHandler(EyeCareController controller)
{
    public async Task<NativeHostResponse> HandleAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var command = ReadCommand(request);
        try
        {
            return command switch
            {
                "ping" => Success(command, new { pong = true }),
                "state" or "get_state" => Success(command, await controller.GetStateAsync(cancellationToken)),
                "settings" or "get_settings" => Success(command, (await controller.GetStateAsync(cancellationToken)).Settings),
                "update_settings" or "set_settings" => Success(command, await UpdateSettingsAsync(request, cancellationToken)),
                "apply" or "apply_filter" or "set_filter" => Success(command, await ApplyAsync(request, cancellationToken)),
                "disable" or "reset" => Success(command, await controller.DisableAsync(cancellationToken)),
                "overlay" or "set_overlay" or "dim" => Success(command, await OverlayAsync(request, cancellationToken)),
                "toggle_overlay" => Success(command, await ToggleOverlayAsync(cancellationToken)),
                "rest_timer_start" => Success(command, await controller.StartRestTimerAsync(cancellationToken)),
                "rest_timer_pause" => Success(command, await controller.PauseRestTimerAsync(cancellationToken)),
                "rest_timer_resume" => Success(command, await controller.ResumeRestTimerAsync(cancellationToken)),
                "rest_timer_reset" => Success(command, await controller.ResetRestTimerAsync(cancellationToken)),
                "import_settings" => Success(command, await controller.ImportLegacySettingsAsync(ReadString(request, "path") ?? string.Empty, cancellationToken)),
                _ => new NativeHostResponse(false, command, null, $"Unknown command '{command}'.")
            };
        }
        catch (Exception exception)
        {
            return new NativeHostResponse(false, command, null, exception.Message);
        }
    }

    private async Task<DisplayEffect> ApplyAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var command = new ApplyEffectCommand(
            ProfileId: ReadString(request, "profileId") ?? ReadString(request, "profile") ?? ReadString(request, "mode"),
            ColorTemperatureKelvin: ReadInt(request, "colorTemperatureKelvin") ?? ReadInt(request, "kelvin") ?? ReadInt(request, "temperature"),
            BrightnessPercent: ReadInt(request, "brightnessPercent") ?? ReadInt(request, "brightness"),
            Enabled: ReadBool(request, "enabled"));

        return await controller.ApplyAsync(command, cancellationToken);
    }

    private async Task<OverlayState> OverlayAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var command = new UpdateOverlayCommand(
            Enabled: ReadBool(request, "enabled"),
            OpacityPercent: ReadInt(request, "opacityPercent") ?? ReadInt(request, "opacity") ?? ReadInt(request, "alpha"),
            ColorHex: ReadString(request, "colorHex") ?? ReadString(request, "color"));

        return await controller.UpdateOverlayAsync(command, cancellationToken);
    }

    private async Task<EyeCareSettings> UpdateSettingsAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var settingsElement = request.TryGetProperty("settings", out var value)
            ? value
            : request;
        var settings = settingsElement.Deserialize<EyeCareSettings>(NativeMessageCodec.JsonOptions)
                       ?? throw new InvalidDataException("Settings payload is empty.");
        return await controller.UpdateSettingsAsync(settings, cancellationToken);
    }

    private async Task<OverlayState> ToggleOverlayAsync(CancellationToken cancellationToken)
    {
        var state = await controller.GetStateAsync(cancellationToken);
        return await controller.UpdateOverlayAsync(new UpdateOverlayCommand(!state.Settings.Overlay.Enabled, null, null), cancellationToken);
    }

    private static NativeHostResponse Success(string command, object data) =>
        new(true, command, data);

    private static string ReadCommand(JsonElement request) =>
        (ReadString(request, "command")
         ?? ReadString(request, "type")
         ?? ReadString(request, "action")
         ?? "state").Trim().ToLowerInvariant();

    private static string? ReadString(JsonElement request, string name)
    {
        if (!request.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? ReadInt(JsonElement request, string name)
    {
        if (!request.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static bool? ReadBool(JsonElement request, string name)
    {
        if (!request.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number != 0,
            _ => null
        };
    }
}

public static class NativeHostLoop
{
    public static async Task RunAsync(
        Stream input,
        Stream output,
        NativeCommandHandler handler,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var request = await NativeMessageCodec.ReadAsync(input, cancellationToken);
            if (request is null)
            {
                break;
            }

            var response = await handler.HandleAsync(request.RootElement, cancellationToken);
            await NativeMessageCodec.WriteAsync(output, response, cancellationToken);
        }
    }
}


