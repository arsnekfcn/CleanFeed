using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using CleanFeed.Render;
using VRage.Utils;

namespace CleanFeed.Compatibility
{
    internal static class CompatibilityReportService
    {
        internal const string IssuesUrl = "https://github.com/arsnekfcn/CleanFeed/issues/new";

        internal static string BuildReport(HudSourceDescriptor source = null)
        {
            var text = new StringBuilder();
            text.AppendLine("CleanFeed compatibility report");
            text.AppendLine("cleanfeed_build=" + typeof(Plugin).Assembly.ManifestModule.ModuleVersionId
                .ToString("N").Substring(0, 8));
            text.AppendLine("redirect_requested=" + HudRedirector.Requested);
            text.AppendLine("redirect_mode=" + HudRedirector.Mode);
            text.AppendLine("privacy_active=" + HudRedirector.PrivacySuppressionActive);
            text.AppendLine("registry=" + HudSourceRegistry.StatusLine());
            text.AppendLine("discovery=" + HudSourceDiscovery.ReportStatusLine());

            if (source != null)
            {
                AppendSource(text, source);
            }
            else
            {
                foreach (HudSourceDescriptor item in HudSourceRegistry.Snapshot().Sources
                             .Where(s => s.Provenance != "verified"
                                         || s.Capability == HudSourceCapability.ObserveOnly))
                    AppendSource(text, item);
            }
            return text.ToString();
        }

        private static void AppendSource(StringBuilder text, HudSourceDescriptor source)
        {
            text.AppendLine();
            text.AppendLine("source=" + Safe(source.DisplayName, 100));
            text.AppendLine("provider_id=" + Safe(source.ProviderId, 120));
            text.AppendLine("provider_version=" + Safe(source.ProviderVersion, 40));
            text.AppendLine("category=" + Safe(source.CategoryId, 140));
            text.AppendLine("provenance=" + source.Provenance);
            text.AppendLine("capability=" + HudSourceRegistry.CapabilityName(source.Capability));
            text.AppendLine("active=" + source.Active);
            text.AppendLine("supported=" + Safe(source.SupportedPrimitives, 180));
            text.AppendLine("unsupported=" + Safe(source.UnsupportedPrimitives, 180));
            text.AppendLine("recording=" + (source.RecordingVisible ? "recorded" : "filtered"));
            text.AppendLine("player=" + (source.PlayerVisible ? "shown" : "hidden"));
            text.AppendLine("effective=" + HudSourceRegistry.RouteName(source.EffectiveRoute));
            text.AppendLine("activity=" + source.ActivityCount);
            text.AppendLine("last_seen_ms=" + source.LastSeenMilliseconds);
            text.AppendLine("detail=" + Safe(source.Detail, 240));
        }

        internal static bool CopyReport(HudSourceDescriptor source, out string error)
        {
            try
            {
                MyClipboardHelper.SetClipboard(BuildReport(source));
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + Safe(ex.Message, 100);
                return false;
            }
        }

        internal static bool OpenIssue(HudSourceDescriptor source, out string error)
        {
            string copyError;
            CopyReport(source, out copyError);
            try
            {
                string title = source == null
                    ? "Compatibility request: unknown HUD provider"
                    : "Compatibility request: " + Safe(source.DisplayName, 70);
                string body = "CleanFeed build: "
                              + typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 8)
                              + "\nProvider: " + (source == null ? "unknown" : Safe(source.DisplayName, 80))
                              + "\nCapability: " + (source == null ? "unknown" : HudSourceRegistry.CapabilityName(source.Capability))
                              + "\n\nA sanitized diagnostic report has been copied to the clipboard. Please paste it here and describe what remains visible or flickers.";
                string url = IssuesUrl + "?labels=compatibility&title=" + Uri.EscapeDataString(title)
                             + "&body=" + Uri.EscapeDataString(body);
                // UseShellExecute hands the string to the shell, so the string that is actually
                // launched is what has to be proven to be an https URL - not the constant it was
                // built from. Title and body are escaped, but the check belongs on the result.
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri destination)
                    || destination.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("issue destination is not HTTPS");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                error = copyError;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + Safe(ex.Message, 100)
                        + (copyError == null ? string.Empty : "; clipboard: " + copyError);
                return false;
            }
        }

        private static string Safe(string value, int limit)
            => HudSourceRegistry.Sanitize(value, limit);
    }
}
