using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using CleanFeed.Diagnostics;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using VRage.Render11.Resources;
using VRageRender;
using D3DDevice = SharpDX.Direct3D11.Device;
using DxgiDevice = SharpDX.DXGI.Device;
using DxgiSurface = SharpDX.DXGI.Surface;

namespace CleanFeed.Render
{
    // Render-thread-only GPU transport. HUD pixels never leave D3D11 resources, and the native
    // composition path deliberately has no second DXGI swapchain or Present for capture hooks.
    internal static class HudGpuTransport
    {
        // DirectComposition surfaces are always created as B8G8R8A8_UNORM.
        private const Format CompositionFormat = Format.B8G8R8A8_UNorm;
        private static Format _layerFormat;

        // Two independent properties of the layer format matter, and only one is inherited from the
        // game's backbuffer.
        //
        // sRGB-ness is inherited. It decides whether the RTV applies a linear->sRGB encode on write,
        // so the layer must match the backbuffer: an _UNorm layer under an _SRgb backbuffer loses an
        // encode and comes out dark, an _SRgb layer under a _UNorm backbuffer adds one and comes out
        // washed out. The game does the same for screenshots, borrowing an RTV in the swapchain's
        // format before re-rendering the main sprites into it.
        //
        // The channel family is not inherited - it is always BGRA8, matching CompositionFormat.
        // CopyToCompositionSurface is a raw bit copy, which D3D11 only permits within one typeless
        // family; an RGBA8 source against the BGRA8 composition surface is an invalid copy that
        // CopySubresourceRegion drops silently (void call, no HRESULT), leaving the composition
        // surfaces transparent and the HUD entirely absent. RGBA8 backbuffers occur in the field, so
        // the family must not be taken from the backbuffer. Overriding it is safe because a render
        // target's channel order is invisible to sprite rendering - shaders write float4 RGBA and the
        // hardware swizzles - unlike a bit copy, which sees the memory layout directly.
        //
        // An _SRgb layer still lands correctly in the UNORM composition surface: DWM interprets
        // DirectComposition content as sRGB, which is exactly what an _SRgb target produced.
        internal static Format LayerFormat
        {
            get
            {
                if (_layerFormat != Format.Unknown) return _layerFormat;
                try
                {
                    var backbuffer = MyRender11.Backbuffer?.Resource as Texture2D;
                    if (backbuffer != null)
                    {
                        switch (backbuffer.Description.Format)
                        {
                            case Format.B8G8R8A8_UNorm_SRgb:
                            case Format.R8G8B8A8_UNorm_SRgb:
                                _layerFormat = Format.B8G8R8A8_UNorm_SRgb;
                                break;
                            case Format.B8G8R8A8_UNorm:
                            case Format.R8G8B8A8_UNorm:
                                _layerFormat = Format.B8G8R8A8_UNorm;
                                break;
                        }
                    }
                }
                catch { /* fall through to the default below */ }
                if (_layerFormat == Format.Unknown) _layerFormat = Format.B8G8R8A8_UNorm_SRgb;
                return _layerFormat;
            }
        }

