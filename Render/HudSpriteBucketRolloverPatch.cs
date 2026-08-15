using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using VRage.Library.Collections;
using VRage.Render11.Sprites;
using VRageRender;
using VRageRender.ExternalApp;
using VRageRender.Messages;

namespace CleanFeed.Render
{
    // Performs MySpritesManager.AddMessage's frame-rollover disposal without an enumerator.
    //
    // THE CRASH THIS REMOVES
    //
    //   System.InvalidOperationException: InvalidOperation_EnumFailedVersion
    //     at VRage.Library.Collections.MyList`1.Enumerator.MoveNext()
    //     at VRage.Render11.Sprites.MySpritesManager.AddMessage(MyRenderMessageBase, Int32)
    //     at VRageRender.MyRender11.ProcessMessageInternal(...)   <- DrawCommands bundle unpacking
    //     at VRageRender.MyRender11.ProcessDrawFrame(...)
    //
    // AddMessage contains exactly one enumerator (MySpritesManager.cs:65-68):
    //
    //     if (value.FrameId != frameId)
    //     {
    //         foreach (MySpriteDrawRenderMessage message2 in value.Messages) message2.Dispose();
    //         value.Messages.SetSize(0);
    //         value.FrameId = frameId;
    //     }
    //
    // so EnumFailedVersion there means value.Messages.m_version moved while that loop ran. The loop
    // body cannot do it: MySpriteDrawRenderMessage.Dispose only decrements a refcount and, at -1,
    // hands the message to MyRenderProxy.MessagePool (MyRenderMessageBase.cs:37-43), whose Return is
    // a Close plus a ConcurrentQueue.Enqueue - nothing on that path touches any MySpriteMessageData.
    // The write is therefore CONCURRENT, from another thread.
    //
    // The engine has exactly three structural writers of a bucket's MyList:
    //   1. MySpritesManager.AddMessage itself - Add / SetSize (render thread only).
    //   2. MySpriteMessageData.Cleanup -> Messages.SetSize(0) (MySpriteMessageData.cs:15-18),
    //      reached from MyObjectPoolManager.Deallocate (MyObjectPoolManager.cs:47-52) and, only when
    //      the pool had to CREATE the instance, from Allocate (MyObjectPoolManager.cs:29-37 -
    //      AllocateOrCreate returns true only on the create path, so bucket reuse never re-Cleanups).
    //   3. MyDebugTextHelpers, which writes only to the never-queued DebugDrawMessages bucket.
    //
    // Writer 2 is the one that runs off the render thread. Both of the engine's own paths into it are
    // ParallelTasks workers holding a bucket that AcquireDrawMessages already removed from
    // m_drawQueue:
    //   MyRender11.RenderMainSpritesWorker  - Parallel.Start at MyRender11.cs:1699, DisposeDrawMessages
    //                                         at MyRender11.cs:1713 and 1718.
    //   MyOffscreenRenderer.RenderWork      - Parallel.Start at MyOffscreenRenderer.cs:167,
    //                                         data.Dispose -> Deallocate at MyOffscreenRenderer.cs:34.
    // Vanilla's whole safety argument is that such an instance is no longer reachable from
    // m_drawQueue, so AddMessage can never be iterating it. That argument holds only while every
    // MySpriteMessageData is Deallocated exactly once: MyGenericObjectPool.Deallocate is
    // `m_active.Remove(item); m_unused.Enqueue(item);` with NO double-return guard, so a single
    // duplicate return puts one instance in the free queue twice and two later Allocate calls - one of
    // them AddMessage's own, MySpritesManager.cs:59 - hand the SAME bucket to m_drawQueue and to a
    // worker at once. From then on any worker's Deallocate can SetSize(0) a bucket the render thread
    // is mid-rollover on, which is precisely this exception.
    //
    // WHY THE FIX IS SHAPED THIS WAY
    //
    // CleanFeed itself acquires and disposes a bucket at most once per pass on every path
    // (HudNamedSpriteFrame.Prepare, HudNamedSpriteRoutes.Process/PrepareSealedGeneration,
    // HudSuppressedSpriteQueue.Drain), and it acquires and disposes on the render thread inside
    // ConsumeMainSprites' postfix, after vanilla has joined its main-sprite worker. What CleanFeed
    // does change is the VOLUME through that pool: vanilla cycles two buckets per frame, CleanFeed up
    // to a dozen (five retained routes plus their seal targets, the player target and the suppressed
    // sink), so any pre-existing duplicate-return in the engine or in another plugin is reached
    // orders of magnitude sooner. That is why the crash shows up under CleanFeed and why the fix
    // belongs at the point of failure rather than at one suspected producer.
    //
    // The rollover does not need an enumerator, and the enumerator is the only thing that turns a
    // concurrent write into a fatal exception: MyList<T>.Enumerator captures m_size at construction
    // and compares m_version ONLY on the terminal MoveNext (MyList.cs:250-274). An index walk over
    // GetInternalArray() with the count read once reads exactly the same slots the enumerator would
    // have - SetSize only writes m_size and m_version (MyList.cs:581-589), it neither reallocates the
    // backing array nor clears its slots - so this disposes exactly the same message objects, in the
    // same order, and leaves the bucket in exactly the same state.
    //
    // SEMANTICS PRESERVED
    //
    // The prefix rolls the bucket over and stamps FrameId, so the original's own
    // `value.FrameId != frameId` branch is false and its foreach never runs; the original still does
    // the key derivation, the pool allocation for a missing bucket, the AddRef and the Add. Nothing
    // about routing, the vanilla-HUD seal, replay retargeting or the RichHud projection is involved:
    // this touches only how one already-doomed list is emptied.
    //
    // Fail-safe: any fault degrades the patch permanently (logged once) and vanilla's own rollover
    // resumes, so the worst case is the pre-existing behaviour. The disposal and the SetSize/FrameId
    // stamp are in a try/finally pair, so a fault mid-walk can never leave a half-emptied bucket for
    // the original to dispose a second time.
    [HarmonyPatch]
    internal static class HudSpriteBucketRolloverPatch
    {
        private const string DefaultTarget = "DefaultOffscreenTarget";
        // Same prefix HudCommandListPatch matches on. Every target this plugin ever writes into a
        // sprite message carries it; nothing the engine or another plugin owns does.
        private const string CleanFeedTargetPrefix = "CleanFeed.";

