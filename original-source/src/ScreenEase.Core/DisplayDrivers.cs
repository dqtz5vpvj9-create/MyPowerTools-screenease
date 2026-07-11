using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenEase.Core;

public interface IDisplayDriver
{
    Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken cancellationToken);

    Task ApplyAsync(DisplayEffectRequest request, CancellationToken cancellationToken);

    Task ResetAsync(CancellationToken cancellationToken);
}

public sealed class InMemoryDisplayDriver : IDisplayDriver
{
    public DisplayEffectRequest? LastRequest { get; private set; }

    public Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MonitorInfo>>(
        [
            new MonitorInfo("memory", "In-memory display", 0, 0, 1920, 1080, true)
        ]);

    public Task ApplyAsync(DisplayEffectRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        LastRequest = new DisplayEffectRequest(false, "system", 6500, 100, false, TimeSpan.Zero);
        return Task.CompletedTask;
    }
}

public sealed class WindowsGammaDisplayDriver : IDisplayDriver
{
    private int terminalOffset;

    public Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<MonitorInfo>>([]);
        }

        var monitors = new List<MonitorInfo>();
        var index = 0;
        Native.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr monitor, IntPtr _, ref Native.Rect rect, IntPtr _) =>
            {
                var info = new Native.MonitorInfoEx();
                info.Size = Marshal.SizeOf<Native.MonitorInfoEx>();
                info.DeviceName = string.Empty;
                if (Native.GetMonitorInfo(monitor, ref info))
                {
                    var width = info.Monitor.Right - info.Monitor.Left;
                    var height = info.Monitor.Bottom - info.Monitor.Top;
                    monitors.Add(new MonitorInfo(
                        Id: string.IsNullOrWhiteSpace(info.DeviceName) ? $"monitor-{index}" : info.DeviceName,
                        DeviceName: info.DeviceName,
                        Left: info.Monitor.Left,
                        Top: info.Monitor.Top,
                        Width: width,
                        Height: height,
                        IsPrimary: (info.Flags & Native.MonitorInfoFlagsPrimary) != 0));
                }

                index++;
                return true;
            },
            IntPtr.Zero);

        return Task.FromResult<IReadOnlyList<MonitorInfo>>(monitors);
    }

    public Task ApplyAsync(DisplayEffectRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        var ramp = request.Enabled
            ? ToNative(ColorTemperature.BuildGammaRamp(
                request.ColorTemperatureKelvin,
                request.BrightnessPercent,
                NextTerminalOffset()))
            : ToNative(ColorTemperature.BuildIdentityRamp());

        var deviceNames = GetMonitorDeviceNames();
        var failures = new List<string>();
        var successCount = 0;
        foreach (var deviceName in deviceNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TrySetDeviceGammaRamp(deviceName, ref ramp, failures))
            {
                successCount++;
            }
        }

        if (deviceNames.Count > 0)
        {
            if (successCount == deviceNames.Count)
            {
                return Task.CompletedTask;
            }

            var failedCount = deviceNames.Count - successCount;
            var failureDetail = string.Join(" ", failures);
            throw new InvalidOperationException(
                $"SetDeviceGammaRamp applied to {successCount}/{deviceNames.Count} display devices; {failedCount} failed. {failureDetail}");
        }

        if (TrySetDesktopGammaRamp(ref ramp, failures))
        {
            return Task.CompletedTask;
        }

        var detail = failures.Count == 0
            ? "No display device context accepted a gamma ramp."
            : string.Join(" ", failures);
        throw new InvalidOperationException($"SetDeviceGammaRamp failed. {detail}");
    }

    public Task ResetAsync(CancellationToken cancellationToken) =>
        ApplyAsync(new DisplayEffectRequest(false, "system", 6500, 100, false, TimeSpan.Zero), cancellationToken);

    private int NextTerminalOffset() =>
        Interlocked.Increment(ref terminalOffset) % 3;

    private static bool TrySetDeviceGammaRamp(string deviceName, ref Native.GammaRamp ramp, List<string> failures)
    {
        var dc = Native.CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            failures.Add($"{deviceName}: CreateDC(DISPLAY) failed with Win32 error {Marshal.GetLastWin32Error()}.");
            dc = Native.CreateDC(deviceName, null, null, IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                failures.Add($"{deviceName}: CreateDC(device) failed with Win32 error {Marshal.GetLastWin32Error()}.");
                return false;
            }
        }

        try
        {
            if (Native.SetDeviceGammaRamp(dc, ref ramp))
            {
                return true;
            }

            failures.Add($"{deviceName}: SetDeviceGammaRamp failed with Win32 error {Marshal.GetLastWin32Error()}.");
            return false;
        }
        finally
        {
            Native.DeleteDC(dc);
        }
    }

    private static bool TrySetDesktopGammaRamp(ref Native.GammaRamp ramp, List<string> failures)
    {
        var screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            failures.Add($"desktop: GetDC failed with Win32 error {Marshal.GetLastWin32Error()}.");
            return false;
        }

        try
        {
            if (Native.SetDeviceGammaRamp(screenDc, ref ramp))
            {
                return true;
            }

            failures.Add($"desktop: SetDeviceGammaRamp failed with Win32 error {Marshal.GetLastWin32Error()}.");
            return false;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static IReadOnlyList<string> GetMonitorDeviceNames()
    {
        var names = new List<string>();
        Native.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr monitor, IntPtr _, ref Native.Rect _, IntPtr _) =>
            {
                var info = new Native.MonitorInfoEx();
                info.Size = Marshal.SizeOf<Native.MonitorInfoEx>();
                info.DeviceName = string.Empty;
                if (Native.GetMonitorInfo(monitor, ref info) && !string.IsNullOrWhiteSpace(info.DeviceName))
                {
                    names.Add(info.DeviceName);
                }

                return true;
            },
            IntPtr.Zero);

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Native.GammaRamp ToNative(GammaRamp ramp) =>
        new()
        {
            Red = ramp.Red,
            Green = ramp.Green,
            Blue = ramp.Blue
        };

    private static partial class Native
    {
        public const int MonitorInfoFlagsPrimary = 1;

        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(
            IntPtr hdc,
            IntPtr clip,
            MonitorEnumProc callback,
            IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

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

[SupportedOSPlatform("windows")]
public sealed class WindowsHybridDisplayDriver : IDisplayDriver, IDisposable
{
    private readonly WindowsGammaDisplayDriver gamma = new();
    private readonly WindowsLayeredOverlayDriver fallbackOverlay = new();
    private bool fallbackActive;

    public Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken cancellationToken) =>
        gamma.GetMonitorsAsync(cancellationToken);

    public async Task ApplyAsync(DisplayEffectRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await gamma.ApplyAsync(request, cancellationToken);
            if (fallbackActive)
            {
                await fallbackOverlay.HideAsync(cancellationToken);
                fallbackActive = false;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ApplyOverlayFallbackAsync(request, cancellationToken);
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        try
        {
            await gamma.ResetAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }

        await fallbackOverlay.HideAsync(cancellationToken);
        fallbackActive = false;
    }

    public void Dispose() => fallbackOverlay.Dispose();

    private async Task ApplyOverlayFallbackAsync(
        DisplayEffectRequest request,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !request.Enabled)
        {
            await fallbackOverlay.HideAsync(cancellationToken);
            fallbackActive = false;
            return;
        }

        var warm = Math.Clamp((6500d - request.ColorTemperatureKelvin) / 4000d, 0d, 1d);
        var dim = Math.Clamp((100d - request.BrightnessPercent) / 100d, 0d, 1d);
        var opacity = (int)Math.Clamp(Math.Round(8d + warm * 24d + dim * 26d), 0d, 42d);
        if (opacity <= 0)
        {
            await fallbackOverlay.HideAsync(cancellationToken);
            fallbackActive = false;
            return;
        }

        var color = ToWarmOverlayColor(warm, dim);
        var monitors = await GetMonitorsAsync(cancellationToken);
        await fallbackOverlay.ApplyAsync(
            new OverlaySettings(true, opacity, color),
            monitors,
            cancellationToken);
        fallbackActive = true;
    }

    private static string ToWarmOverlayColor(double warm, double dim)
    {
        var green = (int)Math.Clamp(Math.Round(245d - warm * 92d - dim * 36d), 120d, 245d);
        var blue = (int)Math.Clamp(Math.Round(235d - warm * 190d - dim * 48d), 48d, 235d);
        return $"#FF{green:X2}{blue:X2}";
    }
}


