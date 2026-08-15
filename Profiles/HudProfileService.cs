using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CleanFeed.Compatibility;
using Sandbox.ModAPI;
using VRage.FileSystem;

namespace CleanFeed.Profiles
{
    internal enum HudEffectiveRoute
    {
        Recorded,
        PlayerOnly,
        Suppressed
    }

    internal sealed class HudProfileSnapshot
    {
        internal const int CurrentSchema = 3;
        private static int _nextGeneration;
        private readonly Dictionary<string, bool> _recordingOverrides;
        private readonly Dictionary<string, bool> _playerOverrides;
        private readonly Dictionary<string, string> _dynamicFingerprints;

        internal HudProfileSnapshot(
            bool globalRecordingVisible,
            IEnumerable<KeyValuePair<string, bool>> recordingOverrides,
            bool globalPlayerVisible,
            IEnumerable<KeyValuePair<string, bool>> playerOverrides,
            bool unfocusedPrivacyEnabled,
            IEnumerable<KeyValuePair<string, string>> dynamicFingerprints = null)
        {
            // Every published snapshot gets a fresh generation. Consumers that memoise a resolved
            // policy compare against it instead of re-deriving the answer per draw call.
            Generation = Interlocked.Increment(ref _nextGeneration);
            GlobalRecordingVisible = globalRecordingVisible;
            GlobalPlayerVisible = globalPlayerVisible;
            UnfocusedPrivacyEnabled = unfocusedPrivacyEnabled;
            _recordingOverrides = recordingOverrides.ToDictionary(
                kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            _playerOverrides = playerOverrides.ToDictionary(
                kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            _dynamicFingerprints = (dynamicFingerprints ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }

        internal int Generation { get; }
        internal bool GlobalRecordingVisible { get; }
        internal bool GlobalPlayerVisible { get; }
        internal bool UnfocusedPrivacyEnabled { get; }

        internal bool RecordingVisible(string key)
            => RecordingVisibleNormalized(HudProfileService.NormalizeKey(key));

        internal bool PlayerVisible(string key)
            => PlayerVisibleNormalized(HudProfileService.NormalizeKey(key));

        internal HudEffectiveRoute EffectiveRoute(
            string key,
            bool? recordingOverride = null,
            bool? playerVisibleOverride = null)
            => EffectiveRouteNormalized(
                HudProfileService.NormalizeKey(key), recordingOverride, playerVisibleOverride);

        // Overloads for callers holding a key that NormalizeKey has already canonicalised - notably
        // HudRouteTag, whose Key is normalised once in its constructor.
        internal bool RecordingVisibleNormalized(string key)
        {
            if (HudSourceRegistry.IsDynamicPolicyKey(key) && !DynamicPolicyConfirmed(key)) return true;
            return key != null && _recordingOverrides.TryGetValue(key, out bool value)
                ? value
                : GlobalRecordingVisible;
        }

        internal bool PlayerVisibleNormalized(string key)
        {
            if (HudSourceRegistry.IsDynamicPolicyKey(key) && !DynamicPolicyConfirmed(key)) return true;
            return key != null && _playerOverrides.TryGetValue(key, out bool value)
                ? value
                : GlobalPlayerVisible;
        }

        internal HudEffectiveRoute EffectiveRouteNormalized(
            string key,
            bool? recordingOverride = null,
            bool? playerVisibleOverride = null)
        {
            // Instance markers may hide an individual producer, but they may not defeat a
            // category-wide player-hide policy.
            bool playerVisible = PlayerVisibleNormalized(key) && (playerVisibleOverride ?? true);
            if (!playerVisible) return HudEffectiveRoute.Suppressed;
            // Same rule on the recording axis: an instance marker may only restrict, never widen.
            // CustomData on a block is writable by whoever owns it, so on a server a hostile owner
            // must not be able to force content into a capture the category filter already hides.
            // (cleanfeed=record is therefore inert while the key is filtered out of the recording.)
            bool recordingVisible = RecordingVisibleNormalized(key) && (recordingOverride ?? true);
            return recordingVisible ? HudEffectiveRoute.Recorded : HudEffectiveRoute.PlayerOnly;
        }

        internal HudEffectiveRoute GlobalEffectiveRoute
            => !GlobalPlayerVisible
                ? HudEffectiveRoute.Suppressed
                : GlobalRecordingVisible ? HudEffectiveRoute.Recorded : HudEffectiveRoute.PlayerOnly;

        internal IReadOnlyDictionary<string, bool> RecordingOverrides => _recordingOverrides;
        internal IReadOnlyDictionary<string, bool> PlayerOverrides => _playerOverrides;
        internal IReadOnlyDictionary<string, string> DynamicFingerprints => _dynamicFingerprints;

        private bool DynamicPolicyConfirmed(string key)
        {
            string current = HudSourceRegistry.CurrentFingerprint(key);
            if (current == null || !_dynamicFingerprints.TryGetValue(key, out string saved))
                return false;
            if (string.Equals(current, saved, StringComparison.OrdinalIgnoreCase)) return true;
            // A changed fingerprint (provider updated or recompiled) reverts this key to fail-open
            // defaults; surface that instead of failing silently. Keys with no saved fingerprint
            // are first-time sightings, not mismatches.
            HudSourceRegistry.NoteFingerprintMismatch(key);
            return false;
        }
    }

    internal static class HudProfileService
    {
        private const string FileName = "CleanFeed.profile.ini";
        private const string FolderName = "CleanFeed";
        // Loader bounds. A profile is a plain text file the user (or anything else with write access
        // to it) can hand us, so the parser refuses to grow unbounded dictionaries from it.
        private const int MaxOverrideEntries = 512;
        private const int MaxFingerprintEntries = 512;
        private const int MaxKeyLength = 96;
        private static readonly object Gate = new object();
        private static string _profileDirectory;
        private static string _profilePath;
        private static string[] _scrubbedPaths;
        private static string _legacyDirectory;
        private static readonly string[] NativeKeys =
        {
            "terminal", "chat", "toolbar", "speed", "gps", "battery-fuel"
        };
        private static readonly string[] CompatibilityKeys =
        {
            "hudlcd", "weaponcore.reload", "weaponcore.radar", "wc-radar",
            "flight-vector", "flight-hud", "thrust-beacon", "flip-burn", "gamecore"
        };
        // The list preserves display order for status lines; the set answers membership without
        // rebuilding a LINQ Concat and linear-scanning it on every IsKnownKey call.
        private static readonly string[] ExplicitKeyList =
            NativeKeys.Concat(CompatibilityKeys).ToArray();
        private static readonly HashSet<string> ExplicitKeySet =
            new HashSet<string>(ExplicitKeyList, StringComparer.OrdinalIgnoreCase);

        private static HudProfileSnapshot _current = CreateDefault();
        private static bool _loadAttempted;
        private static bool _autoRedirect;
        private static string _lastPersistence = "defaults";

        // The snapshot every draw-path route lookup reads. Nothing in this type's initialization may
        // be able to throw, because a TypeInitializationException here would rethrow on every
        // subsequent access for the rest of the process and take routing down with it - hence the
        // lazily computed paths below rather than static readonly initializers.
        internal static HudProfileSnapshot Current => Volatile.Read(ref _current);

        internal static IEnumerable<string> AllKeys
            => NativeKeys.Concat(CompatibilityKeys)
                .Concat(_current.RecordingOverrides.Keys.Where(HudSourceRegistry.IsDynamicPolicyKey))
                .Concat(_current.PlayerOverrides.Keys.Where(HudSourceRegistry.IsDynamicPolicyKey))
                .Distinct(StringComparer.OrdinalIgnoreCase);

        // MyAPIGateway.Utilities.*FileInLocalStorage keys its folder to the compiled assembly's
        // module name, which Pulsar's from-source build randomises per compile - every recompile
        // orphaned the previous settings. Persist to a fixed per-user path instead.
        //
        // Computed on first use rather than in a static initializer, following the BuildId pattern
        // in Render\HudRedirector.cs: Environment.GetFolderPath can throw, and this type is read on
        // the per-draw path through Current, so a folder probe must never be able to poison it. A
        // failure degrades to an empty path, which makes Load and Save no-ops that report
        // "storage-unavailable" - settings stop persisting, nothing else changes. A benign race
        // just computes the same value twice.
        private static string ProfileDirectory
        {
            get
            {
                if (_profileDirectory != null) return _profileDirectory;
                string value;
                try
                {
                    string appData = SafeFolder(Environment.SpecialFolder.ApplicationData);
                    value = string.IsNullOrEmpty(appData)
                        ? string.Empty
                        : Path.Combine(appData, FolderName);
                }
                catch { value = string.Empty; }
                _profileDirectory = value;
                return value;
            }
        }

        private static string ProfilePath
        {
            get
            {
                if (_profilePath != null) return _profilePath;
                string directory = ProfileDirectory;
                string value;
                try
                {
                    value = directory.Length == 0 ? string.Empty : Path.Combine(directory, FileName);
                }
                catch { value = string.Empty; }
                _profilePath = value;
                return value;
            }
        }

        // Directories to strip out of logged exception text; see Sanitize.
        private static string[] ScrubbedPaths
        {
            get
            {
                if (_scrubbedPaths != null) return _scrubbedPaths;
                string[] value;
                try { value = BuildScrubbedPaths(); }
                catch { value = new string[0]; }
                _scrubbedPaths = value;
                return value;
            }
        }

        private static string SafeFolder(Environment.SpecialFolder folder)
        {
            try { return Environment.GetFolderPath(folder); }
            catch { return null; }
        }

        // Session setting rather than snapshot state: it decides whether a redirect starts at all,
        // so it must not force a new snapshot generation on every consumer that memoises routes.
        internal static bool AutoRedirectEnabled
        {
            get
            {
                lock (Gate) { return _autoRedirect; }
            }
        }

        internal static void SetAutoRedirect(bool enabled)
        {
            lock (Gate)
            {
                _autoRedirect = enabled;
                Save();
            }
        }

        internal static void Update()
        {
            if (_loadAttempted || MyAPIGateway.Utilities == null) return;
            lock (Gate)
            {
                if (_loadAttempted || MyAPIGateway.Utilities == null) return;
                _loadAttempted = true;
                Load();
            }
        }

        internal static void ResetSession()
        {
            lock (Gate)
            {
                _loadAttempted = false;
                // Reloaded with the profile next session.
                _autoRedirect = false;
                _lastPersistence = "awaiting-session";
                Volatile.Write(ref _current, CreateDefault());
            }
        }

        internal static bool TrySet(string key, bool recordingVisible, out string result)
        {
            key = NormalizeKey(key);
            if (!IsKnownKey(key))
            {
                result = "unknown HUD source; use /cf profile status";
                return false;
            }

            lock (Gate)
            {
                HudProfileSnapshot current = _current;
                var recording = Copy(current.RecordingOverrides);
                var fingerprints = CopyStrings(current.DynamicFingerprints);
                recording[key] = recordingVisible;
                ConfirmDynamic(key, fingerprints);
                Publish(new HudProfileSnapshot(
                    current.GlobalRecordingVisible, recording,
                    current.GlobalPlayerVisible, current.PlayerOverrides,
                    current.UnfocusedPrivacyEnabled, fingerprints));
                Save();
            }
            result = Describe(key);
            return true;
        }

        // Note the sense: this takes recording *visibility*, the same polarity as `/cf profile`, which
        // is the inverse of the user-facing `/cf hud <key> on|off` filter. Callers coming from the
        // filter commands must pass !enabled.
        internal static void SetAll(bool recordingVisible)
        {
            lock (Gate)
            {
                HudProfileSnapshot current = _current;
                Publish(new HudProfileSnapshot(
                    recordingVisible, new Dictionary<string, bool>(),
                    current.GlobalPlayerVisible, current.PlayerOverrides,
                    current.UnfocusedPrivacyEnabled, current.DynamicFingerprints));
                Save();
            }
        }

        internal static bool TrySetPlayer(string key, bool playerVisible, out string result)
        {
            key = NormalizeKey(key);
            if (!IsKnownKey(key))
            {
                result = "unknown HUD source; use /cf player status";
                return false;
            }

            lock (Gate)
            {
                HudProfileSnapshot current = _current;
                var player = Copy(current.PlayerOverrides);
                var fingerprints = CopyStrings(current.DynamicFingerprints);
                player[key] = playerVisible;
                ConfirmDynamic(key, fingerprints);
                Publish(new HudProfileSnapshot(
                    current.GlobalRecordingVisible, current.RecordingOverrides,
                    current.GlobalPlayerVisible, player,
                    current.UnfocusedPrivacyEnabled, fingerprints));
                Save();
            }
            result = DescribePlayer(key);
            return true;
        }

        internal static void SetAllPlayer(bool playerVisible)
        {
            lock (Gate)
            {
                HudProfileSnapshot current = _current;
                Publish(new HudProfileSnapshot(
                    current.GlobalRecordingVisible, current.RecordingOverrides,
                    playerVisible, new Dictionary<string, bool>(),
                    current.UnfocusedPrivacyEnabled, current.DynamicFingerprints));
                Save();
            }
        }

        internal static void SetUnfocusedPrivacy(bool enabled)
        {
            lock (Gate)
            {
                HudProfileSnapshot current = _current;
                Publish(new HudProfileSnapshot(
                    current.GlobalRecordingVisible, current.RecordingOverrides,
                    current.GlobalPlayerVisible, current.PlayerOverrides, enabled,
                    current.DynamicFingerprints));
                Save();
            }
        }

        internal static void ApplySettings(
            bool globalRecordingVisible,
            bool globalPlayerVisible,
            bool unfocusedPrivacyEnabled,
            IEnumerable<KeyValuePair<string, bool>> recording,
            IEnumerable<KeyValuePair<string, bool>> player)
        {
            lock (Gate)
            {
                var recordingValues = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var playerValues = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var fingerprints = CopyStrings(_current.DynamicFingerprints);
                foreach (KeyValuePair<string, bool> item in recording ?? Enumerable.Empty<KeyValuePair<string, bool>>())
                {
                    string key = NormalizeKey(item.Key);
                    if (IsKnownKey(key))
                    {
                        recordingValues[key] = item.Value;
                        ConfirmDynamic(key, fingerprints);
                    }
                }
                foreach (KeyValuePair<string, bool> item in player ?? Enumerable.Empty<KeyValuePair<string, bool>>())
                {
                    string key = NormalizeKey(item.Key);
                    if (IsKnownKey(key))
                    {
                        playerValues[key] = item.Value;
                        ConfirmDynamic(key, fingerprints);
                    }
                }
                Publish(new HudProfileSnapshot(
                    globalRecordingVisible, recordingValues,
                    globalPlayerVisible, playerValues,
                    unfocusedPrivacyEnabled, fingerprints));
                Save();
            }
        }

        internal static string Describe(string key)
        {
            key = NormalizeKey(key);
            if (!IsKnownKey(key)) return "unknown";
            HudProfileSnapshot current = _current;
            bool visible = current.RecordingVisible(key);
            string configured = current.RecordingOverrides.ContainsKey(key) ? "override" : "global";
            return key + "=" + (visible ? "on (recorded)" : "off (player-only)")
                   + ", source=" + configured
                   + ", player=" + ShowHide(current.PlayerVisible(key))
                   + ", route=" + RouteName(current.EffectiveRoute(key));
        }

        internal static string DescribePlayer(string key)
        {
            key = NormalizeKey(key);
            if (!IsKnownKey(key)) return "unknown player HUD source";
            HudProfileSnapshot current = _current;
            string configured = current.PlayerOverrides.ContainsKey(key) ? "override" : "global";
            return key + " player=" + ShowHide(current.PlayerVisible(key))
                   + ", source=" + configured
                   + ", recording=" + (current.RecordingVisible(key) ? "recorded" : "filtered")
                   + ", route=" + RouteName(current.EffectiveRoute(key));
        }

        internal static string StatusLine()
        {
            HudProfileSnapshot snapshot = _current;
            return "profile=v" + HudProfileSnapshot.CurrentSchema
                   + ", record-all=" + OnOff(snapshot.GlobalRecordingVisible)
                   + ", player-all=" + ShowHide(snapshot.GlobalPlayerVisible)
                   + ", privacy-unfocused=" + OnOff(snapshot.UnfocusedPrivacyEnabled)
                   + ", " + string.Join(", ", AllKeys.Select(k => k + "=" + RouteName(snapshot.EffectiveRoute(k))))
                   + ", persistence=" + _lastPersistence;
        }

        internal static string FilterStatusLine()
        {
            HudProfileSnapshot snapshot = _current;
            return "filters: all=" + OnOff(!snapshot.GlobalRecordingVisible)
                   + ", " + string.Join(", ", AllKeys.Select(k => k + "=" + OnOff(!snapshot.RecordingVisible(k))));
        }

        internal static string PlayerStatusLine()
        {
            HudProfileSnapshot snapshot = _current;
            return "player: all=" + ShowHide(snapshot.GlobalPlayerVisible)
                   + ", " + string.Join(", ", AllKeys.Select(k => k + "=" + ShowHide(snapshot.PlayerVisible(k))));
        }

        internal static string PrivacyStatusLine()
            => "privacy: unfocused=" + OnOff(_current.UnfocusedPrivacyEnabled);

        internal static string DescribeFilter(string key)
        {
            key = NormalizeKey(key);
            if (!IsKnownKey(key)) return "unknown filter";
            bool enabled = !_current.RecordingVisible(key);
            return key + " filter=" + OnOff(enabled)
                   + (enabled ? " (player-only)" : " (recorded)")
                   + ", player=" + ShowHide(_current.PlayerVisible(key))
                   + ", route=" + RouteName(_current.EffectiveRoute(key));
        }

        internal static string NormalizeKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string key = value.Trim().ToLowerInvariant().Replace('_', '-');
            switch (key)
            {
                case "k":
                case "k-menu":
                case "menu": return "terminal";
                case "chat-window":
                case "chat-history": return "chat";
                case "battery":
                case "fuel":
                case "battery/fuel":
                case "batteryfuel": return "battery-fuel";
                case "flightvector": return "flight-vector";
                case "flighthud": return "flight-hud";
                case "weaponcore-reload":
                case "wc-reload": return "weaponcore.reload";
                case "weaponcore-radar":
                case "wc-hud-radar": return "weaponcore.radar";
                case "wcradar": return "wc-radar";
                case "thrustbeacon":
                case "beacon": return "thrust-beacon";
                // Input already has '_' replaced by '-', so only the spaced/joined spellings remain.
                case "flipandburn":
                case "flip-and-burn":
                case "flipburn": return "flip-burn";
                // The PDA is what the key is reached for, so the menu's own names alias to the mod.
                case "pda":
                case "hand-terminal":
                case "handterminal":
                case "game-core": return "gamecore";
                default: return key;
            }
        }

        private static void Load()
        {
            try
            {
                // The file IO here no longer needs MyAPIGateway; Update still waits on
                // MyAPIGateway.Utilities purely as the "game session is up" signal, so load timing
                // (and therefore what the registry knows by now) is unchanged.
                string path = ProfilePath;
                if (string.IsNullOrEmpty(path))
                {
                    // No per-user storage location could be resolved. Defaults stand for the
                    // session and nothing is written; see ProfileDirectory.
                    _lastPersistence = "storage-unavailable";
                    return;
                }
                bool migrated = false;
                if (!File.Exists(path))
                {
                    string legacy = LegacyProfilePath();
                    if (legacy == null || !File.Exists(legacy))
                    {
                        _lastPersistence = "defaults(no-file)";
                        Save();
                        return;
                    }
                    // One-time pull-forward from the old game-Storage location. The old file is left
                    // in place; the Save below writes the profile to its new stable home.
                    path = legacy;
                    migrated = true;
                }

                int schema = 1;
                bool recordingGlobal = false;
                bool playerGlobal = true;
                bool privacyUnfocused = true;
                bool autoRedirect = false;
                var recording = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var player = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool capped = false;
                using (TextReader reader = new StreamReader(path))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                        int equals = line.IndexOf('=');
                        if (equals <= 0) continue;
                        string rawKey = line.Substring(0, equals).Trim();
                        string raw = line.Substring(equals + 1).Trim();
                        if (rawKey.Equals("schema", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(raw, out int parsedSchema)) schema = parsedSchema;
                            continue;
                        }
                        if (rawKey.StartsWith("fingerprint.", StringComparison.OrdinalIgnoreCase))
                        {
                            string fingerprintKey = NormalizeKey(rawKey.Substring("fingerprint.".Length));
                            if (TooLong(fingerprintKey, ref capped)) continue;
                            if (HudSourceRegistry.IsDynamicPolicyKey(fingerprintKey)
                                && raw.Length <= 64 && raw.All(Uri.IsHexDigit))
                            {
                                if (!fingerprints.ContainsKey(fingerprintKey)
                                    && fingerprints.Count >= MaxFingerprintEntries)
                                {
                                    capped = true;
                                    continue;
                                }
                                fingerprints[fingerprintKey] = raw.ToLowerInvariant();
                            }
                            continue;
                        }
                        if (!TryParse(raw, out bool value)) continue;
                        if (rawKey.Equals("privacy.unfocused", StringComparison.OrdinalIgnoreCase))
                        {
                            privacyUnfocused = value;
                            continue;
                        }
                        if (rawKey.Equals("auto.redirect", StringComparison.OrdinalIgnoreCase))
                        {
                            autoRedirect = value;
                            continue;
                        }
                        if (rawKey.StartsWith("player.", StringComparison.OrdinalIgnoreCase))
                        {
                            string playerKey = NormalizeKey(rawKey.Substring("player.".Length));
                            if (playerKey == "all") playerGlobal = value;
                            else if (TooLong(playerKey, ref capped)) { }
                            else if (IsKnownKey(playerKey))
                                AddOverride(player, recording.Count, playerKey, value, ref capped);
                            continue;
                        }

                        string key = NormalizeKey(rawKey);
                        if (key == "all") recordingGlobal = value;
                        else if (TooLong(key, ref capped)) { }
                        else if (IsKnownKey(key))
                            AddOverride(recording, player.Count, key, value, ref capped);
                    }
                }
                if (schema > HudProfileSnapshot.CurrentSchema)
                    throw new InvalidDataException("profile schema " + schema + " is newer than supported");

                Publish(new HudProfileSnapshot(
                    recordingGlobal, recording, playerGlobal, player, privacyUnfocused, fingerprints));
                _autoRedirect = autoRedirect;
                _lastPersistence = (schema < HudProfileSnapshot.CurrentSchema
                                       ? "migrated-v" + schema
                                       : "loaded")
                                   + (capped ? "(capped)" : string.Empty);
                if (migrated) Plugin.Log("profile migrated from game Storage");
                if (migrated || schema < HudProfileSnapshot.CurrentSchema) Save();
            }
            catch (Exception ex)
            {
                _lastPersistence = "load-failed:" + ex.GetType().Name;
                Plugin.Log("profile load failed; using safe defaults: "
                           + ex.GetType().Name + ": " + Sanitize(ex.Message));
                Publish(CreateDefault());
            }
        }

