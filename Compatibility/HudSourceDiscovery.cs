using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace CleanFeed.Compatibility
{
    internal static class HudSourceDiscovery
    {
        private const int WindowSeconds = 30;
        private const int WindowSampleBudget = 96;
        private const int MaxPendingPatches = 128;
        private const int MaxCandidatesPerAssembly = 24;
        private static readonly long MinimumSampleGap = Stopwatch.Frequency / 4;
        private static readonly object Gate = new object();
        private static readonly Queue<MethodBase> PendingPatches = new Queue<MethodBase>();
        private static readonly HashSet<MethodBase> QueuedMethods = new HashSet<MethodBase>();
        private static readonly HashSet<MethodBase> ScopedMethods = new HashSet<MethodBase>();
        private static readonly HashSet<string> ScannedAssemblies =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Static, not per-call: LooksLikeHudProvider and IsCandidateAssembly run across every type of
        // every loaded mod assembly during a scan.
        private static readonly string[] IgnoredAssemblyPrefixes =
        {
            "System", "Microsoft", "mscorlib", "netstandard", "VRage", "Sandbox",
            "SpaceEngineers", "SharpDX", "Harmony", "0Harmony", "Pulsar", "Steamworks",
            "Newtonsoft", "ProtoBuf", "Keen", "UIFun", "TextHud", "HudAPI"
        };

        // A HUD framework that has a dedicated adapter must never also be scoped generically.
        //
        // RichHudFramework is the case that proved it. Its master assembly
        // (1965654081.sbm_RichHudFramework) matched no ignored prefix, and RichHudFramework.UI.Server
        // .HudMain is a textbook HUD-named type with a Draw method, so discovery patched the
        // framework's own draw loop. That wrapped EVERY emitter call the master makes - the whole
        // interleaved client pass and the shared cursor with it - in a detected.* scope. The explicit
        // adapter's cursor resolver then never saw a null route context (resolver-hits in the
        // thousands, resolver-tagged zero) and the cursor quads inherited an unconfirmed dynamic
        // key's Recorded route, passing to the backbuffer underneath the composition layer.
        //
        // UIFun/TextHud/HudAPI are in IgnoredAssemblyPrefixes above for exactly this reason; RichHud
        // needs both a name test and a marker-type test because its assembly is named after a
        // workshop id rather than after itself.
        private const string RichHudNameFragment = "richhudframework";
        private const string RichHudMasterMarker = "RichHudFramework.Server.RichHudMaster";
        // Embedded client copies of the framework live inside ordinary mod assemblies whose OTHER
        // types discover legitimately, so those assemblies stay eligible and only the framework's
        // own namespace is skipped, per type.
        private const string RichHudNamespacePrefix = "RichHudFramework.";
        private static readonly string[] HudProviderTerms =
        {
            "hud", "overlay", "radar", "reticle", "crosshair", "compass", "marker", "flightvector"
        };
        private static readonly HashSet<string> KnownExplicitTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Jawastew.HudLcd.HudLcd", "Digi.BuildInfo.Features.ToolbarInfo.ToolbarLabelRender",
            "FlightVector.Session", "HudCompassMod.HudCompassSession", "ThrustBeacon.Session",
            "WCRadar.Session", "WeaponCore.Data.Scripts.CoreSystems.Ui.Hud.Hud",
            "WeaponCore.Data.Scripts.CoreSystems.Ui.Targeting.TargetUi",
            "UIFun.Messagesv2.ModHUDMessage", "UIFun.Messagesv2.ModBillboardHUDMessage",
            "UIFun.Messagesv2.ModBillboardTriHUDMessage"
        };

        private static long _windowEndsAt;
        private static long _lastSampleAt;
        private static int _samplesRemaining;
        private static long _windows;
        private static long _samples;
        private static long _sampleThrottles;
        private static long _attributed;
        private static long _rejected;
        private static long _patches;
        private static long _patchFailures;
        private static long _candidateTypes;
        private static string _state = "off";
        private static string _lastError = "none";

        internal static void Initialize()
        {
            lock (Gate)
            {
                PendingPatches.Clear();
                QueuedMethods.Clear();
                ScopedMethods.Clear();
                ScannedAssemblies.Clear();
                _state = "initializing";
                _lastError = "none";
            }
            RequestRescan("plugin-start");
        }

        internal static void Shutdown()
        {
            lock (Gate)
            {
                PendingPatches.Clear();
                QueuedMethods.Clear();
                ScopedMethods.Clear();
                ScannedAssemblies.Clear();
                _samplesRemaining = 0;
                _windowEndsAt = 0;
                _state = "off";
            }
        }

        // The assembly list is polled once by the plugin update and shared with the coordinator;
        // discovery must not poll GetAssemblies again.
        internal static void Update(Assembly[] assemblies, bool assembliesChanged)
        {
            if (assembliesChanged)
            {
                OpenWindow("assembly-change");
                ScanAssemblies(assemblies, false);
                ScanHarmonyOwners();
            }
            DrainPendingPatches();
            if (_state.StartsWith("sampling", StringComparison.Ordinal) && !IsSamplingActive()) _state = "idle";
        }

        internal static void RequestRescan(string reason)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            lock (Gate) ScannedAssemblies.Clear();
            OpenWindow(reason);
            ScanAssemblies(assemblies, true);
            ScanHarmonyOwners();
        }

        private static void OpenWindow(string reason)
        {
            lock (Gate)
            {
                _windowEndsAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * WindowSeconds;
                _samplesRemaining = WindowSampleBudget;
                _lastSampleAt = 0;
                _state = "sampling:" + HudSourceRegistry.Sanitize(reason, 40);
                _windows++;
            }
        }

        internal static HudRouteTag TryAttributeCurrentCall(object result)
        {
            if (result == null) return null;
            HudRouteTag current = HudRouteContext.Current;
            if (current != null) return current;
            if (!ReserveSample()) return null;

            MethodBase caller = null;
            try
            {
                var trace = new StackTrace(1, false);
                StackFrame[] frames = trace.GetFrames();
                if (frames != null)
                    foreach (StackFrame frame in frames)
                    {
                        MethodBase method = frame.GetMethod();
                        if (IsExternalCandidate(method)) { caller = method; break; }
                    }
            }
            catch (Exception ex)
            {
                _lastError = ex.GetType().Name + ":" + HudSourceRegistry.Sanitize(ex.Message, 80);
            }

            if (caller == null)
            {
                System.Threading.Interlocked.Increment(ref _rejected);
                return null;
            }

            string detail = "runtime Text HUD attribution at "
                            + caller.DeclaringType?.FullName + "." + caller.Name;
            HudRouteTag tag = HudSourceRegistry.RegisterDetected(caller, detail);
            if (tag == null)
            {
                System.Threading.Interlocked.Increment(ref _rejected);
                return null;
            }

            HudRouteContext.Tag(result, tag);
            QueueScope(caller);
            System.Threading.Interlocked.Increment(ref _attributed);
            return tag;
        }

        private static bool ReserveSample()
        {
            lock (Gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (_samplesRemaining <= 0 || now >= _windowEndsAt) return false;
                if (_lastSampleAt != 0 && now - _lastSampleAt < MinimumSampleGap)
                {
                    _sampleThrottles++;
                    return false;
                }
                _lastSampleAt = now;
                _samplesRemaining--;
                _samples++;
                return true;
            }
        }

        private static bool IsSamplingActive()
        {
            lock (Gate)
                return _samplesRemaining > 0 && Stopwatch.GetTimestamp() < _windowEndsAt;
        }

        private static void QueueScope(MethodBase method)
        {
            if (method == null) return;
            lock (Gate)
            {
                if (ScopedMethods.Contains(method) || QueuedMethods.Contains(method)) return;
                if (PendingPatches.Count >= MaxPendingPatches)
                {
                    _patchFailures++;
                    _lastError = "dynamic patch queue saturated";
                    return;
                }
                QueuedMethods.Add(method);
                PendingPatches.Enqueue(method);
            }
        }

        // Accepted candidates are hooked proactively so a detected component is controllable
        // (routable/hideable) even when Text HUD sampling never attributes it. ScopePrefix is a
        // no-op dictionary miss for anything the policy never touches, and the pending-patch
        // queue's saturation guard still bounds the total work this can add.
        private static void QueueCandidateScopes(Type type)
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static
                                          | BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.DeclaredOnly);
            }
            catch { return; }

            int queued = 0;
            foreach (MethodInfo method in methods)
            {
                if (queued >= 4) break;
                if (!string.Equals(method.Name, "Draw", StringComparison.Ordinal)
                    && !string.Equals(method.Name, "Render", StringComparison.Ordinal)) continue;
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                QueueScope(method);
                queued++;
            }
        }

        private static void DrainPendingPatches()
        {
            for (int i = 0; i < 4; i++)
            {
                MethodBase method;
                lock (Gate)
                {
                    if (PendingPatches.Count == 0) return;
                    method = PendingPatches.Dequeue();
                    QueuedMethods.Remove(method);
                }

                bool patched = false;
                try
                {
                    HudRouteTag tag = HudSourceRegistry.RegisterDetected(method,
                        "cached runtime source scope at " + method.DeclaringType?.FullName + "." + method.Name);
                    patched = tag != null && HudAdapterCoordinator.TryPatchDynamicScope(method, tag);
                }
                catch (Exception ex)
                {
                    _lastError = ex.GetType().Name + ":" + HudSourceRegistry.Sanitize(ex.Message, 80);
                }

                lock (Gate)
                {
                    if (patched)
                    {
                        ScopedMethods.Add(method);
                        _patches++;
                    }
                    else _patchFailures++;
                }
            }
        }

        private static void ScanAssemblies(Assembly[] assemblies, bool force)
        {
            foreach (Assembly assembly in assemblies)
            {
                if (!IsCandidateAssembly(assembly)) continue;
                string identity;
                try { identity = assembly.FullName ?? assembly.GetName().Name; }
                catch { continue; }
                lock (Gate)
                {
                    // A forced rescan revisits assemblies that were already scanned; an incremental
                    // pass skips them. Either way the assembly ends up recorded exactly once.
                    bool firstScan = ScannedAssemblies.Add(identity);
                    if (!force && !firstScan) continue;
                }

                int accepted = 0;
                foreach (Type type in SafeTypes(assembly))
                {
                    if (accepted >= MaxCandidatesPerAssembly) break;
                    if (!LooksLikeHudProvider(type) || IsKnownExplicitType(type)) continue;
                    HudSourceRegistry.RegisterObservedCandidate(
                        assembly, type, "HUD-like type detected; awaiting render attribution");
                    accepted++;
                    System.Threading.Interlocked.Increment(ref _candidateTypes);
                    QueueCandidateScopes(type);
                }
            }
        }

        private static void ScanHarmonyOwners()
        {
            try
            {
                foreach (MethodBase method in Harmony.GetAllPatchedMethods())
                {
                    if (!IsKnownRenderSeam(method)) continue;
                    Patches info = Harmony.GetPatchInfo(method);
                    if (info == null) continue;
                    foreach (string owner in info.Owners)
                        HudSourceRegistry.RegisterHarmonyOwner(owner, method);
                }
            }
            catch (Exception ex)
            {
                _lastError = "HarmonyInventory:" + ex.GetType().Name;
            }
        }

        private static bool IsKnownRenderSeam(MethodBase method)
        {
            string type = method?.DeclaringType?.FullName ?? string.Empty;
            string name = method?.Name ?? string.Empty;
            return type.IndexOf("MyRender", StringComparison.OrdinalIgnoreCase) >= 0
                   && (name.IndexOf("Sprite", StringComparison.OrdinalIgnoreCase) >= 0
                       || name.IndexOf("Present", StringComparison.OrdinalIgnoreCase) >= 0)
                   || type.IndexOf("TransparentGeometry", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCandidateAssembly(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic) return false;
            string name;
            try { name = assembly.GetName().Name ?? string.Empty; }
            catch { return false; }
            if (assembly == typeof(Plugin).Assembly) return false;
            foreach (string prefix in IgnoredAssemblyPrefixes)
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

            // The installed RichHud framework, by name and - because that name is a workshop id on
            // the live install - by the master marker type the explicit adapter keys off. Either way
            // the assembly belongs to HudAdapterCoordinator's RichHud adapter, not to discovery.
            if (name.IndexOf(RichHudNameFragment, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            try
            {
                if (assembly.GetType(RichHudMasterMarker, false, false) != null) return false;
            }
            catch { }
            return true;
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes().Where(t => t != null); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return Enumerable.Empty<Type>(); }
        }

        private static bool LooksLikeHudProvider(Type type)
        {
            string fullName = type.FullName ?? type.Name;
            // Embedded framework copies, inside a mod assembly that is otherwise eligible. Skipping
            // by namespace rather than by assembly keeps the mod's OWN HUD types discoverable while
            // leaving its copy of the framework to the explicit RichHud adapter - which scopes those
            // emitters itself, and whose attribution a detected.* scope would displace.
            if (fullName.StartsWith(RichHudNamespacePrefix, StringComparison.Ordinal)) return false;

            string name = fullName.ToLowerInvariant();
            bool matched = false;
            foreach (string term in HudProviderTerms)
            {
                if (!name.Contains(term)) continue;
                matched = true;
                break;
            }
            if (!matched) return false;
            try
            {
                return type.GetMethods(BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(m => m.Name.IndexOf("Draw", StringComparison.OrdinalIgnoreCase) >= 0
                              || m.Name.IndexOf("Render", StringComparison.OrdinalIgnoreCase) >= 0
                              || m.Name.IndexOf("Hud", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return false; }
        }

        private static bool IsKnownExplicitType(Type type)
            => KnownExplicitTypes.Contains(type.FullName ?? string.Empty);

        private static bool IsExternalCandidate(MethodBase method)
        {
            Type type = method?.DeclaringType;
            Assembly assembly = type?.Assembly;
            if (type == null || !IsCandidateAssembly(assembly)) return false;
            string assemblyName;
            try { assemblyName = assembly.GetName().Name ?? string.Empty; }
            catch { return false; }
            if (assemblyName.StartsWith("UIFun", StringComparison.OrdinalIgnoreCase)
                || assemblyName.IndexOf("TextHud", StringComparison.OrdinalIgnoreCase) >= 0
                || assemblyName.IndexOf("HudAPI", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return !method.IsAbstract && !method.ContainsGenericParameters;
        }

        internal static string StatusLine()
        {
            int remaining;
            int pending;
            lock (Gate)
            {
                remaining = _samplesRemaining;
                pending = PendingPatches.Count;
            }
            return "discovery=" + _state
                   + ", windows=" + _windows
                   + ", samples=" + _samples
                   + ", remaining=" + remaining
                   + ", throttled=" + _sampleThrottles
                   + ", attributed=" + _attributed
                   + ", rejected=" + _rejected
                   + ", candidates=" + _candidateTypes
                   + ", scopes=" + _patches
                   + ", patch-fail=" + _patchFailures
                   + ", pending=" + pending
                   + ", error=" + _lastError;
        }

        internal static string ReportStatusLine()
        {
            int remaining;
            int pending;
            lock (Gate)
            {
                remaining = _samplesRemaining;
                pending = PendingPatches.Count;
            }
            return "state=" + _state
                   + ", windows=" + _windows
                   + ", samples=" + _samples
                   + ", remaining=" + remaining
                   + ", attributed=" + _attributed
                   + ", rejected=" + _rejected
                   + ", candidates=" + _candidateTypes
                   + ", scopes=" + _patches
                   + ", patch-fail=" + _patchFailures
                   + ", pending=" + pending;
        }
    }
}
