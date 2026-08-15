using System;
using System.Collections.Generic;
using System.Reflection;
using CleanFeed.Compatibility;
using CleanFeed.Profiles;
using HarmonyLib;
using VRageRender;

namespace CleanFeed.Render
{
    // Redirects player-only sprite work away from the default (backbuffer) sprite target by
    // rewriting the producer's targetTexture argument.
    //
    // The prefix must take the argument by name, never through Harmony's object[] __args. __args is
    // materialised before the prefix body runs, so it would allocate an array plus a box per argument
    // on every sprite and string draw in the game - including while CleanFeed is switched off and the
    // first statement here returns immediately.
    [HarmonyPatch]
    internal static class NativeSpriteTargetPatch
    {
        private static readonly string[] TargetNames =
        {
            nameof(MyRenderProxy.DrawSprite), nameof(MyRenderProxy.DrawSpriteAtlas),
            nameof(MyRenderProxy.DrawSpriteExt), nameof(MyRenderProxy.DrawString),
            nameof(MyRenderProxy.DrawStringAligned), nameof(MyRenderProxy.SpriteScissorPush),
            nameof(MyRenderProxy.SpriteScissorPop)
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in typeof(MyRenderProxy).GetMethods(
                         BindingFlags.Public | BindingFlags.Static))
            {
                if (Array.IndexOf(TargetNames, method.Name) < 0) continue;
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    // The prefix binds this parameter by name, so only string-typed occurrences can
                    // be patched. A future signature change fails the scan instead of Harmony.
                    if (parameter.Name != "targetTexture") continue;
                    if (parameter.ParameterType == typeof(string)) yield return method;
                    break;
                }
            }
        }

        private static void Prefix(ref string targetTexture)
        {
            if (!HudRedirector.SelectiveInterceptionActive) return;
            HudRouteTag tag = HudRouteContext.Current;
            if (tag != null)
                HudSourceRegistry.RecordActivity(tag, HudPrimitiveFamily.NativeSprite, true);

            // Only claim producers that left the default target in place; an explicit target is
            // already routed, including CleanFeed's own re-dispatched billboard projections.
            if (targetTexture != null) return;
            HudEffectiveRoute route = HudRedirector.ResolveRoute(tag);
            if (route == HudEffectiveRoute.Recorded) return;
            targetTexture = route == HudEffectiveRoute.Suppressed
                ? HudSuppressedSpriteQueue.Target
                : HudNamedSpriteRoutes.TargetFor(tag);
        }
    }
}