        private static void Save()
        {
            try
            {
                // Not an IO requirement any more - this is the sequencing guard that keeps a Save
                // issued before the profile has been loaded from overwriting the file with defaults.
                if (MyAPIGateway.Utilities == null)
                {
                    _lastPersistence = "pending-session";
                    return;
                }
                string directory = ProfileDirectory;
                string path = ProfilePath;
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(path))
                {
                    _lastPersistence = "storage-unavailable";
                    return;
                }

                HudProfileSnapshot current = _current;
                Directory.CreateDirectory(directory);

                // Written to a sibling temp file and moved into place, never straight over the live
                // profile: a crash or a full disk part way through a direct write leaves a
                // truncated file, which the loader reads as a valid profile with most of its
                // entries missing - a silent reset to defaults the user never asked for. Delete
                // plus Move rather than File.Replace because Replace requires an existing
                // destination and offers nothing this needs; the remaining window is between the
                // delete and the move, where the temp file still holds the complete new profile.
                string temporary = path + ".tmp";
                using (TextWriter writer = new StreamWriter(temporary, false))
                {
                    writer.WriteLine("schema=" + HudProfileSnapshot.CurrentSchema);
                    writer.WriteLine("all=" + OnOff(current.GlobalRecordingVisible));
                    foreach (KeyValuePair<string, bool> item in current.RecordingOverrides.OrderBy(kv => kv.Key))
                        writer.WriteLine(item.Key + "=" + OnOff(item.Value));
                    writer.WriteLine("player.all=" + OnOff(current.GlobalPlayerVisible));
                    foreach (KeyValuePair<string, bool> item in current.PlayerOverrides.OrderBy(kv => kv.Key))
                        writer.WriteLine("player." + item.Key + "=" + OnOff(item.Value));
                    foreach (KeyValuePair<string, string> item in current.DynamicFingerprints.OrderBy(kv => kv.Key))
                        writer.WriteLine("fingerprint." + item.Key + "=" + item.Value);
                    writer.WriteLine("privacy.unfocused=" + OnOff(current.UnfocusedPrivacyEnabled));
                    writer.WriteLine("auto.redirect=" + OnOff(_autoRedirect));
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
                _lastPersistence = "saved";
            }
            catch (Exception ex)
            {
                _lastPersistence = "save-failed:" + ex.GetType().Name;
                Plugin.Log("profile save failed: " + ex.GetType().Name + ": " + Sanitize(ex.Message));
            }
        }