        private static FieldInfo _drawQueueField;
        private static object _boundManager;
        private static IDictionary _drawQueue;
        private static int _degraded;
        private static long _rollovers;
        private static long _disposed;
        private static long _repairs;
        private static long _repairEvictions;
        private static int _repairLogged;
        private static FieldInfo _workerThreadField;
        private static int _workerFieldResolved;
        private static long _workerSkips;
        private static int _workerSkipLogged;
        private static int _queueKeyHighWater;

        private static MethodBase TargetMethod()
            => AccessTools.Method(
                typeof(MySpritesManager),
                "AddMessage",
                new[] { typeof(MyRenderMessageBase), typeof(int) });

        // Both the method and the field must exist for the guard to mean anything. If either has
        // drifted the patch is not installed at all and vanilla keeps its enumerator.
        private static bool Prepare()
            => TargetMethod() != null
               && AccessTools.Field(typeof(MySpritesManager), "m_drawQueue") != null;

        // Always returns true - the original runs exactly as it always has. The bool signature and
        // the ShouldSkipOnWorker seam remain from the withdrawn worker-side skip (see the comment on
        // that method) so the detection it still performs keeps its shape and its counter.
        private static bool Prefix(object __instance, MyRenderMessageBase message, int frameId)
        {
            if (Volatile.Read(ref _degraded) != 0) return true;
            try
            {
                // Not the sprite family: leave it entirely alone. The original hard-casts and will
                // throw exactly as it does today.
                if (!(message is MySpriteDrawRenderMessage sprite)) return true;

                string target = sprite.TargetTexture;
                if (ShouldSkipOnWorker(target)) return false;

                IDictionary queue = ResolveQueue(__instance);
                if (queue == null) return true;
                SampleQueueKeys(queue);

                // Same key derivation as MySpritesManager.AddMessage:56.
                if (!(queue[target ?? DefaultTarget] is MySpriteMessageData data)) return true;

                // A bucket already stamped with this frame is not rolled over by the original
                // either; leave it untouched so the message simply appends.
                if (data.FrameId == frameId) return true;

                Rollover(data, frameId);
            }
            catch (Exception ex)
            {
                Degrade(ex);
            }

            return true;
        }

