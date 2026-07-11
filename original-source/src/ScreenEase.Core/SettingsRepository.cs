using System.Text.Json;

namespace ScreenEase.Core;

public interface ISettingsRepository
{
    Task<EyeCareSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(EyeCareSettings settings, CancellationToken cancellationToken);
}

public sealed class JsonSettingsRepository(string path) : ISettingsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<EyeCareSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Defaults.CreateSettings();
        }

        await using var stream = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<EyeCareSettings>(stream, JsonOptions, cancellationToken);
        return settings is null ? Defaults.CreateSettings() : Validation.Normalize(settings);
    }

    public async Task SaveAsync(EyeCareSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, Validation.Normalize(settings), JsonOptions, cancellationToken);
    }
}

public sealed class InMemorySettingsRepository(EyeCareSettings? initial = null) : ISettingsRepository
{
    private EyeCareSettings settings = initial ?? Defaults.CreateSettings();

    public Task<EyeCareSettings> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(settings);

    public Task SaveAsync(EyeCareSettings value, CancellationToken cancellationToken)
    {
        settings = Validation.Normalize(value);
        return Task.CompletedTask;
    }
}


