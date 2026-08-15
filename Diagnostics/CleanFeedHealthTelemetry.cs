using System;
using System.Diagnostics;
using System.Threading;
using CleanFeed.Compatibility;
using CleanFeed.Render;
using Sandbox.ModAPI;

namespace CleanFeed.Diagnostics
{
    // Low-frequency process telemetry for long sessions. Sampling deliberately stays off the
    // render hot path; GPU/resource ownership is recorded separately with allocation-free counters.
    internal static class CleanFeedHealthTelemetry
    {
        private const int SampleIntervalSeconds = 10;
        private const int HeartbeatIntervalMinutes = 5;
        private const long GrowthWarningBytes = 256L * 1024 * 1024;
        private const int GrowthWarningPercent = 20;
        private const int WindowSamples = 31; // roughly five minutes, including both endpoints
        private static readonly long SampleIntervalTicks = Stopwatch.Frequency * SampleIntervalSeconds;
        private static readonly long HeartbeatIntervalTicks = Stopwatch.Frequency * 60 * HeartbeatIntervalMinutes;
        private static readonly object Gate = new object();
        private static readonly long[] PrivateBytesWindow = new long[WindowSamples];

        private static Process _process;
        private static long _startedAt;
        private static long _lastSampleAt;
        private static long _lastHeartbeatAt;
        private static long _lastGrowthWarningAt;
        private static long _baselinePrivateBytes;
        private static long _privateBytes;
        private static long _privateBytesHighWater;
        private static long _workingSetBytes;
        private static long _managedBytes;
        private static int _handleCount;
        private static int _gc0;
        private static int _gc1;
        private static int _gc2;
        private static int _windowIndex;
        private static int _windowCount;
        private static long _samples;
        private static long _growthWarnings;
        private static long _sampleFailures;
        private static string _lastSampleError = "none";
        private static string _viewContext = "unavailable";
        private static long _viewTransitions;
        private static long _viewEntityId;
        private static Type _viewEntityType;
        private static int _viewInitialized;

        internal static void Initialize()
        {
            lock (Gate)
            {
                if (_process != null) return;
                _process = Process.GetCurrentProcess();
                _startedAt = Stopwatch.GetTimestamp();
                _lastHeartbeatAt = _startedAt;
                SampleLocked(_startedAt);
                Plugin.Log("health telemetry started: process=10s, heartbeat=5m, growth-window=5m");
            }
        }

        internal static void Update()
        {
            long now = Stopwatch.GetTimestamp();
            if (now - Interlocked.Read(ref _lastSampleAt) < SampleIntervalTicks
                && now - Interlocked.Read(ref _lastHeartbeatAt) < HeartbeatIntervalTicks)
                return;

            bool heartbeat = false;
            bool growthWarning = false;
            string growthDetail = null;

            lock (Gate)
            {
                if (_process == null) return;
                if (now - _lastSampleAt >= SampleIntervalTicks)
                {
                    SampleLocked(now);
                    long windowStart = WindowStartLocked();
                    long delta = _privateBytes - windowStart;
                    bool enoughGrowth = _windowCount == PrivateBytesWindow.Length
                                        && windowStart > 0
                                        && delta >= GrowthWarningBytes
                                        && delta * 100L >= windowStart * GrowthWarningPercent;
                    if (enoughGrowth
                        && (now - _lastGrowthWarningAt >= HeartbeatIntervalTicks
                            || _lastGrowthWarningAt == 0))
                    {
                        _lastGrowthWarningAt = now;
                        _growthWarnings++;
                        growthWarning = true;
                        growthDetail = "private-memory growth suspect: delta-5m=" + FormatSignedMiB(delta)
                                       + ", current=" + FormatMiB(_privateBytes)
                                       + ", managed=" + FormatMiB(_managedBytes)
                                       + ", handles=" + _handleCount;
                    }
                }

                if (now - _lastHeartbeatAt >= HeartbeatIntervalTicks)
                {
                    _lastHeartbeatAt = now;
                    heartbeat = true;
                }
            }

            if (growthWarning)
                Plugin.Log("health warning: " + growthDetail + "; " + HudRedirector.HealthStatusLine()
                           + "; " + HudSourceRegistry.StatusLine() + "; " + HudSourceDiscovery.ReportStatusLine());
            if (heartbeat && !growthWarning)
                Plugin.Log("health heartbeat: " + StatusLine() + "; " + HudRedirector.HealthStatusLine()
                           + "; " + HudSourceRegistry.StatusLine() + "; " + HudSourceDiscovery.ReportStatusLine());
        }