        // Keeps CleanFeed's sprite traffic off the deferred state-changes worker, which is the whole
        // of this plugin's contribution to the duplicate-key race the finalizer above catches.
        //
        // THE RACE, IN ONE PARAGRAPH
        //
        // MyRender11.Draw runs SimpleDraw (MyRender11.cs:1487) BEFORE it ever waits on m_processTask
        // (:1496), so whenever the previous frame's ProcessStateChanges task is still in flight the
        // render thread's ProcessDrawMessageQueue -> AddMessage and RenderMainSprites ->
        // AcquireDrawMessages run concurrently with that worker's own ProcessMessageQueue(draw:false)
        // -> AddMessage. Two threads, no lock, one Dictionary. What decides whether a sprite message
        // reaches the worker at all is a single field: MySpriteDrawRenderMessage.MessageClass answers
        // StateChangeOnce when TargetTexture is set and Draw when it is null, and ProcessRenderFrame's
        // filter (MyRender11.cs:2359) admits StateChangeOnce on the draw:false pass. So the moment
        // NativeSpriteTargetPatch stamps a target on the producer side, that message stops being
        // render-thread-only. This skip puts it back.
        //
        // WHY IT IS NARROWED TO CleanFeed. TARGETS
        //
        // Vanilla sets TargetTexture too - MyRenderMessageRenderOffscreenTexture is StateChangeOnce
        // (its message class, and MyOffscreenRenderer.Add at MyOffscreenRenderer.cs:94 even calls
        // AcquireDrawMessages from that same worker), which is how LCD panels and offscreen targets
        // are meant to work. That is engine-owned behaviour with engine-owned expectations about when
        // its buckets fill, and dropping those messages would break panel rendering during loading
        // for no gain. The race predates this plugin; all this skip has to remove is the plugin's
        // amplification of it, which is the difference between a handful of LCD messages and the
        // entire HUD every frame.
        //
        // DETECTION ONLY - THE SKIP ITSELF IS WITHDRAWN
        //
        // The skip shipped on the premise that ProcessStateChanges runs only behind
        // MyGuiScreenLoading's defer window (MyGuiScreenLoading.cs:289/:310), making a dropped
        // message invisible. The first live multiplayer session falsified that premise: the counter
        // recorded 632 worker-side CleanFeed adds in ten minutes of ORDINARY GAMEPLAY - roughly one
        // per second - and every one of those dropped a live HUD element's bucket-add, which the
        // operator saw as a constant flicker of elements vanishing and returning. Until the reason
        // the deferred worker pumps during gameplay on a live server is understood, dropping is
        // strictly worse than racing: the race is rare and its only fatal outcome is repaired by the
        // finalizer above, while the drop was frequent and user-visible. So this now only counts and
        // logs; the message proceeds into the original AddMessage on whatever thread it arrived on.
        private static bool ShouldSkipOnWorker(string target)
        {
            if (target == null) return false;
            if (!target.StartsWith(CleanFeedTargetPrefix, StringComparison.Ordinal)) return false;
            if (!IsProcessStateChangesThread()) return false;

            Interlocked.Increment(ref _workerSkips);
            LogWorkerSkipOnce(target);
            return false;
        }

