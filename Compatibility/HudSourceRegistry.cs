using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using CleanFeed.Profiles;

namespace CleanFeed.Compatibility
{
    internal enum HudSourceProvenance
    {
        Verified,
        Detected,
        Unattributed
    }

    internal enum HudSourceCapability
    {
        Inactive,
        Partitioned,
        PartitionedPartial,
        ObserveOnly
    }

    [Flags]
    internal enum HudPrimitiveFamily
    {
        None = 0,
        NativeSprite = 1,
        Billboard = 2,
        Triangle = 4,
        PersistentBillboard = 8,
        WorldLine = 16,
        CustomProjection = 32,
        Unknown = 64
    }

    internal sealed class HudSourceDescriptor
    {
        internal string PolicyKey { get; set; }
        internal string ProviderId { get; set; }
        internal string DisplayName { get; set; }
        internal string ProviderVersion { get; set; }
        internal string CategoryId { get; set; }
        internal string Provenance { get; set; }
        internal HudSourceCapability Capability { get; set; }
        internal bool Active { get; set; }
        internal bool Controllable { get; set; }
        internal string SupportedPrimitives { get; set; }
        internal string UnsupportedPrimitives { get; set; }
        internal string Detail { get; set; }
        internal long ActivityCount { get; set; }
        internal long LastSeenMilliseconds { get; set; }
        internal bool RecordingVisible { get; set; }
        internal bool PlayerVisible { get; set; }
        internal HudEffectiveRoute EffectiveRoute { get; set; }
    }

    internal sealed class HudSourceRegistrySnapshot
    {
        internal HudSourceRegistrySnapshot(int generation, HudSourceDescriptor[] sources)
        {
            Generation = generation;
            Sources = sources;
        }

        internal int Generation { get; }
        internal HudSourceDescriptor[] Sources { get; }
    }

    internal static class HudSourceRegistry
    {
        // Internal rather than private so a HudRouteTag can hold the record it reports activity
        // against. Record instances survive the copy-on-write dictionary swaps in AddRecordLocked,
        // so a held reference stays valid; the registry generation guards the rest.
        internal sealed class SourceRecord
        {
            internal string PolicyKey;
            internal string ProviderId;
            internal string DisplayName;
            internal string ProviderVersion;
            internal string CategoryId;
            internal HudSourceProvenance Provenance;
            internal HudSourceCapability DeclaredCapability;
            internal HudSourceCapability RuntimeCapability;
            internal bool Active;
            internal bool Controllable;
            internal bool Infrastructure;
            internal string OwnerKey;
            internal string Supported;
            internal string Unsupported;
            internal string Detail;
            internal string Fingerprint;
            internal int Targets;
            internal long ActivityCount;
            internal long LastSeen;
            internal int ObservedFamilies;
            internal int UnsupportedFamilies;
        }

        private const int MaxDynamicSources = 256;
        private const int MaxFingerprintMismatchLogs = 64;
        private static readonly object Gate = new object();
        private static Dictionary<string, SourceRecord> _lookup =
            new Dictionary<string, SourceRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> MismatchLogged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int _generation;
        private static int _dynamicSources;
        private static long _evictions;
        private static bool _initialized;

        // Count of dynamic-source fingerprint mismatches observed this session. A mismatch silently
        // reverts a user's confirmed filter for that source back to recorded, so the fact that it
        // happened must leave a trace somewhere the user can reach: StatusLine reports the count and
        // the first mismatch per key is logged.
        internal static long FingerprintMismatches;

        internal static int Generation => Volatile.Read(ref _generation);