        private static readonly long TransientBatchHoldTicks = Stopwatch.Frequency / 20; // 50 ms
        private static readonly long GpuSampleIntervalTicks = Stopwatch.Frequency * 10;
        private static readonly Guid DxgiSurfaceId = new Guid("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");
        private static readonly long[] GpuUsageWindow = new long[31];
        private static NativeDirectComposition _composition;
        private static IntPtr _boundHwnd;
        private static int _width;
        private static int _height;
        private static int _clientWidth;
        private static int _clientHeight;
        private static int _renderThreadId;
        private static bool _updatePending;
        private static int _pendingLayerMask;
        private static long _copies;
        private static long _commits;
        private static long _rebuilds;
        private static long _lastCpuMicroseconds;
        private static long _copyFailures;
        private static long _commitFailures;
        private static long _layerClears;
        private static long _undefinedUpdatesRecovered;
        private static long _overwrittenPendingCopies;
        private static long _abandonedPendingCopies;
        private static long _pendingSince;
        private static long _maxPendingMicroseconds;
        private static long _lastCopyAt;
        private static long _lastCommitAt;
        private static long _emptyBatches;
        private static long _heldEmptyBatches;
        private static long _partialBatches;
        private static long _heldPartialBatches;
        private static long _lastPublishedBatchAt;
        private static int _lastMessageCount = -1;
        private static int _lastPublishedMessageCount = -1;
        private static int _emptyRun;
        private static int _partialRun;
        private static readonly int[] RecentMessageCounts = new int[16];
        private static int _recentMessageIndex;
        private static int _recentMessageSamples;
        private static long _lastGpuSampleAt;
        private static long _gpuLocalUsage;
        private static long _gpuLocalBudget;
        private static long _gpuLocalHighWater;
        private static long _gpuSamples;
        private static long _gpuSampleFailures;
        private static string _lastGpuSampleError = "none";
        private static int _gpuWindowIndex;
        private static int _gpuWindowSamples;
        private static bool _flightHudOccluded;
        private static long _flightHudDetaches;
        private static long _flightHudRestores;

        public static bool IsReady => _composition != null;
        public static int Width => _width;
        public static int Height => _height;

        // True when the caller may touch the composition objects directly. The render thread latches
        // itself on the first transport call; before that latch there is no render thread to hand
        // work to, so an unlatched transport answers true and the caller proceeds itself.
        internal static bool IsRenderThread
        {
            get
            {
                int latched = Volatile.Read(ref _renderThreadId);
                return latched == 0 || latched == Environment.CurrentManagedThreadId;
            }
        }

        public static bool Matches(IntPtr hwnd, int width, int height, int clientWidth, int clientHeight)
            => IsReady && _boundHwnd == hwnd && _width == width && _height == height
               && _clientWidth == clientWidth && _clientHeight == clientHeight;

        // width/height stay the render resolution and size the layers and composition surfaces.
        // clientWidth/clientHeight are the window client rect the composition tree is stretched onto,
        // and only reach the NativeDirectComposition root-visual transform.
        public static void Ensure(IntPtr hwnd, int width, int height, int clientWidth, int clientHeight)
        {
            AssertRenderThread();
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("GPU HUD window has no HWND");
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Invalid HUD surface size");
            if (clientWidth <= 0 || clientHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(clientWidth), "Invalid HUD client size");
            if (Matches(hwnd, width, height, clientWidth, clientHeight)) return;

            DisposeResources();
            // DisposeResources unlatches the render thread; re-latch it immediately so the resources
            // built below are never owned by an unlatched transport, which would let a shutdown on
            // another thread take the direct disposal path instead of waiting for this one.
            AssertRenderThread();
            Interlocked.Increment(ref _rebuilds);

            D3DDevice device = MyRender11.DeviceInstance;
            try
            {
                using (DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>())
                {
                    _composition = new NativeDirectComposition(
                        dxgiDevice.NativePointer, hwnd, width, height, clientWidth, clientHeight);
                }

                _boundHwnd = hwnd;
                _width = width;
                _height = height;
                _clientWidth = clientWidth;
                _clientHeight = clientHeight;
                _flightHudOccluded = false;
                // Order is load-bearing and is the whole of the whiteout fix. The composition tree
                // comes back from the constructor built but unrooted and uncommitted, so nothing it
                // owns can be composited yet; every layer is drawn transparent here while that is
                // still true, and AttachRoot then roots the tree and commits once. DWM's first sight
                // of this tree is therefore a commit that already carries defined, cleared content -
                // there is no pass in which undefined surface memory can be painted over the game,
                // however long the clears take under VRAM pressure.
                ClearAllLayers(device);
                _composition.AttachRoot();
                Interlocked.Increment(ref _layerClears);
                Plugin.Log("GPU HUD logical surface ready: " + width + "x" + height
                           + ", client=" + clientWidth + "x" + clientHeight
                           + ", layer-format=" + LayerFormat + ", transport=DirectComposition");
            }
            catch
            {
                DisposeResources();
                throw;
            }
        }