        // MyRender11 publishes the identity of the deferred worker itself: ProcessStateChanges sets
        // MyRenderProxy.RenderThread.ProcessStateChangesThread on entry (MyRender11.cs:1541) and
        // nulls it on exit (:1547). Nothing in the engine reads it, so it is an exact, free and
        // otherwise unused answer to "am I on that worker right now". It is a plain public field
        // (MyRenderThread.cs:43) but is resolved reflectively anyway: a rename in a future build must
        // degrade this guard, not fail to bind the whole patch class.
        private static bool IsProcessStateChangesThread()
        {
            if (Volatile.Read(ref _workerFieldResolved) == 2) return false;

            FieldInfo field = _workerThreadField;
            if (field == null)
            {
                field = AccessTools.Field(typeof(MyRenderThread), "ProcessStateChangesThread");
                if (field == null || field.FieldType != typeof(Thread))
                {
                    if (Interlocked.Exchange(ref _workerFieldResolved, 2) != 2)
                        Plugin.Log("worker-side sprite skip disabled: "
                                   + "MyRenderThread.ProcessStateChangesThread is missing or retyped,"
                                   + " CleanFeed sprites keep vanilla's threading");
                    return false;
                }
                _workerThreadField = field;
                Volatile.Write(ref _workerFieldResolved, 1);
            }

            MyRenderThread renderThread = MyRenderProxy.RenderThread;
            if (renderThread == null) return false;
            return ReferenceEquals(field.GetValue(renderThread), Thread.CurrentThread);
        }

        private static void LogWorkerSkipOnce(string target)
        {
            if (Interlocked.Exchange(ref _workerSkipLogged, 1) != 0) return;
            Plugin.Log("worker-side sprite add observed on '" + target
                       + "': deferred state changes are pumping CleanFeed sprite messages off the"
                       + " render thread. The message proceeds (the withdrawn skip only counts);"
                       + " the finalizer above owns the failure mode. See worker-adds for the rate.");
        }

        // Cheapest possible health signal for the queue this class exists to protect, taken on a pass
        // that has already resolved it. A key count that climbs and never falls is the signature of
        // entries being added but never acquired, which is the one failure mode neither the rollover
        // nor the finalizer would otherwise make visible.
        private static void SampleQueueKeys(IDictionary queue)
        {
            int count = queue.Count;
            int high;
            while (count > (high = Volatile.Read(ref _queueKeyHighWater))
                   && Interlocked.CompareExchange(ref _queueKeyHighWater, count, high) != high) { }
        }

