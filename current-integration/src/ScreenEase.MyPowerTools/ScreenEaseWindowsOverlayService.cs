using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenEase.MyPowerTools;

[SupportedOSPlatform("windows")]
internal sealed partial class ScreenEaseWindowsOverlayService : IScreenEaseOverlayService
{
    private readonly string _overlayWindowClass = $"MyPowerToolsScreenEaseOverlay_{Guid.NewGuid():N}";
    private readonly Native.WindowProc _windowProcedure = HandleWindowMessage;
    private readonly object _lifecycleGate = new();
    private readonly object _stateGate = new();
    private BlockingCollection<WorkItem>? _queue;
    private TaskCompletionSource? _ready;
    private Thread? _thread;
    private readonly List<IntPtr> _windows = [];
    private ScreenEaseOverlayState _state = ScreenEaseOverlayState.Hidden();
    private bool _disposed;
    private bool _windowClassRegistered;
    private IntPtr _moduleInstance;

    public Task<ScreenEaseOverlayState> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            return Task.FromResult(_state);
        }
    }

    public Task<ScreenEaseOverlayState> ApplyAsync(ScreenEaseOverlaySettings settings, CancellationToken cancellationToken)
    {
        var normalized = ScreenEaseOverlaySettings.Normalize(settings);
        if (!normalized.Enabled || normalized.OpacityPercent <= 0)
        {
            return HideAsync(cancellationToken);
        }

        EnsureThread();
        return EnqueueAsync(
            () =>
            {
                EnsureWindowClass();
                DestroyWindows();
                var color = ParseColorRef(normalized.ColorHex);
                var alpha = (byte)Math.Clamp(normalized.OpacityPercent * 255 / 100, 0, 242);
                foreach (var monitor in EnumerateMonitors())
                {
                    var window = Native.CreateWindowEx(
                        Native.WsExLayered | Native.WsExTransparent | Native.WsExToolWindow | Native.WsExTopMost | Native.WsExNoActivate,
                        _overlayWindowClass,
                        string.Empty,
                        Native.WsPopup | Native.WsVisible,
                        monitor.Left,
                        monitor.Top,
                        monitor.Width,
                        monitor.Height,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero);
                    if (window == IntPtr.Zero)
                    {
                        continue;
                    }

                    Native.SetWindowLongPtr(window, Native.GwlpUserData, (IntPtr)color);
                    Native.SetLayeredWindowAttributes(window, 0, alpha, Native.LwaAlpha);
                    Native.InvalidateRect(window, IntPtr.Zero, true);
                    Native.SetWindowPos(
                        window,
                        Native.HwndTopMost,
                        monitor.Left,
                        monitor.Top,
                        monitor.Width,
                        monitor.Height,
                        Native.SwpNoActivate | Native.SwpShowWindow);
                    _windows.Add(window);
                }

                SetState(new ScreenEaseOverlayState(
                    _windows.Count > 0,
                    normalized.OpacityPercent,
                    normalized.ColorHex,
                    _windows.Count,
                    _windows.Count > 0 ? "applied" : "unavailable",
                    _windows.Count > 0
                        ? $"ScreenEase overlay is active on {_windows.Count} monitor(s)."
                        : "Windows reported no monitor suitable for an overlay window."));
                return ReadState();
            },
            cancellationToken);
    }

    public Task<ScreenEaseOverlayState> HideAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (_thread is null)
            {
                SetState(ScreenEaseOverlayState.Hidden(_state.ColorHex));
                return GetStateAsync(cancellationToken);
            }
        }

        return EnqueueAsync(
            () =>
            {
                DestroyWindows();
                SetState(ScreenEaseOverlayState.Hidden(_state.ColorHex));
                return ReadState();
            },
            cancellationToken);
    }

    public void Dispose()
    {
        BlockingCollection<WorkItem>? queue;
        Thread? thread;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            queue = _queue;
            thread = _thread;
            queue?.CompleteAdding();
        }

        thread?.Join(TimeSpan.FromSeconds(2));
        queue?.Dispose();
    }

    private void EnsureThread()
    {
        Task readyTask;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is null)
            {
                _queue = [];
                _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "MyPowerTools ScreenEase overlay"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }

            readyTask = _ready!.Task;
        }

        readyTask.GetAwaiter().GetResult();
    }

    private async Task<ScreenEaseOverlayState> EnqueueAsync(
        Func<ScreenEaseOverlayState> action,
        CancellationToken cancellationToken)
    {
        EnsureThread();
        var completion = new TaskCompletionSource<ScreenEaseOverlayState>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        _queue!.Add(new WorkItem(action, completion), CancellationToken.None);
        return await completion.Task.ConfigureAwait(false);
    }

    private void Run()
    {
        _ready!.TrySetResult();
        while (!_queue!.IsCompleted)
        {
            if (_queue.TryTake(out var item, TimeSpan.FromMilliseconds(25)))
            {
                try
                {
                    item.Completion.TrySetResult(item.Action());
                }
                catch (Exception exception)
                {
                    item.Completion.TrySetException(exception);
                }
            }

            while (Native.PeekMessage(out var message, IntPtr.Zero, 0, 0, Native.PmRemove))
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
        }

        DestroyWindows();
        UnregisterWindowClass();
    }

    private void DestroyWindows()
    {
        foreach (var window in _windows.Where(window => window != IntPtr.Zero))
        {
            Native.DestroyWindow(window);
        }
        _windows.Clear();
    }

    private void SetState(ScreenEaseOverlayState state)
    {
        lock (_stateGate)
        {
            _state = state;
        }
    }

    private ScreenEaseOverlayState ReadState()
    {
        lock (_stateGate)
        {
            return _state;
        }
    }

    private static IReadOnlyList<OverlayMonitor> EnumerateMonitors()
    {
        var monitors = new List<OverlayMonitor>();
        Native.MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc, ref Native.Rect rect, IntPtr data) =>
        {
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            if (Native.GetMonitorInfo(monitor, ref info))
            {
                monitors.Add(new OverlayMonitor(
                    info.Monitor.Left,
                    info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left,
                    info.Monitor.Bottom - info.Monitor.Top));
            }
            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return monitors;
    }

    private static uint ParseColorRef(string colorHex)
    {
        var normalized = ScreenEaseOverlaySettings.NormalizeColorHex(colorHex);
        var red = Convert.ToByte(normalized.Substring(1, 2), 16);
        var green = Convert.ToByte(normalized.Substring(3, 2), 16);
        var blue = Convert.ToByte(normalized.Substring(5, 2), 16);
        return (uint)(red | (green << 8) | (blue << 16));
    }

    private void EnsureWindowClass()
    {
        if (_windowClassRegistered)
        {
            return;
        }

        _moduleInstance = Native.GetModuleHandle(null);
        var windowClass = new Native.WindowClassEx
        {
            Size = Marshal.SizeOf<Native.WindowClassEx>(),
            WindowProcedure = _windowProcedure,
            Instance = _moduleInstance,
            ClassName = _overlayWindowClass
        };
        var atom = Native.RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed for the ScreenEase overlay. Win32 error {Marshal.GetLastWin32Error()}.");
        }
        _windowClassRegistered = true;
    }

    private void UnregisterWindowClass()
    {
        if (!_windowClassRegistered)
        {
            return;
        }

        Native.UnregisterClass(_overlayWindowClass, _moduleInstance);
        _windowClassRegistered = false;
        _moduleInstance = IntPtr.Zero;
    }

    private sealed record WorkItem(Func<ScreenEaseOverlayState> Action, TaskCompletionSource<ScreenEaseOverlayState> Completion);
    private sealed record OverlayMonitor(int Left, int Top, int Width, int Height);
}
