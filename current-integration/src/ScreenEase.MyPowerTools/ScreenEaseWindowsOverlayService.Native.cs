using System.Runtime.InteropServices;

namespace ScreenEase.MyPowerTools;

internal sealed partial class ScreenEaseWindowsOverlayService
{
    private static IntPtr HandleWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
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
        if (deviceContext == IntPtr.Zero || !Native.GetClientRect(window, out var rect))
        {
            return;
        }

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

    private static class Native
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
        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterClass(string className, IntPtr instance);

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
        public static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

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
            [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
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
        public struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect WorkArea;
            public int Flags;
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
            [MarshalAs(UnmanagedType.Bool)] public bool Erase;
            public Rect Paint;
            [MarshalAs(UnmanagedType.Bool)] public bool Restore;
            [MarshalAs(UnmanagedType.Bool)] public bool IncrementalUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
        }
    }
}
