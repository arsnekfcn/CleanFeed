using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using CleanFeed.Compatibility;
using CleanFeed.Core;
using CleanFeed.Overlay;
using CleanFeed.Profiles;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Gui;
using VRage;
using VRage.Render11.Resources;
using VRageRender;

namespace CleanFeed.Render
{
    internal enum HudRedirectState
    {
        Off,
        Arming,
        Active,
        Suspended,
        Rebuilding,
        Stopping,
        Blocked,
        Faulted
    }

    internal enum HudRedirectMode
    {
        Off,
        Whole,
        Selective
    }

    internal static class HudRedirector
    {
        private const int RearmDelayMs = 175;
        // Render frames a foreground mismatch must survive before the composition is parked. Three is
        // enough to absorb the single-frame activation blips - a notification, an overlay taking the
        // foreground for one frame, the shell handing activation around - that a real alt-tab never
        // looks like, without letting an unfocused window keep a stale HUD on the glass for long.
        private const int ForegroundHoldFrames = 3;
        private const int ShutdownHandoffMs = 500;
        private const int ShutdownPollMs = 10;
        private static readonly long RearmDelayTicks = Stopwatch.Frequency * RearmDelayMs / 1000;
        private static readonly object Gate = new object();
        private static string _buildId;
        private static volatile bool _requested;
        private static volatile bool _faulted;
        private static volatile HudRedirectState _state;
        private static volatile HudRedirectMode _mode;
        private static HudCompositionWindow _window;
        private static string _fault = "none";
        private static string _lastTransition = "initialize";
        private static long _lastTransitionAt;
        private static long _armingStartedAt;
        private static long _armingHwndBits;
        private static long _activeHwndBits;
        private static long _suspensions;
        private static long _rearms;
        private static long _nativeFailOpenFrames;
        private static long _presentFocusRejects;
        private static int _activationGeneration;
        private static volatile bool _privacySuppression;
        private static string _privacyReason = "inactive";
        private static long _privacyActivations;
        private static long _privacyReleases;
        private static long _privacyDiscardFrames;
        private static bool _focusPaused;
        private static long _focusPauses;
        private static long _focusPauseFrames;
        private static volatile bool _foregroundHold;
        // Steam overlay hold. The overlay renders INTO the game's swapchain while the player
        // surface composites ABOVE the window, so an open overlay sat underneath the filtered HUD
        // and was unusable. While the game reports it active the composition parks transparent
        // (one commit) and further commits are held - WITHOUT touching Active or privacy:
        // producers keep diverting, so filtered content cannot fall back to the backbuffer and
        // record mid-overlay, and publishes keep landing in the now-uncommitted surfaces so the
        // release is a single resumed commit already carrying current content.
        //
        // _overlayHold is written on the update thread by the game-service event and read on the
        // render thread; _overlayParked is render-thread-only. The event reports edges only, so a
        // session whose overlay is already open when the redirect arms parks on the first toggle.
        private static volatile bool _overlayHold;
        private static bool _overlayParked;
        private static long _overlayHolds;
        private static long _overlayHoldFrames;
        private static volatile bool _softPaused;
        private static int _foregroundMismatchRun;
        private static long _renderFrames;
        private static long _lastPresentFrame;
        private static long _mismatchCountedFrame = -1;
        private static long _softPauses;
        private static long _softPauseFrames;
        private static long _debounceAbsorbed;

        public static bool Requested => _requested;

        // _foregroundHold gates this rather than the lifecycle state, and that split is the point of
        // the soft pause: copies and commits stop on the first frame a foreground mismatch is seen -
        // nothing new reaches the glass while focus is in doubt - while _state stays Active so the
        // composition device, target, visuals, surfaces, bound HWND and arming state all survive the
        // gap untouched. Everything downstream that asks "may I publish?" asks this.
        public static bool Active => _requested && !_faulted && !_foregroundHold
                                     && _state == HudRedirectState.Active;
        public static HudRedirectMode Mode => _mode;
        public static bool WholeActive => Active && _mode == HudRedirectMode.Whole;
        public static bool SelectiveActive => Active && _mode == HudRedirectMode.Selective;
        public static bool PrivacySuppressionActive
            => _requested && !_faulted && _privacySuppression;
        public static bool SelectiveInterceptionActive
            => _requested && !_faulted && _mode == HudRedirectMode.Selective
               && (Active || PrivacySuppressionActive);
        public static int ActivationGeneration => Volatile.Read(ref _activationGeneration);

        // Computed lazily rather than in a static initializer: reflection over the manifest module can
        // throw, and an exception out of a static initializer poisons the type with
        // TypeInitializationException for the rest of the process - every later member access rethrows
        // it. A diagnostic string must never be able to take the whole redirector down, so a failure
        // degrades to "unknown" instead. A benign race just computes the same value twice.
        private static string BuildId
        {
            get
            {
                if (_buildId != null) return _buildId;
                string value;
                try
                {
                    value = typeof(HudRedirector).Assembly.ManifestModule
                        .ModuleVersionId.ToString("N").Substring(0, 8);
                }
                catch { value = "unknown"; }
                _buildId = value;
                return value;
            }
        }