        // Survival net for the OTHER way this method dies:
        //
        //   System.ArgumentException: An item with the same key has already been added.
        //     at System.Collections.Generic.Dictionary`2.Insert(TKey key, TValue value, Boolean add)
        //     at VRage.Render11.Sprites.MySpritesManager.AddMessage(MyRenderMessageBase, Int32)
        //     at VRageRender.MyRender11.ProcessMessageInternal(...)
        //     at VRageRender.MyRender11.ProcessRenderFrame(...)
        //     at VRageRender.MyRender11.ProcessMessageQueue(...)
        //     at VRageRender.MyRender11.ProcessStateChanges()      <- ParallelTasks worker, not us
        //
        // WHAT THE EXCEPTION PROVES
        //
        // AddMessage has exactly one Add: m_drawQueue.Add(key, value) at MySpritesManager.cs:61, and
        // it is reached ONLY from inside the `if (!m_drawQueue.TryGetValue(key, out value))` miss at
        // :57. TryGetValue and Add walk the same bucket chain, with the same comparer, over the same
        // string key. On a dictionary nobody else is touching, a miss followed two lines later by a
        // duplicate hit cannot happen - and that stays true even for a dictionary corrupted into
        // holding the key twice, because then TryGetValue would find the first copy and the Add
        // would never run at all. So the entry appeared BETWEEN the two calls, which one thread
        // cannot do to itself. Another thread inserted that key. This exception IS the race; it is
        // never a symptom of a statically broken dictionary.
        //
        // WHY THE REPAIR EVICTS ALMOST NOTHING
        //
        // The corollary of the proof is the repair policy. If the key is present when we look, then
        // AddMessage's own TryGetValue will find it on the very next message and the Add branch will
        // not be entered again: the dictionary is already in a working state and there is nothing to
        // fix. Evicting there would be actively harmful - the bucket under that key is the one the
        // winning thread is holding as its local `value` and is about to AddRef and Add into
        // (MySpritesManager.cs:72-73), so removing it hands a live bucket back toward the pool while
        // its owner still writes to it. That manufactures exactly the aliasing this class's header
        // documents. So a reachable entry is left strictly alone.
        //
        // The key is removed only in the inverse case: absent to a lookup yet present to the Insert
        // that just objected. That is the genuinely inconsistent shape, and it is also the only one
        // where removal can be safe, because a lookup that answers nothing hands us no bucket - so
        // there is no MySpriteMessageData to walk, nothing to Dispose and nothing to Deallocate. The
        // evicted entry is quarantined by construction, and that is the direction to want anyway: a
        // leaked bucket costs the pool one instance forever, while a speculative Deallocate of a
        // bucket we cannot prove we own is the third double-return that turns the pool's missing
        // guard into the aliasing crash. One leak beats one more alias every time.
        //
        // WHAT HAPPENS TO THE MESSAGE
        //
        // It is dropped, with no refcount bookkeeping, because none was taken: AddRef is at
        // MySpritesManager.cs:72, strictly after the Add at :61, so an Add that threw left the
        // message at the refcount it arrived with. Ownership stays with the frame's RenderInput
        // list, which disposes it in MyRender11.ProcessPreFrame (MyRender11.cs:2383-2389) exactly as
        // it would have for any message. One sprite is missing from one frame of one target.
        //
        // The bucket the losing thread allocated at MySpritesManager.cs:59 is unreachable from here
        // - it is a local in a method that is unwinding - so it leaks. It is empty and it is one per
        // repair, which is the cheap side of the same trade.
        //
        // Fail-safe: any fault in the repair returns the ORIGINAL exception, never a new one, so the
        // worst case is the crash that was already happening. This deliberately does not consult
        // _degraded: the rollover prefix degrading has no bearing on whether a duplicate-key insert
        // should be fatal, and the frame that needs this net most is the one where something else
        // has already gone wrong.
        private static Exception Finalizer(object __instance, MyRenderMessageBase message, Exception __exception)
        {
            if (__exception == null) return null;

            // ArgumentNullException derives from ArgumentException and is not this failure - the key
            // is coalesced at MySpritesManager.cs:56 and can never be null - so a null-argument
            // fault is something else and must keep propagating. Every other ArgumentException
            // reachable inside AddMessage comes from that one Add: the cast at :55 throws
            // InvalidCastException, the pool allocation at :59 throws from Activator, and
            // MyList.Add throws nothing of this shape.
            if (!(__exception is ArgumentException) || __exception is ArgumentNullException)
                return __exception;

            string key = null;
            try
            {
                if (!(message is MySpriteDrawRenderMessage sprite)) return __exception;

                IDictionary queue = ResolveQueue(__instance);
                if (queue == null) return __exception;

                // Same key derivation as MySpritesManager.AddMessage:56.
                key = sprite.TargetTexture ?? DefaultTarget;

                if (!(queue[key] is MySpriteMessageData))
                {
                    // Unreachable to a lookup, yet the Insert saw it. Unlink it if the chain still
                    // exposes it. Nothing is disposed and nothing is deallocated: see above.
                    queue.Remove(key);
                    Interlocked.Increment(ref _repairEvictions);
                }

                Interlocked.Increment(ref _repairs);
                LogRepairOnce(key);
                return null;
            }
            catch (Exception repairFault)
            {
                LogRepairFaultOnce(key, repairFault);
                return __exception;
            }
        }

        // Once, by key, because the condition is a race and a race that has fired can fire every
        // frame. A counter is the right instrument for the rate; the log line only has to say which
        // target queue it was so the producer can be identified.
        private static void LogRepairOnce(string key)
        {
            if (Interlocked.Exchange(ref _repairLogged, 1) != 0) return;
            Plugin.Log("sprite draw queue duplicate key repaired on '" + key
                       + "': AddMessage lost an insert race for that target and the message was"
                       + " dropped. Concurrent structural writes to MySpritesManager.m_drawQueue;"
                       + " see addmessage-repairs for the rate.");
        }

