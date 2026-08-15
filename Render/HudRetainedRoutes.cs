using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using CleanFeed.Compatibility;
using CleanFeed.Profiles;
using HarmonyLib;
using SharpDX.Mathematics.Interop;
using VRage.Render11.Common;
using VRage.Render11.Resources;
using VRage.Render11.Sprites;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace CleanFeed.Render
{
    // Independently-cadenced HUD producers must not share publication completeness with the
    // vanilla player HUD. Each retained route owns a transparent DirectComposition child surface:
    // no new queue means retain the prior GPU content, while a sealed empty producer call yields an
    // explicit transparent batch and clears only that route.
    internal static class HudNamedSpriteRoutes
    {
        private static readonly long PartialHoldTicks = Stopwatch.Frequency / 20; // 50 ms
        // Ceiling for the cadence-adaptive part of the partial hold. A toolbar that really was
        // removed must still clear inside this window, so the adaptive term is clamped, never open.
        private static readonly long PartialHoldMaxTicks = Stopwatch.Frequency / 4; // 250 ms
        // Below this a published batch is too small to be a credible whole generation, so it neither
        // arms the partial hold nor serves as its reference count.
        private const int PartialFloor = 4;
        // How many consecutive marker-less buckets the completeness gate may hold before it presumes
        // the marker has stopped arriving and disarms. The gate exists to survive a split generation,
        // which is two or three buckets, never a steady stream of them - so this is a wedge bound,
        // not an operating range. Disarming rather than publishing on the bound is deliberate: a
        // route whose producer no longer seals must fall back to the count heuristics wholesale, not
        // strobe one bucket in every FragmentHoldRun.
        private const int FragmentHoldRun = 4;
        // How many consecutive generations a marker-terminated but sharply smaller bucket may be
        // held while waiting for its size to repeat. A split remainder is a different size each
        // generation and never confirms; a toolbar that really shrank confirms on the next one. The
        // bound is what guarantees a legitimate change lands even when the new size is itself noisy.
        private const int ShrinkHoldRun = 2;
        private static readonly long InteractiveIdleClearTicks = Stopwatch.Frequency / 10; // 100 ms
        private static readonly long EmptyHoldTicks = Stopwatch.Frequency / 5; // 200 ms
        // The RichHud route is unsealed, so nothing but silence marks the end of its content. The
        // window is wider than the sealed routes' because the menu is produced from a
        // session-component draw whose cadence is not tied to the GUI phase this pass runs in.
        private static readonly long RichHudIdleClearTicks = Stopwatch.Frequency / 5; // 200 ms
        private static FieldInfo _drawQueueField;
        private static long _activationFlushes;
        private static long _hudHiddenClears;
        private static long _aliasDetected;

        private sealed class RetainedRoute
        {
            internal readonly string Key;
            internal readonly string Target;
            internal readonly string SurfaceName;
            internal readonly HudCompositionLayer Layer;
            internal readonly string SealTarget;
            internal long Seals;
            internal long SealBatches;
            internal long SealRetained;
            internal long SealMismatches;
            internal long StaleDiscarded;
            internal long PeekFailures;
            internal long Batches;
            internal long Published;
            internal long Retained;
            internal long Drained;
            internal long Clears;
            internal long PartialHeld;
            internal long EmptyHeld;
            internal long SealMarkerRetained;
            internal long ZeroKept;
            internal long FragmentHeld;
            internal long ShrinkHeld;
            internal long GateRelaxed;
            internal int EmptySealRun;
            // Render-thread only, like EmptySealRun and MayHaveContent: written and read solely
            // inside Process, never surfaced on the status line, so no volatile access is owed.
            internal bool MarkerSeen;
            internal int FragmentRun;
            internal int ShrinkRun;
            internal int PendingShrinkCount = -1;
            internal int LastCount = -1;
            internal int LastPublishedCount = -1;
            internal int LastSubstantialCount = -1;
            internal long PublishGapTicks;
            internal long LastPublishedAt;
            internal long LastBatchAt;
            internal long LastSealAt;
            internal int LastSealFrame = -1;
            internal int LastDataFrame = -1;
            internal int LastActivationGeneration = -1;
            internal bool MayHaveContent;
            internal bool CompletionSealed => SealTarget != null;

            internal RetainedRoute(
                string key, string target, string surfaceName, HudCompositionLayer layer,
                string sealTarget = null)
            {
                Key = key;
                Target = target;
                SurfaceName = surfaceName;
                Layer = layer;
                SealTarget = sealTarget;
            }
        }

        private static readonly RetainedRoute Toolbar = new RetainedRoute(
            "toolbar", "CleanFeed.PlayerOnly.Toolbar", "CleanFeed.PlayerOnly.Toolbar.Surface",
            HudCompositionLayer.Toolbar);
        private static readonly RetainedRoute FlightHud = new RetainedRoute(
            "flight-hud", "CleanFeed.PlayerOnly.FlightHud", "CleanFeed.PlayerOnly.FlightHud.Surface",
            HudCompositionLayer.FlightHud);
        private static readonly RetainedRoute Interactive = new RetainedRoute(
            "interactive", "CleanFeed.PlayerOnly.Interactive", "CleanFeed.PlayerOnly.Interactive.Surface",
            HudCompositionLayer.Interactive, "CleanFeed.PlayerOnly.Interactive.Seal");
        private static readonly RetainedRoute Chat = new RetainedRoute(
            "chat", "CleanFeed.PlayerOnly.Chat", "CleanFeed.PlayerOnly.Chat.Surface",
            HudCompositionLayer.Chat, "CleanFeed.PlayerOnly.Chat.Seal");
        // Dedicated layer for the projected RichHud nav menu. Deliberately UNSEALED: the projection
        // is emitted from a session-component draw, a different phase than the screen-manager draw
        // the sealed routes' completion markers are issued from, so seal and data would land in
        // different render-side passes and the route would alternate publish and clear every few
        // frames. Plain retention plus an idle clear is the correct contract for it.
        private static readonly RetainedRoute RichHud = new RetainedRoute(
            "richhud", "CleanFeed.PlayerOnly.RichHud", "CleanFeed.PlayerOnly.RichHud.Surface",
            HudCompositionLayer.RichHud);
        private static readonly RetainedRoute[] Routes = { Toolbar, FlightHud, Interactive, Chat, RichHud };

        // The RichHud route's named target. Addressed explicitly by the quad capture; TargetFor
        // never resolves to it, because no producer tag routes here.
        internal static string RichHudTargetName => RichHud.Target;

        // Whether the RichHud layer is currently holding published content, i.e. whether a projected
        // menu is on screen right now.
        //
        // The quad capture uses this to decide whether to pull the shared cursor onto the same
        // layer. Deliberately a plain bool read with no synchronisation: the render thread writes it
        // in Process, the draw thread reads it here, and the value is at worst one frame stale. That
        // staleness is safe in both directions and needs no barrier - as the menu opens, at most one
        // frame of cursor records with it; as it closes, at most one frame of cursor is projected
        // onto a layer about to clear. Both are invisible, and neither can leak menu content, which
        // is governed by the menu's own tag rather than by this flag.
        internal static bool RichHudLayerHasContent => RichHud.MayHaveContent;

        internal static string TargetFor(HudRouteTag tag)
        {
            // Identity, ahead of every key comparison: the vanilla cursor tag deliberately carries
            // the "interactive" key so it inherits that category's policy, but it must land on the
            // RichHud layer rather than the interactive one - it is only tagged at all while a
            // projected menu is on that layer, and the whole point is for it to sit on top of it.
            if (HudAdapterCoordinator.IsVanillaCursorTag(tag)) return RichHud.Target;
            if (string.Equals(tag?.Key, "terminal", StringComparison.OrdinalIgnoreCase))
                return Interactive.Target;
            if (string.Equals(tag?.Key, "chat", StringComparison.OrdinalIgnoreCase))
                return Chat.Target;
            RetainedRoute route = Find(tag?.Key);
            return route?.Target ?? HudNamedSpriteFrame.PlayerTarget;
        }

        internal static void SealInteractiveFrame()
        {
            if (!HudRedirector.SelectiveActive) return;
            SealCompletion(Interactive);
        }

        internal static void Seal(HudRouteTag tag)
        {
            if (!HudRedirector.SelectiveInterceptionActive
                || tag == null
                || HudRedirector.ResolveRoute(tag) != HudEffectiveRoute.PlayerOnly) return;
            RetainedRoute route = Find(tag.Key);
            if (route == null) return;

            if (route.CompletionSealed)
            {
                SealCompletion(route);
                return;
            }

            // A balanced scissor pair is a zero-pixel generation marker. It gives a provider that
            // intentionally drew nothing an explicit empty batch without CPU pixels or readback.
            MyRenderProxy.SpriteScissorPush(new Rectangle(0, 0, 1, 1), route.Target);
            MyRenderProxy.SpriteScissorPop(route.Target);
            Interlocked.Increment(ref route.Seals);
        }

        internal static void Process(Vector2I resolution, bool publish)
        {
            foreach (RetainedRoute route in Routes)
                Process(route, resolution, publish && HudRedirector.SelectiveActive);
        }

        internal static string StatusLine()
        {
            string[] values = new string[Routes.Length];
            for (int i = 0; i < Routes.Length; i++)
            {
                RetainedRoute route = Routes[i];
                values[i] = route.Key + "="
                            + Interlocked.Read(ref route.Published) + "pub/"
                            + Interlocked.Read(ref route.Retained) + "keep/"
                            + Interlocked.Read(ref route.Clears) + "clear/"
                            + Interlocked.Read(ref route.Drained) + "drain"
                            + ", partial-held=" + Interlocked.Read(ref route.PartialHeld)
                            + ", empty-held=" + Interlocked.Read(ref route.EmptyHeld)
                            + ", seal-marker-kept=" + Interlocked.Read(ref route.SealMarkerRetained)
                            + ", zero-kept=" + Interlocked.Read(ref route.ZeroKept)
                            + ", fragment-held=" + Interlocked.Read(ref route.FragmentHeld)
                            + ", shrink-held=" + Interlocked.Read(ref route.ShrinkHeld)
                            + ", gate-relaxed=" + Interlocked.Read(ref route.GateRelaxed)
                            + ", batches=" + Interlocked.Read(ref route.Batches)
                            + ", seals=" + Interlocked.Read(ref route.Seals)
                            + ", count=" + Volatile.Read(ref route.LastCount)
                            + ", published-count=" + Volatile.Read(ref route.LastPublishedCount)
                            + (route.CompletionSealed
                                ? ", completed=" + Interlocked.Read(ref route.SealBatches)
                                  + ", seal-kept=" + Interlocked.Read(ref route.SealRetained)
                                  + ", mismatch=" + Interlocked.Read(ref route.SealMismatches)
                                  + ", stale=" + Interlocked.Read(ref route.StaleDiscarded)
                                  + ", frame=" + Volatile.Read(ref route.LastDataFrame)
                                  + "/" + Volatile.Read(ref route.LastSealFrame)
                                  + ", peek-fail=" + Interlocked.Read(ref route.PeekFailures)
                                : string.Empty);
            }
            // The capture breakdown rides on this line rather than on the projector's: the richhud
            // route's own pub/keep/count figures are what a live session reads first, and the
            // per-reason drop counts are only meaningful next to them.
            return "retained-routes=" + string.Join(";", values)
                   + ", activation-flushes=" + Interlocked.Read(ref _activationFlushes)
                   + ", hud-hidden-clears=" + Interlocked.Read(ref _hudHiddenClears)
                   + ", alias-detected=" + Interlocked.Read(ref _aliasDetected)
                   + "; " + HudRichHudQuadCapture.StatusLine();
        }

        private static void Process(RetainedRoute route, Vector2I resolution, bool publish)
        {
            MySpriteMessageData messages = null;
            MySpriteMessageData sealMessages = null;
            IBorrowedRtvTexture layer = null;
            try
            {
                // A generation already queued when a redirect activates was produced before
                // interception was fully in place, so it can be torn or stale and must never be
                // published - it would sit on the layer until something forced a new generation.
                // Discard every queued bucket on an activation's first pass and publish nothing;
                // Ensure has just initialized the composition surfaces transparent. This runs ahead
                // of the !publish drain block so drain-mode passes flush too, and per route so each
                // flushes independently.
                int generation = HudRedirector.ActivationGeneration;
                if (route.LastActivationGeneration != generation)
                {
                    route.LastActivationGeneration = generation;
                    messages = MyManagers.SpritesManager.AcquireDrawMessages(route.Target);
                    if (route.CompletionSealed)
                        sealMessages = MyManagers.SpritesManager.AcquireDrawMessages(route.SealTarget);
                    if (messages != null)
                    {
                        Interlocked.Increment(ref _activationFlushes);
                        Interlocked.Increment(ref route.Drained);
                    }
                    if (sealMessages != null)
                    {
                        Interlocked.Increment(ref _activationFlushes);
                        Interlocked.Increment(ref route.Drained);
                    }
                    route.MayHaveContent = false;
                    Volatile.Write(ref route.LastPublishedCount, -1);
                    Volatile.Write(ref route.LastSubstantialCount, -1);
                    ResetCompletenessRuns(route);
                    return;
                }

                if (route.CompletionSealed)
                    sealMessages = MyManagers.SpritesManager.AcquireDrawMessages(route.SealTarget);
                if (!publish)
                {
                    messages = MyManagers.SpritesManager.AcquireDrawMessages(route.Target);
                    if (messages != null || sealMessages != null)
                        Interlocked.Increment(ref route.Drained);
                    return;
                }

                // Vanilla has stopped drawing the player HUD, so this route will not receive another
                // batch until it comes back. Retaining the last surface is right for a producer
                // that merely publishes sparsely, but wrong here: the unsealed toolbar and
                // flight-hud routes have no idle clear and would hold a dead HUD on screen for as
                // long as it stays hidden. Clear once - MayHaveContent latches the clear so this is
                // not a per-frame publish - then keep draining so nothing accumulates behind the
                // hidden HUD. Normal processing resumes when it returns and the next real
                // generation republishes.
                //
                // Which predicate applies depends on whether the route's producer can still draw.
                // On the broad MinimalHud/CutsceneHud state a focused chat keeps drawing (see
                // GameHudHidden), so the sealed routes are carved out and left on their normal path:
                // clearing them here would erase the chat box out from under a player typing with
                // the HUD tabbed off, and they idle-clear correctly on their own anyway via
                // InteractiveIdleExpired. When the HUD is fully hidden the HUD-hosted producers
                // all stop, so their routes must come down - but NOT the Interactive route:
                // interactive screens draw from MyScreenManager.Draw regardless of MyHud.IsVisible,
                // and the respawn/medical screen runs precisely in that state (no live local
                // character is WHY IsVisible is false). Draining it here discarded the respawn
                // screen from both outputs. Interactive needs no hidden clear anyway: its seal
                // keeps arriving every frame from MyScreenManager.Draw, and PrepareSealedGeneration
                // publishes transparent the moment a seal arrives with no data bucket, so the layer
                // comes down within a frame of the last interactive screen closing.
                //
                // The RichHud route is carved out of the broad predicate for the same reason even
                // though it is unsealed: its producer hides the vanilla HUD (ShowHud(false)) as part
                // of opening the menu, so MinimalHud is the route's normal operating state and
                // clearing on it would erase the menu on the frame it appears. Its own idle clear
                // below removes it when the menu closes. The narrow predicate still applies to it:
                // the projection is emitted from a gameplay session component, and holding a nav
                // menu on screen over the respawn flow is not a state worth retaining.
                if ((HudRedirector.GameHudFullyHidden() && !ReferenceEquals(route, Interactive))
                    || (!route.CompletionSealed
                        && !ReferenceEquals(route, RichHud)
                        && HudRedirector.GameHudHidden()))
                {
                    messages = MyManagers.SpritesManager.AcquireDrawMessages(route.Target);
                    if (messages != null || sealMessages != null)
                        Interlocked.Increment(ref route.Drained);
                    if (route.MayHaveContent)
                    {
                        PublishTransparent(route, resolution);
                        route.MayHaveContent = false;
                        Interlocked.Increment(ref route.Clears);
                        Interlocked.Increment(ref _hudHiddenClears);
                    }
                    return;
                }

                // The Interactive route is unconditionally player-only: NativeSpriteTargetPatch
                // returns early for a Recorded route, so its queue is only ever filled player-side.
                // If the terminal policy flips to recorded while content is retained, the seal path
                // below sees no data bucket and clears the surface.
                //
                // The RichHud route is unconditionally player-only for a different reason: it has no
                // profile key of its own, and its content is pre-filtered by the capture, which opens
                // a scope only when the emitting tag already resolves to PlayerOnly. Nothing that is
                // not player-only can ever reach this queue.
                bool playerOnly = ReferenceEquals(route, Interactive)
                                  || ReferenceEquals(route, RichHud)
                                  || HudProfileService.Current.EffectiveRoute(route.Key)
                                     == HudEffectiveRoute.PlayerOnly;
                if (!playerOnly)
                {
                    messages = MyManagers.SpritesManager.AcquireDrawMessages(route.Target);
                    if (messages != null) Interlocked.Increment(ref route.Drained);
                    if (route.MayHaveContent)
                    {
                        PublishTransparent(route, resolution);
                        route.MayHaveContent = false;
                        Interlocked.Increment(ref route.Clears);
                    }
                    return;
                }

                if (route.CompletionSealed)
                {
                    if (!PrepareSealedGeneration(route, sealMessages, ref messages, resolution))
                        return;
                }
                else
                {
                    messages = MyManagers.SpritesManager.AcquireDrawMessages(route.Target);
                }

                if (messages == null)
                {
                    // Sealed routes idle-clear on seal-or-data silence; the unsealed RichHud route
                    // has only data silence to go on, and needs an idle clear of its own because
                    // nothing else takes the menu down when it closes - the producer simply stops
                    // emitting, and its MinimalHud carve-out above deliberately declines the
                    // hud-hidden clear. The other unsealed routes keep plain retention.
                    if (route.MayHaveContent
                        && (route.CompletionSealed
                            ? InteractiveIdleExpired(route)
                            : ReferenceEquals(route, RichHud) && RichHudIdleExpired(route)))
                    {
                        PublishTransparent(route, resolution);
                        route.MayHaveContent = false;
                        Interlocked.Increment(ref route.Clears);
                        return;
                    }
                    Interlocked.Increment(ref route.Retained);
                    return;
                }

                int count = messages.Messages?.Count ?? 0;
                Interlocked.Exchange(ref route.LastBatchAt, Stopwatch.GetTimestamp());
                Volatile.Write(ref route.LastCount, count);
                Volatile.Write(ref route.LastDataFrame, messages.FrameId);
                Interlocked.Increment(ref route.Batches);

                // A zero-message bucket cannot come from AddMessage - every add appends at least
                // one message - so count 0 is the signature of a bucket zeroed by another holder
                // racing this consumer through the engine pool's unguarded double-return, seen in
                // the field as toolbar and chat blinking INDEPENDENTLY at high sprite churn
                // (published-count=0 on both routes). Publishing it blanks a healthy element for a
                // generation; retaining costs nothing, because a producer that genuinely stopped
                // goes silent - no bucket at all - and the hud-hidden, idle-clear and seal-silence
                // paths own that case.
                if (count == 0)
                {
                    Interlocked.Increment(ref route.ZeroKept);
                    Interlocked.Increment(ref route.Retained);
                    return;
                }
                route.EmptySealRun = 0;

                // An unsealed route's completion marker is written into the route's OWN queue (see
                // Seal), so a generation where the producer drew nothing does not arrive here as
                // queue silence - it arrives as a real bucket holding just that balanced scissor
                // pair. Publishing it clears the layer, which is the toolbar strobe seen whenever a
                // producer skips a generation. Recognise the marker by shape and retain instead, so
                // the outcome no longer depends on how late the next real generation is.
                //
                // Recognition is exact: every message must be a scissor push or pop, the pairs must
                // balance, and no drawable sprite may be present. A generation that drew real
                // content and carries the marker on the end is an ordinary batch and publishes.
                //
                // A genuinely hidden toolbar emits marker-only buckets too, and retaining them here
                // does not resurrect the tab-to-hide bug: the hud-hidden branch above owns that
                // case for these routes and clears them before any bucket reaches this point.
                // RichHud is excluded because no seal is ever issued for it, so a marker-only bucket
                // there would be the framework's own clipping scope on a frame that projected
                // nothing, and holding it has no bound - that route's empty hold and idle clear are
                // what handle its gaps.
                if (!route.CompletionSealed
                    && !ReferenceEquals(route, RichHud)
                    && IsSealMarkerOnly(messages, count))
                {
                    Interlocked.Increment(ref route.SealMarkerRetained);
                    Interlocked.Increment(ref route.Retained);
                    return;
                }

                // The RichHud route never publishes an empty batch over live content.
                //
                // Its producer is a projection, not a draw: content reaches this queue only on
                // frames where the quad capture resolves the framework's shared buffer AND every
                // stage of the projection succeeds. A frame where any of that comes up short
                // arrives here as a zero-message bucket, and publishing it blanks the surface until
                // the next good frame republishes - which is the full/blank flicker the operator
                // sees, rather than a menu that is simply absent. Nothing else on this route can
                // distinguish that gap from a genuinely closed menu, so the gap is assumed and the
                // previous surface retained.
                //
                // Bounded, so this cannot wedge a stale menu on screen: the hold only applies for
                // 200 ms after the last real publish. A menu that is actually closed still clears,
                // on that timeout instead of on its first empty frame. The route's own idle clear
                // remains the other blanking path, and both stay intact.
                //
                // Deliberately not generalised to the other unsealed routes: toolbar and flight-hud
                // producers draw every frame, so an empty bucket there means something different
                // and is not this problem. Completion-sealed routes are excluded twice over - for
                // them an explicitly empty generation IS the clear signal the seal carries.
                if (ReferenceEquals(route, RichHud) && ShouldHoldEmpty(route, count))
                {
                    Interlocked.Increment(ref route.EmptyHeld);
                    Interlocked.Increment(ref route.Retained);
                    return;
                }

                // Completeness gate for the marked unsealed routes, and the reason the count
                // heuristics below can no longer decide on their own.
                //
                // The layer is redrawn from ONE bucket, so a bucket that holds only part of a
                // generation publishes only part of the HUD. Live telemetry on a busy server showed
                // this route taking almost exactly two buckets per generation, every generation, with
                // one published and one held - the generation is split, not occasionally but as the
                // normal case, because the deferred state-changes worker pumps sprite messages
                // continuously and this pass acquires between the two halves.
                //
                // The seal marker is the exact signal the counts only approximate. Seal writes a
                // balanced zero-pixel scissor pair into this route's OWN queue immediately after the
                // producer's Draw returns, so a bucket containing an ADJACENT push/pop pair holds the
                // end of a generation and a bucket containing none is a leading fragment. Held, not
                // published: a leading fragment is a strict subset of content already on the layer,
                // so retaining always shows more than publishing would.
                //
                // Testing for containment rather than for the pair sitting last is deliberate. The
                // route carries more than one producer - the bridged BuildInfo toolbar panel draws
                // into it from a different phase of the frame - so content can follow the marker in
                // the same bucket without that bucket being incomplete.
                //
                // The gate arms itself: it does nothing until a marked bucket has actually been seen,
                // so a route whose producer never seals behaves exactly as it did before. It also
                // disarms itself rather than wedging, on the run bound below.
                bool marked = !route.CompletionSealed
                              && !ReferenceEquals(route, RichHud)
                              && ContainsSealMarker(messages, count);
                if (marked)
                {
                    route.MarkerSeen = true;
                    route.FragmentRun = 0;
                }
                bool gated = route.MarkerSeen && !route.CompletionSealed
                             && !ReferenceEquals(route, RichHud);
                if (gated && !marked && route.MayHaveContent)
                {
                    if (route.FragmentRun < FragmentHoldRun)
                    {
                        route.FragmentRun++;
                        Interlocked.Increment(ref route.FragmentHeld);
                        Interlocked.Increment(ref route.Retained);
                        return;
                    }

                    // The marker has stopped arriving - a game or mod change moved the producer, or
                    // the seal is faulting. Publishing one bucket in every FragmentHoldRun would
                    // strobe, so the gate steps aside entirely and hands the route back to the count
                    // heuristics until a marked bucket re-arms it.
                    route.MarkerSeen = false;
                    ResetCompletenessRuns(route);
                    Interlocked.Increment(ref route.GateRelaxed);
                    gated = false;
                }

                // A marked bucket that is a sharp drop is ambiguous in a way no single observation can
                // settle: it is either the tail half of a split generation or a toolbar that really
                // got smaller. The seal cadence separates them. A split remainder is a different size
                // every generation and never repeats; a legitimate shrink - a cockpit change, a page
                // switch - draws the same new size again next generation. So hold the first sighting
                // and publish on the second observation at that same size.
                //
                // Deliberately no time window here, unlike ShouldRetainPartial: the question is how
                // many generations have confirmed the size, and a producer gap must not answer it.
                // The run bound is what keeps that from wedging - past it the drop publishes whether
                // or not the size ever repeated, so a change with a noisy size still lands inside two
                // generations.
                if (gated && marked && route.MayHaveContent && IsSubstantialDrop(route, count))
                {
                    if (route.PendingShrinkCount == count || route.ShrinkRun >= ShrinkHoldRun)
                    {
                        ResetCompletenessRuns(route);
                    }
                    else
                    {
                        route.PendingShrinkCount = count;
                        route.ShrinkRun++;
                        Interlocked.Increment(ref route.ShrinkHeld);
                        Interlocked.Increment(ref route.Retained);
                        return;
                    }
                }
                else if (marked)
                {
                    route.PendingShrinkCount = -1;
                    route.ShrinkRun = 0;
                }

                // The partial hold suppresses a batch that is a sharp count drop on the assumption
                // that it is an incremental fragment of a generation still being emitted. The
                // RichHud route is exempt by identity: menu content legitimately swings by large
                // factors between frames - a page changes, a list collapses, a submenu closes - so
                // the heuristic would not be catching fragments there, only adding latency to every
                // real transition.
                // Skipped whenever the completeness gate above was armed for this bucket: the marker
                // is an exact answer where this is an estimate, and running both would hold a
                // confirmed shrink that the gate just decided to publish.
                if (!route.CompletionSealed
                    && !ReferenceEquals(route, RichHud)
                    && !gated
                    && ShouldRetainPartial(route, count))
                {
                    Interlocked.Increment(ref route.PartialHeld);
                    Interlocked.Increment(ref route.Retained);
                    return;
                }
                // Blend state deliberately null so the pass uses vanilla's BlendAlphaPremult; see
                // the matching call in HudNamedSpriteFrame.Prepare for why a custom state is wrong.
                layer = MyRender11.DrawSpritesOffscreen(
                    messages,
                    route.SurfaceName,
                    resolution.X,
                    resolution.Y,
                    HudGpuTransport.LayerFormat,
                    new RawColor4(0f, 0f, 0f, 0f));
                if (layer == null)
                    throw new InvalidOperationException("DrawSpritesOffscreen returned no " + route.Key + " layer");
                HudResourceTelemetry.SelectiveBorrowed();
                HudRedirector.CopyCompletedHud(layer, count, false, route.Layer);
                route.MayHaveContent = true;
                Volatile.Write(ref route.LastPublishedCount, count);
                // Only a substantial generation may become the partial hold's reference count.
                // Publishing a fragment must not lower it, or that one publish disarms the guard for
                // the fragments that follow and the route strobes until a whole generation happens
                // to land in a single bucket.
                //
                // Once the marker is known on this route the test is stricter, and this is the second
                // half of the flicker fix rather than a tidy-up. The size test alone ratchets: a
                // fragment that squeaks past the half threshold becomes the reference, which lowers
                // it, which lets a smaller fragment past next generation, and the route walks itself
                // down to a near-blank layer before a whole generation happens to land intact. A
                // marked bucket is the only one that ever held a generation's end, so it is the only
                // one allowed to say how big a generation is.
                if (count >= PartialFloor && (!route.MarkerSeen || marked))
                    Volatile.Write(ref route.LastSubstantialCount, count);
                long publishedAt = Stopwatch.GetTimestamp();
                long previousPublishedAt = Interlocked.Exchange(ref route.LastPublishedAt, publishedAt);
                if (previousPublishedAt != 0)
                    Interlocked.Exchange(ref route.PublishGapTicks, publishedAt - previousPublishedAt);
                Interlocked.Increment(ref route.Published);
            }
            catch (Exception ex)
            {
                HudRedirector.FailOpen("retained sprite route " + route.Key, ex);
            }
            finally
            {
                if (layer != null)
                {
                    try { layer.Release(); }
                    finally { HudResourceTelemetry.SelectiveReleased(); }
                }
                if (messages != null) MyManagers.SpritesManager.DisposeDrawMessages(messages);
                // Data and seal buckets are acquired under different names and cannot be the same
                // instance unless the engine's bucket pool has handed one out twice - the missing
                // double-return guard in MyGenericObjectPool that HudSpriteBucketRolloverPatch
                // documents. Disposing it twice would deallocate it twice and widen that aliasing
                // instead of merely suffering it, so the second disposal is skipped and the
                // condition is counted rather than assumed impossible.
                if (sealMessages != null)
                {
                    if (ReferenceEquals(messages, sealMessages))
                        Interlocked.Increment(ref _aliasDetected);
                    else
                        MyManagers.SpritesManager.DisposeDrawMessages(sealMessages);
                }
            }
        }

        private static bool PrepareSealedGeneration(
            RetainedRoute route,
            MySpriteMessageData sealMessages,
            ref MySpriteMessageData messages,
            Vector2I resolution)
        {
            if (sealMessages == null)
            {
                if (route.MayHaveContent && InteractiveIdleExpired(route))
                {
                    PublishTransparent(route, resolution);
                    route.MayHaveContent = false;
                    Interlocked.Increment(ref route.Clears);
                }
                else
                {
                    Interlocked.Increment(ref route.SealRetained);
                    Interlocked.Increment(ref route.Retained);
                }
                return false;
            }

            int sealFrame = sealMessages.FrameId;
            Volatile.Write(ref route.LastSealFrame, sealFrame);
            Interlocked.Exchange(ref route.LastSealAt, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref route.SealBatches);

            int dataFrame = PeekDrawFrameId(route.Target, route);
            Volatile.Write(ref route.LastDataFrame, dataFrame);
            if (dataFrame == int.MinValue)
            {
                messages = MyManagers.SpritesManager.AcquireDrawMessages(route.Target);
                dataFrame = messages?.FrameId ?? -1;
                Volatile.Write(ref route.LastDataFrame, dataFrame);
            }

            if (dataFrame < 0)
            {
                // One seal without its data bucket is routinely a SPLIT generation rather than an
                // emptied producer: sprite messages ride two engine pumps (the render thread and
                // the deferred state-changes worker, which live telemetry shows running constantly
                // on this server), so under load a generation's data can land one processing pass
                // behind its completion marker. Clearing on the first lone seal blinked the whole
                // chat box on every split. A producer that really emptied keeps sealing with no
                // data every frame, so requiring a second CONSECUTIVE lone seal converts each
                // split into a retained frame and delays a real clear by one generation -
                // invisible either way. The run resets whenever real data arrives.
                if (route.MayHaveContent && ++route.EmptySealRun >= 2)
                {
                    PublishTransparent(route, resolution);
                    route.MayHaveContent = false;
                    Interlocked.Increment(ref route.Clears);
                    route.EmptySealRun = 0;
                }
                else if (route.MayHaveContent)
                {
                    Interlocked.Increment(ref route.ZeroKept);
                    Interlocked.Increment(ref route.SealRetained);
                    Interlocked.Increment(ref route.Retained);
                }
                return false;
            }

            // AddMessage and this consumer run on the render thread. A data bucket newer than the
            // completion marker is the beginning of the next GUI generation, so leave it queued
            // until its own marker arrives. An older bucket is stale content superseded by an
            // explicitly empty completed generation.
            if (dataFrame > sealFrame)
            {
                Interlocked.Increment(ref route.SealMismatches);
                Interlocked.Increment(ref route.SealRetained);
                Interlocked.Increment(ref route.Retained);
                return false;
            }

            if (messages == null)
                messages = MyManagers.SpritesManager.AcquireDrawMessages(route.Target);
            if (messages == null)
            {
                Interlocked.Increment(ref route.SealRetained);
                Interlocked.Increment(ref route.Retained);
                return false;
            }

            if (messages.FrameId == sealFrame) return true;

            Interlocked.Increment(ref route.StaleDiscarded);
            if (route.MayHaveContent)
            {
                PublishTransparent(route, resolution);
                route.MayHaveContent = false;
                Interlocked.Increment(ref route.Clears);
            }
            return false;
        }

        // Reads the sprite manager's draw queue without taking its lock, which is safe by thread
        // affinity rather than by synchronization: m_drawQueue is mutated only while the render
        // thread processes sprite messages, and this peek also runs on the render thread, in the
        // Process pass that sits between two such phases. There is therefore no concurrent mutation
        // to observe - not merely an unlikely one. The catch is not a race guard: it covers game
        // version drift, where m_drawQueue is renamed, retyped, or gone, and answers int.MinValue so
        // the caller falls back to acquiring the bucket outright.
        private static int PeekDrawFrameId(string target, RetainedRoute route)
        {
            try
            {
                object manager = MyManagers.SpritesManager;
                FieldInfo field = _drawQueueField;
                if (field == null)
                {
                    field = AccessTools.Field(manager.GetType(), "m_drawQueue");
                    if (field == null) throw new MissingFieldException(manager.GetType().FullName, "m_drawQueue");
                    _drawQueueField = field;
                }
                var queue = field.GetValue(manager) as IDictionary;
                var data = queue?[target] as MySpriteMessageData;
                return data?.FrameId ?? -1;
            }
            catch
            {
                Interlocked.Increment(ref route.PeekFailures);
                return int.MinValue;
            }
        }

        private static void SealCompletion(RetainedRoute route)
        {
            MyRenderProxy.SpriteScissorPush(new Rectangle(0, 0, 1, 1), route.SealTarget);
            MyRenderProxy.SpriteScissorPop(route.SealTarget);
            Interlocked.Increment(ref route.Seals);
        }

        private static void PublishTransparent(RetainedRoute route, Vector2I resolution)
        {
            IBorrowedRtvTexture layer = null;
            try
            {
                layer = MyManagers.RwTexturesPool.BorrowRtv(
                    route.SurfaceName + ".Clear", resolution.X, resolution.Y,
                    HudGpuTransport.LayerFormat);
                HudResourceTelemetry.SelectiveBorrowed();
                MyImmediateRC.RC.ClearRtv(layer, new RawColor4(0f, 0f, 0f, 0f));
                HudRedirector.CopyCompletedHud(layer, 0, false, route.Layer);
                Volatile.Write(ref route.LastPublishedCount, 0);
                // The surface no longer holds the generation that count described, so it must not go
                // on gating the partial hold: the first real batch after a clear is new content
                // arriving, not a fragment of something still on screen, and holding it would only
                // delay the layer coming back. The completeness runs are part of the same statement -
                // there is nothing left on the layer for them to be protecting.
                Volatile.Write(ref route.LastSubstantialCount, -1);
                ResetCompletenessRuns(route);
                Interlocked.Exchange(ref route.LastPublishedAt, Stopwatch.GetTimestamp());
            }
            finally
            {
                if (layer != null)
                {
                    try { layer.Release(); }
                    finally { HudResourceTelemetry.SelectiveReleased(); }
                }
            }
        }

        private static RetainedRoute Find(string key)
        {
            if (string.Equals(key, Toolbar.Key, StringComparison.OrdinalIgnoreCase)) return Toolbar;
            if (string.Equals(key, FlightHud.Key, StringComparison.OrdinalIgnoreCase)) return FlightHud;
            if (string.Equals(key, Interactive.Key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "terminal", StringComparison.OrdinalIgnoreCase)) return Interactive;
            if (string.Equals(key, Chat.Key, StringComparison.OrdinalIgnoreCase)) return Chat;
            return null;
        }

        // An empty bucket counts as a producer gap only while the route is actually holding
        // something to protect and only inside the hold window. Once the window lapses the empty
        // batch publishes normally and takes the layer down, so a closed menu always clears.
        private static bool ShouldHoldEmpty(RetainedRoute route, int count)
        {
            if (count != 0 || !route.MayHaveContent) return false;
            long publishedAt = Interlocked.Read(ref route.LastPublishedAt);
            return publishedAt != 0 && Stopwatch.GetTimestamp() - publishedAt <= EmptyHoldTicks;
        }

        // A sharp count drop is read as an incremental fragment of a generation still being emitted,
        // and held rather than published, so the layer is not cleared and redrawn a frame later.
        //
        // The reference is the last SUBSTANTIAL published count, not the last published count of any
        // size: a fragment that does publish would otherwise become the reference, drop it under the
        // floor, and disarm the guard for every fragment behind it.
        //
        // The window follows the route's own publish cadence - twice the last inter-publish gap,
        // floored at 50 ms and capped at 250 ms - because a fixed 50 ms floor is shorter than a
        // routine producer gap on a live multiplayer server, which is precisely the condition that
        // produces fragments. The cap is what keeps a producer that really stopped clearing promptly.
        private static bool ShouldRetainPartial(RetainedRoute route, int count)
        {
            if (!route.MayHaveContent) return false;
            if (!IsSubstantialDrop(route, count)) return false;

            long publishedAt = Interlocked.Read(ref route.LastPublishedAt);
            if (publishedAt == 0) return false;
            long hold = Math.Max(PartialHoldTicks, Interlocked.Read(ref route.PublishGapTicks) * 2);
            if (hold > PartialHoldMaxTicks) hold = PartialHoldMaxTicks;
            return Stopwatch.GetTimestamp() - publishedAt <= hold;
        }

        // The size half of the partial-hold question, shared with the completeness gate so both read
        // "sharp drop" the same way. Under half the last count credible as a whole generation, which
        // is a fragment-sized answer rather than a resize-sized one.
        private static bool IsSubstantialDrop(RetainedRoute route, int count)
        {
            int previous = Volatile.Read(ref route.LastSubstantialCount);
            if (previous < PartialFloor || count >= previous) return false;
            return (long)count * 2 < previous;
        }

        // Clears the completeness gate's per-run state without touching MarkerSeen, which records
        // what this route's producer does rather than where a generation got to.
        private static void ResetCompletenessRuns(RetainedRoute route)
        {
            route.FragmentRun = 0;
            route.ShrinkRun = 0;
            route.PendingShrinkCount = -1;
        }

        // Does this bucket hold the end of a generation? Seal emits its zero-pixel marker as a push
        // immediately followed by its pop, so an ADJACENT pair is the marker's exact shape and
        // nothing a producer drawing content can produce in the middle of its own work. A producer
        // that opened a clipping scope and drew nothing inside it matches too, which is a false
        // positive in the safe direction: the bucket publishes, exactly as it did before this gate
        // existed.
        private static bool ContainsSealMarker(MySpriteMessageData messages, int count)
        {
            var list = messages.Messages;
            if (list == null || list.Count < count) return false;
            for (int i = 1; i < count; i++)
            {
                if (list[i] is MyRenderMessageSpriteScissorPop
                    && list[i - 1] is MyRenderMessageSpriteScissorPush)
                    return true;
            }
            return false;
        }

        // Shape test for the zero-pixel generation marker Seal writes into an unsealed route's own
        // queue. Balanced scissor scopes move no pixels, so a bucket made only of them drew nothing
        // whether the pair came from Seal or from a producer that opened a clip and emitted no
        // sprite inside it. Either way the correct answer is the same: this generation has no
        // content of its own and the surface must be left as it is.
        private static bool IsSealMarkerOnly(MySpriteMessageData messages, int count)
        {
            if (count <= 0) return false;
            var list = messages.Messages;
            if (list == null || list.Count < count) return false;
            int depth = 0;
            for (int i = 0; i < count; i++)
            {
                MySpriteDrawRenderMessage message = list[i];
                if (message is MyRenderMessageSpriteScissorPush)
                {
                    depth++;
                    continue;
                }
                if (message is MyRenderMessageSpriteScissorPop)
                {
                    if (--depth < 0) return false;
                    continue;
                }
                return false;
            }
            return depth == 0;
        }

        private static bool InteractiveIdleExpired(RetainedRoute route)
        {
            long lastBatchAt = Interlocked.Read(ref route.LastBatchAt);
            long lastSealAt = Interlocked.Read(ref route.LastSealAt);
            long lastActivity = Math.Max(lastBatchAt, lastSealAt);
            return lastActivity != 0
                   && Stopwatch.GetTimestamp() - lastActivity >= InteractiveIdleClearTicks;
        }

        // The unsealed counterpart: no seal timestamp exists for this route, so the last data batch
        // is the only activity there is.
        private static bool RichHudIdleExpired(RetainedRoute route)
        {
            long lastBatchAt = Interlocked.Read(ref route.LastBatchAt);
            return lastBatchAt != 0
                   && Stopwatch.GetTimestamp() - lastBatchAt >= RichHudIdleClearTicks;
        }
    }
}