        internal static HudEffectiveRoute ResolveRoute(HudRouteTag tag)
        {
            if (PrivacySuppressionActive) return HudEffectiveRoute.Suppressed;
            return tag == null ? HudProfileService.Current.GlobalEffectiveRoute : tag.ConfiguredRoute;
        }

        public static void Initialize()
        {
            VanillaHudGenerationSeal.Reset();
            _state = HudRedirectState.Off;
            _mode = HudRedirectMode.Off;
            SetPrivacySuppression(false, "initialize");
            _lastTransitionAt = Stopwatch.GetTimestamp();
        }

        public static bool Enable(out string reason) => Enable(HudRedirectMode.Whole, out reason);

        public static bool EnableSelective(out string reason) => Enable(HudRedirectMode.Selective, out reason);

        private static bool Enable(HudRedirectMode mode, out string reason)
        {
            if (!CheckDisplayCompatibility(out reason))
            {
                SetBlocked(reason, false);
                return false;
            }

            if (!CheckRendererCompatibility(out reason))
            {
                SetBlocked("renderer conflict: " + reason, false);
                return false;
            }

            _faulted = false;
            _fault = "none";
            _mode = mode;
            _requested = true;
            SetPrivacySuppression(
                HudProfileService.Current.UnfocusedPrivacyEnabled,
                "enable-arming");
            Interlocked.Increment(ref _activationGeneration);
            ResetArming();
            try
            {
                lock (Gate)
                {
                    if (_window == null || _window.Failed)
                    {
                        _window?.Dispose();
                        _window = new HudCompositionWindow();
                    }
                    _window.Start();
                }
            }
            catch (Exception ex)
            {
                _requested = false;
                _mode = HudRedirectMode.Off;
                SetPrivacySuppression(false, "target-start-fault");
                _faulted = true;
                _fault = "target start: " + ex.GetType().Name + " - " + ex.Message;
                Transition(HudRedirectState.Faulted, "target-start");
                reason = _fault;
                return false;
            }
            Transition(HudRedirectState.Arming, "enable-" + mode.ToString().ToLowerInvariant());
            reason = "game-HWND GPU transport arming";
            return true;
        }

        public static void Disable()
        {
            _requested = false;
            _mode = HudRedirectMode.Off;
            SetPrivacySuppression(false, "disabled");
            ResetArming();
            if (_state != HudRedirectState.Off && _state != HudRedirectState.Faulted)
                Transition(HudRedirectState.Stopping, "disabled-by-user");
            lock (Gate) _window?.RequestHide();
        }