        internal static void Initialize()
        {
            lock (Gate)
            {
                if (_initialized) return;
                _initialized = true;

                AddExplicit("terminal", "Terminal and menus", "space-engineers", HudSourceCapability.Partitioned,
                    "native-sprite", string.Empty);
                AddExplicit("chat", "Chat", "space-engineers", HudSourceCapability.Partitioned,
                    "native-sprite", string.Empty);
                AddExplicit("toolbar", "Toolbar", "space-engineers", HudSourceCapability.Partitioned,
                    "native-sprite,text-hud", string.Empty);
                AddExplicit("speed", "Speed", "space-engineers", HudSourceCapability.Partitioned,
                    "native-sprite", string.Empty);
                AddExplicit("gps", "GPS and antenna markers", "space-engineers", HudSourceCapability.Partitioned,
                    "native-sprite", string.Empty);
                AddExplicit("battery-fuel", "Battery, fuel, and power", "space-engineers",
                    HudSourceCapability.Partitioned, "native-sprite", string.Empty);
                AddExplicit("hudlcd", "HUDLCD", "hudlcd", HudSourceCapability.Partitioned,
                    "text-hud,billboard", "persistent/custom primitives may fail open");
                AddExplicit("weaponcore.reload", "WeaponCore reload HUD", "weaponcore",
                    HudSourceCapability.PartitionedPartial, "native-sprite,billboard", "triangles/world geometry");
                AddExplicit("weaponcore.radar", "WeaponCore radar HUD", "weaponcore",
                    HudSourceCapability.PartitionedPartial, "native-sprite,billboard", "triangles/world geometry");
                AddExplicit("wc-radar", "WC Radar", "wc-radar", HudSourceCapability.PartitionedPartial,
                    "text-hud,billboard", "world lines/deferred geometry");
                AddExplicit("flight-vector", "Flight Vector", "flight-vector",
                    HudSourceCapability.PartitionedPartial, "text-hud,billboard", "world lines");
                AddExplicit("flight-hud", "Flight HUD", "flight-hud",
                    HudSourceCapability.PartitionedPartial, "text-hud,billboard", "non-billboard geometry");
                AddExplicit("thrust-beacon", "Thrust/Power Signals", "thrust-beacon",
                    HudSourceCapability.PartitionedPartial, "text-hud,billboard", "managed native beacon marker");
                AddExplicit("flip-burn", "Flip and Burn nav HUD", "flip-and-burn",
                    HudSourceCapability.PartitionedPartial, "text-hud,billboard",
                    "world gates/rings/spheres");
                AddExplicit("gamecore", "GameCore PDA (Hand Terminal)", "gamecore",
                    HudSourceCapability.PartitionedPartial, "billboard",
                    "hangar hologram/world geometry");

                AddInfrastructure("interactive-ui", "Interactive UI bridge", "terminal");
                AddInfrastructure("texthud", "Text HUD backend", "hudlcd");
                AddInfrastructure("buildinfo-toolbar", "BuildInfo toolbar bridge", "toolbar");
                // Scopes the nav menu, which draws through RichHud rather than through
                // FlipAndBurn.Hud.Draw, so its state belongs with flip-burn rather than on its own row.
                AddInfrastructure("richhud-nav", "RichHud nav menu bridge", "flip-burn");
                // The gamecore row is partitioned-partial because of world geometry the route can
                // never reach; this row says whether the routing half is installed at all, which is
                // the part an operator can act on.
                AddInfrastructure("richhud-pda", "RichHud PDA bridge", "gamecore");
                PublishLookupLocked();
            }
        }

        internal static void Shutdown()
        {
            lock (Gate)
            {
                _lookup = new Dictionary<string, SourceRecord>(StringComparer.OrdinalIgnoreCase);
                MismatchLogged.Clear();
                _generation = 0;
                _dynamicSources = 0;
                _evictions = 0;
                Interlocked.Exchange(ref FingerprintMismatches, 0);
                _initialized = false;
            }
        }

        private static void AddExplicit(
            string key, string displayName, string providerId,
            HudSourceCapability capability, string supported, string unsupported)
        {
            _lookup[key] = new SourceRecord
            {
                PolicyKey = key,
                ProviderId = providerId,
                DisplayName = displayName,
                ProviderVersion = "built-in",
                CategoryId = key,
                Provenance = HudSourceProvenance.Verified,
                DeclaredCapability = capability,
                RuntimeCapability = HudSourceCapability.Inactive,
                Controllable = true,
                Supported = supported,
                Unsupported = unsupported,
                Detail = "awaiting provider scan"
            };
        }

        private static void AddInfrastructure(string key, string displayName, string ownerKey)
        {
            _lookup[key] = new SourceRecord
            {
                PolicyKey = key,
                ProviderId = "cleanfeed-adapter",
                DisplayName = displayName,
                ProviderVersion = "built-in",
                CategoryId = key,
                Provenance = HudSourceProvenance.Verified,
                DeclaredCapability = HudSourceCapability.Partitioned,
                RuntimeCapability = HudSourceCapability.Inactive,
                Controllable = false,
                Infrastructure = true,
                OwnerKey = ownerKey,
                Detail = "awaiting provider scan"
            };
        }

