using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using CleanFeed.Compatibility;
using CleanFeed.Profiles;
using HarmonyLib;
using VRageRender;
using VRageRender.Messages;

namespace CleanFeed.Render
{
    // Re-resolves sprite routing for pre-recorded GUI command lists on every submission.
    //
    // Why NativeSpriteTargetPatch is not enough: MyRichLabel.Draw (chat text and every other rich
    // label) records its draw calls once via MyRenderProxy.BeginRecordingDeferredMessages /
    // FinishRecordingDeferredMessages, then replays the same bundle on every later frame whose
    // layout is unchanged through MyRenderProxy.ExecuteCommands(m_lastDrawing, disposeAfterDraw:
    // false). The replay never re-enters DrawString/DrawSprite, so the producer-side prefix runs
    // only at record time and the TargetTexture chosen then is baked into the message objects: a
    // bundle recorded before a redirect keeps leaking to the backbuffer, and one recorded during a
    // redirect keeps a CleanFeed target after that queue stops being consumed.
    //
    // Version-sensitive shape observed in the decompiled game source (see
    // VRage.Render\VRageRender\MyRenderProxy.cs and VRage.Render\VRageRender\Messages\):
    //   MyRenderProxy.ExecuteCommands(MyRenderMessageDrawCommands commands, bool disposeAfterDraw)
    //       - exactly one overload; disposeAfterDraw has a default, so the signature is 2 params.
    //   MyRenderMessageDrawCommands : MyRenderMessageBase
    //       - public List<MyRenderMessageBase> Messages    (the recorded messages)
    //       - public bool DisposeAfterDraw
    //   MySpriteDrawRenderMessage : MyRenderMessageBase   (abstract)
    //       - public string TargetTexture { get; set; }    (the single routing field)
    //     Every sprite-family message derives from it, so one type test covers the whole family:
    //       MyRenderMessageDrawSprite, MyRenderMessageDrawSpriteExt, MyRenderMessageDrawSpriteAtlas,
    //       MyRenderMessageDrawString, MyRenderMessageDrawStringAligned (: DrawString),
    //       MyRenderMessageDrawVideo, MyRenderMessageSpriteScissorPush, MyRenderMessageSpriteScissorPop.
    //   MyRender11.ProcessMessageInternal dispatches MyRenderMessageEnum.DrawCommands by iterating
    //   Messages and calling ProcessMessage on each, so the nested TargetTexture is what routes.
    //
    // Mutating the recorded messages in place is intentional. The bundles are AddRef'd and replayed
    // repeatedly, and this prefix re-evaluates the routing on every submission, so a state change -
    // enable, disable, privacy suppression, a per-source filter flip - is re-applied to the same
    // message objects on the next replay. Nothing else in the game writes TargetTexture after the
    // producer call that created the message.
    //
    // Decision table, mirroring NativeSpriteTargetPatch's live semantics:
    //   intercepting && tag != null - the replay is scoped. Resolve the tag's route (Recorded ->
    //     default target, Suppressed -> the suppressed queue, PlayerOnly -> the tag's named target)
    //     and apply it to every message left on the default target or carrying one of our own
    //     targets. An explicit foreign target belongs to a mod routing its own offscreen work.
    //   intercepting && tag == null - the replay site is deliberately outside any scope
    //     (MyGuiScreenHudSpace.Draw replays its own bundle there). A null TargetTexture is
    //     ambiguous here: it means either "never scoped, never claimed" or "scoped, and its route
    //     resolved to Recorded, which IS the null/backbuffer target". Record-time stamping (see
    //     HudCommandListRecordPatch) separates the two:
    //       FRESH bundle (stamped Intercepting under the current ActivationGeneration) - touch
    //         nothing at all. Every message in it already passed through the live producer-side
    //         prefix while this same redirect was up, so its target - CleanFeed target or null - is
    //         the routing decision, not an absence of one. Claiming its nulls drags Recorded-routed
    //         content (the bottom stat panels among others) out of the capture.
    //       STALE bundle (no stamp, a stamp from an earlier generation, or one recorded while not
    //         intercepting) - its nulls really are unclassified. Resolve the global/privacy route
    //         exactly as the live prefix does for an untagged draw and claim ONLY messages still on
    //         the default target; claiming them player-side is the fail-closed choice for content
    //         whose category is unknown, because a pre-redirect recording must not leak into the
    //         capture. Then stamp the bundle current so later replays take the fresh fast path.
    //     In both cases messages already carrying a CleanFeed target keep it: the record-time scope
    //     is authoritative for content identity, and an untagged replay context does not make the
    //     content untagged. Restoring them here dumps scoped content - GPS markers, toolbar cluster,
    //     hudLCD text - onto the backbuffer mid-capture.
    //   !intercepting, redirect off - the only branch that restores CleanFeed targets to null.
    //     Stale bundles recorded during a redirect would otherwise never render again after /cf hud
    //     off. "Off" means not Requested, or requested in some other mode - Disable, Shutdown and
    //     FailOpen all clear Requested - and NOT merely that interception is inactive this frame.
    //     The bundle's stamp is downgraded to not-intercepting so a later re-enable sees it as
    //     stale and re-claims its nulls.
    //   !intercepting, redirect still up - a transient lifecycle window (Arming, Rebuilding, the
    //     175 ms re-arm gap). Nothing is touched, targets or stamp. See the comment on the hold in
    //     Retarget for why restoring here is a leak and why holding is the safe direction.
    //
    // The tagged branch never consults the stamp: a scoped replay must follow live filter flips, so
    // it re-resolves its route per submission even on a freshly recorded bundle.
    [HarmonyPatch]
    internal static class HudCommandListPatch
    {
        private const string TargetPrefix = "CleanFeed.";
        private static long _bundles;
        private static long _rewritten;
        private static long _claimedGlobal;
        private static long _freshSkips;
        private static long _restored;
        private static long _held;
        private static bool _logged;

