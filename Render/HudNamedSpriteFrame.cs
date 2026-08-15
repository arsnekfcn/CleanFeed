using System;
using System.Diagnostics;
using System.Threading;
using SharpDX.Mathematics.Interop;
using VRage.Render11.Common;
using VRage.Render11.Resources;
using VRage.Render11.Sprites;
using VRageMath;
using VRageRender;

namespace CleanFeed.Render
{
    // The named queue is processed synchronously after ConsumeMainSprites has joined vanilla's
    // asynchronous main-sprite worker. Never call RenderMainSpritesWorker directly and never touch
    // the sprite manager while vanilla's worker is running.
    internal static class HudNamedSpriteFrame
    {
        internal const string PlayerTarget = "CleanFeed.PlayerOnly";
        private const string SurfaceName = "CleanFeed.PlayerOnly.Surface";
        // How long the seal may keep answering retain over held content before the layer is taken
        // down anyway. Long enough that no legitimate GUI cadence reaches it - a vanilla generation
        // arrives every few frames even at the lowest GUI rates - and short enough that a wedged
        // layer is a blink rather than a dead HUD painted over whatever replaced it.
        private static readonly long RetainTimeoutTicks = Stopwatch.Frequency * 7 / 10; // 700 ms
        private static IBorrowedRtvTexture _layer;
        private static bool _publish;
        private static bool _holdTransientBatches;
        private static int _messageCount;
        private static long _frames;
        private static long _published;
        private static long _drained;
        private static long _overlapRejects;
        private static long _hudHiddenClears;
        private static long _retainTimeoutClears;
        private static long _retainRun;
        private static long _retainRunHighWater;
        private static long _lastPublishAt;
        private static bool _mayHaveContent;
        private static bool _hudHiddenCleared;
        private static bool _retainTimeoutCleared;
        private static bool _retainSawHidden;
        private static long _countSamples;
        private static long _countChanges;
        private static long _largeCountSwings;
        private static int _lastCount = -1;
        private static int _minimumCount = int.MaxValue;
        private static int _maximumCount;

