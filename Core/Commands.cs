using System;
using CleanFeed.Compatibility;
using CleanFeed.Diagnostics;
using CleanFeed.Profiles;

namespace CleanFeed.Core
{
    internal sealed class Commands
    {
        private readonly Plugin _plugin;

        public Commands(Plugin plugin) { _plugin = plugin; }

        public void OnMessage(string messageText, ref bool sendToOthers)
        {
            if (string.IsNullOrWhiteSpace(messageText)) return;
            string text = messageText.Trim();
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0
                || (!parts[0].Equals("/cleanfeed", StringComparison.OrdinalIgnoreCase)
                    && !parts[0].Equals("/cf", StringComparison.OrdinalIgnoreCase))) return;

            sendToOthers = false;
            string command = parts.Length > 1 ? parts[1].ToLowerInvariant() : "help";

            try
            {
                switch (command)
                {
                    case "settings":
                    case "config":
                        _plugin.OpenSettings();
                        break;
                    case "sources":
                        Notify.Chat(HudSourceRegistry.StatusLine() + "; " + HudSourceDiscovery.StatusLine());
                        break;
                    case "rescan":
                        HudSourceDiscovery.RequestRescan("chat-command");
                        Notify.Hud("CleanFeed HUD source discovery restarted for 30 seconds.", 3500);
                        break;
                    case "hud":
                        HandleHud(parts);
                        break;
                    case "redirect":
                        _plugin.EnableHudSelective();
                        break;
                    case "whole":
                        _plugin.EnableHudRedirect();
                        break;
                    case "auto":
                        HandleAuto(parts);
                        break;
                    case "profile":
                        HandleProfile(parts, 2);
                        break;
                    case "player":
                        HandlePlayer(parts);
                        break;
                    case "privacy":
                        HandlePrivacy(parts);
                        break;
                    // Undocumented alias kept for muscle memory from earlier builds.
                    case "compat":
                        HandleProfile(parts, 2);
                        break;
                    case "all":
                        HandleAll(parts);
                        break;
                    case "terminal":
                    case "chat":
                    case "toolbar":
                    case "speed":
                    case "gps":
                    case "battery-fuel":
                    case "battery":
                    case "hudlcd":
                    case "flight-vector":
                    case "flight-hud":
                    case "thrust-beacon":
                    case "wc-radar":
                    case "weaponcore.reload":
                    case "weaponcore.radar":
                    case "flip-burn":
                    case "gamecore":
                    case "pda":
                        HandleDirectProfile(command, parts);
                        break;
                    case "off":
                        _plugin.DisableHudRedirect();
                        break;
                    case "status":
                        string status = _plugin.StatusLine();
                        Plugin.Log("status: " + status);
                        Notify.Chat(status);
                        break;
                    default:
                        Notify.Chat("/cf settings | redirect | auto on|off | hud <item> on|off | player <item> show|hide | privacy unfocused on|off");
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log("command failed: " + ex);
                Notify.Chat("command failed; see SpaceEngineers.log");
            }
        }

        private void HandleHud(string[] parts)
        {
            string action = parts.Length > 2 ? parts[2].ToLowerInvariant() : "status";
            switch (action)
            {
                case "redirect":
                case "selective":
                case "on":
                    _plugin.EnableHudSelective();
                    break;
                case "whole":
                    _plugin.EnableHudRedirect();
                    break;
                case "framelock":
                case "frame-lock":
                case "adaptive":
                    Notify.Chat("frame-lock was retired; /cleanfeed hud redirect now uses GPU composition");
                    break;
                case "off":
                    _plugin.DisableHudRedirect();
                    break;
                case "status":
                    string status = _plugin.HudStatusLine();
                    Plugin.Log("HUD status: " + status);
                    Notify.Chat(status);
                    break;
                default:
                    string key = HudProfileService.NormalizeKey(action);
                    if (key == "all" || HudProfileService.IsKnownKey(key))
                        HandleFilter(parts, 2);
                    else
                        Notify.Chat("/cf hud all|<item> on|off|status | redirect | whole | off");
                    break;
            }
        }


        private void HandleAuto(string[] parts)
        {
            string action = parts.Length > 2 ? parts[2].ToLowerInvariant() : "status";
            if (action == "status")
            {
                Notify.Chat("auto-redirect=" + (HudProfileService.AutoRedirectEnabled ? "on" : "off"));
                return;
            }
            if (!TryOnOff(action, out bool enabled))
            {
                Notify.Chat("/cf auto on|off|status");
                return;
            }

            HudProfileService.SetAutoRedirect(enabled);
            Notify.Chat(enabled
                ? "auto-redirect=on (selective redirect starts each session)"
                : "auto-redirect=off");
            // Turning it on mid-session applies immediately; the session hook only arms on entry.
            if (enabled && !CleanFeed.Render.HudRedirector.Requested) _plugin.EnableHudSelective();
        }