        // What interception state a bundle was recorded under. Mutable fields are never written
        // after publication - a changed stamp is a new instance swapped in under StampGate - so
        // lock-free readers only ever observe a fully built stamp.
        private sealed class RecordStamp
        {
            internal bool Intercepting;
            internal int Generation;
        }

        private static readonly object StampGate = new object();
        private static readonly ConditionalWeakTable<MyRenderMessageDrawCommands, RecordStamp> _stamps =
            new ConditionalWeakTable<MyRenderMessageDrawCommands, RecordStamp>();

        // MyRenderProxy.FinishRecordingDeferredMessages hands out bundles from MessagePool, so the
        // same MyRenderMessageDrawCommands instance is recycled across unrelated recordings. The
        // mapping must therefore be overwritten, not merely added, and net48's ConditionalWeakTable
        // has no atomic AddOrUpdate - same constraint HudRouteContext.Tag works around with a
        // Remove/Add pair under a gate. The probe ahead of the lock keeps the steady state
        // lock-free and allocation-free.
        private static void StampBundle(
            MyRenderMessageDrawCommands bundle, bool intercepting, int generation)
        {
            if (bundle == null) return;
            RecordStamp existing = LookupStamp(bundle);
            if (existing != null
                && existing.Intercepting == intercepting
                && existing.Generation == generation) return;

            RecordStamp stamp = new RecordStamp { Intercepting = intercepting, Generation = generation };
            lock (StampGate)
            {
                _stamps.Remove(bundle);
                _stamps.Add(bundle, stamp);
            }
        }

        // Deliberately lock-free: this sits on the per-frame replay path and is one CWT probe. The
        // only race is against a StampBundle Remove/Add, whose window can make the stamp look
        // absent - which is read as "stale", the fail-closed answer that claims nulls player-side.
        private static RecordStamp LookupStamp(MyRenderMessageDrawCommands bundle)
            => bundle != null && _stamps.TryGetValue(bundle, out RecordStamp stamp) ? stamp : null;

        internal static void StampRecorded(MyRenderMessageDrawCommands bundle)
            => StampBundle(bundle, HudRedirector.SelectiveInterceptionActive, HudRedirector.ActivationGeneration);

