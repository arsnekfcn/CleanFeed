using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CleanFeed.Overlay
{
    internal static class NativeMethods
    {
        public const uint WdaNone = 0x00000000;
        public const uint WdaExcludeFromCapture = 0x00000011;
        public const int GwlExStyle = -20;
        public const int WsExTransparent = 0x00000020;
        public const int WsExToolWindow = 0x00000080;
        public const int WsExNoActivate = 0x08000000;
        public const int WsExLayered = 0x00080000;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpShowWindow = 0x0040;
        public const int CursorShowing = 0x00000001;
        public static readonly IntPtr HwndTopmost = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct GuiThreadInfo
        {
            public int CbSize;
            public int Flags;
            public IntPtr HwndActive;
            public IntPtr HwndFocus;
            public IntPtr HwndCapture;
            public IntPtr HwndMenuOwner;
            public IntPtr HwndMoveSize;
            public IntPtr HwndCaret;
            public Rect CaretRect;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CursorInfo
        {
            public int CbSize;
            public int Flags;
            public IntPtr Cursor;
            public Point ScreenPosition;
        }

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y,
            int width, int height, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorInfo(ref CursorInfo info);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClipCursor(out Rect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        // Process.GetCurrentProcess() allocates a Process that owns a native handle, and this is read
        // once per enumerated top-level window. The id never changes, so it is cached.
        private static readonly uint CurrentProcessId = ReadCurrentProcessId();

        private static uint ReadCurrentProcessId()
        {
            using (Process process = Process.GetCurrentProcess()) return (uint)process.Id;
        }

        internal static bool BelongsToCurrentProcess(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == CurrentProcessId;
        }

        internal static IntPtr FindGameWindow(IntPtr overlayHwnd)
        {
            // MainWindowHandle is cached on the Process instance and can go stale, so it must be
            // re-read; the instance owns a native handle and must be disposed.
            IntPtr main;
            using (Process process = Process.GetCurrentProcess()) main = process.MainWindowHandle;
            if (main != IntPtr.Zero && main != overlayHwnd && IsWindow(main)) return main;

            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindowsProc callback = (hwnd, _) =>
            {
                if (hwnd == overlayHwnd || !IsWindowVisible(hwnd) || !BelongsToCurrentProcess(hwnd)) return true;
                if (!GetClientRect(hwnd, out Rect rect)) return true;
                long area = (long)rect.Width * rect.Height;
                if (area > bestArea) { best = hwnd; bestArea = area; }
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return best;
        }

        internal static string DescribeGuiThread(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return "gui=unavailable";

            uint threadId = GetWindowThreadProcessId(hwnd, out _);
            var info = new GuiThreadInfo { CbSize = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if (threadId == 0 || !GetGUIThreadInfo(threadId, ref info)) return "gui=read-failed";

            return "gui-flags=0x" + info.Flags.ToString("X")
                   + ", active=" + Relation(info.HwndActive, hwnd)
                   + ", focus=" + Relation(info.HwndFocus, hwnd)
                   + ", capture=" + Relation(info.HwndCapture, hwnd)
                   + ", menu=" + Relation(info.HwndMenuOwner, hwnd);
        }

        internal static string DescribeCursorAndClip()
        {
            var cursor = new CursorInfo { CbSize = Marshal.SizeOf(typeof(CursorInfo)) };
            string cursorState = GetCursorInfo(ref cursor)
                ? ((cursor.Flags & CursorShowing) != 0 ? "shown" : "hidden")
                  + "@" + cursor.ScreenPosition.X + "," + cursor.ScreenPosition.Y
                : "read-failed";
            string clip = GetClipCursor(out Rect rect)
                ? rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom
                : "read-failed";
            return "cursor=" + cursorState + ", clip=" + clip;
        }

        private static string Relation(IntPtr value, IntPtr gameHwnd)
        {
            if (value == IntPtr.Zero) return "none";
            return value == gameHwnd ? "game" : "other";
        }
    }
}
