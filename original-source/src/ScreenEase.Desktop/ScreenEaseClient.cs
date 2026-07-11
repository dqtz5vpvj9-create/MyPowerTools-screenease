using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenEase.Desktop;

public sealed class ScreenEaseClient : IDisposable
{
    private const string DefaultEndpoint = "pipe:screenease.core";
    private const string PipePrefix = "pipe:";
    private const string PipePathPrefix = "\\\\.\\pipe\\";
    private const int MaximumMessageBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly HttpClient? _http;
    private readonly string? _pipeName;

    public ScreenEaseClient(string endpoint)
    {
        var normalized = string.IsNullOrWhiteSpace(endpoint)
            ? DefaultEndpoint
            : endpoint.Trim();

        if (TryReadPipeName(normalized, out var pipeName))
        {
            _pipeName = pipeName;
            return;
        }

        if (!normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        _http = new HttpClient
        {
            BaseAddress = new Uri(normalized, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(4)
        };
    }

    public async Task<EyeCareState> GetStateAsync(CancellationToken cancellationToken) =>
        _pipeName is not null
            ? await SendPipeAsync<EyeCareState>(new { command = "state" }, cancellationToken)
            : await GetJsonAsync<EyeCareState>("api/state", cancellationToken);

    public async Task<EyeCareSettings> UpdateSettingsAsync(
        EyeCareSettings settings,
        CancellationToken cancellationToken) =>
        _pipeName is not null
            ? await SendPipeAsync<EyeCareSettings>(new { command = "update_settings", settings }, cancellationToken)
            : await SendHttpJsonAsync<EyeCareSettings>(HttpMethod.Put, "api/settings", settings, cancellationToken);

    public async Task<DisplayEffect> ApplyAsync(
        ApplyEffectCommand command,
        CancellationToken cancellationToken) =>
        _pipeName is not null
            ? await SendPipeAsync<DisplayEffect>(
                new
                {
                    command = "apply",
                    command.ProfileId,
                    command.ColorTemperatureKelvin,
                    command.BrightnessPercent,
                    command.Enabled
                },
                cancellationToken)
            : await SendHttpJsonAsync<DisplayEffect>(HttpMethod.Post, "api/apply", command, cancellationToken);

    public async Task<DisplayEffect> DisableAsync(CancellationToken cancellationToken) =>
        _pipeName is not null
            ? await SendPipeAsync<DisplayEffect>(new { command = "disable" }, cancellationToken)
            : await SendHttpJsonAsync<DisplayEffect>(HttpMethod.Post, "api/disable", null, cancellationToken);

    public async Task<OverlayState> UpdateOverlayAsync(
        UpdateOverlayCommand command,
        CancellationToken cancellationToken) =>
        _pipeName is not null
            ? await SendPipeAsync<OverlayState>(
                new
                {
                    command = "overlay",
                    command.Enabled,
                    command.OpacityPercent,
                    command.ColorHex
                },
                cancellationToken)
            : await SendHttpJsonAsync<OverlayState>(HttpMethod.Put, "api/overlay", command, cancellationToken);

    public async Task<RestTimerState> StartRestTimerAsync(CancellationToken cancellationToken) =>
        await SendTimerAsync("rest_timer_start", "api/rest-timer/start", cancellationToken);

    public async Task<RestTimerState> PauseRestTimerAsync(CancellationToken cancellationToken) =>
        await SendTimerAsync("rest_timer_pause", "api/rest-timer/pause", cancellationToken);

    public async Task<RestTimerState> ResumeRestTimerAsync(CancellationToken cancellationToken) =>
        await SendTimerAsync("rest_timer_resume", "api/rest-timer/resume", cancellationToken);

    public async Task<RestTimerState> ResetRestTimerAsync(CancellationToken cancellationToken) =>
        await SendTimerAsync("rest_timer_reset", "api/rest-timer/reset", cancellationToken);

    public void Dispose() => _http?.Dispose();

    private async Task<RestTimerState> SendTimerAsync(
        string pipeCommand,
        string httpPath,
        CancellationToken cancellationToken) =>
        _pipeName is not null
            ? await SendPipeAsync<RestTimerState>(new { command = pipeCommand }, cancellationToken)
            : await SendHttpJsonAsync<RestTimerState>(HttpMethod.Post, httpPath, null, cancellationToken);

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await RequireHttpClient().GetAsync(path, cancellationToken);
        return await ReadHttpResponseAsync<T>(response, cancellationToken);
    }

    private async Task<T> SendHttpJsonAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await RequireHttpClient().SendAsync(request, cancellationToken);
        return await ReadHttpResponseAsync<T>(response, cancellationToken);
    }

    private async Task<T> SendPipeAsync<T>(object message, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName ?? throw new InvalidOperationException("Pipe endpoint is not configured."),
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(4000, cancellationToken);
        await WritePipeMessageAsync(pipe, message, cancellationToken);
        using var responseDocument = await ReadPipeMessageAsync(pipe, cancellationToken);
        var response = responseDocument.RootElement.Deserialize<PipeResponse<T>>(JsonOptions)
                       ?? throw new InvalidOperationException("The pipe returned an empty response.");
        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error ?? $"Pipe command {response.Command} failed.");
        }

        return response.Data ?? throw new InvalidOperationException("The pipe response did not include data.");
    }

    private static async Task WritePipeMessageAsync(
        Stream pipe,
        object message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidOperationException("Pipe request is too large.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, cancellationToken);
        await pipe.WriteAsync(payload, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadPipeMessageAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(pipe, 4, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageBytes)
        {
            throw new InvalidOperationException($"Pipe response length {length} is outside the allowed range.");
        }

        var payload = await ReadExactAsync(pipe, length, cancellationToken);
        return JsonDocument.Parse(payload);
    }

    private static async Task<byte[]> ReadExactAsync(
        Stream pipe,
        int count,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var total = 0;
        while (total < count)
        {
            var read = await pipe.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("The pipe closed before the response was complete.");
            }

            total += read;
        }

        return buffer;
    }

    private static async Task<T> ReadHttpResponseAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        var value = JsonSerializer.Deserialize<T>(body, JsonOptions);
        return value ?? throw new InvalidOperationException("The service returned an empty response.");
    }

    private static bool TryReadPipeName(string endpoint, out string pipeName)
    {
        if (endpoint.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
        {
            pipeName = endpoint[PipePrefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(pipeName);
        }

        if (endpoint.StartsWith(PipePathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            pipeName = endpoint[PipePathPrefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(pipeName);
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            pipeName = endpoint;
            return !string.IsNullOrWhiteSpace(pipeName);
        }

        pipeName = string.Empty;
        return false;
    }

    private HttpClient RequireHttpClient() =>
        _http ?? throw new InvalidOperationException("HTTP endpoint is not configured.");

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class PipeResponse<T>
    {
        public bool Ok { get; set; }
        public string Command { get; set; } = string.Empty;
        public T? Data { get; set; }
        public string? Error { get; set; }
    }
}