        public static void CopyCompletedHud(
            IBorrowedRtvTexture layer,
            int messageCount,
            bool holdTransientBatches = true,
            HudCompositionLayer compositionLayer = HudCompositionLayer.Main)
        {
            AssertRenderThread();
            if (!IsReady) throw new InvalidOperationException("GPU HUD surface is not ready");
            var source = layer?.Resource as Texture2D;
            if (source == null) throw new InvalidOperationException("HUD RTV resource is not a Texture2D");

            bool mainLayer = compositionLayer == HudCompositionLayer.Main;
            if (mainLayer)
            {
                _lastMessageCount = messageCount;
                RecordMessageCount(messageCount);
            }
            long now = Stopwatch.GetTimestamp();
            int publishedCount = Volatile.Read(ref _lastPublishedMessageCount);
            bool substantialDrop = mainLayer
                                   && messageCount > 0
                                   && publishedCount >= 64
                                   && (long)messageCount * 2 < publishedCount;
            if (mainLayer && messageCount == 0)
            {
                Interlocked.Increment(ref _emptyBatches);
                _emptyRun++;
            }
            else if (mainLayer)
            {
                _emptyRun = 0;
            }
            if (mainLayer && substantialDrop)
            {
                Interlocked.Increment(ref _partialBatches);
                _partialRun++;
            }
            else _partialRun = 0;

            // HUD producers emit small incremental batches between their complete ones, and
            // replacing the full logical surface with one strobes every redirected element. Keep the
            // composed GPU surface across a brief empty or sharp count drop; accept a lower batch
            // that outlives the hold window so genuine HUD removal stays responsive.
            long lastPublished = Interlocked.Read(ref _lastPublishedBatchAt);
            bool transientCandidate = messageCount == 0 || substantialDrop;
            if (mainLayer && holdTransientBatches && transientCandidate
                && (lastPublished == 0 || now - lastPublished <= TransientBatchHoldTicks))
            {
                if (messageCount == 0) Interlocked.Increment(ref _heldEmptyBatches);
                if (substantialDrop) Interlocked.Increment(ref _heldPartialBatches);
                return;
            }

            long started = now;
            try { CopyToCompositionSurface(source, compositionLayer); }
            catch
            {
                Interlocked.Increment(ref _copyFailures);
                throw;
            }
            int layerBit = 1 << (int)compositionLayer;
            if ((_pendingLayerMask & layerBit) != 0)
                Interlocked.Increment(ref _overwrittenPendingCopies);
            if (!_updatePending)
                Interlocked.Exchange(ref _pendingSince, now);
            _pendingLayerMask |= layerBit;
            _updatePending = true;
            Interlocked.Exchange(ref _lastCopyAt, now);
            if (mainLayer)
            {
                Volatile.Write(ref _lastPublishedMessageCount, messageCount);
                Interlocked.Exchange(ref _lastPublishedBatchAt, now);
            }
            Interlocked.Increment(ref _copies);
            RecordCpuTime(started);
        }

