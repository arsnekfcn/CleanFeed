using System;
using System.Runtime.CompilerServices;
using System.Threading;
using CleanFeed.Profiles;

namespace CleanFeed.Compatibility
{
    // Identity of a HUD producer plus its resolved policy. One tag is shared by every draw call from
    // the same source, so it is also where per-sprite policy work is memoised.
    internal sealed class HudRouteTag
    {
        // Profile and registry generations packed into one word, so policy answers are recomputed
        // only when either side changes. Resolving uncached re-normalises an already-canonical key,
        // which allocates two strings per sprite draw.
        private long _policyStamp = -1L;
        private long _recordStamp = -1L;
        private HudEffectiveRoute _policyRoute;
        private HudSourceRegistry.SourceRecord _record;

        internal HudRouteTag(
            string key,
            string instanceId = null,
            bool? recordingOverride = null,
            bool? playerVisibleOverride = null,
            HudEffectiveRoute? forceRoute = null)
        {
            Key = HudProfileService.NormalizeKey(key);
            InstanceId = instanceId;
            RecordingOverride = recordingOverride;
            PlayerVisibleOverride = playerVisibleOverride;
            ForceRoute = forceRoute;
        }

        internal string Key { get; }
        internal string InstanceId { get; }
        internal bool? RecordingOverride { get; }
        internal bool? PlayerVisibleOverride { get; }
        internal HudEffectiveRoute? ForceRoute { get; }

        internal HudEffectiveRoute ConfiguredRoute
        {
            get
            {
                if (ForceRoute.HasValue) return ForceRoute.Value;
                HudProfileSnapshot profile = HudProfileService.Current;
                long stamp = Stamp(profile.Generation, HudSourceRegistry.Generation);
                // Acquire on the stamp pairs with the release below, so a reader that observes the
                // current stamp also observes the route written before it.
                if (Volatile.Read(ref _policyStamp) == stamp) return _policyRoute;

                HudEffectiveRoute route = profile.EffectiveRouteNormalized(
                    Key, RecordingOverride, PlayerVisibleOverride);
                _policyRoute = route;
                Volatile.Write(ref _policyStamp, stamp);
                return route;
            }
        }

        // Activity counters live on a registry record whose object identity is stable across the
        // registry's copy-on-write dictionary swaps, so the reference is held rather than looked up
        // by string key on every primitive.
        internal HudSourceRegistry.SourceRecord ActivityRecord
        {
            get
            {
                long stamp = HudSourceRegistry.Generation;
                if (Volatile.Read(ref _recordStamp) == stamp) return _record;
                HudSourceRegistry.SourceRecord record = HudSourceRegistry.ResolveRecord(Key);
                _record = record;
                Volatile.Write(ref _recordStamp, stamp);
                return record;
            }
        }

        private static long Stamp(int profileGeneration, int registryGeneration)
            => ((long)profileGeneration << 32) | (uint)registryGeneration;

        public override string ToString()
            => Key + (InstanceId == null ? string.Empty : "#" + InstanceId)
               + "=" + (ConfiguredRoute == HudEffectiveRoute.Recorded
                   ? "record"
                   : ConfiguredRoute == HudEffectiveRoute.PlayerOnly ? "player" : "suppressed");
    }

    internal static class HudRouteContext
    {
        // Depth of the enclosing-scope chain that is actually retained. Scope nesting is driven by
        // HudAdapterCoordinator's Draw scopes, which are two or three deep in practice; eight is
        // slack, not a budget.
        private const int MaxTrackedDepth = 8;

        [ThreadStatic] private static HudRouteTag _current;
        // Parents of the scopes currently on the stack: _parents[i] is the tag that was Current when
        // the i-th scope was pushed. Thread-affine like _current, so no locking. Callers still thread
        // the previous tag through Push/Restore themselves; this chain only adds the ancestors that
        // an echo decision needs, and never changes what Current returns.
        [ThreadStatic] private static HudRouteTag[] _parents;
        [ThreadStatic] private static int _depth;

        private static readonly object TagGate = new object();
        private static ConditionalWeakTable<object, HudRouteTag> _tags =
            new ConditionalWeakTable<object, HudRouteTag>();

        internal static HudRouteTag Current => _current;

        // The scope enclosing the innermost one, or null at the outermost scope. Beyond
        // MaxTrackedDepth the parent is simply not tracked and this reads null - the callers treat a
        // null parent as "nothing to do", so over-deep nesting degrades to today's behaviour rather
        // than to a wrong answer.
        internal static HudRouteTag CurrentParent
            => _depth >= 1 && _depth <= MaxTrackedDepth ? _parents[_depth - 1] : null;

        internal static HudRouteTag Push(HudRouteTag tag)
        {
            HudRouteTag previous = _current;
            if (_depth < MaxTrackedDepth)
            {
                HudRouteTag[] parents = _parents ?? (_parents = new HudRouteTag[MaxTrackedDepth]);
                parents[_depth] = previous;
            }
            // Counted even when it is not stored, so Restore's decrement stays paired with Push and
            // the depth returns to zero at the end of the frame either way.
            _depth++;
            _current = tag;
            return previous;
        }

        internal static void Restore(HudRouteTag previous)
        {
            _current = previous;
            if (_depth > 0) _depth--;
            // Cleared so a finished scope's tag is not kept alive by the thread's slot.
            if (_depth < MaxTrackedDepth && _parents != null) _parents[_depth] = null;
        }

        // net48's ConditionalWeakTable has no atomic AddOrUpdate, so a changed mapping needs a
        // Remove/Add pair under the lock. Re-tagging an unchanged mapping must return without
        // touching the table, or the Remove/Add window lets a concurrent reader miss the entry and
        // route a filtered sprite to the default target. Reads keep the lock for the same reason:
        // TryGetValue is not documented as safe against a concurrent write, and the failure mode is
        // a leak into the recording.
        internal static void Tag(object value, HudRouteTag tag)
        {
            if (value == null || tag == null) return;
            lock (TagGate)
            {
                if (_tags.TryGetValue(value, out HudRouteTag existing))
                {
                    if (ReferenceEquals(existing, tag)) return;
                    _tags.Remove(value);
                }
                _tags.Add(value, tag);
            }
        }

        internal static HudRouteTag Lookup(object value)
        {
            if (value == null) return null;
            lock (TagGate) return _tags.TryGetValue(value, out HudRouteTag tag) ? tag : null;
        }

        internal static void Reset()
        {
            _current = null;
            _depth = 0;
            if (_parents != null) Array.Clear(_parents, 0, _parents.Length);
            lock (TagGate) _tags = new ConditionalWeakTable<object, HudRouteTag>();
        }
    }
}