        internal static void ObserveViewContext()
        {
            var entity = MyAPIGateway.Session?.Player?.Controller?.ControlledEntity?.Entity;
            long entityId = entity?.EntityId ?? 0;
            Type entityType = entity?.GetType();
            if (Volatile.Read(ref _viewInitialized) != 0
                && Interlocked.Read(ref _viewEntityId) == entityId
                && ReferenceEquals(Volatile.Read(ref _viewEntityType), entityType))
                return;

            string current;
            string previous;
            lock (Gate)
            {
                if (_viewInitialized != 0
                    && _viewEntityId == entityId
                    && ReferenceEquals(_viewEntityType, entityType))
                    return;
                // Keep entity identity out of diagnostics. The ID is retained only in memory to
                // detect a controller transition; logs and user-visible status expose the type.
                current = entityType == null ? "none" : entityType.Name;
                previous = _viewContext;
                _viewEntityId = entityId;
                _viewEntityType = entityType;
                _viewContext = current;
                _viewTransitions++;
                Volatile.Write(ref _viewInitialized, 1);
            }

            if (HudRedirector.Requested)
                Plugin.Log("view transition: " + previous + " -> " + current
                           + "; " + HudRedirector.HealthStatusLine());
        }

        internal static string StatusLine()
        {
            lock (Gate)
            {
                if (_process == null) return "health=off";
                long now = Stopwatch.GetTimestamp();
                long ageSeconds = Math.Max(0, (now - _lastSampleAt) / Stopwatch.Frequency);
                long uptimeMinutes = Math.Max(0, (now - _startedAt) / Stopwatch.Frequency / 60);
                long windowDelta = _privateBytes - WindowStartLocked();
                return "health=on"
                       + ", uptime=" + uptimeMinutes + "m"
                       + ", samples=" + _samples
                       + ", sample-age=" + ageSeconds + "s"
                       + ", private=" + FormatMiB(_privateBytes)
                       + ", private-base=" + FormatMiB(_baselinePrivateBytes)
                       + ", private-high=" + FormatMiB(_privateBytesHighWater)
                       + ", private-5m=" + FormatSignedMiB(windowDelta)
                       + ", working=" + FormatMiB(_workingSetBytes)
                       + ", managed=" + FormatMiB(_managedBytes)
                       + ", handles=" + _handleCount
                       + ", gc=" + _gc0 + "/" + _gc1 + "/" + _gc2
                       + ", growth-warnings=" + _growthWarnings
                       + ", probe-failures=" + _sampleFailures
                       + ", probe-error=" + _lastSampleError
                       + ", view=" + _viewContext
                       + ", view-transitions=" + _viewTransitions;
            }
        }

        internal static void Shutdown()
        {
            lock (Gate)
            {
                _process?.Dispose();
                _process = null;
            }
        }

        private static void SampleLocked(long now)
        {
            _lastSampleAt = now;
            try
            {
                _process.Refresh();
                _privateBytes = _process.PrivateMemorySize64;
                _workingSetBytes = _process.WorkingSet64;
                _handleCount = _process.HandleCount;
                _managedBytes = GC.GetTotalMemory(false);
                _gc0 = GC.CollectionCount(0);
                _gc1 = GC.CollectionCount(1);
                _gc2 = GC.CollectionCount(2);
                if (_baselinePrivateBytes == 0) _baselinePrivateBytes = _privateBytes;
                if (_privateBytes > _privateBytesHighWater) _privateBytesHighWater = _privateBytes;
                PrivateBytesWindow[_windowIndex] = _privateBytes;
                _windowIndex = (_windowIndex + 1) % PrivateBytesWindow.Length;
                if (_windowCount < PrivateBytesWindow.Length) _windowCount++;
                _samples++;
                _lastSampleError = "none";
            }
            catch (Exception ex)
            {
                _sampleFailures++;
                _lastSampleError = ex.GetType().Name + ":" + ex.Message;
            }
        }

        private static long WindowStartLocked()
        {
            if (_windowCount == 0) return _privateBytes;
            int index = _windowCount == PrivateBytesWindow.Length ? _windowIndex : 0;
            return PrivateBytesWindow[index];
        }

        private static string FormatMiB(long bytes)
            => (bytes / (1024d * 1024d)).ToString("0.0") + "MiB";

        private static string FormatSignedMiB(long bytes)
            => (bytes >= 0 ? "+" : string.Empty) + FormatMiB(bytes);
    }
}