        // Called from the RenderMainSprites prefix. Focus must be checked here, at frame cadence, so
        // native HUD suppression can never lag behind a focus change by more than a frame.
        public static bool TryBeginFrame(int width, int height)
        {
            // The frame counter is what makes the foreground debounce count frames rather than
            // probes: TryBeginFrame and OnPresent both probe the target, and a mismatch both of them
            // see is still one frame of evidence. This is the frame boundary for that count.
            Interlocked.Increment(ref _renderFrames);
            if (!_requested || _faulted)
            {
                SetPrivacySuppression(false, "not-requested");
                return false;
            }
            if (!CheckDisplayCompatibility(out string displayReason))
            {
                SetBlocked(displayReason, true);
                CountFailOpenFrame();
                return false;
            }

            HudCompositionWindow window;
            lock (Gate) window = _window;
            if (window == null)
            {
                SetPrivacyForGap("target-missing");
                SuspendOnRenderThread("target-missing");
                CountFailOpenFrame();
                return false;
            }
            if (window.Failed)
            {
                FailOpen("composition target", new InvalidOperationException(window.LastError));
                CountFailOpenFrame();
                return false;
            }
            if (!window.TryGetReadyTarget(
                    out IntPtr hwnd,
                    out int clientWidth,
                    out int clientHeight,
                    out HudTargetUnreadyReason targetReason))
            {
                if (TryHoldForeground(targetReason)) return false;
                SetPrivacyForGap(HudCompositionWindow.Describe(targetReason));
                SuspendOnRenderThread(HudCompositionWindow.Describe(targetReason));
                CountFailOpenFrame();
                return false;
            }

            // A client rect that differs from the render resolution is configuration, not a fault: in
            // Fullscreen Window the client rect stays at desktop size while the game renders at the
            // selected resolution and stretches its backbuffer across the window. The client size is
            // passed to the transport, which applies the same stretch to the composition tree.
            IntPtr activeHwnd = new IntPtr(Interlocked.Read(ref _activeHwndBits));
            if (_state == HudRedirectState.Active && activeHwnd == hwnd)
            {
                try
                {
                    // The client size is part of the match so a live client-rect change - window
                    // resize, or a resolution change in either direction - rebuilds the transform.
                    if (!HudGpuTransport.Matches(hwnd, width, height, clientWidth, clientHeight))
                    {
                        Transition(HudRedirectState.Rebuilding, "viewport-rebuild");
                        HudGpuTransport.Ensure(hwnd, width, height, clientWidth, clientHeight);
                        Transition(HudRedirectState.Active, "viewport-ready");
                    }
                    ReleaseForegroundHold();
                    SetPrivacySuppression(false, "active-ready");
                    if (_overlayHold)
                    {
                        if (!_overlayParked)
                        {
                            try { HudGpuTransport.PublishTransparentAll(); }
                            catch (Exception ex)
                            {
                                FailOpen("overlay park", ex);
                                CountFailOpenFrame();
                                return false;
                            }
                            _overlayParked = true;
                        }
                        Interlocked.Increment(ref _overlayHoldFrames);
                    }
                    else if (_overlayParked)
                    {
                        _overlayParked = false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    FailOpen("composition rebuild", ex);
                    CountFailOpenFrame();
                    return false;
                }
            }

            long now = Stopwatch.GetTimestamp();
            IntPtr armingHwnd = new IntPtr(Interlocked.Read(ref _armingHwndBits));
            if (_state != HudRedirectState.Arming || armingHwnd != hwnd || _armingStartedAt == 0)
            {
                SetPrivacyForGap("foreground-stabilizing");
                // The tree this hold was protecting is about to be destroyed, so the hold has
                // nothing left to describe.
                ClearForegroundHold();
                if (HudGpuTransport.IsReady) HudGpuTransport.DisposeResources();
                Interlocked.Exchange(ref _activeHwndBits, 0);
                Interlocked.Exchange(ref _armingHwndBits, hwnd.ToInt64());
                Interlocked.Exchange(ref _armingStartedAt, now);
                Transition(HudRedirectState.Arming, "foreground-stabilizing");
                CountFailOpenFrame();
                return false;
            }

            if (now - Interlocked.Read(ref _armingStartedAt) < RearmDelayTicks)
            {
                SetPrivacyForGap("foreground-stabilizing");
                CountFailOpenFrame();
                return false;
            }

            try
            {
                HudGpuTransport.Ensure(hwnd, width, height, clientWidth, clientHeight);
                Interlocked.Exchange(ref _activeHwndBits, hwnd.ToInt64());
                Interlocked.Increment(ref _rearms);
                Transition(HudRedirectState.Active, "foreground-stable");
                ReleaseForegroundHold();
                SetPrivacySuppression(false, "foreground-stable");
                return true;
            }
            catch (Exception ex)
            {
                FailOpen("composition setup", ex);
                CountFailOpenFrame();
                return false;
            }
        }

        public static void CopyCompletedHud(
            IBorrowedRtvTexture layer,
            int messageCount,
            bool holdTransientBatches = true,
            HudCompositionLayer compositionLayer = HudCompositionLayer.Main)
        {
            if (!Active) return;
            HudGpuTransport.CopyCompletedHud(
                layer, messageCount, holdTransientBatches, compositionLayer);
        }

        // Two different vanilla gates stop the player HUD, and they stop different amounts of it, so
        // the redirect needs both notions of "hidden" rather than one. Both live here.
        //
        // The narrow one, GameHudFullyHidden: MyGuiScreenHudSpace.Draw() returns before drawing
        // anything when !MyHud.IsVisible (no live local character, medical screen, and similar).
        // Nothing at all is produced, so every named queue starves at once and every layer must be
        // taken down, sealed routes included.
        //
        // The broad one, GameHudHidden: the individual producers are additionally gated on
        // MyHud.MinimalHud and MyHud.CutsceneHud - see MyGuiScreenHudSpace.Draw,
        // toolbarControl.Visible = !m_hiddenToolbar && !MyHud.MinimalHud && !MyHud.CutsceneHud.
        // This is the state the Tab cycle drives, and it stops most producers but NOT all of them:
        // m_chatControl.Visible = !MyHud.MinimalHud || m_chatControl.HasFocus || MyHud.CutsceneHud,
        // so a focused chat keeps drawing with the HUD tabbed off, which is documented vanilla
        // behaviour. Callers that would clear a producer still capable of drawing must therefore use
        // the narrow predicate for it; see the sealed-route carve-out in HudNamedSpriteRoutes.
        //
        // MinimalHud rather than HudState is the member to read: MyStatHudMode cycles
        // MyHud.HudState 0/1/2 on TOGGLE_HUD and then assigns MyHud.MinimalHud = MyHud.IsHudMinimal
        // (state 0), so MinimalHud tracks the Tab cycle exactly while additionally covering the
        // photo-mode and visual-script hides that stop the same producers. Reading MinimalHud
        // therefore matches the moment the producers stop, which is what the routes need.
        // The m_transitionAlpha < 1f half of Draw's early return is deliberately not mirrored:
        // those are fade frames that still draw, and treating them as hidden would flicker.
        //
        // Both are called on the render thread. These are plain statics written from the update
        // thread, so a read may be one frame stale; that only moves the clear or the resume by a
        // frame, which is harmless. Any exception answers false so the redirect falls back to normal
        // retention.
        internal static bool GameHudFullyHidden()
        {
            try { return !MyHud.IsVisible; }
            catch { return false; }
        }

        internal static bool GameHudHidden()
        {
            try { return !MyHud.IsVisible || MyHud.MinimalHud || MyHud.CutsceneHud; }
            catch { return false; }
        }

        // Focus is checked a second time at the commit boundary. If focus changed after HUD command
        // generation, detach the visual and leave the already-running game present on its vanilla path.
        public static void OnPresent()
        {
            if (!_requested)
            {
                CleanupOnRenderThread();
                return;
            }
            // Present is the frame boundary that always happens. TryBeginFrame normally advances the
            // counter the foreground debounce measures against; this advances it only when the sprite
            // pass did not, so a frame loop that presents without rendering main sprites cannot stall
            // the debounce halfway and hold a stale HUD on the glass indefinitely.
            long frame = Interlocked.Read(ref _renderFrames);
            if (frame == _lastPresentFrame) frame = Interlocked.Increment(ref _renderFrames);
            _lastPresentFrame = frame;

            // Deliberately the lifecycle state rather than Active: a foreground hold must not stop
            // this probe running. The hold is a publishing gate, and this is still the frame's last
            // chance to notice a real target loss - and to keep privacy latched on the render thread
            // while the hold lasts.
            if (_faulted || _state != HudRedirectState.Active) return;

            HudCompositionWindow window;
            lock (Gate) window = _window;
            if (window == null)
            {
                Interlocked.Increment(ref _presentFocusRejects);
                SetPrivacyForGap("present-target-missing");
                SuspendOnRenderThread("present-target-missing");
                return;
            }
            if (!window.TryGetReadyTarget(
                    out IntPtr hwnd, out _, out _, out HudTargetUnreadyReason reason))
            {
                if (TryHoldForeground(reason)) return;
                Interlocked.Increment(ref _presentFocusRejects);
                SetPrivacyForGap("present-" + HudCompositionWindow.Describe(reason));
                SuspendOnRenderThread("present-" + HudCompositionWindow.Describe(reason));
                return;
            }

            if (hwnd != new IntPtr(Interlocked.Read(ref _activeHwndBits)))
            {
                Interlocked.Increment(ref _presentFocusRejects);
                SetPrivacyForGap("present-target-changed");
                SuspendOnRenderThread("present-target-changed");
                return;
            }

            // The target reads ready again, but the hold is released at the next frame boundary,
            // which is also where copies resume. Committing here would publish a frame nothing was
            // copied into, and while a soft pause is parked it would publish it over the transparent
            // clear that took the HUD off the glass.
            if (_foregroundHold) return;
            // Parked under the Steam overlay: the transparent clear is already on the glass, and
            // committing here would publish the surfaces' uncommitted content right back over it.
            if (_overlayHold && _overlayParked) return;

            try { HudGpuTransport.CommitLatest(); }
            catch (Exception ex) { FailOpen("overlay commit", ex); }
        }

        public static void FailOpen(string stage, Exception exception)
        {
            bool firstFault = !_faulted;
            _fault = stage + ": " + exception.GetType().Name + " - " + exception.Message;
            _faulted = true;
            _requested = false;
            _mode = HudRedirectMode.Off;
            SetPrivacySuppression(false, "fault");
            ResetArming();
            Transition(HudRedirectState.Faulted, stage);
            lock (Gate) _window?.RequestHide();
            if (firstFault) Plugin.Log("HUD redirect fail-open at " + stage + ": " + exception);
        }

        public static void Shutdown()
        {
            _requested = false;
            _mode = HudRedirectMode.Off;
            SetPrivacySuppression(false, "shutdown");
            ResetArming();

            // Invariant: the composition COM objects are created on the render thread and are normally
            // destroyed there too. _requested is already false above, so the next OnPresent runs
            // CleanupOnRenderThread and disposes them on the owning thread. Shutdown is called from
            // Plugin.Dispose on the update thread, so it waits for that handover instead of tearing
            // the objects out from under a render thread that may be mid BeginDraw/EndDraw/Commit.
            // Falling through without the handover is a last resort for a render loop that has already
            // stopped - the game exiting - where waiting longer would only stall the shutdown.
            if (HudGpuTransport.IsReady && !HudGpuTransport.IsRenderThread)
            {
                for (int waited = 0;
                     waited < ShutdownHandoffMs && HudGpuTransport.IsReady;
                     waited += ShutdownPollMs)
                {
                    Thread.Sleep(ShutdownPollMs);
                }
                if (HudGpuTransport.IsReady)
                    Plugin.Log("HUD shutdown: render thread did not service cleanup; disposing directly");
            }

            HudOffscreenSlot.ReleaseAndClear();
            HudNamedSpriteFrame.ReleaseAndClear();
            HudGpuTransport.DisposeResources();
            VanillaHudGenerationSeal.Reset();

            HudCompositionWindow window;
            lock (Gate)
            {
                window = _window;
                _window = null;
            }
            window?.Dispose();
            _state = _faulted ? HudRedirectState.Faulted : HudRedirectState.Off;
        }

        public static string StatusLine()
        {
            HudCompositionWindow window;
            lock (Gate) window = _window;
            long now = Stopwatch.GetTimestamp();
            long transitionAgeMs = Math.Max(0, (now - Interlocked.Read(ref _lastTransitionAt)) * 1000
                                                / Stopwatch.Frequency);
            long armingAt = Interlocked.Read(ref _armingStartedAt);
            long rearmRemainingMs = armingAt == 0
                ? 0
                : Math.Max(0, RearmDelayMs - (now - armingAt) * 1000 / Stopwatch.Frequency);
            return "hud=" + _state.ToString().ToLowerInvariant()
                   + ", requested=" + _requested
                   + ", mode=" + _mode.ToString().ToLowerInvariant()
                   + ", build=" + BuildId
                   + ", display=" + CurrentWindowMode()
                   + ", transition=" + _lastTransition
                   + ", transition-age=" + transitionAgeMs + "ms"
                   + ", rearm-in=" + rearmRemainingMs + "ms"
                   + ", suspends=" + Interlocked.Read(ref _suspensions)
                   + ", rearms=" + Interlocked.Read(ref _rearms)
                   + ", native-failopen=" + Interlocked.Read(ref _nativeFailOpenFrames)
                   + ", present-focus-rejects=" + Interlocked.Read(ref _presentFocusRejects)
                   + ", privacy-config=" + HudProfileService.Current.UnfocusedPrivacyEnabled
                   + ", privacy-active=" + PrivacySuppressionActive
                   + ", privacy-reason=" + _privacyReason
                   + ", privacy-transitions=" + Interlocked.Read(ref _privacyActivations)
                   + "/" + Interlocked.Read(ref _privacyReleases)
                   + ", privacy-discard-frames=" + Interlocked.Read(ref _privacyDiscardFrames)
                   + ", focus-pause=" + (_focusPaused ? "paused" : "off")
                   + ", focus-pauses=" + Interlocked.Read(ref _focusPauses)
                   + ", focus-pause-frames=" + Interlocked.Read(ref _focusPauseFrames)
                   + ", soft-pause=" + DescribeForegroundHold()
                   + ", soft-pauses=" + Interlocked.Read(ref _softPauses)
                   + ", soft-pause-frames=" + Interlocked.Read(ref _softPauseFrames)
                   + ", steam-overlay=" + (_overlayHold ? (_overlayParked ? "parked" : "pending") : "off")
                   + ", overlay-holds=" + Interlocked.Read(ref _overlayHolds)
                   + "/" + Interlocked.Read(ref _overlayHoldFrames)
                   + ", debounce-absorbed=" + Interlocked.Read(ref _debounceAbsorbed)
                   + ", debounce-run=" + Volatile.Read(ref _foregroundMismatchRun)
                   + "/" + ForegroundHoldFrames
                   + ", overlay-input-violations=0(no-overlay-hwnd)"
                   + ", fault=" + _fault
                   + "; " + (window?.StatusLine() ?? "target=none")
                   + "; " + HudGpuTransport.StatusLine()
                   + "; " + HudNamedSpriteFrame.StatusLine()
                   + "; " + HudRecordedEcho.StatusLine()
                   + "; " + HudCommandListPatch.StatusLine();
        }

        // "parked" is a soft pause holding a transparent, uncommitted tree; "debounce" is a mismatch
        // still inside its debounce window, publishing nothing but with the last committed HUD still
        // on the glass.
        private static string DescribeForegroundHold()
            => _softPaused ? "parked" : (_foregroundHold ? "debounce" : "off");

        internal static string HealthStatusLine()
        {
            HudCompositionWindow window;
            lock (Gate) window = _window;
            return "hud-health=" + _state.ToString().ToLowerInvariant()
                   + "/" + _mode.ToString().ToLowerInvariant()
                   + ", requested=" + _requested
                   + ", generation=" + Volatile.Read(ref _activationGeneration)
                   + ", suspends=" + Interlocked.Read(ref _suspensions)
                   + ", rearms=" + Interlocked.Read(ref _rearms)
                   + ", failopen-frames=" + Interlocked.Read(ref _nativeFailOpenFrames)
                   + ", present-rejects=" + Interlocked.Read(ref _presentFocusRejects)
                   + ", privacy=" + PrivacySuppressionActive + ":" + _privacyReason
                   + ", privacy-discard=" + Interlocked.Read(ref _privacyDiscardFrames)
                   + ", focus-pause=" + (_focusPaused ? "paused" : "off")
                   + ":" + Interlocked.Read(ref _focusPauses)
                   + ", soft-pause=" + DescribeForegroundHold()
                   + ":" + Interlocked.Read(ref _softPauses)
                   + "/" + Interlocked.Read(ref _softPauseFrames)
                   + ", steam-overlay=" + (_overlayHold ? (_overlayParked ? "parked" : "pending") : "off")
                   + ":" + Interlocked.Read(ref _overlayHolds)
                   + "/" + Interlocked.Read(ref _overlayHoldFrames)
                   + ", debounce-absorbed=" + Interlocked.Read(ref _debounceAbsorbed)
                   + ", fault=" + _fault
                   + "; " + (window?.HealthStatusLine() ?? "target=none")
                   + "; " + HudGpuTransport.HealthStatusLine()
                   + "; " + HudNamedSpriteFrame.StatusLine();
        }

        // The single owner of what a foreground mismatch costs. Both probe sites route here, and a
        // mismatch never reaches SuspendOnRenderThread from either of them.
        //
        // A mismatch is far more often a single-frame blip than a real target loss, and a teardown
        // costs the whole overlay for the best part of a second: DisposeResources destroys the device,
        // target and all six visuals and surfaces, and coming back costs the rearm delay plus a full
        // Ensure. So the mismatch is debounced across ForegroundHoldFrames frames and, if it
        // persists, parks the composition instead of destroying it. Real target loss - window gone,
        // hidden, minimized, client rect invalid, present target changed - is untouched and still
        // suspends and rebuilds, because those are not states a held tree can survive.
        //
        // Whatever the debounce decides, the frame is not published: the hold gates Active from the
        // first mismatch frame, so producers stop reaching the composition immediately, and privacy
        // suppression is applied ahead of every other decision here so producers route to the
        // suppressed sink on the same frame. The debounce delays the teardown decision, never the
        // privacy decision.
        //
        // _focusPaused/_foregroundHold/_softPaused contract: the render thread owns the transitions -
        // it is the only thread that ever sets them, and it does so on the same thread as the
        // render-side state transitions. Update-thread writers (Enable/Disable/Shutdown/FailOpen via
        // ResetArming) only ever clear them, never set them, so they cannot resurrect a stale hold.
        // That makes an interlock unnecessary: the worst a torn read can cost is one spurious resume
        // log line.
        private static bool TryHoldForeground(HudTargetUnreadyReason reason)
        {
            if (reason != HudTargetUnreadyReason.ForegroundMismatch) return false;
            if (_state != HudRedirectState.Active) return false;
            if (!HudGpuTransport.IsReady) return false;

            if (!HudProfileService.Current.UnfocusedPrivacyEnabled)
            {
                // Privacy off: retention is the whole point. Surfaces, copies and Active are all
                // kept and only the commit is skipped, so DWM keeps presenting the last committed
                // HUD and refocus resumes without a rearm rebuild. Unchanged behaviour, including
                // the case where privacy is switched off while a soft pause is parked - the pause
                // is released into this path and the retained tree is simply transparent until the
                // producers republish. The debounce run goes with it, so a park that ended by
                // configuration is never later counted as a blip the debounce absorbed.
                ClearForegroundHold();
                if (!_focusPaused)
                {
                    _focusPaused = true;
                    Interlocked.Increment(ref _focusPauses);
                    Plugin.Log("HUD focus-pause: retaining composition surfaces while unfocused (privacy off)");
                }
                Interlocked.Increment(ref _focusPauseFrames);
                return true;
            }

            // Privacy first and unconditionally, before the debounce is even consulted: producers
            // must be routed to the suppressed sink on the very frame the mismatch is seen.
            SetPrivacyForGap("foreground-mismatch");
            _focusPaused = false;
            _foregroundHold = true;

            long frame = Interlocked.Read(ref _renderFrames);
            if (Interlocked.Read(ref _mismatchCountedFrame) != frame)
            {
                Interlocked.Exchange(ref _mismatchCountedFrame, frame);
                if (_softPaused) Interlocked.Increment(ref _softPauseFrames);
                else _foregroundMismatchRun++;
            }

            if (_softPaused) return true;
            if (Volatile.Read(ref _foregroundMismatchRun) < ForegroundHoldFrames) return true;

            EngageSoftPause();
            return true;
        }

        // Park the composition rather than destroy it: publish transparent to all six layers and
        // commit that once, so neither sensitive nor stale content is left on the glass - with
        // privacy on the HUD must leave the player's view too, which is what the old teardown
        // achieved - then stop committing and simply hold. Device, target, visuals and surfaces stay
        // alive, so the return to foreground is a resumed commit: no rearm delay, no DisposeResources,
        // no Ensure.
        //
        // The clear goes through the transport rather than the per-route publish paths; see
        // HudGpuTransport.PublishTransparentAll for why that cannot leave a route able to leak.
        private static void EngageSoftPause()
        {
            try { HudGpuTransport.PublishTransparentAll(); }
            catch (Exception ex)
            {
                FailOpen("soft-pause clear", ex);
                return;
            }

            _softPaused = true;
            Interlocked.Increment(ref _softPauses);
            Interlocked.Increment(ref _softPauseFrames);
            Plugin.Log("HUD soft-pause: composition parked transparent while unfocused"
                       + " (privacy on), surfaces retained");
        }

        // Update-thread entry, wired to MyGameService.OnOverlayActivated by the plugin. Flag and
        // log only: the render thread performs the park (transparent publish plus one commit) on
        // its next ready frame and gates further commits itself, so no composition work happens on
        // this thread.
        public static void SetOverlayHold(bool active)
        {
            if (_overlayHold == active) return;
            _overlayHold = active;
            if (active) Interlocked.Increment(ref _overlayHolds);
            Plugin.Log(active
                ? "Steam overlay opened: parking the player surface beneath it"
                : "Steam overlay closed: player surface commits resume");
        }

        // The foreground is back. A run that never reached the park was a blip the debounce absorbed
        // with no visible cost at all - the glass kept the last committed HUD throughout.
        private static void ReleaseForegroundHold()
        {
            if (Volatile.Read(ref _foregroundMismatchRun) > 0 && !_softPaused)
                Interlocked.Increment(ref _debounceAbsorbed);
            if (_softPaused)
                Plugin.Log("HUD soft-resume: commits resumed on foreground return, no rebuild");
            ClearFocusPause();
            ClearForegroundHold();
        }

        private static void ClearFocusPause()
        {
            if (!_focusPaused) return;
            _focusPaused = false;
            Plugin.Log("HUD focus-resume: composition surfaces retained across focus loss");
        }

        // Flags only, with no logging and no telemetry: for the paths that are already destroying or
        // rebuilding the composition the hold was describing, where a resume never happened.
        private static void ClearForegroundHold()
        {
            _foregroundHold = false;
            _softPaused = false;
            Volatile.Write(ref _foregroundMismatchRun, 0);
            Interlocked.Exchange(ref _mismatchCountedFrame, -1);
        }

        private static void SuspendOnRenderThread(string reason)
        {
            _focusPaused = false;
            ClearForegroundHold();
            SetPrivacyForGap(reason);
            bool changed = _state != HudRedirectState.Suspended
                           || !_lastTransition.Equals(reason, StringComparison.Ordinal);
            if (HudGpuTransport.IsReady) HudGpuTransport.DisposeResources();
            Interlocked.Exchange(ref _activeHwndBits, 0);
            Interlocked.Exchange(ref _armingHwndBits, 0);
            Interlocked.Exchange(ref _armingStartedAt, 0);
            if (!changed) return;

            Interlocked.Increment(ref _suspensions);
            Transition(HudRedirectState.Suspended, reason);
        }

        private static void CleanupOnRenderThread()
        {
            if (_requested) return;
            if (HudGpuTransport.IsReady) HudGpuTransport.DisposeResources();
            Interlocked.Exchange(ref _activeHwndBits, 0);

            HudCompositionWindow window;
            lock (Gate)
            {
                if (_requested) return;
                window = _window;
                _window = null;
            }
            window?.Dispose();
            _mode = HudRedirectMode.Off;
            if (!_faulted && _state != HudRedirectState.Blocked)
                Transition(HudRedirectState.Off, "cleanup-complete");
            SetPrivacySuppression(false, "cleanup-complete");
        }

        private static void ResetArming()
        {
            _focusPaused = false;
            ClearForegroundHold();
            Interlocked.Exchange(ref _armingStartedAt, 0);
            Interlocked.Exchange(ref _armingHwndBits, 0);
            Interlocked.Exchange(ref _activeHwndBits, 0);
        }

        private static void CountFailOpenFrame()
            => Interlocked.Increment(ref _nativeFailOpenFrames);

        internal static void CountPrivacyDiscardFrame()
            => Interlocked.Increment(ref _privacyDiscardFrames);

        internal static void ObserveFocusPrivacy()
        {
            if (!_requested || _faulted || !HudProfileService.Current.UnfocusedPrivacyEnabled)
            {
                SetPrivacySuppression(false, "privacy-disabled");
                return;
            }

            // A render-thread foreground hold latches suppression exactly as the Suspended state
            // does, and for the same reason: the hold has already stopped copies and commits, and
            // releasing suppression from this thread before the render thread has resumed would let
            // one vanilla-drawn frame through on the frame in between.
            if (_foregroundHold)
            {
                SetPrivacySuppression(true, "foreground-hold");
                return;
            }

            if (_state == HudRedirectState.Arming
                || _state == HudRedirectState.Suspended
                || _state == HudRedirectState.Rebuilding)
            {
                SetPrivacySuppression(true, _lastTransition);
                return;
            }
            if (_state != HudRedirectState.Active) return;

            HudCompositionWindow window;
            lock (Gate) window = _window;
            if (window == null)
            {
                SetPrivacySuppression(true, "observe-target-missing");
                return;
            }
            if (!window.TryGetReadyTarget(out _, out _, out _, out HudTargetUnreadyReason reason))
                SetPrivacySuppression(true, "observe-" + HudCompositionWindow.Describe(reason));
            else
                SetPrivacySuppression(false, "observe-ready");
        }

        private static void SetPrivacyForGap(string reason)
            => SetPrivacySuppression(HudProfileService.Current.UnfocusedPrivacyEnabled, reason);

        private static void SetPrivacySuppression(bool active, string reason)
        {
            bool previous = _privacySuppression;
            _privacySuppression = active;
            _privacyReason = active ? reason : "inactive";
            if (previous == active) return;
            if (active) Interlocked.Increment(ref _privacyActivations);
            else Interlocked.Increment(ref _privacyReleases);
            Plugin.Log("HUD privacy suppression " + (active ? "active" : "released")
                       + ", reason=" + reason);
        }

        private static void Transition(HudRedirectState next, string reason)
        {
            HudRedirectState previous = _state;
            if (previous == next && _lastTransition.Equals(reason, StringComparison.Ordinal)) return;

            long at = Stopwatch.GetTimestamp();
            _state = next;
            _lastTransition = reason;
            Interlocked.Exchange(ref _lastTransitionAt, at);
            Plugin.Log("HUD lifecycle " + previous.ToString().ToLowerInvariant()
                       + " -> " + next.ToString().ToLowerInvariant()
                       + ", reason=" + reason + ", qpc=" + at);
        }

        private static bool CheckDisplayCompatibility(out string reason)
        {
            try
            {
                if (MySandboxGame.Config != null
                    && MySandboxGame.Config.WindowMode == MyWindowModeEnum.Fullscreen)
                {
                    reason = "exclusive fullscreen is unsupported; use Fullscreen Window";
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = "window mode check failed: " + ex.GetType().Name;
                return false;
            }

            reason = "compatible";
            return true;
        }

        private static string CurrentWindowMode()
        {
            try { return MySandboxGame.Config?.WindowMode.ToString().ToLowerInvariant() ?? "unknown"; }
            catch { return "unknown"; }
        }

        private static void SetBlocked(string reason, bool notify)
        {
            bool changed = _state != HudRedirectState.Blocked
                           || !_fault.Equals("blocked: " + reason, StringComparison.Ordinal);
            _requested = false;
            _mode = HudRedirectMode.Off;
            SetPrivacySuppression(false, "blocked");
            _fault = "blocked: " + reason;
            ResetArming();
            lock (Gate) _window?.RequestHide();
            if (!changed) return;

            Transition(HudRedirectState.Blocked, reason);
            Plugin.Log("HUD redirect blocked: " + reason);
            if (notify) Notify.Chat("HUD redirect disabled: " + reason);
        }

        private static bool CheckRendererCompatibility(out string reason)
        {
            var targets = new Dictionary<string, MethodBase>
            {
                { "RenderMainSprites", AccessTools.Method(typeof(MyRender11), nameof(MyRender11.RenderMainSprites), Type.EmptyTypes) },
                { "RenderMainSpritesWorker", AccessTools.Method(typeof(MyRender11), "RenderMainSpritesWorker") },
                { "ConsumeMainSprites", AccessTools.Method(typeof(MyRender11), nameof(MyRender11.ConsumeMainSprites), Type.EmptyTypes) },
                { "Present", AccessTools.Method(typeof(MyRender11), nameof(MyRender11.Present), Type.EmptyTypes) }
            };

            foreach (KeyValuePair<string, MethodBase> target in targets)
            {
                if (target.Value == null)
                {
                    reason = target.Key + " target missing";
                    return false;
                }

                Patches patches = Harmony.GetPatchInfo(target.Value);
                if (patches == null) continue;
                foreach (string owner in patches.Owners)
                {
                    if (!owner.Equals(Plugin.HarmonyId, StringComparison.Ordinal))
                    {
                        reason = target.Key + " is also patched by " + owner;
                        return false;
                    }
                }
            }

            reason = "compatible";
            return true;
        }
    }
}
