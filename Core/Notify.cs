using System;
using Sandbox;
using Sandbox.ModAPI;

namespace CleanFeed.Core
{
    internal static class Notify
    {
        private static void OnMain(Action action)
        {
            try
            {
                MySandboxGame.Static?.Invoke(action, "CleanFeed");
            }
            catch (Exception ex) { Plugin.Log("notification failed: " + ex.Message); }
        }

        public static void Hud(string text, int milliseconds = 3000)
            => OnMain(() => { try { MyAPIGateway.Utilities?.ShowNotification(text, milliseconds); } catch { } });

        public static void Chat(string text)
            => OnMain(() => { try { MyAPIGateway.Utilities?.ShowMessage("CleanFeed", text); } catch { } });
    }
}
