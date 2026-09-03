using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MyPowerTools.Platform.Abstractions;

namespace ScreenEase.MyPowerTools;

/// <summary>
/// macOS counterpart of <see cref="ScreenEaseWindowsGammaDisplayService"/>. It writes the same
/// ramp produced by <see cref="ScreenEaseGammaRampMath"/> through the CoreGraphics display
/// transfer table, reports per-display partial failures, and hands the displays back to ColorSync
/// when the effect is disabled.
/// </summary>
internal sealed class ScreenEaseMacGammaDisplayService : IDisplayService, IScreenEaseDisplayResetService
{
    private readonly IDisplayService _inventory;
    private int _terminalOffset;

    public ScreenEaseMacGammaDisplayService(IDisplayService inventory)
    {
        _inventory = inventory;
    }

    public async Task<IReadOnlyList<DisplaySnapshot>> ListDisplaysAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return await _inventory.ListDisplaysAsync(cancellationToken).ConfigureAwait(false);
        }

        var native = EnumerateDisplays();
        return native.Count > 0
            ? native
            : await _inventory.ListDisplaysAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DisplayWriterStatus> GetWriterStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return await _inventory.GetWriterStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        var displays = EnumerateNativeDisplays();
        if (displays.Count == 0)
        {
            return new DisplayWriterStatus(false, "unavailable", "CoreGraphics reported no active display device.");
        }

        var writable = 0;
        var failures = new List<string>();
        foreach (var display in displays)
        {
            if (display.GammaCapacity > 0)
            {
                writable++;
            }
            else
            {
                failures.Add($"{display.Id}: CGDisplayGammaTableCapacity reported no writable gamma table.");
            }
        }

        return writable > 0
            ? new DisplayWriterStatus(
                true,
                "ready",
                $"CoreGraphics gamma-ramp writer can address {writable} local display device(s).")
            : new DisplayWriterStatus(false, "unavailable", string.Join("; ", failures));
    }

    public Task<BrokerOperationResult> ApplyProfileAsync(
        DisplayProfileIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return _inventory.ApplyProfileAsync(intent, cancellationToken);
        }

        var temperature = Math.Clamp(intent.ColorTemperature ?? 6500, 1000, 10000);
        var brightness = Math.Clamp(intent.Brightness ?? 100, 1, 150);
        var ramp = ScreenEaseGammaRampMath.BuildGammaRamp(
            temperature,
            brightness,
            Interlocked.Increment(ref _terminalOffset) % 3);
        return Task.FromResult(ApplyRamp(intent.DisplayId, ramp, cancellationToken));
    }

    public Task<BrokerOperationResult> ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return Task.FromResult(new BrokerOperationResult(true, "logical-only", "No macOS gamma ramp required a reset."));
        }

        var result = ApplyRamp("all", ScreenEaseGammaRampMath.BuildIdentityRamp(), cancellationToken, reset: true);
        Native.CGDisplayRestoreColorSyncSettings();
        return Task.FromResult(result);
    }

    [SupportedOSPlatform("macos")]
    private static BrokerOperationResult ApplyRamp(
        string displayId,
        ScreenEaseGammaRamp ramp,
        CancellationToken cancellationToken,
        bool reset = false)
    {
        var targets = EnumerateNativeDisplays()
            .Where(display => string.Equals(displayId, "all", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(display.Id, displayId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (targets.Length == 0)
        {
            return string.Equals(displayId, "all", StringComparison.OrdinalIgnoreCase)
                ? new BrokerOperationResult(false, "display-not-found", "CoreGraphics exposed no active display target.")
                : new BrokerOperationResult(false, "display-not-found", $"Display target '{displayId}' was not found.");
        }

        var failures = new List<string>();
        var successCount = 0;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target.GammaCapacity == 0)
            {
                failures.Add($"{target.Id}: CGDisplayGammaTableCapacity reported no writable gamma table.");
                continue;
            }

            var tableSize = (int)Math.Min(target.GammaCapacity, (uint)ScreenEaseGammaRampMath.RampLength);
            var error = Native.CGSetDisplayTransferByTable(
                target.DisplayId,
                (uint)tableSize,
                ToTransferTable(ramp.Red, tableSize),
                ToTransferTable(ramp.Green, tableSize),
                ToTransferTable(ramp.Blue, tableSize));
            if (error == Native.CGErrorSuccess)
            {
                successCount++;
            }
            else
            {
                failures.Add($"{target.Id}: CGSetDisplayTransferByTable failed with CGError {error}.");
            }
        }

        if (successCount == targets.Length)
        {
            return reset
                ? new BrokerOperationResult(
                    true,
                    "reset",
                    $"ScreenEase reset the macOS gamma ramp on {successCount} display device(s) and returned them to ColorSync.")
                : new BrokerOperationResult(
                    true,
                    "applied",
                    $"ScreenEase applied the macOS gamma ramp on {successCount} display device(s).");
        }

        var action = reset ? "reset" : "applied";
        return new BrokerOperationResult(
            false,
            "partial-failure",
            $"ScreenEase {action} the gamma ramp on {successCount}/{targets.Length} display device(s). {string.Join(" ", failures)}");
    }

    /// <summary>
    /// Normalises a 16-bit ScreenEase channel ramp into the 0..1 CGGammaValue table the display
    /// advertises. Displays report a 256-entry table in practice, in which case the mapping is
    /// one entry per ramp step.
    /// </summary>
    private static float[] ToTransferTable(ushort[] channel, int tableSize)
    {
        var table = new float[tableSize];
        var lastTableIndex = Math.Max(tableSize - 1, 1);
        for (var index = 0; index < tableSize; index++)
        {
            var source = tableSize == ScreenEaseGammaRampMath.RampLength
                ? index
                : (int)((long)index * (ScreenEaseGammaRampMath.RampLength - 1) / lastTableIndex);
            table[index] = channel[source] / (float)ushort.MaxValue;
        }

        return table;
    }

    [SupportedOSPlatform("macos")]
    private static IReadOnlyList<DisplaySnapshot> EnumerateDisplays()
    {
        return EnumerateNativeDisplays()
            .Select(display => new DisplaySnapshot(
                display.Id,
                display.Name,
                "connected",
                display.Width,
                display.Height,
                display.RefreshRateHz,
                display.Width >= display.Height ? "landscape" : "portrait",
                display.Primary,
                $"Bounds {display.Left},{display.Top} {display.Width}x{display.Height}; CoreGraphics gamma-ramp target with a {display.GammaCapacity}-entry transfer table"))
            .ToArray();
    }

    [SupportedOSPlatform("macos")]
    private static IReadOnlyList<NativeDisplay> EnumerateNativeDisplays()
    {
        if (Native.CGGetActiveDisplayList(0, null, out var available) != Native.CGErrorSuccess || available == 0)
        {
            return [];
        }

        var ids = new uint[available];
        if (Native.CGGetActiveDisplayList(available, ids, out var count) != Native.CGErrorSuccess)
        {
            return [];
        }

        var displays = new List<NativeDisplay>((int)count);
        for (var index = 0; index < count; index++)
        {
            var id = ids[index];
            var bounds = Native.CGDisplayBounds(id);
            var builtIn = Native.CGDisplayIsBuiltin(id) != 0;
            displays.Add(new NativeDisplay(
                id,
                id.ToString(CultureInfo.InvariantCulture),
                builtIn ? "Built-in Display" : $"Display {index + 1}",
                (int)bounds.Origin.X,
                (int)bounds.Origin.Y,
                (int)bounds.Size.Width,
                (int)bounds.Size.Height,
                ReadRefreshRate(id),
                Native.CGDisplayIsMain(id) != 0,
                Native.CGDisplayGammaTableCapacity(id)));
        }

        return displays;
    }

    [SupportedOSPlatform("macos")]
    private static int ReadRefreshRate(uint displayId)
    {
        var mode = Native.CGDisplayCopyDisplayMode(displayId);
        if (mode == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return (int)Math.Round(Native.CGDisplayModeGetRefreshRate(mode));
        }
        finally
        {
            Native.CGDisplayModeRelease(mode);
        }
    }

    private sealed record NativeDisplay(
        uint DisplayId,
        string Id,
        string Name,
        int Left,
        int Top,
        int Width,
        int Height,
        int RefreshRateHz,
        bool Primary,
        uint GammaCapacity);

    [SupportedOSPlatform("macos")]
    private static class Native
    {
        public const int CGErrorSuccess = 0;

        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(CoreGraphics)]
        public static extern int CGGetActiveDisplayList(uint maxDisplays, uint[]? activeDisplays, out uint displayCount);

        [DllImport(CoreGraphics)]
        public static extern CGRect CGDisplayBounds(uint display);

        [DllImport(CoreGraphics)]
        public static extern int CGDisplayIsMain(uint display);

        [DllImport(CoreGraphics)]
        public static extern int CGDisplayIsBuiltin(uint display);

        [DllImport(CoreGraphics)]
        public static extern IntPtr CGDisplayCopyDisplayMode(uint display);

        [DllImport(CoreGraphics)]
        public static extern double CGDisplayModeGetRefreshRate(IntPtr mode);

        [DllImport(CoreGraphics)]
        public static extern void CGDisplayModeRelease(IntPtr mode);

        [DllImport(CoreGraphics)]
        public static extern uint CGDisplayGammaTableCapacity(uint display);

        [DllImport(CoreGraphics)]
        public static extern int CGSetDisplayTransferByTable(
            uint display,
            uint tableSize,
            float[] redTable,
            float[] greenTable,
            float[] blueTable);

        [DllImport(CoreGraphics)]
        public static extern void CGDisplayRestoreColorSyncSettings();

        [StructLayout(LayoutKind.Sequential)]
        public struct CGPoint
        {
            public double X;
            public double Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CGSize
        {
            public double Width;
            public double Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CGRect
        {
            public CGPoint Origin;
            public CGSize Size;
        }
    }
}