        internal static void Prepare(Vector2I resolution, bool publish)
        {
            if (_layer != null)
            {
                Interlocked.Increment(ref _overlapRejects);
                HudRedirector.FailOpen("selective frame overlap",
                    new InvalidOperationException("RenderMainSprites ran again before ConsumeMainSprites completed"));
                return;
            }

            MySpriteMessageData messages = null;
            IBorrowedRtvTexture layer = null;
            try
            {
                messages = MyManagers.SpritesManager.AcquireDrawMessages(PlayerTarget);
                int count = messages?.Messages?.Count ?? 0;
                if (!publish)
                {
                    if (messages != null)
                    {
                        Interlocked.Increment(ref _frames);
                        Interlocked.Increment(ref _drained);
                    }
                    // The hide latch is released here as well as on the visible publish path
                    // below. A drain frame is not a hide, and nothing published on it can be left
                    // on screen, so leaving the latch set would let a publish-drain-publish cycle
                    // that spanned a hide skip the one clear that hide was owed.
                    _hudHiddenCleared = false;
                    // Same reasoning for the retain watch: a pass that is not publishing at all is
                    // not a stalled publisher, so it must not accumulate elapsed time against the
                    // retain timeout and clear a healthy layer on the frame publication resumes.
                    ResetRetainWatch();
                    return;
                }

                // Vanilla has stopped drawing the player HUD. The transport holds the last published
                // copy of this layer, and with no generations arriving nothing would ever take it
                // down, so publish one transparent layer and then stay quiet until the HUD returns.
                // Classify is skipped on purpose: a hidden frame is not a vanilla generation, and
                // recording it would advance the seal's handled sequence past a draw that never
                // happened. _hudHiddenCleared latches the clear to once per hide rather than once
                // per frame, and releases only when the HUD is visible again.
                if (HudRedirector.GameHudHidden())
                {
                    if (messages != null)
                    {
                        Interlocked.Increment(ref _frames);
                        Interlocked.Increment(ref _drained);
                    }
                    if (!_hudHiddenCleared)
                    {
                        _hudHiddenCleared = true;
                        if (_mayHaveContent)
                        {
                            PublishTransparent(resolution);
                            Interlocked.Increment(ref _hudHiddenClears);
                        }
                    }
                    return;
                }
                _hudHiddenCleared = false;

                ObserveCount(count);

                VanillaHudSealDecision sealDecision = VanillaHudGenerationSeal.Classify(messages);
                if (sealDecision == VanillaHudSealDecision.RetainNoGeneration
                    || sealDecision == VanillaHudSealDecision.RetainMismatch)
                {
                    ObserveRetain(resolution);
                    return;
                }
                Interlocked.Exchange(ref _retainRun, 0);
                _retainSawHidden = false;
                bool holdTransientBatches = sealDecision == VanillaHudSealDecision.Fallback;

                if (messages == null)
                {
                    layer = MyManagers.RwTexturesPool.BorrowRtv(
                        SurfaceName, resolution.X, resolution.Y, HudGpuTransport.LayerFormat);
                    MyImmediateRC.RC.ClearRtv(layer, new RawColor4(0f, 0f, 0f, 0f));
                }
                else
                {
                    // Blend state deliberately null: the sprite pass then uses BlendAlphaPremult
                    // (colour Src=One), exactly as vanilla's own main-sprite pass does. The sprite
                    // pixel shaders already emit premultiplied colour - straight-alpha batches are
                    // premultiplied in-shader - so Src=One is correct on a transparent layer and
                    // the alpha channel accumulates true coverage. Substituting a Src=SourceAlpha
                    // colour equation multiplies the premultiplied colour by alpha a second time
                    // (rgb*a^2, translucent elements go dark/ghostly) and zeroes out cached label
                    // textures such as chat text, whose content is premultiplied RGB at alpha 0.
                    layer = MyRender11.DrawSpritesOffscreen(
                        messages,
                        SurfaceName,
                        resolution.X,
                        resolution.Y,
                        HudGpuTransport.LayerFormat,
                        new RawColor4(0f, 0f, 0f, 0f));
                }

                if (layer == null) throw new InvalidOperationException("DrawSpritesOffscreen returned no layer");
                HudResourceTelemetry.SelectiveBorrowed();
                _layer = layer;
                _publish = publish;
                _holdTransientBatches = holdTransientBatches;
                _messageCount = count;
                layer = null;
                Interlocked.Increment(ref _frames);
            }
            catch (Exception ex)
            {
                if (layer != null)
                {
                    try { layer.Release(); }
                    finally { HudResourceTelemetry.SelectiveReleased(); }
                }
                HudRedirector.FailOpen("selective sprite consume", ex);
            }
            finally
            {
                // DrawSpritesOffscreen has synchronously converted messages to draw batches, and
                // vanilla's asynchronous main-sprite worker has already been joined.
                if (messages != null) MyManagers.SpritesManager.DisposeDrawMessages(messages);
            }
        }

        internal static void Complete()
        {
            IBorrowedRtvTexture layer = _layer;
            if (layer == null) return;
            bool publish = _publish;
            bool holdTransientBatches = _holdTransientBatches;
            int count = _messageCount;
            _layer = null;
            _publish = false;
            _holdTransientBatches = false;
            _messageCount = 0;
            try
            {
                if (publish && HudRedirector.SelectiveActive)
                {
                    // SE's GUI producer cadence can leave the named queue empty on alternating
                    // render frames at high refresh rates. Bridge those transient gaps using the
                    // transport's 50 ms hold; a genuinely removed HUD still clears after timeout.
                    HudRedirector.CopyCompletedHud(layer, count, holdTransientBatches);
                    _mayHaveContent = true;
                    // A real publish is what releases the retain-timeout latch and restarts its
                    // clock. Until one lands, one transparent clear per wedge is all the timeout
                    // is entitled to.
                    Interlocked.Exchange(ref _lastPublishAt, Stopwatch.GetTimestamp());
                    _retainTimeoutCleared = false;
                    Interlocked.Increment(ref _published);
                }
            }
            catch (Exception ex) { HudRedirector.FailOpen("selective sprite completion", ex); }
            finally
            {
                try { layer.Release(); }
                finally { HudResourceTelemetry.SelectiveReleased(); }
            }
        }

