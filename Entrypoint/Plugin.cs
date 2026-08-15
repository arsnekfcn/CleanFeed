using System;
using System.Reflection;
using CleanFeed.Core;
using CleanFeed.Compatibility;
using CleanFeed.Diagnostics;
using CleanFeed.Profiles;
using CleanFeed.Render;
using CleanFeed.Screens;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Plugins;
using VRage.Utils;

#if !LOCAL_BUILD
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
#endif

namespace CleanFeed
{
    // Plugin entry point: lifecycle, chat commands, the adapter scan cadence, and the redirect's
    // session gating. The composed player surface deliberately carries no capture-protection flag,
    // so NVIDIA and OBS game-capture paths read the game swapchain untouched.
    public sealed class Plugin : IPlugin
    {
        public const string Id = "cleanfeed";
        public const string HarmonyId = "cleanfeed.hud-redirect";
        public static Plugin Instance { get; private set; }

        private const int AssemblyPollTicks = 120;
        private const int ViewContextPollTicks = 30;

        private Commands _commands;
        private bool _chatHooked;
        private Harmony _harmony;
        private bool _renderPatchesReady;
        private int _assemblyPoll;
        private int _assemblyCount;
        private int _viewContextPoll;
        private bool _autoRedirectAttempted;
        private HudRedirectMode _resumeRedirectMode = HudRedirectMode.Off;

        public void Init(object gameInstance)
        {
            try
            {
                Instance = this;
                _commands = new Commands(this);
                HudRedirector.Initialize();
                CleanFeedHealthTelemetry.Initialize();
                try
                {
                    _harmony = new Harmony(HarmonyId);
                    _harmony.PatchAll(typeof(Plugin).Assembly);
                    HudAdapterCoordinator.Initialize(_harmony);
                    _renderPatchesReady = true;
                }
                catch (Exception ex) { Log("render patches unavailable: " + ex); }
                try
                {
                    // The Steam overlay renders into the game's swapchain, underneath the composed
                    // player surface; the redirector parks while it is open so Shift+Tab stays
                    // usable. Guarded separately from the patches: a service without the event
                    // (EOS, offline) costs the park, never the plugin.
                    Sandbox.Engine.Networking.MyGameService.OnOverlayActivated += OnGameOverlayActivated;
                }
                catch (Exception ex) { Log("Steam overlay hook unavailable: " + ex.Message); }
                Log("ready: /cf settings; /cf redirect; /cf hud <item> on|off; /cf player <item> show|hide; /cf privacy unfocused on|off; /cf auto on|off");
            }
            catch (Exception ex) { Log("Init FAILED: " + ex); }
        }

        public void Update()
        {
            try { CleanFeedHealthTelemetry.Update(); }
            catch (Exception ex) { LogOnce("health telemetry", ex); }
            if (MyAPIGateway.Session == null)
            {
                // Leaving a world re-arms the auto-enable for the next one.
                _autoRedirectAttempted = false;
                // The redirect is world-scoped and must never survive into the main menu or a
                // loading screen: those are MyGuiScreenBase subclasses that the selective scope
                // treats as interactive, so a redirect left running diverts the loader itself onto
                // the composition layers. It comes back only once MySession.Static.Ready reports
                // the next world is up.
                if (HudRedirector.Requested)
                {
                    _resumeRedirectMode = HudRedirector.Mode;
                    HudRedirector.Disable();
                    Log("session ended: HUD redirect suspended until the next world is ready");
                }
                return;
            }

            // A five-deep ModAPI property walk purely for a diagnostic log line does not need
            // simulation-frame cadence.
            if (++_viewContextPoll >= ViewContextPollTicks)
            {
                _viewContextPoll = 0;
                try { CleanFeedHealthTelemetry.ObserveViewContext(); }
                catch (Exception ex) { LogOnce("view telemetry", ex); }
            }

            if (!_chatHooked && _commands != null && MyAPIGateway.Utilities != null)
            {
                try
                {
                    MyAPIGateway.Utilities.MessageEntered += _commands.OnMessage;
                    _chatHooked = true;
                }
                catch { /* session utilities can be transiently unavailable while a world opens */ }
            }

            try
            {
                HudProfileService.Update();
                HudRedirector.ObserveFocusPrivacy();
                PollAdapters();
            }
            catch (Exception ex) { LogOnce("profiles/adapters", ex); }
            try { MaybeResumeRedirect(); }
            catch (Exception ex) { LogOnce("redirect-resume", ex); }
            try { MaybeAutoEnable(); }
            catch (Exception ex) { LogOnce("auto-redirect", ex); }
        }

        // MyAPIGateway.Session comes back well before the world is playable, so readiness - not
        // session presence - is what gates arming the redirect.
        private static bool SessionReady()
        {
            try
            {
                return Sandbox.Game.World.MySession.Static != null
                       && Sandbox.Game.World.MySession.Static.Ready;
            }
            catch { return false; }
        }

        // Restores the redirect that the session-end teardown suspended, once the next world is up.
        // Mirrors MaybeAutoEnable's preconditions so the profile and adapters are in place before
        // anything is routed again.
        private void MaybeResumeRedirect()
        {
            if (_resumeRedirectMode == HudRedirectMode.Off) return;
            if (!_chatHooked || !_renderPatchesReady || !SessionReady()) return;

            HudRedirectMode mode = _resumeRedirectMode;
            _resumeRedirectMode = HudRedirectMode.Off;
            // The resume supersedes auto-enable for this session; it already carries the operator's
            // last choice, including a whole-HUD redirect that auto-enable would not have picked.
            _autoRedirectAttempted = true;
            Log("session ready: resuming HUD redirect");
            if (mode == HudRedirectMode.Selective) EnableHudSelective();
            else EnableHudRedirect();
        }

