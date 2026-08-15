using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CleanFeed.Compatibility;
using CleanFeed.Core;
using CleanFeed.Profiles;
using CleanFeed.Render;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace CleanFeed.Screens
{
    public sealed class CleanFeedSettingsScreen : MyGuiScreenBase
    {
        private sealed class PendingPolicy
        {
            internal bool Recording;
            internal bool Player;
        }

        private static readonly Vector4 Accent = new Vector4(0.42f, 0.88f, 0.93f, 1f);
        private static readonly Vector4 Good = new Vector4(0.40f, 0.90f, 0.52f, 1f);
        private static readonly Vector4 Warn = new Vector4(1f, 0.72f, 0.25f, 1f);
        private static readonly Vector4 Muted = new Vector4(0.70f, 0.78f, 0.80f, 1f);
        private static readonly Vector4 Bad = new Vector4(1f, 0.42f, 0.35f, 1f);
        private readonly Dictionary<string, PendingPolicy> _pending =
            new Dictionary<string, PendingPolicy>(StringComparer.OrdinalIgnoreCase);

        private HudSourceRegistrySnapshot _registry;
        private bool _globalRecording;
        private bool _globalPlayer;
        private bool _privacy;
        private bool _autoRedirect;
        private bool _dirty;
        private bool _rebuildRequested;
        private int _updateCounter;
        private MyGuiControlButton _recordAllButton;
        private MyGuiControlButton _playerAllButton;
        private MyGuiControlButton _privacyButton;
        private MyGuiControlButton _autoRedirectButton;

        public CleanFeedSettingsScreen()
            : base(position: new Vector2(0.5f, 0.5f),
                backgroundColor: MyGuiConstants.SCREEN_BACKGROUND_COLOR,
                size: new Vector2(0.92f, 0.94f),
                isTopMostScreen: false,
                backgroundTexture: MyGuiConstants.TEXTURE_SCREEN_BACKGROUND.Texture)
        {
            EnabledBackgroundFade = true;
            CloseButtonEnabled = true;
            CaptureCurrentState();
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "CleanFeedSettingsScreen";

        public override bool Update(bool hasFocus)
        {
            bool result = base.Update(hasFocus);
            // Rebuilds are deferred to here rather than run inside a button's click handler, which
            // would dispose and replace the Controls collection the GUI framework is iterating to
            // dispatch that very click.
            if (_rebuildRequested)
            {
                _rebuildRequested = false;
                RecreateControls(false);
            }
            if (++_updateCounter >= 120)
            {
                _updateCounter = 0;
                if (!_dirty && _registry != null && _registry.Generation != HudSourceRegistry.Generation)
                {
                    CaptureCurrentState();
                    _rebuildRequested = true;
                }
            }
            return result;
        }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);

            AddCaption("CleanFeed Settings", null, new Vector2(0f, -0.012f), 0.82f);
            AddCentered("Capture privacy and player HUD routing", -0.392f, Muted, 0.72f);
            string redirect = HudRedirector.Requested
                ? "Redirect: " + HudRedirector.Mode + " / "
                  + (HudRedirector.PrivacySuppressionActive ? "privacy active" : "ready")
                : "Redirect: off";
            AddCentered(redirect + "    |    " + HudSourceRegistry.StatusLine(), -0.355f,
                HudRedirector.Requested ? Good : Muted, 0.68f);

            _privacyButton = MakeButton(PrivacyText(), new Vector2(-0.3375f, -0.302f), new Vector2(0.22f, 0.043f),
                _ => TogglePrivacy());
            _autoRedirectButton = MakeButton(AutoRedirectText(), new Vector2(-0.1125f, -0.302f), new Vector2(0.22f, 0.043f),
                _ => ToggleAutoRedirect());
            _recordAllButton = MakeButton(RecordAllText(), new Vector2(0.1125f, -0.302f), new Vector2(0.22f, 0.043f),
                _ => ToggleRecordAll());
            _playerAllButton = MakeButton(PlayerAllText(), new Vector2(0.3375f, -0.302f), new Vector2(0.22f, 0.043f),
                _ => TogglePlayerAll());
            Controls.Add(_privacyButton);
            Controls.Add(_autoRedirectButton);
            Controls.Add(_recordAllButton);
            Controls.Add(_playerAllButton);

            var list = new MyGuiControlList(new Vector2(0f, 0.015f), new Vector2(0.86f, 0.56f),
                null, null, MyGuiControlListStyleEnum.Default);
            var rows = new List<MyGuiControlBase>();
            AddSection(rows, "VERIFIED SOURCES", "Explicit CleanFeed adapters and native HUD categories", "verified");
            AddSourceRows(rows, "verified");
            AddSection(rows, "DETECTED BEST-EFFORT", "Runtime candidates; partial controls are opt-in", "detected");
            AddSourceRows(rows, "detected");
            AddSection(rows, "UNSUPPORTED / UNATTRIBUTED", "Visible for diagnostics and compatibility requests", "unattributed");
            AddUnsupportedRows(rows);
            list.InitControls(rows);
            Controls.Add(list);

            MakeFooterButtons();
        }

        private void CaptureCurrentState()
        {
            HudProfileSnapshot profile = HudProfileService.Current;
            _globalRecording = profile.GlobalRecordingVisible;
            _globalPlayer = profile.GlobalPlayerVisible;
            _privacy = profile.UnfocusedPrivacyEnabled;
            _autoRedirect = HudProfileService.AutoRedirectEnabled;
            _registry = HudSourceRegistry.Snapshot();
            _pending.Clear();
            foreach (HudSourceDescriptor source in _registry.Sources)
                if (source.Controllable)
                    _pending[source.PolicyKey] = new PendingPolicy
                    {
                        Recording = source.RecordingVisible,
                        Player = source.PlayerVisible
                    };
            _dirty = false;
        }

        private void AddSection(List<MyGuiControlBase> rows, string title, string subtitle, string provenance)
        {
            int count = provenance == "unattributed"
                ? _registry.Sources.Count(s => s.Provenance == "unattributed"
                                               || s.Capability == HudSourceCapability.ObserveOnly)
                : _registry.Sources.Count(s => s.Provenance == provenance
                                               && s.Capability != HudSourceCapability.ObserveOnly);
            var row = new MyGuiControlParent(size: new Vector2(0.82f, 0.058f),
                backgroundColor: new Vector4(0.06f, 0.11f, 0.13f, 0.85f));
            row.Controls.Add(new MyGuiControlLabel(new Vector2(-0.39f, -0.014f), null,
                title + "  (" + count + ")", Accent, 0.73f)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });
            row.Controls.Add(new MyGuiControlLabel(new Vector2(-0.39f, 0.018f), null,
                subtitle, Muted, 0.58f)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });
            rows.Add(row);
        }

        private void AddSourceRows(List<MyGuiControlBase> rows, string provenance)
        {
            foreach (HudSourceDescriptor source in _registry.Sources.Where(s =>
                         s.Provenance == provenance && s.Capability != HudSourceCapability.ObserveOnly))
                rows.Add(BuildSourceRow(source));
        }

        private void AddUnsupportedRows(List<MyGuiControlBase> rows)
        {
            foreach (HudSourceDescriptor source in _registry.Sources.Where(s =>
                         s.Provenance == "unattributed" || s.Capability == HudSourceCapability.ObserveOnly))
                rows.Add(BuildSourceRow(source));
        }

        private MyGuiControlBase BuildSourceRow(HudSourceDescriptor source)
        {
            // Tall enough for the name line plus a two-line detail, so the row shows the whole
            // description instead of pushing its tail into the hover tooltip.
            var row = new MyGuiControlParent(size: new Vector2(0.82f, 0.086f),
                backgroundColor: new Vector4(0.025f, 0.055f, 0.065f, 0.72f),
                toolTip: BuildTooltip(source));
            Vector4 color = source.Provenance == "verified"
                ? Good
                : source.Capability == HudSourceCapability.ObserveOnly ? Bad : Warn;
            string status = source.Active ? HudSourceRegistry.CapabilityName(source.Capability) : "inactive";
            row.Controls.Add(new MyGuiControlLabel(new Vector2(-0.39f, -0.027f), null,
                Truncate(source.DisplayName, 38) + "  [" + status + "]", color, 0.67f)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });
            WrapDetail(RowDetail(source), out string detailFirst, out string detailSecond);
            row.Controls.Add(new MyGuiControlLabel(new Vector2(-0.39f, 0.001f), null,
                detailFirst, Muted, 0.54f)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });
            if (detailSecond.Length != 0)
                row.Controls.Add(new MyGuiControlLabel(new Vector2(-0.39f, 0.026f), null,
                    detailSecond, Muted, 0.54f)
                { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });

            // The Hand Terminal's hangar submenu is a list of GPS names; whoever is about to press
            // REC on this row needs to know that before the stream does. Sits above the buttons so
            // it cannot collide with the left-aligned text.
            if (string.Equals(source.PolicyKey, "gamecore", StringComparison.OrdinalIgnoreCase))
                row.Controls.Add(new MyGuiControlLabel(new Vector2(0.21f, -0.029f), null,
                    "THIS EXPOSES GPS", Bad, 0.72f)
                { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER });

            PendingPolicy policy = null;
            bool controllable = source.Controllable && _pending.TryGetValue(source.PolicyKey, out policy);
            MyGuiControlButton record = null;
            record = MakeButton(controllable && !policy.Recording ? "FILTER" : "REC",
                new Vector2(0.065f, -0.005f), new Vector2(0.13f, 0.042f), _ =>
                {
                    if (!controllable) return;
                    policy.Recording = !policy.Recording;
                    record.Text = policy.Recording ? "REC" : "FILTER";
                    _dirty = true;
                });
            record.SetToolTip(controllable
                ? "REC records this source. FILTER keeps supported portions player-only."
                : "Recording filter unavailable: " + source.UnsupportedPrimitives);
            record.Enabled = controllable;
            row.Controls.Add(record);

            MyGuiControlButton player = null;
            player = MakeButton(controllable && !policy.Player ? "HIDE" : "SHOW",
                new Vector2(0.215f, -0.005f), new Vector2(0.13f, 0.042f), _ =>
                {
                    if (!controllable) return;
                    policy.Player = !policy.Player;
                    player.Text = policy.Player ? "SHOW" : "HIDE";
                    _dirty = true;
                });
            player.SetToolTip(controllable
                ? "SHOW displays it to the player. HIDE suppresses supported portions everywhere."
                : "Player control unavailable: source is observe-only.");
            player.Enabled = controllable;
            row.Controls.Add(player);

            var report = MakeButton("REPORT", new Vector2(0.35f, -0.005f), new Vector2(0.11f, 0.042f),
                _ => Report(source));
            report.SetToolTip("Copy a sanitized report and open the CleanFeed compatibility request page.");
            row.Controls.Add(report);
            return row;
        }

        private void MakeFooterButtons()
        {
            var redirect = MakeButton(HudRedirector.Requested ? "Stop redirect" : "Start redirect",
                new Vector2(-0.32f, 0.335f), new Vector2(0.19f, 0.042f), _ =>
                {
                    if (HudRedirector.Requested) Plugin.Instance?.DisableHudRedirect();
                    else Plugin.Instance?.EnableHudSelective();
                    _rebuildRequested = true;
                });
            var rescan = MakeButton("Rescan sources", new Vector2(-0.105f, 0.335f), new Vector2(0.19f, 0.042f), _ =>
                {
                    HudSourceDiscovery.RequestRescan("settings");
                    if (!_dirty) CaptureCurrentState();
                    _rebuildRequested = true;
                    Notify.Hud("CleanFeed HUD source discovery restarted for 30 seconds.", 3500);
                });
            var copy = MakeButton("Copy diagnostics", new Vector2(0.105f, 0.335f), new Vector2(0.19f, 0.042f), _ =>
                {
                    if (CompatibilityReportService.CopyReport(null, out string error))
                        Notify.Hud("CleanFeed diagnostics copied to clipboard.", 3000);
                    else Notify.Hud("Diagnostic copy failed: " + error, 5000);
                });
            var issue = MakeButton("Request support", new Vector2(0.32f, 0.335f), new Vector2(0.19f, 0.042f), _ => Report(null));
            Controls.Add(redirect);
            Controls.Add(rescan);
            Controls.Add(copy);
            Controls.Add(issue);

            Controls.Add(MakeButton("Cancel", new Vector2(-0.15f, 0.392f), new Vector2(0.24f, 0.044f),
                _ => CloseScreen(false)));
            Controls.Add(MakeButton("Apply", new Vector2(0.15f, 0.392f), new Vector2(0.24f, 0.044f),
                _ => ApplyAndClose()));
        }

        private void TogglePrivacy()
        {
            _privacy = !_privacy;
            _privacyButton.Text = PrivacyText();
            _dirty = true;
        }

        private void ToggleAutoRedirect()
        {
            _autoRedirect = !_autoRedirect;
            _autoRedirectButton.Text = AutoRedirectText();
            _dirty = true;
        }

        private void ToggleRecordAll()
        {
            _globalRecording = !_globalRecording;
            foreach (PendingPolicy policy in _pending.Values) policy.Recording = _globalRecording;
            _recordAllButton.Text = RecordAllText();
            _dirty = true;
            _rebuildRequested = true;
        }

        private void TogglePlayerAll()
        {
            _globalPlayer = !_globalPlayer;
            foreach (PendingPolicy policy in _pending.Values) policy.Player = _globalPlayer;
            _playerAllButton.Text = PlayerAllText();
            _dirty = true;
            _rebuildRequested = true;
        }

        private void ApplyAndClose()
        {
            HudSourceRegistrySnapshot latest = HudSourceRegistry.Snapshot();
            var valid = new HashSet<string>(latest.Sources.Where(s => s.Controllable)
                .Select(s => s.PolicyKey), StringComparer.OrdinalIgnoreCase);
            if (_pending.Keys.Any(key => !valid.Contains(key)))
            {
                Notify.Hud("HUD sources changed while editing. Review the refreshed list and apply again.", 5000);
                CaptureCurrentState();
                _rebuildRequested = true;
                return;
            }

            HudProfileSnapshot current = HudProfileService.Current;
            var recording = current.RecordingOverrides.ToDictionary(kv => kv.Key, kv => kv.Value,
                StringComparer.OrdinalIgnoreCase);
            var player = current.PlayerOverrides.ToDictionary(kv => kv.Key, kv => kv.Value,
                StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, PendingPolicy> item in _pending)
            {
                recording[item.Key] = item.Value.Recording;
                player[item.Key] = item.Value.Player;
            }
            HudProfileService.ApplySettings(
                _globalRecording, _globalPlayer, _privacy, recording, player);
            // Persist only: turning this on here must not start a redirect mid-session, since the
            // screen can be open while the redirect is deliberately off. /cf auto on does that.
            if (_autoRedirect != HudProfileService.AutoRedirectEnabled)
                HudProfileService.SetAutoRedirect(_autoRedirect);
            HudRedirector.ObserveFocusPrivacy();
            Notify.Hud("CleanFeed settings applied.", 3000);
            CloseScreen(false);
        }

        private static void Report(HudSourceDescriptor source)
        {
            if (CompatibilityReportService.OpenIssue(source, out string error))
                Notify.Hud(error == null
                    ? "Compatibility report copied; opening GitHub."
                    : "Opening GitHub; clipboard warning: " + error, 5000);
            else
                Notify.Hud("Could not open compatibility request: " + error, 6000);
        }

        private string PrivacyText() => "Unfocused privacy: " + (_privacy ? "ON" : "OFF");
        private string AutoRedirectText() => "Auto-start redirect: " + (_autoRedirect ? "ON" : "OFF");
        private string RecordAllText() => "Recording default: " + (_globalRecording ? "REC" : "FILTER");
        private string PlayerAllText() => "Player default: " + (_globalPlayer ? "SHOW" : "HIDE");

        private static string RowDetail(HudSourceDescriptor source)
        {
            string route = HudSourceRegistry.RouteName(source.EffectiveRoute);
            string detail = source.Detail;
            if (string.IsNullOrEmpty(detail)) detail = source.UnsupportedPrimitives;
            return source.Provenance + " | " + route + " | " + detail;
        }

        private static string BuildTooltip(HudSourceDescriptor source)
        {
            return source.DisplayName + "\n"
                   + "ID: " + source.PolicyKey + "\n"
                   + "Provider version: " + source.ProviderVersion + "\n"
                   + "Capability: " + HudSourceRegistry.CapabilityName(source.Capability) + "\n"
                   + "Supported: " + source.SupportedPrimitives + "\n"
                   + "Possible leaks: " + source.UnsupportedPrimitives + "\n"
                   + source.Detail;
        }

        private void AddCentered(string text, float y, Vector4 color, float scale)
        {
            Controls.Add(new MyGuiControlLabel(new Vector2(0f, y), null, text, color, scale)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER });
        }

        private static MyGuiControlButton MakeButton(
            string text, Vector2 position, Vector2 size, Action<MyGuiControlButton> click)
        {
            return new MyGuiControlButton(position: position,
                visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
                size: size,
                text: new StringBuilder(text ?? string.Empty),
                onButtonClick: click);
        }

        private static string Truncate(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= limit ? value : value.Substring(0, Math.Max(1, limit - 1)) + "...";
        }

        // Word-boundary wrap into at most two lines. The per-line budget matches the footprint the
        // old single-line truncation occupied, so neither line can run under the button column; a
        // detail longer than two lines still ends in an ellipsis and the tooltip keeps the full
        // text.
        private static void WrapDetail(string value, out string first, out string second)
        {
            const int PerLine = 74;
            first = string.Empty;
            second = string.Empty;
            if (string.IsNullOrEmpty(value)) return;
            if (value.Length <= PerLine)
            {
                first = value;
                return;
            }
            int split = value.LastIndexOf(' ', Math.Min(PerLine, value.Length - 1));
            if (split < PerLine / 2) split = PerLine;
            first = value.Substring(0, split).TrimEnd();
            second = Truncate(value.Substring(split).TrimStart(), PerLine);
        }
    }
}