        // Called immediately before the game's sole DXGI Present. Commit publishes the already-copied
        // GPU surface to DWM but does not create a second presentation stream for NVIDIA to hook.
        public static void CommitLatest()
        {
            AssertRenderThread();
            if (!IsReady) return;

            long now = Stopwatch.GetTimestamp();
            SampleGpuMemoryIfDue(now);
            bool modalOccluded = HudModalState.Active;
            if (modalOccluded != _flightHudOccluded
                && _composition.SetFlightHudVisible(!modalOccluded))
            {
                _flightHudOccluded = modalOccluded;
                if (modalOccluded) Interlocked.Increment(ref _flightHudDetaches);
                else Interlocked.Increment(ref _flightHudRestores);
                if (!_updatePending) Interlocked.Exchange(ref _pendingSince, now);
                _updatePending = true;
            }
            if (!_updatePending) return;

            long started = now;
            try { _composition.Commit(); }
            catch
            {
                Interlocked.Increment(ref _commitFailures);
                throw;
            }
            _updatePending = false;
            _pendingLayerMask = 0;
            long pendingSince = Interlocked.Exchange(ref _pendingSince, 0);
            if (pendingSince != 0)
                RaiseHighWater(ref _maxPendingMicroseconds,
                    (now - pendingSince) * 1000000L / Stopwatch.Frequency);
            Interlocked.Exchange(ref _lastCommitAt, now);
            Interlocked.Increment(ref _commits);
            RecordCpuTime(started);
        }

        // Callable from either thread. The render thread is the normal caller; the update thread only
        // reaches here as a last resort, when the render loop has already stopped and can no longer
        // service its own cleanup (see HudRedirector.Shutdown).
        public static void DisposeResources()
        {
            if (_updatePending) Interlocked.Increment(ref _abandonedPendingCopies);
            _updatePending = false;
            _pendingLayerMask = 0;
            Interlocked.Exchange(ref _pendingSince, 0);
            _composition?.Dispose();
            _composition = null;
            _boundHwnd = IntPtr.Zero;
            _width = 0;
            _height = 0;
            _clientWidth = 0;
            _clientHeight = 0;
            _flightHudOccluded = false;
            // Unlatch the render thread with the resources it owned. The game can retire and recreate
            // its render thread (device reset, renderer restart); without this the stale id would make
            // AssertRenderThread reject the new thread forever and the transport could never rearm.
            Interlocked.Exchange(ref _renderThreadId, 0);
        }

        public static string StatusLine()
            => "surface=" + (IsReady
                   ? Width + "x" + Height + "/bgra8/logical"
                     + "/client=" + _clientWidth + "x" + _clientHeight
                   : "none")
               + ", layer-format=" + LayerFormat
               + ", copies=" + Interlocked.Read(ref _copies)
               + ", commits=" + Interlocked.Read(ref _commits)
               + ", rebuilds=" + Interlocked.Read(ref _rebuilds)
               + ", messages=" + _lastMessageCount
               + ", empty=" + Interlocked.Read(ref _emptyBatches)
               + ", held=" + Interlocked.Read(ref _heldEmptyBatches)
               + ", empty-run=" + _emptyRun
               + ", partial=" + Interlocked.Read(ref _partialBatches)
               + ", partial-held=" + Interlocked.Read(ref _heldPartialBatches)
               + ", partial-run=" + _partialRun
               + ", published-messages=" + Volatile.Read(ref _lastPublishedMessageCount)
               + ", recent=" + RecentMessageCountsLine()
               + ", last-cpu=" + Interlocked.Read(ref _lastCpuMicroseconds) + "us"
               + ", copy-failures=" + Interlocked.Read(ref _copyFailures)
               + ", commit-failures=" + Interlocked.Read(ref _commitFailures)
               + ", layer-clears=" + Interlocked.Read(ref _layerClears)
               + ", update-recoveries=" + Interlocked.Read(ref _undefinedUpdatesRecovered)
               + ", pending=" + (_updatePending ? PendingAgeMicroseconds() + "us" : "none")
               + ", pending-max=" + Interlocked.Read(ref _maxPendingMicroseconds) + "us"
               + ", pending-overwrites=" + Interlocked.Read(ref _overwrittenPendingCopies)
               + ", pending-abandoned=" + Interlocked.Read(ref _abandonedPendingCopies)
               + ", copy-age=" + AgeMilliseconds(Interlocked.Read(ref _lastCopyAt)) + "ms"
               + ", commit-age=" + AgeMilliseconds(Interlocked.Read(ref _lastCommitAt)) + "ms"
               + ", " + GpuMemoryStatusLine()
               + ", render-thread=" + Volatile.Read(ref _renderThreadId)
               + ", flight-modal=" + (_flightHudOccluded ? "detached" : "visible")
               + ", flight-detach=" + Interlocked.Read(ref _flightHudDetaches)
               + ", flight-restore=" + Interlocked.Read(ref _flightHudRestores)
               + ", " + HudResourceTelemetry.StatusLine();