        // Starts the selective redirect once per session when the operator has opted in. It waits for
        // the chat hook and the render patches, so the profile has been loaded and the adapters
        // scanned before anything is routed.
        private void MaybeAutoEnable()
        {
            // SessionReady is what keeps the redirect from arming against a loading screen.
            if (_autoRedirectAttempted || !_chatHooked || !_renderPatchesReady || !SessionReady()) return;
            if (HudRedirector.Requested)
            {
                _autoRedirectAttempted = true;
                return;
            }
            // Not marked attempted: the profile may not have loaded yet, and a false here is
            // indistinguishable from an opt-out until it has.
            if (!HudProfileService.AutoRedirectEnabled) return;

            _autoRedirectAttempted = true;
            Log("auto-enable: starting selective HUD redirect");
            EnableHudSelective();
        }

        // AppDomain.GetAssemblies allocates an array, and neither the adapter scan nor provider
        // discovery needs simulation-frame cadence. This is the single poll that drives both.
        private void PollAdapters()
        {
            Assembly[] assemblies = null;
            bool changed = false;
            if (++_assemblyPoll >= AssemblyPollTicks)
            {
                _assemblyPoll = 0;
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
                changed = assemblies.Length != _assemblyCount;
                _assemblyCount = assemblies.Length;
            }
            HudAdapterCoordinator.Update(assemblies, changed);
        }



        internal void EnableHudRedirect()
        {
            if (!_renderPatchesReady)
            {
                Notify.Chat("HUD redirect unavailable; see SpaceEngineers.log");
                return;
            }

            if (!HudRedirector.Enable(out string reason))
            {
                Notify.Chat("HUD redirect blocked: " + reason);
                Log("HUD redirect blocked: " + reason);
                return;
            }

            Notify.Hud("CleanFeed GPU HUD redirect starting. Vanilla HUD remains until the surface is ready.", 5500);
            Log("GPU HUD redirect requested");
        }

        internal void EnableHudSelective()
        {
            if (!_renderPatchesReady)
            {
                Notify.Chat("selective HUD unavailable; see SpaceEngineers.log");
                return;
            }

            HudProfileService.Update();
            if (!HudRedirector.EnableSelective(out string reason))
            {
                Notify.Chat("selective HUD blocked: " + reason);
                Log("selective HUD blocked: " + reason);
                return;
            }

            Notify.Hud("CleanFeed selective HUD starting. Applying recording, player, and focus-privacy policies.", 5500);
            Log("selective GPU HUD requested; " + HudProfileService.StatusLine());
        }

        internal void DisableHudRedirect()
        {
            HudRedirector.Disable();
            Notify.Hud("CleanFeed HUD redirect OFF. Vanilla HUD restored.", 3500);
            Log("HUD redirect disabled");
        }

        internal string HudStatusLine()
            => HudRedirector.StatusLine() + "; " + HudProfileService.StatusLine()
               + "; " + HudAdapterCoordinator.StatusLine()
               + "; " + HudBillboardProjector.StatusLine()
               + "; " + CleanFeedHealthTelemetry.StatusLine();

        public string StatusLine() => HudStatusLine();

        internal void OpenSettings()
        {
            try { MyGuiSandbox.AddScreen(new CleanFeedSettingsScreen()); }
            catch (Exception ex)
            {
                Log("settings screen failed: " + ex);
                Notify.Hud("CleanFeed settings failed to open; see SpaceEngineers.log", 5000);
            }
        }

        // Pulsar calls this by reflection for the plugin Settings action.
        public void OpenConfigDialog() => OpenSettings();

        public void Dispose()
        {
            try
            {
                if (_chatHooked && _commands != null && MyAPIGateway.Utilities != null)
                    MyAPIGateway.Utilities.MessageEntered -= _commands.OnMessage;
            }
            catch { }
            try { Sandbox.Engine.Networking.MyGameService.OnOverlayActivated -= OnGameOverlayActivated; }
            catch { }

            try
            {
                HudRedirector.Shutdown();
                HudAdapterCoordinator.Shutdown();
                HudProfileService.ResetSession();
                // This overload removes only patches owned by CleanFeed's Harmony ID.
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch (Exception ex) { Log("render patch dispose failed: " + ex); }
            CleanFeedHealthTelemetry.Shutdown();
            Instance = null;
            Log("Dispose");
        }

        private static void OnGameOverlayActivated(bool active)
        {
            try { HudRedirector.SetOverlayHold(active); }
            catch (Exception ex) { LogOnce("overlay hold", ex); }
        }

        // Per-step rather than a single last-error slot: two steps failing on alternate ticks would
        // defeat a shared slot and write a full exception to the log every frame.
        private static readonly System.Collections.Generic.Dictionary<string, string> LastStepErrors =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);

        private static void LogOnce(string step, Exception ex)
        {
            string message = ex.Message;
            lock (LastStepErrors)
            {
                if (LastStepErrors.TryGetValue(step, out string previous)
                    && string.Equals(previous, message, StringComparison.Ordinal))
                    return;
                LastStepErrors[step] = message;
            }
            Log(step + " failed (logged once): " + ex);
        }

        public static void Log(string message)
            => MyLog.Default?.WriteLineAndConsole("[" + Id + "] " + message);
    }
}