        private static void LogRepairFaultOnce(string key, Exception ex)
        {
            if (Interlocked.Exchange(ref _repairLogged, 1) != 0) return;
            Plugin.Log("sprite draw queue duplicate-key repair failed on '" + (key ?? "?")
                       + "', original exception rethrown: " + ex);
        }

        // Byte-for-byte the vanilla rollover, minus the enumerator. Count is read once, exactly as
        // MyList<T>.Enumerator captures m_size in its constructor, and the backing array reference is
        // taken once because a concurrent SetSize cannot replace or clear it.
        private static void Rollover(MySpriteMessageData data, int frameId)
        {
            MyList<MySpriteDrawRenderMessage> messages = data.Messages;
            if (messages == null) return;
            try
            {
                MySpriteDrawRenderMessage[] items = messages.GetInternalArray();
                int count = messages.Count;
                if (items == null) count = 0;
                else if (count > items.Length) count = items.Length;
                for (int i = 0; i < count; i++)
                {
                    MySpriteDrawRenderMessage current = items[i];
                    if (current != null) current.Dispose();
                }
                Interlocked.Add(ref _disposed, count);
            }
            finally
            {
                // Unconditional: the bucket must end in the state the original's rollover would have
                // left it in, or the original re-enters its own foreach over messages this pass has
                // already disposed.
                messages.SetSize(0);
                data.FrameId = frameId;
                Interlocked.Increment(ref _rollovers);
            }
        }

        // m_drawQueue is a readonly field, so the dictionary instance is stable for the manager's
        // lifetime and is cached rather than re-read per sprite message - this sits on the hottest
        // render seam there is. A benign race just resolves the same reference twice.
        private static IDictionary ResolveQueue(object manager)
        {
            if (manager == null) return null;
            if (ReferenceEquals(manager, _boundManager)) return _drawQueue;

            FieldInfo field = _drawQueueField;
            if (field == null)
            {
                field = AccessTools.Field(manager.GetType(), "m_drawQueue");
                if (field == null)
                    throw new MissingFieldException(manager.GetType().FullName, "m_drawQueue");
                _drawQueueField = field;
            }

            var queue = field.GetValue(manager) as IDictionary;
            if (queue == null)
                throw new InvalidOperationException("m_drawQueue is not a dictionary");
            _drawQueue = queue;
            _boundManager = manager;
            return queue;
        }

        private static void Degrade(Exception ex)
        {
            if (Interlocked.Exchange(ref _degraded, 1) != 0) return;
            _boundManager = null;
            _drawQueue = null;
            Plugin.Log("sprite bucket rollover guard degraded to vanilla: " + ex);
        }

        // Reaches BOTH operator surfaces without either of them naming this class: HudRedirector's
        // verbose StatusLine and its HealthStatusLine each end in HudNamedSpriteFrame.StatusLine,
        // which chains here. Anything added below is therefore in the health line too, and the pool
        // guard rides along on the same chain rather than needing its own call site.
        internal static string StatusLine()
            => "bucket-rollover=" + (Volatile.Read(ref _degraded) != 0 ? "degraded" : "guarded")
               + ", rollovers:" + Interlocked.Read(ref _rollovers)
               + ", disposed:" + Interlocked.Read(ref _disposed)
               // Non-zero means the engine ran two threads through AddMessage at once. It is not a
               // CleanFeed fault counter, it is a concurrency detector, and any non-zero reading is
               // worth reporting: evictions is the subset where the dictionary was inconsistent
               // rather than merely contended.
               + ", addmessage-repairs:" + Interlocked.Read(ref _repairs)
               + "/" + Interlocked.Read(ref _repairEvictions)
               // Expected to be non-zero only across a loading screen, and expected to stop rising
               // the moment one ends. Rising during gameplay would mean ProcessStateChanges is being
               // pumped somewhere this analysis did not find.
               + ", worker-adds:" + Interlocked.Read(ref _workerSkips)
               + ", drawqueue-keys-max:" + Volatile.Read(ref _queueKeyHighWater);
    }
}
