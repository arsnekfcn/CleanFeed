using System;
using System.Threading;

namespace CleanFeed.Overlay
{
    internal enum HudTargetUnreadyReason
    {
        None,
        NotRequested,
        GameWindowMissing,
        GameWindowHidden,
        GameWindowMinimized,
        ForegroundMismatch,
        ClientRectUnavailable,
        InvalidClientSize
    }

    // Tracks the Space Engineers HWND used directly as the DirectComposition target. There is no
    // CleanFeed-owned top-level HUD window in this path, so CleanFeed cannot participate in hit
    // testing, activation, focus, mouse capture, clipping, or cursor ownership.
    internal sealed class HudCompositionWindow : IDisposable
    {
        private long _gameHwndBits;
        private int _requested;
        private int _disposed;
        private int _ready;
        private int _width;
        private int _height;
        private long _targetChanges;
        private long _probes;
        private long _rejectedProbes;
        private long _nullForegroundProbes;
        private long _sameProcessForegroundProbes;
        private int _lastReason;
        private string _lastError;

        public IntPtr GameHwnd => new IntPtr(Interlocked.Read(ref _gameHwndBits));
        public bool IsReady => Volatile.Read(ref _ready) != 0 && GameHwnd != IntPtr.Zero;
        public bool Failed => Volatile.Read(ref _lastError) != null;
        public string LastError => Volatile.Read(ref _lastError) ?? "none";
        public int Width => Volatile.Read(ref _width);
        public int Height => Volatile.Read(ref _height);
        public HudTargetUnreadyReason LastReason
            => (HudTargetUnreadyReason)Volatile.Read(ref _lastReason);

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(HudCompositionWindow));