        internal static string HealthStatusLine()
            => "transport=" + (IsReady ? "ready" : "off")
               + ", copy-commit=" + Interlocked.Read(ref _copies) + "/" + Interlocked.Read(ref _commits)
               + ", failures=" + Interlocked.Read(ref _copyFailures) + "/" + Interlocked.Read(ref _commitFailures)
               + ", layer-clears=" + Interlocked.Read(ref _layerClears)
               + ", update-recoveries=" + Interlocked.Read(ref _undefinedUpdatesRecovered)
               + ", pending=" + (_updatePending ? PendingAgeMicroseconds() + "us" : "none")
               + ", pending-max=" + Interlocked.Read(ref _maxPendingMicroseconds) + "us"
               + ", pending-overwrites=" + Interlocked.Read(ref _overwrittenPendingCopies)
               + ", pending-abandoned=" + Interlocked.Read(ref _abandonedPendingCopies)
               + ", flight-modal=" + (_flightHudOccluded ? "detached" : "visible")
               + ", flight-transitions=" + Interlocked.Read(ref _flightHudDetaches)
               + "/" + Interlocked.Read(ref _flightHudRestores)
               + ", " + GpuMemoryStatusLine()
               + ", " + HudResourceTelemetry.StatusLine();

        private static void RecordMessageCount(int count)
        {
            int index = _recentMessageIndex;
            Volatile.Write(ref RecentMessageCounts[index], count);
            _recentMessageIndex = (index + 1) % RecentMessageCounts.Length;
            if (_recentMessageSamples < RecentMessageCounts.Length) _recentMessageSamples++;
        }

        private static string RecentMessageCountsLine()
        {
            int samples = Math.Min(Volatile.Read(ref _recentMessageSamples), RecentMessageCounts.Length);
            if (samples == 0) return "none";
            int next = Volatile.Read(ref _recentMessageIndex);
            int start = samples == RecentMessageCounts.Length ? next : 0;
            var values = new List<string>(samples);
            for (int i = 0; i < samples; i++)
                values.Add(Volatile.Read(ref RecentMessageCounts[(start + i) % RecentMessageCounts.Length]).ToString());
            return string.Join("/", values);
        }

        // Takes the HUD off the glass without taking the composition down: every layer is drawn
        // transparent and the clear is published with a single commit, while device, target, visuals
        // and surfaces all stay alive. HudRedirector uses this to park the overlay for an unfocused
        // window, so the return to foreground is a resumed commit rather than a rebuild.
        //
        // The clear is issued at transport level rather than through the per-route publish paths, so
        // a route's MayHaveContent latch can still read true over a surface that is now transparent.
        // That inconsistency only ever runs one way and cannot leak: the surface underneath is
        // already blank, nothing but a fresh publish can put content back on it, and the worst the
        // stale latch costs is one redundant transparent publish the next time the route clears.
        // Routes whose producers draw every frame republish on their next generation regardless.
        internal static void PublishTransparentAll()
        {
            AssertRenderThread();
            if (!IsReady) return;

            ClearAllLayers(MyRender11.DeviceInstance);
            _composition.Commit();
            // Any copy still pending was overwritten by the clears above, so it must not be counted
            // as abandoned and must not be waiting for a later commit.
            _updatePending = false;
            _pendingLayerMask = 0;
            Interlocked.Exchange(ref _pendingSince, 0);
            Interlocked.Exchange(ref _lastCommitAt, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _layerClears);
        }