        private static MethodBase TargetMethod()
            => AccessTools.Method(
                typeof(MyRenderProxy),
                nameof(MyRenderProxy.ExecuteCommands),
                new[] { typeof(MyRenderMessageDrawCommands), typeof(bool) });

        // Unlike NativeSpriteTargetPatch this must also run while interception is inactive: stale
        // bundles recorded during a redirect still carry CleanFeed targets and would never render
        // again after /cf hud off. The inactive pass is a read-only scan that writes nothing unless
        // it finds one of our targets.
        private static void Prefix(MyRenderMessageDrawCommands commands)
        {
            try { Retarget(commands); }
            catch (Exception ex)
            {
                // Fail open: a routing miss is a cosmetic defect, an exception here would take the
                // GUI down. Logged once so a systemic failure is still visible.
                if (_logged) return;
                _logged = true;
                Plugin.Log("HUD command-list retarget fail-open: " + ex);
            }
        }

        private static void Retarget(MyRenderMessageDrawCommands commands)
        {
            List<MyRenderMessageBase> messages = commands?.Messages;
            int count = messages == null ? 0 : messages.Count;
            if (count == 0) return;
            Interlocked.Increment(ref _bundles);

            HudRouteTag tag = HudRouteContext.Current;
            bool intercepting = HudRedirector.SelectiveInterceptionActive;
            int generation = HudRedirector.ActivationGeneration;
            bool claimTagged = intercepting && tag != null;
            bool claimGlobal = intercepting && tag == null;

            // Restoring is gated on the redirect being OFF, not on interception being inactive at
            // this instant. SelectiveInterceptionActive is false through every transient lifecycle
            // window - Arming, Rebuilding, the 175 ms foreground re-arm gap - and those windows are
            // frames of a live redirect, not the end of one. Reverting our targets there hands the
            // replayed bundles (chat, toolbar rich labels) straight to the backbuffer for the
            // duration, which puts them in the recording the redirect exists to keep them out of.
            // Requested is the durable answer: Disable, Shutdown and FailOpen all clear it, so a
            // deliberate /cf hud off and a faulted session both still restore.
            bool redirectOff = !HudRedirector.Requested
                               || HudRedirector.Mode != HudRedirectMode.Selective;
            bool restore = !intercepting && redirectOff;

            // Transient window: touch nothing at all. The CleanFeed targets stay where the live
            // prefix put them and the existing drain paths empty those queues, so the content shows
            // in neither output for those frames - the privacy-first direction, and the only one
            // that does not leak. The stamp is left alone too: downgrading it here would make a
            // genuinely fresh bundle read as stale on the far side of the gap, and the stale path
            // claims nulls player-side, dragging Recorded-routed content out of the capture.
            if (!intercepting && !restore)
            {
                Interlocked.Increment(ref _held);
                return;
            }

            if (claimGlobal)
            {
                // Fresh bundle: every message was already routed by the live producer-side prefix,
                // so a null here is Recorded by policy, not unclassified. See the FRESH case in the
                // type header.
                RecordStamp stamp = LookupStamp(commands);
                if (stamp != null && stamp.Intercepting && stamp.Generation == generation)
                {
                    Interlocked.Increment(ref _freshSkips);
                    return;
                }
            }

            string desired = null;
            if (intercepting)
            {
                // Once per bundle, not per message: the whole command list came from one producer,
                // and only a scoped replay is attributable to a source.
                if (tag != null)
                    HudSourceRegistry.RecordActivity(tag, HudPrimitiveFamily.NativeSprite, true);

                // ResolveRoute(null) yields the global/privacy route and TargetFor(null) the default
                // player target, so the untagged case needs no separate resolution path.
                HudEffectiveRoute route = HudRedirector.ResolveRoute(tag);
                desired = route == HudEffectiveRoute.Recorded
                    ? null
                    : route == HudEffectiveRoute.Suppressed
                        ? HudSuppressedSpriteQueue.Target
                        : HudNamedSpriteRoutes.TargetFor(tag);
            }

            for (int i = 0; i < count; i++)
            {
                if (!(messages[i] is MySpriteDrawRenderMessage sprite)) continue;
                string current = sprite.TargetTexture;
                string next;
                if (claimTagged)
                {
                    // Only claim the default target or one of our own; an explicit named target
                    // belongs to a mod that is already routing its own offscreen work.
                    if (current != null && !IsCleanFeedTarget(current)) continue;
                    next = desired;
                }
                else if (claimGlobal)
                {
                    // Untagged replay of a STALE bundle: claim only what is still on the default
                    // target, and keep a baked CleanFeed target - it records the scope the content
                    // was produced under. Accepted staleness: if that category's route changes
                    // after recording, the baked target stands until the producer re-records (any
                    // layout or alpha change does), so only a frozen bundle can lag.
                    if (current != null) continue;
                    next = desired;
                }
                else
                {
                    // Restore. The redirect is off, so a bundle still carrying one of our targets
                    // would never render again; nothing else here is ours to touch.
                    if (current == null || !IsCleanFeedTarget(current)) continue;
                    next = null;
                }

                if (string.Equals(current, next, StringComparison.Ordinal)) continue;
                sprite.TargetTexture = next;
                if (claimTagged) Interlocked.Increment(ref _rewritten);
                else if (claimGlobal) Interlocked.Increment(ref _claimedGlobal);
                if (next == null) Interlocked.Increment(ref _restored);
            }

            if (claimGlobal)
            {
                // The stale bundle is now classified, so later replays take the fresh fast path
                // instead of re-walking it every frame.
                StampBundle(commands, true, generation);
            }
            else if (restore)
            {
                // Downgrade rather than delete: a bundle that survives a /cf hud off + on cycle
                // must read as stale on the next enable so its nulls get re-claimed. (No-op after
                // the first pass - StampBundle short-circuits when the stamp already matches.)
                StampBundle(commands, false, generation);
            }
        }

