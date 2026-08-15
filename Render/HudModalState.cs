using System.Threading;

namespace CleanFeed.Render
{
    // Tracks whether a modal, non-gameplay screen drew during the current GUI frame. The retained
    // Flight HUD visual is detached while one is up so its centre roll indicator cannot cover
    // K-menu or pause content.
    internal static class HudModalState
    {
        private static int _observedThisFrame;
        private static int _active;
        private static long _guiFrames;
        private static long _transitions;

        internal static bool Active => Volatile.Read(ref _active) != 0;

        internal static void ObserveInteractiveScreen()
            => Interlocked.Exchange(ref _observedThisFrame, 1);

        internal static void CompleteGuiFrame()
        {
            bool active = Interlocked.Exchange(ref _observedThisFrame, 0) != 0;
            int next = active ? 1 : 0;
            if (Interlocked.Exchange(ref _active, next) != next)
                Interlocked.Increment(ref _transitions);
            Interlocked.Increment(ref _guiFrames);
        }

        internal static string StatusLine()
            => "modal-ui=" + (Active ? "active" : "inactive")
               + ", gui-frames=" + Interlocked.Read(ref _guiFrames)
               + ", transitions=" + Interlocked.Read(ref _transitions);

        internal static void Reset()
        {
            Interlocked.Exchange(ref _observedThisFrame, 0);
            Interlocked.Exchange(ref _active, 0);
        }
    }
}