        private static string LegacyProfilePath()
        {
            try
            {
                string root = MyFileSystem.UserDataPath;
                if (string.IsNullOrEmpty(root)) return null;
                string directory = Path.Combine(root, "Storage", FolderName);
                _legacyDirectory = directory;
                return Path.Combine(directory, FileName);
            }
            catch (Exception)
            {
                // MyFileSystem throws if the game has not initialised it; treat that as "no legacy file".
                return null;
            }
        }

        // File IO exceptions quote the full profile path, which contains the Windows user name, and
        // the plugin log is something users paste into bug reports. Log the exception type plus a
        // message with every profile directory reduced to a constant placeholder.
        private static string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            string[] scrubbed = ScrubbedPaths;
            for (int i = 0; i < scrubbed.Length; i++)
                message = message.Replace(scrubbed[i], "<profile>");
            string legacy = _legacyDirectory;
            if (!string.IsNullOrEmpty(legacy)) message = message.Replace(legacy, "<profile>");
            return message;
        }

        private static string[] BuildScrubbedPaths()
        {
            // Longest first so the specific profile directory is replaced before its parents.
            var paths = new List<string>
            {
                ProfileDirectory,
                SafeFolder(Environment.SpecialFolder.ApplicationData),
                SafeFolder(Environment.SpecialFolder.UserProfile)
            };
            return paths.Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(p => p.Length)
                .ToArray();
        }