        private static void HandleFilter(string[] parts, int keyIndex)
        {
            string key = parts[keyIndex];
            string action = parts.Length > keyIndex + 1 ? parts[keyIndex + 1].ToLowerInvariant() : "status";
            if (action == "status")
            {
                Notify.Chat(HudProfileService.NormalizeKey(key) == "all"
                    ? HudProfileService.FilterStatusLine()
                    : HudProfileService.DescribeFilter(key));
                return;
            }
            if (!TryOnOff(action, out bool enabled))
            {
                Notify.Chat("/cf hud " + key + " on|off|status");
                return;
            }

            // User-facing HUD switches control the CleanFeed filter. Filter ON means the
            // item stays player-visible but is not recording-visible.
            bool recordingVisible = !enabled;
            if (HudProfileService.NormalizeKey(key) == "all")
            {
                HudProfileService.SetAll(recordingVisible);
                Notify.Chat(HudProfileService.FilterStatusLine());
                return;
            }
            if (HudProfileService.TrySet(key, recordingVisible, out _))
                Notify.Chat(HudProfileService.DescribeFilter(key));
            else
                Notify.Chat("unknown HUD filter: " + key);
        }

        private static void HandleProfile(string[] parts, int keyIndex)
        {
            if (parts.Length <= keyIndex || parts[keyIndex].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                Notify.Chat(HudProfileService.StatusLine());
                return;
            }

            string key = parts[keyIndex];
            string action = parts.Length > keyIndex + 1 ? parts[keyIndex + 1].ToLowerInvariant() : "status";
            if (HudProfileService.NormalizeKey(key) == "all")
            {
                if (!TryOnOff(action, out bool allValue))
                {
                    Notify.Chat("/cf profile all on|off");
                    return;
                }
                HudProfileService.SetAll(allValue);
                Notify.Chat(HudProfileService.StatusLine());
                return;
            }
            if (action == "status")
            {
                Notify.Chat(HudProfileService.Describe(key));
                return;
            }
            if (!TryOnOff(action, out bool value))
            {
                Notify.Chat("/cf profile <item> on|off|status");
                return;
            }
            // TrySet's out parameter carries either the new state or the failure reason, so both
            // outcomes report through the same channel.
            HudProfileService.TrySet(key, value, out string result);
            Notify.Chat(result);
        }

        private static void HandlePlayer(string[] parts)
        {
            if (parts.Length < 3 || parts[2].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                Notify.Chat(HudProfileService.PlayerStatusLine());
                return;
            }

            string key = HudProfileService.NormalizeKey(parts[2]);
            string action = parts.Length > 3 ? parts[3].ToLowerInvariant() : "status";
            if (action == "status")
            {
                Notify.Chat(key == "all"
                    ? HudProfileService.PlayerStatusLine()
                    : HudProfileService.DescribePlayer(key));
                return;
            }

            bool visible;
            if (action == "show" || action == "shown") visible = true;
            else if (action == "hide" || action == "hidden") visible = false;
            else
            {
                Notify.Chat("/cf player all|<item> show|hide|status");
                return;
            }

            string result;
            if (key == "all")
            {
                HudProfileService.SetAllPlayer(visible);
                result = HudProfileService.PlayerStatusLine();
            }
            else if (!HudProfileService.TrySetPlayer(key, visible, out result))
            {
                Notify.Chat(result);
                return;
            }

            // Chat or terminal may have just hidden the command-response path itself.
            Notify.Hud("CleanFeed " + (visible ? "showing " : "hiding ")
                       + (key == "all" ? "all supported HUD" : key), 4000);
            Plugin.Log("player HUD policy: " + result);
        }

        private static void HandlePrivacy(string[] parts)
        {
            int actionIndex = parts.Length > 2
                              && parts[2].Equals("unfocused", StringComparison.OrdinalIgnoreCase)
                ? 3
                : 2;
            string action = parts.Length > actionIndex ? parts[actionIndex].ToLowerInvariant() : "status";
            if (action == "status")
            {
                Notify.Chat(HudProfileService.PrivacyStatusLine()
                            + ", active=" + CleanFeed.Render.HudRedirector.PrivacySuppressionActive);
                return;
            }
            if (!TryOnOff(action, out bool enabled))
            {
                Notify.Chat("/cf privacy unfocused on|off|status");
                return;
            }
            HudProfileService.SetUnfocusedPrivacy(enabled);
            CleanFeed.Render.HudRedirector.ObserveFocusPrivacy();
            string result = HudProfileService.PrivacyStatusLine();
            Notify.Hud("CleanFeed unfocused privacy " + (enabled ? "ON" : "OFF"), 4000);
            Plugin.Log(result);
        }

        private static void HandleAll(string[] parts)
        {
            string action = parts.Length > 2 ? parts[2].ToLowerInvariant() : "status";
            if (!TryOnOff(action, out bool value))
            {
                Notify.Chat(HudProfileService.StatusLine());
                return;
            }
            // `/cf all on` is a filter switch, so it inverts into recording visibility. HandleProfile
            // passes the raw value instead: /cf profile already speaks in recording visibility.
            HudProfileService.SetAll(!value);
            Notify.Chat(HudProfileService.FilterStatusLine());
        }

        private static void HandleDirectProfile(string command, string[] parts)
        {
            string action = parts.Length > 2 ? parts[2].ToLowerInvariant() : "status";
            HandleFilter(new[] { "/cf", "hud", command, action }, 2);
        }

        private static bool TryOnOff(string value, out bool result)
        {
            if (value.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }
            if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                return true;
            }
            result = false;
            return false;
        }

    }
}