        internal static void SetExplicitStatus(string key, string capability, int targets, string detail)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate)
            {
                if (!_lookup.TryGetValue(key, out SourceRecord record))
                {
                    record = new SourceRecord
                    {
                        PolicyKey = key,
                        ProviderId = "explicit-adapter",
                        DisplayName = key,
                        ProviderVersion = "built-in",
                        CategoryId = key,
                        Provenance = HudSourceProvenance.Verified,
                        DeclaredCapability = ParseCapability(capability),
                        Controllable = HudProfileService.IsExplicitKey(key)
                    };
                    AddRecordLocked(key, record);
                }

                HudSourceCapability parsed = ParseCapability(capability);
                bool active = targets > 0 && parsed != HudSourceCapability.Inactive;
                HudSourceCapability runtime = active ? parsed : HudSourceCapability.Inactive;
                string safeDetail = Sanitize(detail, 180);
                if (record.Active == active && record.RuntimeCapability == runtime
                    && record.Targets == targets && string.Equals(record.Detail, safeDetail, StringComparison.Ordinal))
                    return;

                record.Active = active;
                record.RuntimeCapability = runtime;
                record.Targets = targets;
                record.Detail = safeDetail;
                Interlocked.Increment(ref _generation);
            }
        }

        internal static HudRouteTag RegisterDetected(MethodBase method, string detail)
        {
            Type type = method?.DeclaringType;
            Assembly assembly = type?.Assembly;
            if (type == null || assembly == null) return null;
            string key = CreateDetectedKey(assembly, type);
            string assemblyName = SafeAssemblyName(assembly);
            string version = SafeVersion(assembly);

            lock (Gate)
            {
                if (!_lookup.TryGetValue(key, out SourceRecord record))
                {
                    if (_dynamicSources >= MaxDynamicSources)
                    {
                        _evictions++;
                        return null;
                    }
                    record = new SourceRecord
                    {
                        PolicyKey = key,
                        ProviderId = key,
                        DisplayName = FriendlyName(assemblyName, type),
                        ProviderVersion = version,
                        CategoryId = type.FullName ?? type.Name,
                        Provenance = HudSourceProvenance.Detected,
                        DeclaredCapability = HudSourceCapability.PartitionedPartial,
                        RuntimeCapability = HudSourceCapability.PartitionedPartial,
                        Active = true,
                        Controllable = true,
                        Supported = "text-hud/native-sprite/billboard when observed",
                        Unsupported = "world lines,persistent billboards,custom projections",
                        Detail = Sanitize(detail, 180),
                        Fingerprint = Fingerprint(assembly, type, method)
                    };
                    AddRecordLocked(key, record);
                    _dynamicSources++;
                }
                else
                {
                    record.Active = true;
                    record.Controllable = true;
                    record.Provenance = HudSourceProvenance.Detected;
                    record.DeclaredCapability = HudSourceCapability.PartitionedPartial;
                    record.RuntimeCapability = HudSourceCapability.PartitionedPartial;
                    record.ProviderVersion = version;
                    record.Detail = Sanitize(detail, 180);
                    record.Fingerprint = Fingerprint(assembly, type, method);
                    Interlocked.Increment(ref _generation);
                }
            }
            return new HudRouteTag(key);
        }

        internal static void RegisterObservedCandidate(Assembly assembly, Type type, string detail)
        {
            if (assembly == null || type == null) return;
            string key = CreateDetectedKey(assembly, type);
            lock (Gate)
            {
                if (_lookup.ContainsKey(key) || _dynamicSources >= MaxDynamicSources) return;
                AddRecordLocked(key, new SourceRecord
                {
                    PolicyKey = key,
                    ProviderId = key,
                    DisplayName = FriendlyName(SafeAssemblyName(assembly), type),
                    ProviderVersion = SafeVersion(assembly),
                    CategoryId = type.FullName ?? type.Name,
                    Provenance = HudSourceProvenance.Detected,
                    DeclaredCapability = HudSourceCapability.ObserveOnly,
                    RuntimeCapability = HudSourceCapability.ObserveOnly,
                    Active = true,
                    Controllable = false,
                    Supported = string.Empty,
                    Unsupported = "render source not yet attributed",
                    Detail = Sanitize(detail, 180),
                    Fingerprint = Fingerprint(assembly, type, null)
                });
                _dynamicSources++;
            }
        }

        internal static void RegisterHarmonyOwner(string owner, MethodBase target)
        {
            owner = Sanitize(owner, 80);
            if (string.IsNullOrEmpty(owner) || owner == Plugin.HarmonyId) return;
            string key = "detected.harmony." + StableHash(owner).ToString("x8");
            lock (Gate)
            {
                if (_lookup.ContainsKey(key) || _dynamicSources >= MaxDynamicSources) return;
                AddRecordLocked(key, new SourceRecord
                {
                    PolicyKey = key,
                    ProviderId = key,
                    DisplayName = owner,
                    ProviderVersion = "unknown",
                    CategoryId = target?.DeclaringType?.FullName + "." + target?.Name,
                    Provenance = HudSourceProvenance.Unattributed,
                    DeclaredCapability = HudSourceCapability.ObserveOnly,
                    RuntimeCapability = HudSourceCapability.ObserveOnly,
                    Active = true,
                    Controllable = false,
                    Unsupported = "Harmony owner has no proven HUD source scope",
                    Detail = "patches known HUD/render seam"
                });
                _dynamicSources++;
            }
        }

        internal static SourceRecord ResolveRecord(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            Dictionary<string, SourceRecord> lookup = Volatile.Read(ref _lookup);
            return lookup.TryGetValue(key, out SourceRecord record) ? record : null;
        }

        // Runs per primitive, so the record must come from the tag's cache rather than a keyed
        // lookup: the hot path is two interlocked increments.
        internal static void RecordActivity(
            HudRouteTag tag, HudPrimitiveFamily family, bool supported)
            => RecordActivity(tag?.ActivityRecord, family, supported);

        internal static void RecordActivity(
            SourceRecord record, HudPrimitiveFamily family, bool supported)
        {
            if (record == null) return;
            Interlocked.Increment(ref record.ActivityCount);
            Interlocked.Exchange(ref record.LastSeen, Stopwatch.GetTimestamp());
            if (supported) SetBits(ref record.ObservedFamilies, (int)family);
            else SetBits(ref record.UnsupportedFamilies, (int)family);
        }

        private static void SetBits(ref int location, int bits)
        {
            int before;
            int after;
            do
            {
                before = Volatile.Read(ref location);
                after = before | bits;
                if (after == before) return;
            } while (Interlocked.CompareExchange(ref location, after, before) != before);
        }

        internal static HudSourceRegistrySnapshot Snapshot()
        {
            lock (Gate)
            {
                HudProfileSnapshot profile = HudProfileService.Current;
                long now = Stopwatch.GetTimestamp();
                var infrastructure = _lookup.Values.Where(r => r.Infrastructure && r.OwnerKey != null)
                    .GroupBy(r => r.OwnerKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
                HudSourceDescriptor[] descriptors = _lookup.Values
                    .Where(r => !r.Infrastructure)
                    .Select(r => ToDescriptor(r, profile, now,
                        infrastructure.TryGetValue(r.PolicyKey, out SourceRecord[] children) ? children : null))
                    .OrderBy(d => d.Provenance == "verified" ? 0 : d.Provenance == "detected" ? 1 : 2)
                    .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new HudSourceRegistrySnapshot(_generation, descriptors);
            }
        }

        private static HudSourceDescriptor ToDescriptor(
            SourceRecord record, HudProfileSnapshot profile, long now, SourceRecord[] infrastructure)
        {
            HudSourceCapability capability = record.Active
                ? record.RuntimeCapability
                : record.DeclaredCapability;
            int observed = Volatile.Read(ref record.ObservedFamilies);
            int unsupported = Volatile.Read(ref record.UnsupportedFamilies);
            string detail = record.Detail ?? string.Empty;
            if (infrastructure != null)
            {
                string bridges = string.Join(", ", infrastructure.Select(r =>
                    r.DisplayName + "=" + (r.Active ? CapabilityName(r.RuntimeCapability) : "inactive")));
                if (bridges.Length > 0) detail = Append(detail, bridges);
            }
            if (observed != 0) detail = Append(detail, "observed=" + PrimitiveNames(observed));
            if (unsupported != 0) detail = Append(detail, "leaks=" + PrimitiveNames(unsupported));

            bool recording = profile.RecordingVisible(record.PolicyKey);
            bool player = profile.PlayerVisible(record.PolicyKey);
            long last = Interlocked.Read(ref record.LastSeen);
            long ageMs = last == 0 ? -1 : Math.Max(0, (now - last) * 1000 / Stopwatch.Frequency);
            return new HudSourceDescriptor
            {
                PolicyKey = record.PolicyKey,
                ProviderId = record.ProviderId,
                DisplayName = record.DisplayName,
                ProviderVersion = record.ProviderVersion,
                CategoryId = record.CategoryId,
                Provenance = ProvenanceName(record.Provenance),
                Capability = capability,
                Active = record.Active,
                Controllable = record.Controllable && capability != HudSourceCapability.ObserveOnly,
                SupportedPrimitives = Append(record.Supported, observed == 0 ? null : PrimitiveNames(observed)),
                UnsupportedPrimitives = Append(record.Unsupported, unsupported == 0 ? null : PrimitiveNames(unsupported)),
                Detail = detail,
                ActivityCount = Interlocked.Read(ref record.ActivityCount),
                LastSeenMilliseconds = ageMs,
                RecordingVisible = recording,
                PlayerVisible = player,
                EffectiveRoute = profile.EffectiveRoute(record.PolicyKey)
            };
        }

        internal static string StatusLine()
        {
            HudSourceRegistrySnapshot snapshot = Snapshot();
            int verified = snapshot.Sources.Count(s => s.Provenance == "verified");
            int detected = snapshot.Sources.Count(s => s.Provenance == "detected");
            int unsupported = snapshot.Sources.Count(s => s.Provenance == "unattributed"
                                                       || s.Capability == HudSourceCapability.ObserveOnly);
            return "sources=g" + snapshot.Generation
                   + ", verified:" + verified
                   + ", detected:" + detected
                   + ", unsupported:" + unsupported
                   + ", dynamic:" + _dynamicSources
                   + ", drops:" + Interlocked.Read(ref _evictions)
                   + ", fingerprint-mismatches:" + Interlocked.Read(ref FingerprintMismatches);
        }

        // Called by HudProfileService when a stored dynamic policy's fingerprint no longer matches
        // the live one, which drops the user's confirmed filter back to recorded. Counting and
        // logging here keeps the evidence with the source registry rather than in the profile
        // snapshot, which has no channel to the user.
        internal static void NoteFingerprintMismatch(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            Interlocked.Increment(ref FingerprintMismatches);
            bool first;
            lock (Gate)
                first = MismatchLogged.Count < MaxFingerprintMismatchLogs && MismatchLogged.Add(key);
            if (first)
                Plugin.Log("HUD source fingerprint changed: " + Sanitize(key, 80)
                           + "; confirmed filter reverted to recorded");
        }

        internal static bool IsDynamicPolicyKey(string key)
            => key != null && key.StartsWith("detected.", StringComparison.OrdinalIgnoreCase);

        internal static string CurrentFingerprint(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            Dictionary<string, SourceRecord> lookup = Volatile.Read(ref _lookup);
            return lookup.TryGetValue(key, out SourceRecord record) ? record.Fingerprint : null;
        }

        internal static string CapabilityName(HudSourceCapability value)
        {
            switch (value)
            {
                case HudSourceCapability.Partitioned: return "partitioned";
                case HudSourceCapability.PartitionedPartial: return "partitioned-partial";
                case HudSourceCapability.ObserveOnly: return "observe-only";
                default: return "inactive";
            }
        }

        internal static string RouteName(HudEffectiveRoute route)
            => route == HudEffectiveRoute.Recorded
                ? "recorded"
                : route == HudEffectiveRoute.PlayerOnly ? "player-only" : "suppressed";

        internal static string CreateDetectedKey(Assembly assembly, Type type)
        {
            string assemblyName = SanitizeIdentifier(SafeAssemblyName(assembly));
            string identity = SafeAssemblyName(assembly) + "|" + (type?.FullName ?? type?.Name ?? "unknown");
            return "detected." + assemblyName + "." + StableHash(identity).ToString("x8");
        }

        private static void AddRecordLocked(string key, SourceRecord record)
        {
            var copy = new Dictionary<string, SourceRecord>(_lookup, StringComparer.OrdinalIgnoreCase)
            {
                [key] = record
            };
            Volatile.Write(ref _lookup, copy);
            Interlocked.Increment(ref _generation);
        }

        private static void PublishLookupLocked()
        {
            Volatile.Write(ref _lookup,
                new Dictionary<string, SourceRecord>(_lookup, StringComparer.OrdinalIgnoreCase));
            Interlocked.Increment(ref _generation);
        }

        private static HudSourceCapability ParseCapability(string value)
        {
            if (string.Equals(value, "partitioned", StringComparison.OrdinalIgnoreCase))
                return HudSourceCapability.Partitioned;
            if (string.Equals(value, "partitioned-partial", StringComparison.OrdinalIgnoreCase))
                return HudSourceCapability.PartitionedPartial;
            if (string.Equals(value, "observe-only", StringComparison.OrdinalIgnoreCase))
                return HudSourceCapability.ObserveOnly;
            return HudSourceCapability.Inactive;
        }

        private static string ProvenanceName(HudSourceProvenance value)
            => value == HudSourceProvenance.Verified
                ? "verified"
                : value == HudSourceProvenance.Detected ? "detected" : "unattributed";

        private static string PrimitiveNames(int bits)
        {
            var names = new List<string>();
            if ((bits & (int)HudPrimitiveFamily.NativeSprite) != 0) names.Add("native-sprite");
            if ((bits & (int)HudPrimitiveFamily.Billboard) != 0) names.Add("billboard");
            if ((bits & (int)HudPrimitiveFamily.Triangle) != 0) names.Add("triangle");
            if ((bits & (int)HudPrimitiveFamily.PersistentBillboard) != 0) names.Add("persistent");
            if ((bits & (int)HudPrimitiveFamily.WorldLine) != 0) names.Add("world-line");
            if ((bits & (int)HudPrimitiveFamily.CustomProjection) != 0) names.Add("custom-projection");
            if ((bits & (int)HudPrimitiveFamily.Unknown) != 0) names.Add("unknown");
            return string.Join(",", names);
        }

        private static string Append(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left)) return right ?? string.Empty;
            if (string.IsNullOrWhiteSpace(right)) return left;
            return left + "; " + right;
        }

        private static string FriendlyName(string assemblyName, Type type)
        {
            string typeName = type.Name;
            if (assemblyName.StartsWith("IngameScript", StringComparison.OrdinalIgnoreCase)
                || assemblyName.StartsWith("Script", StringComparison.OrdinalIgnoreCase))
                return typeName;
            return assemblyName + " / " + typeName;
        }

        private static string SafeAssemblyName(Assembly assembly)
        {
            try { return Sanitize(assembly.GetName().Name, 80); }
            catch { return "unknown"; }
        }

        private static string SafeVersion(Assembly assembly)
        {
            try { return assembly.GetName().Version?.ToString() ?? "unknown"; }
            catch { return "unknown"; }
        }

        private static string SanitizeIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown";
            char[] chars = value.ToLowerInvariant().Select(c =>
                char.IsLetterOrDigit(c) ? c : '-').ToArray();
            string result = new string(chars).Trim('-');
            while (result.Contains("--")) result = result.Replace("--", "-");
            return result.Length == 0 ? "unknown" : result.Substring(0, Math.Min(40, result.Length));
        }

        // Mod-controlled display strings reach the clipboard report and the settings list, so the
        // output is restricted to printable ASCII: CR/LF/tab collapse to spaces so a name cannot
        // forge report fields, and everything outside 0x20-0x7E becomes '?' so bidi overrides,
        // zero-width joiners, and homoglyphs cannot spoof another source's identity in either
        // surface. Truncation is applied last, to the already-sanitized text.
        internal static string Sanitize(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var chars = new char[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\r' || c == '\n' || c == '\t') chars[i] = ' ';
                else if (c < 0x20 || c > 0x7E) chars[i] = '?';
                else chars[i] = c;
            }
            string clean = new string(chars).Trim();
            return clean.Length <= limit ? clean : clean.Substring(0, limit);
        }

        private static uint StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in value ?? string.Empty)
                {
                    hash ^= char.ToLowerInvariant(c);
                    hash *= 16777619;
                }
                return hash;
            }
        }

        private static string Fingerprint(Assembly assembly, Type type, MethodBase method)
        {
            string value = SafeAssemblyName(assembly) + "|" + SafeVersion(assembly)
                           + "|" + SafeModuleId(assembly)
                           + "|" + (type?.FullName ?? type?.Name ?? "unknown")
                           + "|" + (method?.Name ?? "candidate");
            return StableHash(value).ToString("x8");
        }

        private static string SafeModuleId(Assembly assembly)
        {
            try { return assembly.ManifestModule.ModuleVersionId.ToString("N"); }
            catch { return "unknown"; }
        }
    }
}