        private static bool TooLong(string key, ref bool capped)
        {
            if (key == null || key.Length <= MaxKeyLength) return false;
            capped = true;
            return true;
        }

        private static void AddOverride(
            IDictionary<string, bool> target, int otherCount, string key, bool value, ref bool capped)
        {
            if (!target.ContainsKey(key) && target.Count + otherCount >= MaxOverrideEntries)
            {
                capped = true;
                return;
            }
            target[key] = value;
        }

        private static HudProfileSnapshot CreateDefault()
            => new HudProfileSnapshot(
                false, new Dictionary<string, bool>(),
                true, new Dictionary<string, bool>(), true);

        private static Dictionary<string, bool> Copy(IReadOnlyDictionary<string, bool> source)
            => source.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, string> CopyStrings(IReadOnlyDictionary<string, string> source)
            => source.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        private static void ConfirmDynamic(string key, IDictionary<string, string> fingerprints)
        {
            if (!HudSourceRegistry.IsDynamicPolicyKey(key)) return;
            string fingerprint = HudSourceRegistry.CurrentFingerprint(key);
            if (fingerprint != null) fingerprints[key] = fingerprint;
        }

        internal static bool IsExplicitKey(string key)
            => key != null && ExplicitKeySet.Contains(key);

        internal static bool IsKnownKey(string key)
            => IsExplicitKey(key) || HudSourceRegistry.IsDynamicPolicyKey(key);

        private static void Publish(HudProfileSnapshot snapshot)
            => Volatile.Write(ref _current, snapshot);

        private static bool TryParse(string raw, out bool value)
        {
            if (raw.Equals("on", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("show", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("shown", StringComparison.OrdinalIgnoreCase)
                || raw == "1")
            {
                value = true;
                return true;
            }
            if (raw.Equals("off", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("hide", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("hidden", StringComparison.OrdinalIgnoreCase)
                || raw == "0")
            {
                value = false;
                return true;
            }
            value = false;
            return false;
        }

        private static string RouteName(HudEffectiveRoute route)
            => route == HudEffectiveRoute.Recorded
                ? "recorded"
                : route == HudEffectiveRoute.PlayerOnly ? "player-only" : "suppressed";

        private static string ShowHide(bool value) => value ? "shown" : "hidden";
        private static string OnOff(bool value) => value ? "on" : "off";
    }
}
