using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenEase.Core;

public interface IOverlayDriver
{
    Task<OverlayState> GetStateAsync(CancellationToken cancellationToken);

    Task<OverlayState> ApplyAsync(
        OverlaySettings settings,
        IReadOnlyList<MonitorInfo> monitors,
        CancellationToken cancellationToken);

    Task<OverlayState> HideAsync(CancellationToken cancellationToken);
}

public sealed class InMemoryOverlayDriver : IOverlayDriver
{
    private OverlayState state = new(false, 0, "#000000", 0);

    public Task<OverlayState> GetStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(state);

    public Task<OverlayState> ApplyAsync(
        OverlaySettings settings,
        IReadOnlyList<MonitorInfo> monitors,
        CancellationToken cancellationToken)
    {
        var normalized = Validation.Normalize(settings);
        state = normalized.Enabled
            ? new OverlayState(true, normalized.OpacityPercent, normalized.ColorHex, monitors.Count)
            : new OverlayState(false, 0, normalized.ColorHex, 0);
        return Task.FromResult(state);
    }

    public Task<OverlayState> HideAsync(CancellationToken cancellationToken)
    {
        state = state with { Enabled = false, OpacityPercent = 0, WindowCount = 0 };
        return Task.FromResult(state);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsLayeredOverlayDriver : IOverlayDriver, IDisposable
{
    private const string OverlayWindowClass = "ScreenEaseOverlayWindow";
    private static readonly Native.WindowProc OverlayWindowProcedure = HandleOverlayWindowMessage;
    private static readonly object windowClassLock = new();
    private static bool windowClassRegistered;

    private readonly BlockingCollection<WorkItem> queue = [];
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly List<IntPtr> windows = [];
    private readonly object stateLock = new();
    private OverlayState state = new(false, 0, "#000000", 0);
    private bool disposed;

    public WindowsLayeredOverlayDriver()
    {
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ScreenEase overlay"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    public Task<OverlayState> GetStateAsync(CancellationToken cancellationToken)
    {
        lock (stateLock)
        {
            return Task.FromResult(state);
        }
    }

    public Task<OverlayState> ApplyAsync(
        OverlaySettings settings,
        IReadOnlyList<MonitorInfo> monitors,
        CancellationToken cancellationToken)
    {
        var normalized = Validation.Normalize(settings);
        if (!OperatingSystem.IsWindows() || !normalized.Enabled || normalized.OpacityPercent <= 0)
        {
            return HideAsync(cancellationToken);
        }

        return Enqueue(
            () =>
            {
                EnsureWindowClass();
                DestroyWindows();
                var color = ParseColorRef(normalized.ColorHex);
                var alpha = (byte)Math.Clamp(normalized.OpacityPercent * 255 / 100, 0, 242);

                foreach (var monitor in monitors)
                {
                    var hwnd = Native.CreateWindowEx(
                        Native.WsExLayered
                        | Native.WsExTransparent
                        | Native.WsExToolWindow
                        | Native.WsExTopMost
                        | Native.WsExNoActivate,
                        OverlayWindowClass,
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

                    if (hwnd == IntPtr.Zero)
                    {
                        continue;
                    }

                    Native.SetWindowLongPtr(hwnd, Native.GwlpUserData, (IntPtr)color);
                    Native.SetLayeredWindowAttributes(hwnd, 0, alpha, Native.LwaAlpha);
                    Native.InvalidateRect(hwnd, IntPtr.Zero, true);
                    Native.SetWindowPos(
                        hwnd,
                        Native.HwndTopMost,
                        monitor.Left,
                        monitor.Top,
                        monitor.Width,
                        monitor.Height,
                        Native.SwpNoActivate | Native.SwpShowWindow);
                    windows.Add(hwnd);
                }

                SetState(new OverlayState(windows.Count > 0, normalized.OpacityPercent, normalized.ColorHex, windows.Count));
                return GetStateUnsafe();
            },
            cancellationToken);
    }

    public Task<OverlayState> HideAsync(CancellationToken cancellationToken) =>
        Enqueue(
            () =>
            {
                DestroyWindows();
                SetState(state with { Enabled = false, OpacityPercent = 0, WindowCount = 0 });
                return GetStateUnsafe();
            },
            cancellationToken);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        queue.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(2));
        queue.Dispose();
    }

    private Task<OverlayState> Enqueue(Func<OverlayState> action, CancellationToken cancellationToken)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsLayeredOverlayDriver));
        }

        var completion = new TaskCompletionSource<OverlayState>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        queue.Add(new WorkItem(action, completion), CancellationToken.None);
        return completion.Task;
    }

    private void Run()
    {
        ready.SetResult();

        while (!queue.IsCompleted)
        {
            if (queue.TryTake(out var item, TimeSpan.FromMilliseconds(25)))
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
    }

    private void DestroyWindows()
    {
        foreach (var hwnd in windows)
        {
            if (hwnd != IntPtr.Zero)
            {
                Native.DestroyWindow(hwnd);
            }
        }

        windows.Clear();
    }

    private void SetState(OverlayState value)
    {
        lock (stateLock)
        {
            state = value;
        }
    }

    private OverlayState GetStateUnsafe()
    {
        lock (stateLock)
        {
            return state;
        }
    }

    private static uint ParseColorRef(string colorHex)
    {
        var normalized = Validation.NormalizeColorHex(colorHex);
        var red = Convert.ToByte(normalized.Substring(1, 2), 16);
        var green = Convert.ToByte(normalized.Substring(3, 2), 16);
        var blue = Convert.ToByte(normalized.Substring(5, 2), 16);
        return (uint)(red | (green << 8) | (blue << 16));
    }

    private static void EnsureWindowClass()
    {
        lock (windowClassLock)
        {
            if (windowClassRegistered)
            {
                return;
            }

            var windowClass = new Native.WindowClassEx
            {
                Size = Marshal.SizeOf<Native.WindowClassEx>(),
                WindowProcedure = OverlayWindowProcedure,
                Instance = Native.GetModuleHandle(null),
                ClassName = OverlayWindowClass
            };
            var atom = Native.RegisterClassEx(ref windowClass);
            if (atom == 0)
            {
                var error = Marshal.GetLastWin32Error();
                const int classAlreadyExists = 1410;
                if (error != classAlreadyExists)
                {
                    throw new InvalidOperationException($"RegisterClassEx failed for overlay window class. Win32 error: {error}.");
                }
            }

            windowClassRegistered = true;
        }
    }

    private static IntPtr HandleOverlayWindowMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        switch (message)
        {
            case Native.WmEraseBackground:
                PaintOverlay(window, wParam);
                return new IntPtr(1);
            case Native.WmPaint:
                var paint = new Native.PaintStruct { Reserved = new byte[32] };
                var deviceContext = Native.BeginPaint(window, ref paint);
                try
                {
                    PaintOverlay(window, deviceContext);
                }
                finally
                {
                    Native.EndPaint(window, ref paint);
                }

                return IntPtr.Zero;
            default:
                return Native.DefWindowProc(window, message, wParam, lParam);
        }
    }

    private static void PaintOverlay(IntPtr window, IntPtr deviceContext)
    {
        if (deviceContext == IntPtr.Zero)
        {
            return;
        }

        Native.GetClientRect(window, out var rect);
        var color = (uint)Native.GetWindowLongPtr(window, Native.GwlpUserData).ToInt64();
        var brush = Native.CreateSolidBrush(color);
        if (brush == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Native.FillRect(deviceContext, ref rect, brush);
        }
        finally
        {
            Native.DeleteObject(brush);
        }
    }

    private sealed record WorkItem(
        Func<OverlayState> Action,
        TaskCompletionSource<OverlayState> Completion);

    private static partial class Native
    {
        public const uint WsExLayered = 0x00080000;
        public const uint WsExTransparent = 0x00000020;
        public const uint WsExToolWindow = 0x00000080;
        public const uint WsExTopMost = 0x00000008;
        public const uint WsExNoActivate = 0x08000000;
        public const uint WsPopup = 0x80000000;
        public const uint WsVisible = 0x10000000;
        public const uint LwaAlpha = 0x00000002;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpShowWindow = 0x0040;
        public const uint PmRemove = 0x0001;
        public const int GwlpUserData = -21;
        public const uint WmPaint = 0x000F;
        public const uint WmEraseBackground = 0x0014;
        public static readonly IntPtr HwndTopMost = new(-1);

        public delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            uint exStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetLayeredWindowAttributes(
            IntPtr window,
            uint colorKey,
            byte alpha,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PeekMessage(out Message message, IntPtr window, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref Message message);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref Message message);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InvalidateRect(IntPtr window, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr BeginPaint(IntPtr window, ref PaintStruct paint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EndPaint(IntPtr window, ref PaintStruct paint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int FillRect(IntPtr deviceContext, ref Rect rect, IntPtr brush);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateSolidBrush(uint color);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr gdiObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WindowClassEx
        {
            public int Size;
            public uint Style;
            public WindowProc WindowProcedure;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string ClassName;
            public IntPtr IconSmall;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Message
        {
            public IntPtr Window;
            public uint MessageId;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int PointX;
            public int PointY;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PaintStruct
        {
            public IntPtr DeviceContext;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Erase;
            public Rect Paint;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Restore;
            [MarshalAs(UnmanagedType.Bool)]
            public bool IncrementalUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] Reserved;
        }
    }
}