            Interlocked.Exchange(ref _requested, 1);
            RefreshGameWindow();
        }

        public void RequestHide()
        {
            Interlocked.Exchange(ref _requested, 0);
            MarkRejected(HudTargetUnreadyReason.NotRequested);
        }

        // Render-thread readiness probe. GetForegroundWindow is intentionally checked at frame
        // cadence so focus loss fails open without waiting for a timer owned by another thread.
        public bool TryGetReadyTarget(
            out IntPtr gameHwnd,
            out int clientWidth,
            out int clientHeight,
            out HudTargetUnreadyReason reason)
        {
            Interlocked.Increment(ref _probes);
            gameHwnd = GameHwnd;
            clientWidth = 0;
            clientHeight = 0;

            if (Volatile.Read(ref _requested) == 0)
                return Reject(HudTargetUnreadyReason.NotRequested, out reason);

            if (gameHwnd == IntPtr.Zero || !NativeMethods.IsWindow(gameHwnd))
            {
                gameHwnd = RefreshGameWindow();
                if (gameHwnd == IntPtr.Zero)
                    return Reject(HudTargetUnreadyReason.GameWindowMissing, out reason);
            }

            if (!NativeMethods.IsWindowVisible(gameHwnd))
                return Reject(HudTargetUnreadyReason.GameWindowHidden, out reason);
            if (NativeMethods.IsIconic(gameHwnd))
                return Reject(HudTargetUnreadyReason.GameWindowMinimized, out reason);
            // Foreground is only a mismatch when a window of ANOTHER process holds it. Exact-hwnd
            // equality flickered the whole redirect: GetForegroundWindow transiently returns null
            // during every activation change (documented behaviour, observed at ~11% of probes in
            // a live session - target-rejects 6767/62356 while the operator was provably playing),
            // and a same-process handle (the game's own splash, a tool window, CleanFeed's own
            // overlay) never means the player left. Each false mismatch engaged privacy
            // suppression for at least a frame, which the operator saw as a constant HUD flicker.
            // A real focus loss produces a foreign non-null handle within a frame or two, and the
            // debounce owns that transition; nothing sensitive rides on the null moment, because
            // with the redirect active the vanilla HUD is not on the backbuffer for a recorder to
            // catch regardless.
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground != gameHwnd)
            {
                if (foreground == IntPtr.Zero)
                    Interlocked.Increment(ref _nullForegroundProbes);
                else if (NativeMethods.BelongsToCurrentProcess(foreground))
                    Interlocked.Increment(ref _sameProcessForegroundProbes);
                else
                    return Reject(HudTargetUnreadyReason.ForegroundMismatch, out reason);
            }
            if (!NativeMethods.GetClientRect(gameHwnd, out NativeMethods.Rect rect))
                return Reject(HudTargetUnreadyReason.ClientRectUnavailable, out reason);
            if (rect.Width <= 0 || rect.Height <= 0)
                return Reject(HudTargetUnreadyReason.InvalidClientSize, out reason);

            clientWidth = rect.Width;
            clientHeight = rect.Height;
            Volatile.Write(ref _width, clientWidth);
            Volatile.Write(ref _height, clientHeight);
            Volatile.Write(ref _lastReason, (int)HudTargetUnreadyReason.None);
            Interlocked.Exchange(ref _ready, 1);
            reason = HudTargetUnreadyReason.None;
            return true;
        }

        public string StatusLine()
        {
            IntPtr hwnd = GameHwnd;
            IntPtr fg = NativeMethods.GetForegroundWindow();
            string foreground = hwnd != IntPtr.Zero && fg == hwnd ? "exact"
                : fg == IntPtr.Zero ? "null"
                : NativeMethods.BelongsToCurrentProcess(fg) ? "same-process"
                : "other";
            string gui = NativeMethods.DescribeGuiThread(hwnd);
            string cursor = NativeMethods.DescribeCursorAndClip();
            return "target=game-hwnd"
                   + ", hwnd=" + (hwnd == IntPtr.Zero ? "none" : "ready")
                   + ", visible-ready=" + IsReady
                   + ", client=" + Width + "x" + Height
                   + ", foreground=" + foreground
                   + ", input-surface=none"
                   + ", reason=" + Describe(LastReason)
                   + ", target-changes=" + Interlocked.Read(ref _targetChanges)
                   + ", probes=" + Interlocked.Read(ref _probes)
                   + ", rejected=" + Interlocked.Read(ref _rejectedProbes)
                   + ", " + gui
                   + ", " + cursor
                   + (Failed ? ", target-fault=" + LastError : string.Empty);
        }

        public string HealthStatusLine()
            => "target-ready=" + IsReady
               + ", target-reason=" + Describe(LastReason)
               + ", target-size=" + Width + "x" + Height
               + ", target-changes=" + Interlocked.Read(ref _targetChanges)
               + ", target-probes=" + Interlocked.Read(ref _probes)
               + ", target-rejects=" + Interlocked.Read(ref _rejectedProbes)
               + ", fg-null=" + Interlocked.Read(ref _nullForegroundProbes)
               + ", fg-same-process=" + Interlocked.Read(ref _sameProcessForegroundProbes)
               + ", target-fault=" + LastError;

        public static string Describe(HudTargetUnreadyReason reason)
        {
            switch (reason)
            {
                case HudTargetUnreadyReason.None: return "ready";
                case HudTargetUnreadyReason.NotRequested: return "not-requested";
                case HudTargetUnreadyReason.GameWindowMissing: return "game-window-missing";
                case HudTargetUnreadyReason.GameWindowHidden: return "game-window-hidden";
                case HudTargetUnreadyReason.GameWindowMinimized: return "game-window-minimized";
                case HudTargetUnreadyReason.ForegroundMismatch: return "foreground-mismatch";
                case HudTargetUnreadyReason.ClientRectUnavailable: return "client-rect-unavailable";
                case HudTargetUnreadyReason.InvalidClientSize: return "invalid-client-size";
                default: return "unknown";
            }
        }

        private IntPtr RefreshGameWindow()
        {
            try
            {
                IntPtr previous = GameHwnd;
                IntPtr current = NativeMethods.FindGameWindow(IntPtr.Zero);
                if (current != previous)
                {
                    Interlocked.Exchange(ref _gameHwndBits, current.ToInt64());
                    Interlocked.Increment(ref _targetChanges);
                }
                return current;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _lastError, ex.GetType().Name + ": " + ex.Message);
                Interlocked.Exchange(ref _ready, 0);
                return IntPtr.Zero;
            }
        }

        private bool Reject(HudTargetUnreadyReason rejected, out HudTargetUnreadyReason reason)
        {
            MarkRejected(rejected);
            reason = rejected;
            return false;
        }

        private void MarkRejected(HudTargetUnreadyReason reason)
        {
            Interlocked.Exchange(ref _ready, 0);
            Volatile.Write(ref _lastReason, (int)reason);
            Interlocked.Increment(ref _rejectedProbes);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            RequestHide();
            Interlocked.Exchange(ref _gameHwndBits, 0);
        }
    }
}