        // Draws all six layers transparent and commits nothing. The caller decides how the clear
        // reaches DWM: the enable path publishes it with the tree's first and only attach commit, the
        // soft-pause path with a commit of its own.
        private static void ClearAllLayers(D3DDevice device)
        {
            var description = new Texture2DDescription
            {
                // Exactly the logical surface size, which is what ValidateSource demands of every
                // source: a full-surface update that is not fully written leaves undefined pixels.
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                // ValidateSource requires every source to be in the backbuffer-derived layer format;
                // the copy into the UNORM composition surface stays a raw BGRA8-typeless bit copy,
                // and the cleared content is all zero so the format choice does not change pixels.
                Format = LayerFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };

            using (var transparent = new Texture2D(device, description))
            using (var renderTarget = new RenderTargetView(device, transparent))
            {
                device.ImmediateContext.ClearRenderTargetView(renderTarget, new RawColor4(0, 0, 0, 0));
                CopyToCompositionSurface(transparent, HudCompositionLayer.Main);
                CopyToCompositionSurface(transparent, HudCompositionLayer.Toolbar);
                CopyToCompositionSurface(transparent, HudCompositionLayer.FlightHud);
                CopyToCompositionSurface(transparent, HudCompositionLayer.Interactive);
                CopyToCompositionSurface(transparent, HudCompositionLayer.Chat);
                CopyToCompositionSurface(transparent, HudCompositionLayer.RichHud);
            }
        }

        // BeginDraw is called with a null update rect, so the update it opens covers the whole layer
        // and EndDraw publishes the whole layer whether or not anything was written into it. Every
        // step that can be decided without an open update is therefore decided before BeginDraw, and
        // the two that cannot - obtaining the update texture and checking the offset against the
        // atlas DirectComposition handed back - leave the update defined before it is closed.
        private static void CopyToCompositionSurface(
            Texture2D source, HudCompositionLayer compositionLayer)
        {
            ValidateSource(source.Description);

            IntPtr updateObject = IntPtr.Zero;
            DxgiSurface surface = null;
            Texture2D destination = null;
            bool drawStarted = false;
            bool defined = false;
            int offsetX = 0;
            int offsetY = 0;
            try
            {
                Guid iid = DxgiSurfaceId;
                updateObject = _composition.BeginDraw(
                    compositionLayer, ref iid, out offsetX, out offsetY);
                drawStarted = true;

                surface = new DxgiSurface(updateObject);
                updateObject = IntPtr.Zero; // The wrapper now owns the BeginDraw reference.
                destination = surface.QueryInterface<Texture2D>();
                HudResourceTelemetry.CopyDestinationAcquired();
                ValidateUpdateTarget(source.Description, destination.Description, offsetX, offsetY);
                MyRender11.DeviceInstance.ImmediateContext.CopySubresourceRegion(
                    source, 0, null, destination, 0, offsetX, offsetY, 0);
                defined = true;
            }
            finally
            {
                try
                {
                    // Ahead of EndDraw, because EndDraw is what publishes the update: a failure
                    // between BeginDraw and the copy must not close an update nothing wrote to.
                    if (drawStarted && !defined)
                        LeaveUpdateDefined(compositionLayer, destination);
                }
                finally
                {
                    try
                    {
                        // Keep the BeginDraw object alive until DirectComposition has closed the
                        // update.
                        if (drawStarted) _composition.EndDraw();
                    }
                    finally
                    {
                        try
                        {
                            try { destination?.Dispose(); }
                            finally
                            {
                                if (destination != null)
                                    HudResourceTelemetry.CopyDestinationReleased();
                            }
                        }
                        finally
                        {
                            try
                            {
                                surface?.Dispose();
                                if (updateObject != IntPtr.Zero) Marshal.Release(updateObject);
                            }
                            finally
                            {
                                if (drawStarted) HudResourceTelemetry.UpdateObjectReleased();
                            }
                        }
                    }
                }
            }
        }

