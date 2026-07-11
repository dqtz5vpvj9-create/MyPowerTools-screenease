using System.Runtime.InteropServices;
using MyPowerTools.Platform.Abstractions;

namespace ScreenEase.MyPowerTools;

internal interface IScreenEaseDisplayResetService
{
    Task<BrokerOperationResult> ResetAsync(CancellationToken cancellationToken);
}

internal sealed class ScreenEaseWindowsGammaDisplayService : IDisplayService, IScreenEaseDisplayResetService
{
    private const int RemoteSessionMetric = 0x1000;
    private readonly IDisplayService _inventory;
    private int _terminalOffset;

    public ScreenEaseWindowsGammaDisplayService(IDisplayService inventory)
    {
        _inventory = inventory;
    }

    public async Task<IReadOnlyList<DisplaySnapshot>> ListDisplaysAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
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
        if (!OperatingSystem.IsWindows())
        {
            return await _inventory.GetWriterStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Native.GetSystemMetrics(RemoteSessionMetric) != 0)
        {
            return new DisplayWriterStatus(
                false,
                "remote-session",
                "Windows gamma-ramp writes are unavailable in the current Remote Desktop session.");
        }

        var displays = EnumerateNativeMonitors();
        if (displays.Count == 0)
        {
            var desktopDc = Native.GetDC(IntPtr.Zero);
            if (desktopDc == IntPtr.Zero)
            {
                return new DisplayWriterStatus(false, "unavailable", "Windows reported no local display device or desktop device context.");
            }

            Native.ReleaseDC(IntPtr.Zero, desktopDc);
            return new DisplayWriterStatus(true, "ready", "Windows gamma-ramp writer can address the desktop device context.");
        }

        var writable = 0;
        var failures = new List<string>();
        foreach (var display in displays)
        {
            var dc = CreateDisplayDc(display.DeviceName, failures);
            if (dc == IntPtr.Zero)
            {
                continue;
            }

            writable++;
            Native.DeleteDC(dc);
        }

