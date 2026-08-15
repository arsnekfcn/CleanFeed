using System;
using System.Threading;
using CleanFeed.Compatibility;
using CleanFeed.Profiles;
using HarmonyLib;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace CleanFeed.Render
{
    // Restores vanilla z-order for a Recorded element drawn NESTED inside a player-only scope.
    //
    // The bottom-left panel is the live case: with `speed` recorded and `battery-fuel` player-only,
    // the panel's plate sprites are rewritten to the player-only layer (DirectComposition, over the
    // game window) while the speed gauge nested inside that panel stays on the backbuffer, under it.
    // Vanilla draws the plate first and the gauge on top; split across two surfaces the order
    // inverts, and the semi-transparent plate dims the gauge on the live view. The general shape:
    // any Recorded-routed draw inside a player-only scope loses its z-order for the player.
    //
    // The fix is an echo, not a re-route. The original call still runs and still lands on the
    // backbuffer, so the recorder sees exactly what it saw before; the player additionally gets a
    // copy composited into the enclosing scope's layer. Because the echo is enqueued at the point of
    // the nested Draw call, it lands in that layer's sprite bucket between the plate draws and the
    // panel's scissor pop - so in-bucket order is exactly vanilla order, with no sorting needed.
    //
    // Why a postfix and not a prefix: the original must run first, so the backbuffer copy the
    // recorder needs exists before the echo is enqueued, and the echo lands after it rather than
    // before. A prefix would also have to decide whether to skip the original, which is never what
    // this wants.
    //
    // Re-entrancy: the echo re-invokes the same public MyRenderProxy method, which re-enters both
    // NativeSpriteTargetPatch's prefix (harmless - it only claims calls that left targetTexture
    // null, and the echo passes an explicit target) and these postfixes (which would otherwise echo
    // the echo). A [ThreadStatic] _echoing guard closes that loop, matching the _replaying pattern
    // in HudBillboardProjector.
    //
    // Scissor calls are deliberately NOT echoed. SpriteScissorPush/Pop issued by the enclosing
    // player-only scope are already rewritten onto the layer by NativeSpriteTargetPatch, so the
    // clip rect the echo needs is present; echoing a nested scope's scissors would double-push onto
    // that same layer and leave the stack unbalanced.
    //
    // Cost: one extra sprite enqueue, and only for a recorded draw nested under a player-only scope
    // (today: the speed gauge). Every common path - interception off, no scope, no parent scope,
    // parent not player-only, current not recorded, target already explicit - exits on a branch
    // before anything is allocated or enqueued.
    internal static class HudRecordedEcho
    {
        [ThreadStatic] private static bool _echoing;
        private static long _echoed;

        // Returns true with the enclosing scope's target when this draw needs an echo, and leaves
        // the guard set - the caller must pair every true with End() in a finally.
        //
        // The volatile interception check comes first, ahead of the [ThreadStatic] re-entrancy
        // guard, because it is the cheaper and far more selective gate: it is false for every draw
        // in the game while CleanFeed is off, and this runs on every sprite and string draw. The
        // reorder is safe because an echo is only ever in flight while interception is on - the
        // guard is set by this method, which cannot be reached with interception off - so no state
        // the guard protects can exist when the first check declines. A flip between the two reads
        // is equally harmless: an interception-off read simply declines the nested call, which is
        // what the guard would have done anyway, and End() in the caller's finally still runs.
        internal static bool TryBegin(string targetTexture, out string echoTarget)
        {
            echoTarget = null;
            if (!HudRedirector.SelectiveInterceptionActive) return false;
            if (_echoing) return false;
            // An explicit target means the call was already routed by its producer - to a CleanFeed
            // layer by NativeSpriteTargetPatch's prefix, or to a mod's own render target. Either
            // way it is not a backbuffer draw and there is nothing to reorder.
            if (targetTexture != null) return false;
            HudRouteTag tag = HudRouteContext.Current;
            if (tag == null) return false;
            if (HudRedirector.ResolveRoute(tag) != HudEffectiveRoute.Recorded) return false;
            HudRouteTag parent = HudRouteContext.CurrentParent;
            if (parent == null) return false;
            if (HudRedirector.ResolveRoute(parent) != HudEffectiveRoute.PlayerOnly) return false;

            // Guard-set ordering is load bearing: every fallible call above happens before
            // _echoing is raised, and the only statement between raising it and returning true is
            // an Interlocked increment. A throw out of this method therefore always leaves the
            // guard clear, so it can never latch on a path where the caller never sees true and
            // never runs the paired End().
            echoTarget = HudNamedSpriteRoutes.TargetFor(parent);
            _echoing = true;
            Interlocked.Increment(ref _echoed);
            return true;
        }

        internal static void End() => _echoing = false;

        // Surfaced through HudRedirector.StatusLine, alongside the other per-subsystem chains.
        internal static string StatusLine() => "echo=" + Interlocked.Read(ref _echoed);
    }

    // One patch class per overload, each with a typed postfix binding the full parameter list.
    // Never Harmony's object[] __args: it is materialised before the postfix body runs, so it would
    // allocate an array plus a box per argument on every sprite and string draw in the game -
    // including while CleanFeed is switched off and TryBegin returns on its first branch.
    //
    // MyRenderProxy declares exactly one overload of each of these five names, but the argument
    // types are still spelled out so a signature change fails the patch scan loudly instead of
    // silently binding a different method.
    //
    // Every postfix body - the TryBegin call included - is wrapped in a catch-all swallow. These
    // sit on the hottest draw methods in the game, and TryBegin reaches route resolution, which
    // reads the published profile snapshot and the redirector's state and is not provably
    // non-throwing. What the echo buys is cosmetic z-order for one nested case, so a fault here
    // must cost exactly that and nothing else: an escaping exception would be raised out of the
    // patched method on every sprite the game draws. Silence is the correct failure mode, and the
    // echo counter in StatusLine is what shows whether the path is running at all.

    [HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.DrawSprite),
        new[]
        {
            typeof(string), typeof(RectangleF), typeof(Rectangle?), typeof(Color), typeof(float),
            typeof(bool), typeof(bool), typeof(string), typeof(string), typeof(float)
        },
        new[]
        {
            ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal,
            ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal,
            ArgumentType.Normal, ArgumentType.Normal
        })]
    internal static class HudDrawSpriteEchoPatch
    {
        private static void Postfix(
            string texture, ref RectangleF destination, Rectangle? sourceRectangle, Color color,
            float rotation, bool ignoreBounds, bool waitTillLoaded, string targetTexture,
            string maskTexture, float rotSpeed)
        {
            try
            {
                string echoTarget;
                if (!HudRecordedEcho.TryBegin(targetTexture, out echoTarget)) return;
                try
                {
                    MyRenderProxy.DrawSprite(texture, ref destination, sourceRectangle, color,
                        rotation, ignoreBounds, waitTillLoaded, echoTarget, maskTexture, rotSpeed);
                }
                finally { HudRecordedEcho.End(); }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.DrawSpriteExt),
        new[]
        {
            typeof(string), typeof(RectangleF), typeof(Rectangle?), typeof(Color), typeof(Vector2),
            typeof(Vector2), typeof(bool), typeof(bool), typeof(string), typeof(string)
        },
        new[]
        {
            ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal,
            ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal,
            ArgumentType.Normal, ArgumentType.Normal
        })]
    internal static class HudDrawSpriteExtEchoPatch
    {
        private static void Postfix(
            string texture, ref RectangleF destination, Rectangle? sourceRectangle, Color color,
            ref Vector2 rightVector, ref Vector2 origin, bool ignoreBounds, bool waitTillLoaded,
            string targetTexture, string maskTexture)
        {
            try
            {
                string echoTarget;
                if (!HudRecordedEcho.TryBegin(targetTexture, out echoTarget)) return;
                try
                {
                    MyRenderProxy.DrawSpriteExt(texture, ref destination, sourceRectangle, color,
                        ref rightVector, ref origin, ignoreBounds, waitTillLoaded, echoTarget,
                        maskTexture);
                }
                finally { HudRecordedEcho.End(); }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.DrawSpriteAtlas),
        new[]
        {
            typeof(string), typeof(Vector2), typeof(Vector2), typeof(Vector2), typeof(Vector2),
            typeof(Vector2), typeof(Color), typeof(Vector2), typeof(bool), typeof(string)
        })]
    internal static class HudDrawSpriteAtlasEchoPatch
    {
        private static void Postfix(
            string texture, Vector2 position, Vector2 textureOffset, Vector2 textureSize,
            Vector2 rightVector, Vector2 scale, Color color, Vector2 halfSize, bool ignoreBounds,
            string targetTexture)
        {
            try
            {
                string echoTarget;
                if (!HudRecordedEcho.TryBegin(targetTexture, out echoTarget)) return;
                try
                {
                    MyRenderProxy.DrawSpriteAtlas(texture, position, textureOffset, textureSize,
                        rightVector, scale, color, halfSize, ignoreBounds, echoTarget);
                }
                finally { HudRecordedEcho.End(); }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.DrawString),
        new[]
        {
            typeof(int), typeof(Vector2), typeof(Color), typeof(string), typeof(float),
            typeof(float), typeof(bool), typeof(string)
        })]
    internal static class HudDrawStringEchoPatch
    {
        private static void Postfix(
            int fontIndex, Vector2 screenCoord, Color colorMask, string text, float screenScale,
            float screenMaxWidth, bool ignoreBounds, string targetTexture)
        {
            try
            {
                string echoTarget;
                if (!HudRecordedEcho.TryBegin(targetTexture, out echoTarget)) return;
                try
                {
                    MyRenderProxy.DrawString(fontIndex, screenCoord, colorMask, text, screenScale,
                        screenMaxWidth, ignoreBounds, echoTarget);
                }
                finally { HudRecordedEcho.End(); }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(MyRenderProxy), nameof(MyRenderProxy.DrawStringAligned),
        new[]
        {
            typeof(int), typeof(Vector2), typeof(Color), typeof(string), typeof(float),
            typeof(float), typeof(bool), typeof(string), typeof(int),
            typeof(MyRenderTextAlignmentEnum)
        })]
    internal static class HudDrawStringAlignedEchoPatch
    {
        private static void Postfix(
            int fontIndex, Vector2 screenCoord, Color colorMask, string text, float screenScale,
            float screenMaxWidth, bool ignoreBounds, string targetTexture, int textureWidthinPx,
            MyRenderTextAlignmentEnum align)
        {
            try
            {
                string echoTarget;
                if (!HudRecordedEcho.TryBegin(targetTexture, out echoTarget)) return;
                try
                {
                    MyRenderProxy.DrawStringAligned(fontIndex, screenCoord, colorMask, text,
                        screenScale, screenMaxWidth, ignoreBounds, echoTarget, textureWidthinPx,
                        align);
                }
                finally { HudRecordedEcho.End(); }
            }
            catch { }
        }
    }
}