        // Gives an update that will not be written a defined value before it is closed. The update
        // texture is preferred: clearing it leaves the layer transparent, which is exactly what an
        // empty layer should look like. Clearing it whole rather than only the update rect is
        // deliberate - the pixels around it belong to updates already closed this frame, and this
        // path only runs when the frame is about to fail open, which detaches the whole tree before
        // any further commit, so no half-cleared atlas can ever be composited. If the update texture
        // itself could not be obtained there is nothing left to draw into and the layer is detached
        // instead, which keeps the undrawn surface out of the composition entirely.
        //
        // Nothing here is allowed to throw: it runs inside the copy's finally, and the original
        // failure is the one the caller must see.
        private static void LeaveUpdateDefined(
            HudCompositionLayer compositionLayer, Texture2D destination)
        {
            try
            {
                if (destination != null)
                {
                    using (var view = new RenderTargetView(MyRender11.DeviceInstance, destination))
                    {
                        MyRender11.DeviceInstance.ImmediateContext.ClearRenderTargetView(
                            view, new RawColor4(0, 0, 0, 0));
                    }
                }
                else _composition.DetachLayerContent(compositionLayer);
                Interlocked.Increment(ref _undefinedUpdatesRecovered);
            }
            catch
            {
                try { _composition.DetachLayerContent(compositionLayer); }
                catch { /* the tree is already coming down; nothing further can be salvaged */ }
            }
        }

        private static void SampleGpuMemoryIfDue(long now)
        {
            long previous = Interlocked.Read(ref _lastGpuSampleAt);
            if (previous != 0 && now - previous < GpuSampleIntervalTicks) return;
            Interlocked.Exchange(ref _lastGpuSampleAt, now);
            try
            {
                using (var dxgiDevice = MyRender11.DeviceInstance.QueryInterface<DxgiDevice>())
                using (var adapter = dxgiDevice.Adapter)
                using (var adapter3 = adapter.QueryInterface<Adapter3>())
                {
                    QueryVideoMemoryInformation info = adapter3.QueryVideoMemoryInfo(
                        0, MemorySegmentGroup.Local);
                    Interlocked.Exchange(ref _gpuLocalUsage, info.CurrentUsage);
                    Interlocked.Exchange(ref _gpuLocalBudget, info.Budget);
                    RaiseHighWater(ref _gpuLocalHighWater, info.CurrentUsage);
                    int index = _gpuWindowIndex;
                    Volatile.Write(ref GpuUsageWindow[index], info.CurrentUsage);
                    _gpuWindowIndex = (index + 1) % GpuUsageWindow.Length;
                    if (_gpuWindowSamples < GpuUsageWindow.Length) _gpuWindowSamples++;
                    Interlocked.Increment(ref _gpuSamples);
                    Volatile.Write(ref _lastGpuSampleError, "none");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _gpuSampleFailures);
                Volatile.Write(ref _lastGpuSampleError, ex.GetType().Name + ":" + ex.Message);
            }
        }

        private static string GpuMemoryStatusLine()
        {
            long usage = Interlocked.Read(ref _gpuLocalUsage);
            long budget = Interlocked.Read(ref _gpuLocalBudget);
            long windowStart = GpuWindowStart();
            return "gpu-local=" + FormatMiB(usage) + "/" + FormatMiB(budget)
                   + ", gpu-high=" + FormatMiB(Interlocked.Read(ref _gpuLocalHighWater))
                   + ", gpu-5m=" + FormatSignedMiB(usage - windowStart)
                   + ", gpu-samples=" + Interlocked.Read(ref _gpuSamples)
                   + ", gpu-probe-failures=" + Interlocked.Read(ref _gpuSampleFailures)
                   + ", gpu-probe-error=" + Volatile.Read(ref _lastGpuSampleError);
        }

