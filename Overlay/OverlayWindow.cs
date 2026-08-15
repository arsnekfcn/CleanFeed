// The static capture diagnostic is WPF and therefore Windows-only. The fence symbol is LINUX -
// the one Pulsar's compiler actually defines on native Linux (post-2.3.1; v2.3.1 defines only
// NETFRAMEWORK/NETCOREAPP + TRACE, and PLATFORM_WINDOWS was never a Pulsar symbol, so fencing on
// it silently stripped this overlay from EVERY from-source install, Windows included). Proton is
// deliberately not fenced: Pulsar's IsWindows() answers true there, so no symbol can cover it -
// the manifest's <Platforms>Windows</Platforms> is what excludes Proton and Linux outright on
// Pulsar builds that understand it, and v2.3.1 has no Linux support to fence against.
#if !LINUX
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CleanFeed.Overlay
{
    internal sealed class OverlayWindow : Window
    {
        private readonly DispatcherTimer _syncTimer;
        private TextBlock _bannerText;
        private IntPtr _overlayHwnd;
        private IntPtr _gameHwnd;
        private bool _requestedVisible = true;

        public CaptureRoute Route { get; private set; }
        public bool AffinityRequested => Route == CaptureRoute.Affinity;
        public bool AffinitySetSucceeded { get; private set; }
        public int AffinityError { get; private set; }
        public bool AffinityReadbackSucceeded { get; private set; }
        public uint AffinityReadback { get; private set; }
        public IntPtr GameHwnd => _gameHwnd;

        public OverlayWindow(CaptureRoute route)
        {
            Route = route;
            Title = "CleanFeed Player-Only Test Overlay";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            IsHitTestVisible = false;
            Opacity = 0.0;
            Content = BuildContent();
            UpdateBanner();

            SourceInitialized += OnSourceInitialized;
            Closing += (_, __) => ClearCapturePolicy();
            Closed += (_, __) => Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);

            _syncTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _syncTimer.Tick += (_, __) => SyncToGameWindow();
            _syncTimer.Start();
        }

        public void SetRoute(CaptureRoute route)
        {
            Route = route;
            UpdateBanner();
            if (_overlayHwnd != IntPtr.Zero) ApplyCapturePolicy();
        }

        public void SetRequestedVisible(bool visible)
        {
            _requestedVisible = visible;
            SyncToGameWindow();
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _overlayHwnd = new WindowInteropHelper(this).Handle;
            int style = NativeMethods.GetWindowLong(_overlayHwnd, NativeMethods.GwlExStyle);
            NativeMethods.SetWindowLong(_overlayHwnd, NativeMethods.GwlExStyle,
                style | NativeMethods.WsExTransparent | NativeMethods.WsExToolWindow
                | NativeMethods.WsExNoActivate | NativeMethods.WsExLayered);

            ApplyCapturePolicy();
            SyncToGameWindow();
        }

        private void ApplyCapturePolicy()
        {
            uint requested = AffinityRequested
                ? NativeMethods.WdaExcludeFromCapture
                : NativeMethods.WdaNone;
            AffinitySetSucceeded = NativeMethods.SetWindowDisplayAffinity(_overlayHwnd, requested);
            AffinityError = AffinitySetSucceeded ? 0 : Marshal.GetLastWin32Error();
            AffinityReadbackSucceeded = NativeMethods.GetWindowDisplayAffinity(
                _overlayHwnd, out uint affinityReadback);
            AffinityReadback = affinityReadback;

            Plugin.Log("test capture route=" + OverlayController.RouteName(Route)
                       + ", affinity set=" + (AffinitySetSucceeded ? "ok" : "FAILED(" + AffinityError + ")")
                       + ", readback=" + (AffinityReadbackSucceeded
                           ? "0x" + AffinityReadback.ToString("X") : "FAILED"));
        }

        private void ClearCapturePolicy()
        {
            _syncTimer.Stop();
            if (_overlayHwnd == IntPtr.Zero) return;
            bool cleared = NativeMethods.SetWindowDisplayAffinity(_overlayHwnd, NativeMethods.WdaNone);
            Plugin.Log("test capture affinity clear="
                       + (cleared ? "ok" : "FAILED(" + Marshal.GetLastWin32Error() + ")"));
        }

        private void SyncToGameWindow()
        {
            if (_overlayHwnd == IntPtr.Zero) return;
            if (_gameHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_gameHwnd))
                _gameHwnd = NativeMethods.FindGameWindow(_overlayHwnd);

            bool targetUsable = _gameHwnd != IntPtr.Zero
                                && NativeMethods.IsWindowVisible(_gameHwnd)
                                && !NativeMethods.IsIconic(_gameHwnd);
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            bool gameProcessForeground = foreground != IntPtr.Zero
                                         && NativeMethods.BelongsToCurrentProcess(foreground);
            Opacity = _requestedVisible && targetUsable && gameProcessForeground ? 1.0 : 0.0;
            if (!targetUsable) return;

            if (!NativeMethods.GetClientRect(_gameHwnd, out NativeMethods.Rect rect)) return;
            var origin = new NativeMethods.Point();
            if (!NativeMethods.ClientToScreen(_gameHwnd, ref origin)) return;
            if (rect.Width <= 0 || rect.Height <= 0) return;

            NativeMethods.SetWindowPos(_overlayHwnd, NativeMethods.HwndTopmost,
                origin.X, origin.Y, rect.Width, rect.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        private UIElement BuildContent()
        {
            var root = new Grid
            {
                Background = Brushes.Transparent,
                IsHitTestVisible = false
            };

            root.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(235, 0, 255, 105)),
                BorderThickness = new Thickness(8),
                CornerRadius = new CornerRadius(18),
                Margin = new Thickness(18),
                IsHitTestVisible = false
            });

            _bannerText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 24,
                TextAlignment = TextAlignment.Center
            };
            root.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(225, 20, 0, 45)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 40, 225)),
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(24, 12, 24, 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 42, 0, 0),
                Child = _bannerText
            });

            root.Children.Add(new TextBlock
            {
                Text = "CF",
                Foreground = new SolidColorBrush(Color.FromArgb(190, 0, 255, 105)),
                FontFamily = new FontFamily("Segoe UI Black"),
                FontSize = 150,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 55, 35)
            });
            return root;
        }

        private void UpdateBanner()
        {
            if (_bannerText == null) return;
            switch (Route)
            {
                case CaptureRoute.Obs:
                    _bannerText.Text = "CLEANFEED OBS PLAIN OVERLAY | SHOULD BE ABSENT FROM GAME CAPTURE";
                    break;
                case CaptureRoute.Affinity:
                    _bannerText.Text = "CLEANFEED AFFINITY OVERLAY | MAY BLOCK NVIDIA RECORDING";
                    break;
                default:
                    _bannerText.Text = "CLEANFEED NVIDIA PLAIN OVERLAY | SHOULD BE ABSENT FROM GAME CAPTURE";
                    break;
            }
        }
    }
}
#endif
