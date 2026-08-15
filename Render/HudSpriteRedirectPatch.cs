using System;
using System.Diagnostics;
using System.Threading;
using CleanFeed.Diagnostics;
using HarmonyLib;
using SharpDX.Mathematics.Interop;
using VRage.Render11.Common;
using VRage.Render11.Resources;
using VRage.Render11.Sprites;
using VRageMath;
using VRageRender;

namespace CleanFeed.Render
{
    internal static class HudOffscreenSlot
    {
        public static IBorrowedRtvTexture Current { get; private set; }

        public static void Replace(IBorrowedRtvTexture texture)
        {
            ReleaseAndClear();
            Current = texture;
            if (texture != null) HudResourceTelemetry.WholeBorrowed();
        }

        public static void ReleaseAndClear()
        {
            IBorrowedRtvTexture current = Current;
            Current = null;
            if (current == null) return;
            try { current.Release(); }
            finally { HudResourceTelemetry.WholeReleased(); }
        }
    }

    internal static class HudSpriteBatchState
    {
        private static int _messageCount = -1;

        public static void Begin() => Volatile.Write(ref _messageCount, -1);

        public static void Record(int messageCount)
            => Volatile.Write(ref _messageCount, Math.Max(0, messageCount));

        public static int Read() => Volatile.Read(ref _messageCount);

        public static void Reset() => Volatile.Write(ref _messageCount, -1);
    }

    [HarmonyPatch(typeof(MyRender11), nameof(MyRender11.RenderMainSprites), new Type[0])]
    internal static class RenderMainSpritesPatch
    {
        private static bool Prefix()
        {
            Vector2I resolution = MyRender11.ViewportResolution;
            bool ready = HudRedirector.TryBeginFrame(resolution.X, resolution.Y);
            HudRedirectMode mode = HudRedirector.Mode;

            bool privacyDiscard = HudRedirector.PrivacySuppressionActive;
            if (!privacyDiscard && (mode != HudRedirectMode.Whole || !ready)) return true;

            try
            {
                var layer = MyManagers.RwTexturesPool.BorrowRtv(
                    privacyDiscard ? "CleanFeed.PrivacyDiscard" : "CleanFeed.HudOffscreen",
                    resolution.X, resolution.Y,
                    HudGpuTransport.LayerFormat);
                HudOffscreenSlot.Replace(layer);
                HudSpriteBatchState.Begin();
                MyImmediateRC.RC.ClearRtv(layer, new RawColor4(0, 0, 0, 0));

                var viewport = new MyViewport(resolution.X, resolution.Y);
                MyRender11.RenderMainSprites(
                    layer,
                    MyRender11.ScaleMainViewport(viewport),
                    viewport,
                    new Vector2(resolution.X, resolution.Y));
                if (privacyDiscard) HudRedirector.CountPrivacyDiscardFrame();
                return false;
            }
            catch (Exception ex)
            {
                HudOffscreenSlot.ReleaseAndClear();
                HudRedirector.FailOpen("RenderMainSprites", ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(MyRender11), "RenderMainSpritesWorker")]
    internal static class RenderMainSpritesWorkerPatch
    {
        private static void Prefix(
            IRtvBindable rtv,
            MySpriteMessageData defaultMessages,
            MySpriteMessageData debugMessages)
        {
            if (!ReferenceEquals(rtv, HudOffscreenSlot.Current)) return;
            int defaultCount = defaultMessages?.Messages?.Count ?? 0;
            int debugCount = debugMessages?.Messages?.Count ?? 0;
            HudSpriteBatchState.Record(defaultCount + debugCount);
        }
    }

    [HarmonyPatch(typeof(MyRender11), nameof(MyRender11.ConsumeMainSprites), new Type[0])]
    internal static class ConsumeMainSpritesPatch
    {
        private static void Postfix()
        {
            IBorrowedRtvTexture layer = HudOffscreenSlot.Current;
            try
            {
                if (layer != null && HudRedirector.WholeActive)
                    HudRedirector.CopyCompletedHud(layer, HudSpriteBatchState.Read());
            }
            catch (Exception ex)
            {
                HudRedirector.FailOpen("ConsumeMainSprites/GPU copy", ex);
            }
            finally
            {
                HudSpriteBatchState.Reset();
                HudOffscreenSlot.ReleaseAndClear();
                // Acquire the private queue only after vanilla has joined its asynchronous
                // main-sprite worker. Acquiring it in RenderMainSprites' prefix splits one logical
                // producer update across adjacent composition frames, which strobes the whole
                // redirected HUD under large mod HUD workloads.
                Vector2I resolution = MyRender11.ViewportResolution;
                bool publishSelective = HudRedirector.SelectiveActive;
                HudNamedSpriteFrame.Prepare(resolution, publishSelective);
                HudNamedSpriteFrame.Complete();
                HudNamedSpriteRoutes.Process(resolution, publishSelective);
                HudSuppressedSpriteQueue.Drain();
            }
        }

        // Harmony postfixes do not run when the original throws. The finalizer guarantees that a
        // borrowed VRage render target cannot be stranded across frames.
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            HudSpriteBatchState.Reset();
            HudOffscreenSlot.ReleaseAndClear();
            HudNamedSpriteFrame.ReleaseAndClear();
            HudRedirector.FailOpen("ConsumeMainSprites/original", __exception);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(MyRender11), nameof(MyRender11.Present), new Type[0])]
    internal static class PresentPatch
    {
        private static void Prefix()
        {
            HudRedirector.OnPresent();
        }
    }
}
