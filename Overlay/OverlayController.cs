#if !LINUX
using System;
using System.Threading;
using System.Windows.Threading;

namespace CleanFeed.Overlay
{
    // WPF is retained only for static recorder-diagnostic cards. Dynamic HUD pixels never enter
    // this controller; the GPU HUD uses HudCompositionWindow and DirectComposition instead.
    internal sealed class OverlayController : IDisposable
    {
        private readonly object _gate = new object();
        private Thread _thread;
        private volatile OverlayWindow _window;
        private volatile bool _requestedVisible;
        private volatile bool _disposed;
        private volatile int _requestedRoute = (int)CaptureRoute.Nvidia;

        public CaptureRoute CurrentRoute => (CaptureRoute)_requestedRoute;

        public bool Toggle()
        {
            if (_requestedVisible) Hide(); else Show(CurrentRoute);
            return _requestedVisible;
        }

        public void Show(CaptureRoute route)
        {
            if (_disposed) return;
            _requestedRoute = (int)route;
            _requestedVisible = true;
            EnsureStarted();
            Dispatch(window =>
            {
                window.SetRoute(route);
                window.SetRequestedVisible(true);
            });
        }

        public void ShowTest(CaptureRoute route) => Show(route);

        public void Hide()
        {
            _requestedVisible = false;
            StopWindow(1500, "overlay did not close within 1500 ms");
        }

        public string StatusLine()
        {
            CaptureRoute route = CurrentRoute;
            OverlayWindow window = _window;
            if (window == null)
                return "test-route=" + RouteName(route) + ", test-overlay="
                       + (_requestedVisible ? "starting" : "off")
                       + ", affinity=none";

            string affinity = window.AffinityRequested
                ? (window.AffinitySetSucceeded ? "requested" : "FAILED(" + window.AffinityError + ")")
                : "off";
            return "test-route=" + RouteName(window.Route)
                   + ", test-overlay=" + (_requestedVisible ? "on" : "stopping")
                   + ", affinity=" + affinity
                   + ", readback=" + (window.AffinityReadbackSucceeded
                       ? "0x" + window.AffinityReadback.ToString("X") : "FAILED")
                   + ", game-window=" + (window.GameHwnd == IntPtr.Zero ? "not-found" : "found");
        }

        public static string RouteName(CaptureRoute route)
            => route.ToString().ToLowerInvariant();

        private void EnsureStarted()
        {
            lock (_gate)
            {
                if (_thread != null && _thread.IsAlive) return;
                Thread thread = null;
                thread = new Thread(() =>
                {
                    try
                    {
                        var window = new OverlayWindow((CaptureRoute)_requestedRoute);
                        _window = window;
                        window.Show();
                        if (!_requestedVisible)
                        {
                            window.Close();
                            return;
                        }
                        window.SetRequestedVisible(true);
                        Dispatcher.Run();
                    }
                    catch (Exception ex) { Plugin.Log("test overlay thread failed: " + ex); }
                    finally
                    {
                        _window = null;
                        lock (_gate)
                        {
                            if (ReferenceEquals(_thread, thread)) _thread = null;
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "CleanFeed test overlay"
                };
                _thread = thread;
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
        }

        private void Dispatch(Action<OverlayWindow> action)
        {
            OverlayWindow window = _window;
            if (window == null) return;
            try { window.Dispatcher.BeginInvoke(action, window); }
            catch (Exception ex) { Plugin.Log("test overlay dispatch failed: " + ex.Message); }
        }

        private void StopWindow(int timeoutMs, string timeoutMessage)
        {
            OverlayWindow window = _window;
            Thread thread = _thread;
            if (window != null)
            {
                try { window.Dispatcher.BeginInvoke(new Action(window.Close)); }
                catch { }
            }

            if (thread != null && thread.IsAlive && !ReferenceEquals(thread, Thread.CurrentThread)
                && !thread.Join(timeoutMs))
                Plugin.Log(timeoutMessage);
        }

        public void Dispose()
        {
            _disposed = true;
            _requestedVisible = false;
            StopWindow(1500, "test overlay thread did not exit within 1500 ms");
            _thread = null;
        }
    }
}
#endif