        private static long GpuWindowStart()
        {
            int samples = Math.Min(Volatile.Read(ref _gpuWindowSamples), GpuUsageWindow.Length);
            if (samples == 0) return Interlocked.Read(ref _gpuLocalUsage);
            int next = Volatile.Read(ref _gpuWindowIndex);
            int index = samples == GpuUsageWindow.Length ? next : 0;
            return Volatile.Read(ref GpuUsageWindow[index]);
        }

        private static long PendingAgeMicroseconds()
        {
            long started = Interlocked.Read(ref _pendingSince);
            return started == 0 ? 0 : Math.Max(0,
                (Stopwatch.GetTimestamp() - started) * 1000000L / Stopwatch.Frequency);
        }

        private static long AgeMilliseconds(long timestamp)
            => timestamp == 0 ? -1 : Math.Max(0,
                (Stopwatch.GetTimestamp() - timestamp) * 1000L / Stopwatch.Frequency);

        private static string FormatMiB(long bytes)
            => (bytes / (1024d * 1024d)).ToString("0.0") + "MiB";

        private static string FormatSignedMiB(long bytes)
            => (bytes >= 0 ? "+" : string.Empty) + FormatMiB(bytes);

        // Everything about the source, checked before an update is opened. The size is measured
        // against the logical surface rather than against whatever texture DirectComposition hands
        // back: that texture may be a larger shared atlas, so a source that merely fits inside it can
        // still be the wrong size for this layer - too large and it writes over a neighbour, too
        // small and it leaves part of a full-surface update undefined. Both are rejected here, where
        // rejecting still costs nothing but a failed copy.
        private static void ValidateSource(Texture2DDescription source)
        {
            if (source.Width != _width
                || source.Height != _height
                || source.SampleDescription.Count != 1
                // The source must be the layer format; see LayerFormat for why the channel family is
                // fixed while sRGB-ness is inherited.
                || source.Format != LayerFormat)
            {
                throw new InvalidOperationException(
                    "HUD copy source mismatch: source=" + Describe(source)
                    + ", logical=" + _width + "x" + _height + "/" + LayerFormat);
            }
        }

        // The remainder, checkable only once BeginDraw has named an update texture and an offset
        // into it. The source has already been proved to be exactly the logical surface, so this is
        // the atlas-side half: the layer's region must sit inside the texture handed back, and that
        // texture must be the composition surface's own format. Source and destination may differ in
        // sRGB-ness but never in channel family, which is what makes the raw bit copy legal.
        private static void ValidateUpdateTarget(
            Texture2DDescription source, Texture2DDescription destination, int offsetX, int offsetY)
        {
            if (offsetX < 0
                || offsetY < 0
                || offsetX + source.Width > destination.Width
                || offsetY + source.Height > destination.Height
                || destination.SampleDescription.Count != 1
                || destination.Format != CompositionFormat)
            {
                throw new InvalidOperationException(
                    "HUD copy mismatch: source=" + Describe(source)
                    + ", destination=" + Describe(destination)
                    + ", offset=" + offsetX + "," + offsetY);
            }
        }

        private static string Describe(Texture2DDescription description)
            => description.Width + "x" + description.Height + "/" + description.Format
               + "/samples=" + description.SampleDescription.Count;

        private static void AssertRenderThread()
        {
            int current = Environment.CurrentManagedThreadId;
            if (_renderThreadId == 0) Interlocked.CompareExchange(ref _renderThreadId, current, 0);
            if (_renderThreadId != current)
                throw new InvalidOperationException("GPU HUD transport called from a non-render thread");
        }

        private static void RecordCpuTime(long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Exchange(ref _lastCpuMicroseconds, elapsed * 1000000L / Stopwatch.Frequency);
        }

        private static void RaiseHighWater(ref long highWater, long value)
        {
            long current;
            while (value > (current = Interlocked.Read(ref highWater))
                   && Interlocked.CompareExchange(ref highWater, value, current) != current) { }
        }
    }
}