        return writable > 0
            ? new DisplayWriterStatus(
                true,
                "ready",
                $"Windows gamma-ramp writer can address {writable} local display device(s).")
            : new DisplayWriterStatus(
                false,
                "unavailable",
                failures.Count == 0 ? "No display device context is available." : string.Join("; ", failures));
    }

    public Task<BrokerOperationResult> ApplyProfileAsync(
        DisplayProfileIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return _inventory.ApplyProfileAsync(intent, cancellationToken);
        }

        if (Native.GetSystemMetrics(RemoteSessionMetric) != 0)
        {
            return Task.FromResult(new BrokerOperationResult(
                false,
                "remote-session",
                "ScreenEase retained the logical effect; Windows blocked gamma-ramp writes in this Remote Desktop session."));
        }

        var temperature = Math.Clamp(intent.ColorTemperature ?? 6500, 1000, 10000);
        var brightness = Math.Clamp(intent.Brightness ?? 100, 1, 150);
        var ramp = ToNative(BuildGammaRamp(
            temperature,
            brightness,
            Interlocked.Increment(ref _terminalOffset) % 3));
        return Task.FromResult(ApplyRamp(intent.DisplayId, ref ramp, cancellationToken));
    }

    public Task<BrokerOperationResult> ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new BrokerOperationResult(true, "logical-only", "No Windows gamma ramp required a reset."));
        }

        if (Native.GetSystemMetrics(RemoteSessionMetric) != 0)
        {
            return Task.FromResult(new BrokerOperationResult(
                true,
                "logical-only",
                "ScreenEase effect was disabled; the Remote Desktop session exposes no local gamma ramp to reset."));
        }

        var ramp = ToNative(BuildIdentityRamp());
        return Task.FromResult(ApplyRamp("all", ref ramp, cancellationToken, reset: true));
    }

    internal static ScreenEaseGammaRamp BuildGammaRamp(int kelvin, int brightnessPercent, int terminalOffset = 0)
    {
        var color = ToRgbChannels(kelvin);
        var brightness = Math.Clamp(brightnessPercent, 1, 150);
        return BuildRamp(
            ScaleStep(color.Red, brightness),
            ScaleStep(color.Green, brightness),
            ScaleStep(color.Blue, brightness),
            terminalOffset);
    }

    internal static ScreenEaseGammaRamp BuildIdentityRamp() => BuildRamp(257, 257, 257, 0);

    private static ScreenEaseRgbChannels ToRgbChannels(int kelvin)
    {
        var temperature = Math.Clamp(kelvin, 1000, 10000) / 100.0;
        var red = temperature <= 66
            ? 255
            : 329.698727466 * Math.Pow(temperature - 60, -0.1332047592);
        var green = temperature <= 66
            ? 99.4708025861 * Math.Log(temperature) - 161.1195681661
            : 288.1221695283 * Math.Pow(temperature - 60, -0.0755148492);
        var blue = temperature >= 66
            ? 255
            : temperature <= 19
                ? 0
                : 138.5177312231 * Math.Log(temperature - 10) - 305.0447927307;
        return new ScreenEaseRgbChannels(ToChannel(red), ToChannel(green), ToChannel(blue));
    }

    private BrokerOperationResult ApplyRamp(
        string displayId,
        ref Native.GammaRamp ramp,
        CancellationToken cancellationToken,
        bool reset = false)
    {
        var monitors = EnumerateNativeMonitors();
        var targets = monitors
            .Where(monitor => string.Equals(displayId, "all", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(monitor.DeviceName, displayId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (targets.Length == 0)
        {
            if (string.Equals(displayId, "all", StringComparison.OrdinalIgnoreCase))
            {
                var desktopDc = Native.GetDC(IntPtr.Zero);
                if (desktopDc == IntPtr.Zero)
                {
                    return new BrokerOperationResult(false, "display-not-found", "Windows exposed no monitor target or desktop device context.");
                }

                try
                {
                    var success = Native.SetDeviceGammaRamp(desktopDc, ref ramp);
                    var desktopAction = reset ? "reset" : "applied";
                    return success
                        ? new BrokerOperationResult(true, reset ? "reset" : "applied", $"ScreenEase {desktopAction} the Windows desktop gamma ramp.")
                        : new BrokerOperationResult(false, "write-failed", $"SetDeviceGammaRamp failed on the desktop device context with Win32 error {Marshal.GetLastWin32Error()}.");
                }
                finally
                {
                    Native.ReleaseDC(IntPtr.Zero, desktopDc);
                }
            }

            return new BrokerOperationResult(false, "display-not-found", $"Display target '{displayId}' was not found.");
        }

        var failures = new List<string>();
        var successCount = 0;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dc = CreateDisplayDc(target.DeviceName, failures);
            if (dc == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                if (Native.SetDeviceGammaRamp(dc, ref ramp))
                {
                    successCount++;
                }
                else
                {
                    failures.Add($"{target.DeviceName}: SetDeviceGammaRamp failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }
            }
            finally
            {
                Native.DeleteDC(dc);
            }
        }

        var action = reset ? "reset" : "applied";
        return successCount == targets.Length
            ? new BrokerOperationResult(true, reset ? "reset" : "applied", $"ScreenEase {action} the Windows gamma ramp on {successCount} display device(s).")
            : new BrokerOperationResult(
                false,
                "partial-failure",
                $"ScreenEase {action} the gamma ramp on {successCount}/{targets.Length} display device(s). {string.Join(" ", failures)}");
    }

    private static IReadOnlyList<DisplaySnapshot> EnumerateDisplays()
    {
        return EnumerateNativeMonitors()
            .Select(monitor => new DisplaySnapshot(
                monitor.DeviceName,
                monitor.DeviceName,
                "connected",
                monitor.Width,
                monitor.Height,
                0,
                monitor.Width >= monitor.Height ? "landscape" : "portrait",
                monitor.Primary,
                $"Bounds {monitor.Left},{monitor.Top} {monitor.Width}x{monitor.Height}; Windows gamma-ramp target"))
            .ToArray();
    }

    private static IReadOnlyList<NativeMonitor> EnumerateNativeMonitors()
    {
        var monitors = new List<NativeMonitor>();
        Native.MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc, ref Native.Rect rect, IntPtr data) =>
        {
            var info = new Native.MonitorInfoEx
            {
                Size = Marshal.SizeOf<Native.MonitorInfoEx>(),
                DeviceName = string.Empty
            };
            if (Native.GetMonitorInfo(monitor, ref info) && !string.IsNullOrWhiteSpace(info.DeviceName))
            {
                monitors.Add(new NativeMonitor(
                    info.DeviceName,
                    info.Monitor.Left,
                    info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left,
                    info.Monitor.Bottom - info.Monitor.Top,
                    (info.Flags & Native.PrimaryMonitorFlag) != 0));
            }

            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return monitors
            .DistinctBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IntPtr CreateDisplayDc(string deviceName, ICollection<string> failures)
    {
        var dc = Native.CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
        if (dc != IntPtr.Zero)
        {
            return dc;
        }

        failures.Add($"{deviceName}: CreateDC(DISPLAY) failed with Win32 error {Marshal.GetLastWin32Error()}.");
        dc = Native.CreateDC(deviceName, null, null, IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            failures.Add($"{deviceName}: CreateDC(device) failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return dc;
    }

    private static ScreenEaseGammaRamp BuildRamp(int redStep, int greenStep, int blueStep, int terminalOffset)
    {
        return new ScreenEaseGammaRamp(
            BuildChannelRamp(redStep, terminalOffset),
            BuildChannelRamp(greenStep, terminalOffset),
            BuildChannelRamp(blueStep, terminalOffset));
    }

    private static ushort[] BuildChannelRamp(int step, int terminalOffset)
    {
        var values = new ushort[256];
        var accumulated = 0;
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (ushort)Math.Clamp(accumulated, ushort.MinValue, ushort.MaxValue);
            accumulated += step;
        }

        values[^1] = (ushort)Math.Clamp(values[^1] + Math.Clamp(terminalOffset, 0, 2), ushort.MinValue, ushort.MaxValue);
        return values;
    }

    private static int ScaleStep(int channel, int brightnessPercent) =>
        (int)Math.Floor(channel * (Math.Clamp(brightnessPercent, 1, 150) / 100.0) + 0.5);

    private static int ToChannel(double value) => (int)Math.Floor(Math.Clamp(value, 0, 255) + 0.5);

    private static Native.GammaRamp ToNative(ScreenEaseGammaRamp ramp) => new()
    {
        Red = ramp.Red,
        Green = ramp.Green,
        Blue = ramp.Blue
    };

    private sealed record NativeMonitor(string DeviceName, int Left, int Top, int Width, int Height, bool Primary);

    private static class Native
    {
        public const int PrimaryMonitorFlag = 1;
        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateDC(string? driver, string? device, string? output, IntPtr initData);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDeviceGammaRamp(IntPtr dc, ref GammaRamp ramp);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MonitorInfoEx
        {
            public int Size;
            public Rect Monitor;
            public Rect WorkArea;
            public int Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GammaRamp
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Red;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Green;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Blue;
        }
    }
}

internal sealed record ScreenEaseGammaRamp(ushort[] Red, ushort[] Green, ushort[] Blue);

internal readonly record struct ScreenEaseRgbChannels(int Red, int Green, int Blue);
