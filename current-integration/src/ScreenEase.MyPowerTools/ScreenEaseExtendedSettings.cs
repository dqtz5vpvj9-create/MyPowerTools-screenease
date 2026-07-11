using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;

namespace ScreenEase.MyPowerTools;

internal sealed record ScreenEaseAdvancedSettings(bool SmoothTransitions, int TransitionDurationMs)
{
    public static ScreenEaseAdvancedSettings Default() => new(true, 2000);

    public static ScreenEaseAdvancedSettings FromJson(JsonObject node) => new(
        SettingsJson.ReadBool(node, "smoothTransitions") ?? true,
        Math.Clamp(SettingsJson.ReadInt(node, "transitionDurationMs") ?? 2000, 0, 120_000));

    public JsonObject ToJson() => new()
    {
        ["smoothTransitions"] = SmoothTransitions,
        ["transitionDurationMs"] = TransitionDurationMs
    };
}

internal sealed record ScreenEaseOverlaySettings(bool Enabled, int OpacityPercent, string ColorHex)
{
    public static ScreenEaseOverlaySettings Default() => new(false, 18, "#FFC98A");

    public static ScreenEaseOverlaySettings FromJson(JsonObject node) => Normalize(new(
        SettingsJson.ReadBool(node, "enabled") ?? false,
        SettingsJson.ReadInt(node, "opacityPercent") ?? 18,
        SettingsJson.ReadString(node, "colorHex") ?? "#FFC98A"));

    public static ScreenEaseOverlaySettings Normalize(ScreenEaseOverlaySettings value) => value with
    {
        OpacityPercent = Math.Clamp(value.OpacityPercent, 0, 95),
        ColorHex = NormalizeColorHex(value.ColorHex)
    };

    public JsonObject ToJson() => new()
    {
        ["enabled"] = Enabled,
        ["opacityPercent"] = OpacityPercent,
        ["colorHex"] = ColorHex
    };

    internal static string NormalizeColorHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "#000000";
        }

        var normalized = value.Trim();
        if (!normalized.StartsWith('#'))
        {
            normalized = "#" + normalized;
        }

        return normalized.Length == 7 && normalized.Skip(1).All(Uri.IsHexDigit)
            ? normalized.ToUpperInvariant()
            : "#000000";
    }
}

internal sealed record ScreenEaseOverlayState(
    bool Enabled,
    int OpacityPercent,
    string ColorHex,
    int WindowCount,
    string State,
    string Message)
{
    public static ScreenEaseOverlayState Hidden(string colorHex = "#000000", string message = "Screen overlay is hidden.") =>
        new(false, 0, ScreenEaseOverlaySettings.NormalizeColorHex(colorHex), 0, "hidden", message);

    public JsonObject ToJson() => new()
    {
        ["enabled"] = Enabled,
        ["opacityPercent"] = OpacityPercent,
        ["colorHex"] = ColorHex,
        ["windowCount"] = WindowCount,
        ["state"] = State,
        ["message"] = Message
    };
}

internal sealed record ScreenEaseHotkeyBinding(string Id, string Gesture, bool Enabled)
{
    public static IReadOnlyList<ScreenEaseHotkeyBinding> Defaults() =>
    [
        new("toggle-enabled", "Ctrl+Alt+F9", false),
        new("brightness-up", "Ctrl+Alt+Up", false),
        new("brightness-down", "Ctrl+Alt+Down", false),
        new("temperature-up", "Ctrl+Alt+Right", false),
        new("temperature-down", "Ctrl+Alt+Left", false),
        new("long-read-profile", "Ctrl+Alt+R", false),
        new("low-blue-evening-profile", "Ctrl+Alt+H", false),
        new("toggle-overlay", "Ctrl+Alt+D", false)
    ];

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["gesture"] = Gesture,
        ["enabled"] = Enabled
    };

    public static IReadOnlyList<ScreenEaseHotkeyBinding> Normalize(IEnumerable<ScreenEaseHotkeyBinding>? bindings)
    {
        var supplied = (bindings ?? [])
            .Where(binding => !string.IsNullOrWhiteSpace(binding.Id))
            .GroupBy(binding => binding.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        return Defaults()
            .Select(fallback => supplied.TryGetValue(fallback.Id, out var binding)
                ? fallback with
                {
                    Gesture = NormalizeGesture(binding.Gesture),
                    Enabled = binding.Enabled && !string.IsNullOrWhiteSpace(NormalizeGesture(binding.Gesture))
                }
                : fallback)
            .ToArray();
    }

    private static string NormalizeGesture(string? gesture) => string.Join(
        '+',
        (gesture ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

internal interface IScreenEaseOverlayService : IDisposable
{
    Task<ScreenEaseOverlayState> GetStateAsync(CancellationToken cancellationToken);
    Task<ScreenEaseOverlayState> ApplyAsync(ScreenEaseOverlaySettings settings, CancellationToken cancellationToken);
    Task<ScreenEaseOverlayState> HideAsync(CancellationToken cancellationToken);
}

internal sealed class ScreenEaseLogicalOverlayService : IScreenEaseOverlayService
{
    private ScreenEaseOverlayState _state = ScreenEaseOverlayState.Hidden();

    public Task<ScreenEaseOverlayState> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_state);
    }

    public Task<ScreenEaseOverlayState> ApplyAsync(ScreenEaseOverlaySettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ScreenEaseOverlaySettings.Normalize(settings);
        _state = normalized.Enabled
            ? new ScreenEaseOverlayState(true, normalized.OpacityPercent, normalized.ColorHex, 0, "logical-only", "Overlay settings were saved; this platform exposes no native overlay windows.")
            : ScreenEaseOverlayState.Hidden(normalized.ColorHex);
        return Task.FromResult(_state);
    }

    public Task<ScreenEaseOverlayState> HideAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = ScreenEaseOverlayState.Hidden(_state.ColorHex);
        return Task.FromResult(_state);
    }

    public void Dispose()
    {
    }
}