        private static bool IsCleanFeedTarget(string target)
            => target.StartsWith(TargetPrefix, StringComparison.Ordinal);

        internal static string StatusLine()
            => "command-lists=bundles:" + Interlocked.Read(ref _bundles)
               + ", rewritten:" + Interlocked.Read(ref _rewritten)
               + ", claimed-global:" + Interlocked.Read(ref _claimedGlobal)
               + ", fresh:" + Interlocked.Read(ref _freshSkips)
               + ", restored:" + Interlocked.Read(ref _restored)
               + ", held:" + Interlocked.Read(ref _held);
    }

    // Stamps every bundle, at the moment it is recorded, with the interception state it was recorded
    // under. That is the one piece of information the replay site cannot reconstruct: a
    // deliberately-Recorded null target is byte-for-byte identical to a never-scoped one. See the
    // FRESH/STALE split in HudCommandListPatch's header for what the stamp decides.
    //
    // Shape confirmed in the decompiled game source (VRage.Render\VRageRender\MyRenderProxy.cs):
    //   public static MyRenderMessageDrawCommands FinishRecordingDeferredMessages()
    //       - exactly one overload, no parameters, no defaulted arguments.
    //       - returns a MessagePool-owned MyRenderMessageDrawCommands whose Messages list is the
    //         recording just closed; pooling is why the stamp table overwrites rather than adds.
    [HarmonyPatch]
    internal static class HudCommandListRecordPatch
    {
        private static bool _logged;

        private static MethodBase TargetMethod()
            => AccessTools.Method(
                typeof(MyRenderProxy),
                nameof(MyRenderProxy.FinishRecordingDeferredMessages),
                Type.EmptyTypes);

        private static void Postfix(MyRenderMessageDrawCommands __result)
        {
            try
            {
                if (__result == null) return;
                HudCommandListPatch.StampRecorded(__result);
            }
            catch (Exception ex)
            {
                // Fail open, same rationale as the replay prefix: a missing stamp only costs a
                // stale-path re-claim, an exception here would take the GUI down.
                if (_logged) return;
                _logged = true;
                Plugin.Log("HUD command-list record stamp fail-open: " + ex);
            }
        }
    }
}