        internal static void ReleaseAndClear()
        {
            IBorrowedRtvTexture layer = _layer;
            _layer = null;
            _publish = false;
            _holdTransientBatches = false;
            _messageCount = 0;
            _mayHaveContent = false;
            _hudHiddenCleared = false;
            ResetRetainWatch();
            if (layer == null) return;
            try { layer.Release(); }
            finally { HudResourceTelemetry.SelectiveReleased(); }
        }

        // Same shape as the messages == null branch in Prepare, but published immediately rather
        // than staged for Complete: this runs on a pass that has no layer of its own to hand over.
        // CopyCompletedHud stays gated on Active exactly as elsewhere, and holdTransientBatches is
        // false so the transport's 50 ms gap bridge cannot resurrect the copy being taken down.
        //
        // Reason counters belong to the callers: two different conditions clear this layer and an
        // operator has to be able to tell which one fired.
        private static void PublishTransparent(Vector2I resolution)
        {
            IBorrowedRtvTexture layer = null;
            try
            {
                layer = MyManagers.RwTexturesPool.BorrowRtv(
                    SurfaceName + ".Clear", resolution.X, resolution.Y, HudGpuTransport.LayerFormat);
                HudResourceTelemetry.SelectiveBorrowed();
                MyImmediateRC.RC.ClearRtv(layer, new RawColor4(0f, 0f, 0f, 0f));
                HudRedirector.CopyCompletedHud(layer, 0, false);
                _mayHaveContent = false;
                Interlocked.Exchange(ref _lastPublishAt, Stopwatch.GetTimestamp());
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

        // The seal's two retain outcomes have no exit of their own. RetainNoGeneration answers while
        // no newer vanilla generation has been processed and RetainMismatch while the newest one
        // disagrees with the bucket's frame id, and a hide/blip can leave the pair alternating
        // indefinitely: hidden frames deliberately skip Classify, so the handled sequence jumps and
        // the frame ids can stay out of step until some later generation happens to line up again.
        // Meanwhile the transport keeps showing the last committed content, which by then belongs to
        // a HUD state that is gone.
        //
        // The escape keys off TIME since the last publish, never off run length. Consecutive retains
        // are the normal steady state, not a symptom: whenever render fps exceeds GUI fps the seal
        // answers RetainNoGeneration on the frames in between, so any small run-count threshold
        // would fire continuously on a healthy HUD.
        //
        // The clear is latched to one per wedge and released by the next real publish, and it cannot
        // duplicate the hide clear: that path publishes transparent and leaves _mayHaveContent
        // false, so a wedge entered after a hide finds nothing left to take down.
        private static void ObserveRetain(Vector2I resolution)
        {
            long run = Interlocked.Increment(ref _retainRun);
            long highWater;
            while (run > (highWater = Interlocked.Read(ref _retainRunHighWater))
                   && Interlocked.CompareExchange(ref _retainRunHighWater, run, highWater) != highWater) { }

            // The wedge this timeout exists for is entered THROUGH a hidden state: a hide or a
            // suppression stalls publication and the layer resumes against mismatched frame ids.
            // A retain run with no hidden observation is just the GUI thread running behind -
            // signal-dense areas stall generations past the threshold routinely - and blanking a
            // healthy layer on every such stall read as rapid marker flicker in the field
            // (retain-timeout-clears=80 in one session). Load stalls therefore FREEZE, which is
            // the pre-timeout behaviour, and only hidden-tainted runs may clear.
            if (HudRedirector.GameHudHidden()) _retainSawHidden = true;

            if (_retainTimeoutCleared || !_mayHaveContent || !_retainSawHidden) return;
            long publishedAt = Interlocked.Read(ref _lastPublishAt);
            if (publishedAt == 0 || Stopwatch.GetTimestamp() - publishedAt <= RetainTimeoutTicks) return;

            _retainTimeoutCleared = true;
            PublishTransparent(resolution);
            Interlocked.Increment(ref _retainTimeoutClears);
        }

        private static void ResetRetainWatch()
        {
            Interlocked.Exchange(ref _retainRun, 0);
            Interlocked.Exchange(ref _lastPublishAt, 0);
            _retainTimeoutCleared = false;
            _retainSawHidden = false;
        }

        internal static string StatusLine()
        {
            long samples = Interlocked.Read(ref _countSamples);
            return "selective-frames=" + Interlocked.Read(ref _frames)
                   + ", published=" + Interlocked.Read(ref _published)
                   + ", drained=" + Interlocked.Read(ref _drained)
                   + ", overlap-rejects=" + Interlocked.Read(ref _overlapRejects)
                   + ", hud-hidden-clears=" + Interlocked.Read(ref _hudHiddenClears)
                   + ", retain-timeout-clears=" + Interlocked.Read(ref _retainTimeoutClears)
                   + ", retain-run=" + Interlocked.Read(ref _retainRun)
                   + "/" + Interlocked.Read(ref _retainRunHighWater)
                   + ", batches=" + samples
                   + ", count=" + Volatile.Read(ref _lastCount)
                   + ", min=" + (samples == 0 ? 0 : Volatile.Read(ref _minimumCount))
                   + ", max=" + Volatile.Read(ref _maximumCount)
                   + ", count-changes=" + Interlocked.Read(ref _countChanges)
                   + ", large-swings=" + Interlocked.Read(ref _largeCountSwings)
                   + "; " + VanillaHudGenerationSeal.StatusLine()
                   + "; " + HudNamedSpriteRoutes.StatusLine()
                   + "; " + HudSuppressedSpriteQueue.StatusLine()
                   + "; " + HudModalState.StatusLine()
                   + "; " + HudSpriteBucketRolloverPatch.StatusLine();
        }

        private static void ObserveCount(int count)
        {
            int previous = Volatile.Read(ref _lastCount);
            if (previous >= 0 && previous != count)
            {
                Interlocked.Increment(ref _countChanges);
                int difference = Math.Abs(previous - count);
                if (difference >= Math.Max(32, previous / 4))
                    Interlocked.Increment(ref _largeCountSwings);
            }
            Volatile.Write(ref _lastCount, count);
            Interlocked.Increment(ref _countSamples);

            int minimum;
            while (count < (minimum = Volatile.Read(ref _minimumCount))
                   && Interlocked.CompareExchange(ref _minimumCount, count, minimum) != minimum) { }
            int maximum;
            while (count > (maximum = Volatile.Read(ref _maximumCount))
                   && Interlocked.CompareExchange(ref _maximumCount, count, maximum) != maximum) { }
        }
    }

    // Sink for categories the player has hidden from both outputs. Its batches are acquired and
    // dropped without ever being drawn.
    internal static class HudSuppressedSpriteQueue
    {
        internal const string Target = "CleanFeed.Suppressed";
        private static long _batches;
        private static long _messages;
        private static long _emptyBatches;

        internal static void Drain()
        {
            MySpriteMessageData messages = null;
            try
            {
                messages = MyManagers.SpritesManager.AcquireDrawMessages(Target);
                if (messages == null) return;
                int count = messages.Messages?.Count ?? 0;
                Interlocked.Increment(ref _batches);
                Interlocked.Add(ref _messages, count);
                if (count == 0) Interlocked.Increment(ref _emptyBatches);
            }
            finally
            {
                if (messages != null) MyManagers.SpritesManager.DisposeDrawMessages(messages);
            }
        }

        internal static string StatusLine()
            => "suppressed=batches:" + Interlocked.Read(ref _batches)
               + ", messages:" + Interlocked.Read(ref _messages)
               + ", empty:" + Interlocked.Read(ref _emptyBatches);
    }
}
