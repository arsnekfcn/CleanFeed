using System;
using System.Runtime.InteropServices;

namespace CleanFeed.Render
{
    // Declaration order is composition order: each layer is parented above the previous one, so the
    // last member composites on top of everything else. RichHud is last deliberately - the nav menu
    // is modal and must sit above the toolbar, flight HUD, terminal and chat.
    internal enum HudCompositionLayer
    {
        Main,
        Toolbar,
        FlightHud,
        Interactive,
        Chat,
        RichHud
    }

    // Minimal native IDCompositionDevice/Target/Visual/Surface binding. Space Engineers ships
    // unsigned SharpDX assemblies, while the archived SharpDX.DirectComposition package requires
    // signed SharpDX identities, so using that wrapper would split the game's graphics type system.
    //
    // Every vtable entry this type calls is resolved once at construction. Marshal.GetDelegateForFunctionPointer
    // allocates a delegate and does an interop-stub lookup per call, and BeginDraw/EndDraw/Commit run
    // several times per frame on the render thread, so they must never resolve per call.
    internal sealed class NativeDirectComposition : IDisposable
    {
        private const int B8G8R8A8Unorm = 87;
        private const int AlphaModePremultiplied = 1;
        private const int LayerCount = 6;
        private static readonly Guid DeviceId = new Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
        private static readonly Guid VisualId = new Guid("4d93059d-097b-4651-9a60-f0f25116e2f3");

        // A composition layer owns one visual and one surface plus the vtable entries used to drive
        // them. The main layer's visual is the root visual, which additionally parents the others.
        private sealed class CompositionLayer
        {
            internal IntPtr Visual;
            internal IntPtr Surface;
            internal SetObjectDelegate SetVisualContent;
            internal BeginDrawDelegate BeginDraw;
            internal EndDrawDelegate EndDraw;
        }

        private readonly CompositionLayer[] _layers = new CompositionLayer[LayerCount];
        private IntPtr _device;
        private IntPtr _target;
        private CommitDelegate _commit;
        private SetObjectDelegate _setTargetRoot;
        private RemoveAllVisualsDelegate _removeAllVisuals;
        private bool _rootAttached;
        private bool _flightHudVisible = true;
        private bool _drawing;
        private HudCompositionLayer _activeLayer;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        // D2D_MATRIX_3X2_F, the row-major 3x2 affine matrix DirectComposition takes for a visual
        // transform.
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMatrix3x2
        {
            public float M11;
            public float M12;
            public float M21;
            public float M22;
            public float M31;
            public float M32;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CommitDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateTargetDelegate(IntPtr self, IntPtr hwnd, int topmost, out IntPtr target);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateVisualDelegate(IntPtr self, out IntPtr visual);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateSurfaceDelegate(
            IntPtr self, uint width, uint height, int format, int alphaMode, out IntPtr surface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int BeginDrawDelegate(
            IntPtr self, IntPtr updateRect, ref Guid iid, out IntPtr updateObject, out NativePoint updateOffset);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EndDrawDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetObjectDelegate(IntPtr self, IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AddVisualDelegate(
            IntPtr self, IntPtr visual, int insertAbove, IntPtr referenceVisual);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RemoveAllVisualsDelegate(IntPtr self);

        // SetTransform(const D2D_MATRIX_3X2_F&) - a C++ reference parameter is a pointer at the ABI
        // level, so the matrix is passed by ref rather than by value.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetTransformMatrixDelegate(IntPtr self, ref NativeMatrix3x2 matrix);

        [DllImport("dcomp.dll", ExactSpelling = true)]
        private static extern int DCompositionCreateDevice(
            IntPtr dxgiDevice, ref Guid iid, out IntPtr compositionDevice);

        // width/height are the render resolution the HUD layers are drawn at; clientWidth/clientHeight
        // are the window client rect the tree must fill. In Fullscreen Window the two differ whenever
        // the render resolution is not the desktop resolution; ApplyRootTransform reproduces the
        // stretch the game applies to its own backbuffer.
        //
        // Construction deliberately stops one step short of a visible tree: the root visual is not
        // handed to the target and nothing is committed. An IDCompositionSurface holds undefined
        // memory until it has been drawn, and premultiplied garbage composites as opaque and bright,
        // so rooting six undrawn surfaces and committing published a fullscreen whiteout for however
        // many DWM passes fell between that commit and the one that cleared them. The caller draws
        // every surface first and calls AttachRoot once; see AttachRoot.
        public NativeDirectComposition(
            IntPtr dxgiDevice, IntPtr hwnd, int width, int height, int clientWidth, int clientHeight)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (clientWidth <= 0) throw new ArgumentOutOfRangeException(nameof(clientWidth));
            if (clientHeight <= 0) throw new ArgumentOutOfRangeException(nameof(clientHeight));

            try
            {
                Guid iid = DeviceId;
                int result = DCompositionCreateDevice(dxgiDevice, ref iid, out _device);
                TrackAcquire(_device);
                Check(result);
                RequireInterface(_device, DeviceId, "IDCompositionDevice");
                _commit = GetDelegate<CommitDelegate>(_device, 3);

                result = GetDelegate<CreateTargetDelegate>(_device, 6)(_device, hwnd, 1, out _target);
                TrackAcquire(_target);
                Check(result);
                _setTargetRoot = GetDelegate<SetObjectDelegate>(_target, 3);

                // The root visual carries the main HUD surface and parents the five child layers.
                CompositionLayer main = CreateLayer((int)HudCompositionLayer.Main, width, height);
                RequireInterface(main.Visual, VisualId, "IDCompositionVisual");
                _removeAllVisuals = GetDelegate<RemoveAllVisualsDelegate>(main.Visual, 18);
                AddVisualDelegate addVisual = GetDelegate<AddVisualDelegate>(main.Visual, 16);

                IntPtr previous = IntPtr.Zero;
                for (int index = 1; index < LayerCount; index++)
                {
                    CompositionLayer layer = CreateLayer(index, width, height);
                    Check(addVisual(main.Visual, layer.Visual, 1, previous));
                    previous = layer.Visual;
                }

                // Applied here rather than in AttachRoot so the scale is already on the root visual
                // before the tree can be published: the attach commit therefore carries content and
                // transform together and no unscaled frame can reach the screen. Exercising the slot
                // on every construction keeps its wrong-slot failure mode deterministic; see
                // ApplyRootTransform.
                ApplyRootTransform(main.Visual, width, height, clientWidth, clientHeight);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        // Roots the finished tree and publishes it with the single commit that makes it visible. The
        // caller must have drawn every surface first: this commit is the first thing DWM ever sees of
        // this tree, and everything it carries - cleared surfaces and root transform alike - is
        // already defined. Nothing done before it can reach the screen, so a failure anywhere between
        // construction and here costs only a disposal, never a frame of undefined pixels.
        public void AttachRoot()
        {
            if (_device == IntPtr.Zero || _target == IntPtr.Zero || _setTargetRoot == null)
                throw new ObjectDisposedException(nameof(NativeDirectComposition));
            if (_rootAttached) return;

            CompositionLayer main = _layers[(int)HudCompositionLayer.Main];
            if (main == null || main.Visual == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(NativeDirectComposition));

            Check(_setTargetRoot(_target, main.Visual));
            Commit();
            _rootAttached = true;
        }

        // The returned COM object owns one reference and is valid only until EndDraw. DirectComposition
        // may return a larger atlas texture, so the caller must honor updateOffset when copying pixels.
        public IntPtr BeginDraw(
            HudCompositionLayer layer, ref Guid interfaceId, out int offsetX, out int offsetY)
        {
            CompositionLayer target = _layers[(int)layer];
            if (target == null || target.Surface == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(NativeDirectComposition));
            if (_drawing) throw new InvalidOperationException("DirectComposition surface draw is already active");

            IntPtr updateObject;
            NativePoint updateOffset;
            int result = target.BeginDraw(
                target.Surface, IntPtr.Zero, ref interfaceId, out updateObject, out updateOffset);
            if (result < 0)
            {
                if (updateObject != IntPtr.Zero) Marshal.Release(updateObject);
                Check(result);
            }
            _activeLayer = layer;
            _drawing = true;
            HudResourceTelemetry.BeginDraw();
            offsetX = updateOffset.X;
            offsetY = updateOffset.Y;
            return updateObject;
        }

        public void EndDraw()
        {
            if (!_drawing) return;
            bool succeeded = false;
            try
            {
                // BeginDraw calls are serialized; the remembered layer guarantees an update can never
                // be closed on a different child surface than the one it was opened on.
                CompositionLayer layer = _layers[(int)_activeLayer];
                if (layer != null && layer.Surface != IntPtr.Zero)
                {
                    Check(layer.EndDraw(layer.Surface));
                    succeeded = true;
                }
            }
            finally
            {
                _drawing = false;
                HudResourceTelemetry.EndDraw(succeeded);
            }
        }

        public void Commit()
        {
            if (_device == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeDirectComposition));
            Check(_commit(_device));
        }

        public bool SetFlightHudVisible(bool visible)
        {
            CompositionLayer layer = _layers[(int)HudCompositionLayer.FlightHud];
            if (layer == null || layer.Visual == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(NativeDirectComposition));
            if (_flightHudVisible == visible) return false;
            Check(layer.SetVisualContent(layer.Visual, visible ? layer.Surface : IntPtr.Zero));
            _flightHudVisible = visible;
            return true;
        }

        // Last-resort escape for a surface update that could not be given defined content. Once
        // BeginDraw has opened a full-surface update, the only way to keep undrawn memory off the
        // screen without drawing it is to take the layer out of the composition entirely. The detach
        // stands until the layer's content is set again, which only a rebuild does, and the caller
        // that needs this is already failing the frame open.
        public void DetachLayerContent(HudCompositionLayer layer)
        {
            CompositionLayer target = _layers[(int)layer];
            if (target?.SetVisualContent == null || target.Visual == IntPtr.Zero) return;
            Check(target.SetVisualContent(target.Visual, IntPtr.Zero));
            // Keep the flight-HUD visibility latch truthful, or the next modal transition would
            // decide the content is already where it wants it and skip the reattach.
            if (layer == HudCompositionLayer.FlightHud) _flightHudVisible = false;
        }

        public void Dispose()
        {
            if (_drawing)
            {
                try { EndDraw(); }
                catch { _drawing = false; }
            }

            // The teardown commit is issued whether or not the tree was ever attached: on a tree that
            // never reached AttachRoot the root was never set, so clearing it is a no-op and the
            // commit publishes nothing. Only an attached tree has anything on screen to take down.
            if (_device != IntPtr.Zero)
            {
                try
                {
                    CompositionLayer main = _layers[(int)HudCompositionLayer.Main];
                    if (main != null && main.Visual != IntPtr.Zero)
                        _removeAllVisuals?.Invoke(main.Visual);
                    foreach (CompositionLayer layer in _layers)
                    {
                        if (layer?.SetVisualContent == null || layer.Visual == IntPtr.Zero) continue;
                        layer.SetVisualContent(layer.Visual, IntPtr.Zero);
                    }
                    if (_target != IntPtr.Zero) _setTargetRoot?.Invoke(_target, IntPtr.Zero);
                    _commit?.Invoke(_device);
                }
                catch { }
            }

            // Children are released before the root visual they were parented to.
            for (int index = LayerCount - 1; index >= 0; index--)
            {
                CompositionLayer layer = _layers[index];
                if (layer == null) continue;
                Release(ref layer.Surface);
                Release(ref layer.Visual);
                layer.SetVisualContent = null;
                layer.BeginDraw = null;
                layer.EndDraw = null;
                _layers[index] = null;
            }
            Release(ref _target);
            Release(ref _device);
            _commit = null;
            _setTargetRoot = null;
            _removeAllVisuals = null;
            _rootAttached = false;
            _flightHudVisible = false;
        }

        // Stretches the composition tree from the render resolution onto the window client rect: the
        // same non-uniform full-window stretch the game applies to its backbuffer - independent X and
        // Y scale, no letterbox - so HUD pixels land where the stretched game pixels do. A visual's
        // transform covers its whole subtree, so the children need no transform of their own.
        //
        // Slot 8 is SetTransform(const D2D_MATRIX_3X2_F&). MSVC reverses the order of overloaded
        // methods within a vtable, so dcomp.h's IDCompositionVisual lays out as [3]=SetOffsetX(anim),
        // [4]=SetOffsetX(float), [5]=SetOffsetY(anim), [6]=SetOffsetY(float),
        // [7]=SetTransform(IDCompositionTransform*), [8]=SetTransform(matrix), [9]=SetTransformParent,
        // [10]=SetEffect, [11]=SetBitmapInterpolationMode, [12]=SetBorderMode, [13]=SetClip(clip),
        // [14]=SetClip(rect), [15]=SetContent, [16]=AddVisual, [17]=RemoveVisual, [18]=RemoveAllVisuals.
        // The slots this type already calls successfully - 15, 16 and 18 - corroborate that layout.
        //
        // SetBitmapInterpolationMode is deliberately not called: the default
        // DCOMPOSITION_BITMAP_INTERPOLATION_MODE gives DWM's default linear filtering, which is the
        // same filtering the game's own backbuffer stretch gets.
        //
        // The call is unconditional even though matching sizes need no transform - a visual is
        // identity by default, so the identity matrix is a no-op on content. Exercising the slot on
        // every construction makes a wrong-slot condition manifest deterministically at enable time
        // on every machine, instead of latently on whichever machine first runs at a non-native
        // render resolution.
        private static void ApplyRootTransform(
            IntPtr rootVisual, int width, int height, int clientWidth, int clientHeight)
        {
            bool scaled = clientWidth != width || clientHeight != height;
            var matrix = new NativeMatrix3x2
            {
                M11 = scaled ? clientWidth / (float)width : 1f,
                M12 = 0f,
                M21 = 0f,
                M22 = scaled ? clientHeight / (float)height : 1f,
                M31 = 0f,
                M32 = 0f
            };
            Check(GetDelegate<SetTransformMatrixDelegate>(rootVisual, 8)(rootVisual, ref matrix));
        }

        // The layer is registered before any fallible call so a partially constructed layer is still
        // released by Dispose.
        private CompositionLayer CreateLayer(int index, int width, int height)
        {
            var layer = new CompositionLayer();
            _layers[index] = layer;
            int result = GetDelegate<CreateVisualDelegate>(_device, 7)(_device, out layer.Visual);
            TrackAcquire(layer.Visual);
            Check(result);
            result = GetDelegate<CreateSurfaceDelegate>(_device, 8)(
                _device, (uint)width, (uint)height, B8G8R8A8Unorm, AlphaModePremultiplied, out layer.Surface);
            TrackAcquire(layer.Surface);
            Check(result);

            layer.SetVisualContent = GetDelegate<SetObjectDelegate>(layer.Visual, 15);
            layer.BeginDraw = GetDelegate<BeginDrawDelegate>(layer.Surface, 3);
            layer.EndDraw = GetDelegate<EndDrawDelegate>(layer.Surface, 4);
            Check(layer.SetVisualContent(layer.Visual, layer.Surface));
            return layer;
        }

        private static T GetDelegate<T>(IntPtr instance, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(instance);
            IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(method, typeof(T)) as T;
        }

        // Every vtable slot this type uses is hand-derived from dcomp.h, so the slot map is only valid
        // if the pointer really is the interface it was derived against. QueryInterface is the one
        // check COM guarantees at slot 0/1/2, so it is the only safe thing to call before the derived
        // slots: an object that answers for the IID is that interface, and one that does not is
        // rejected here rather than crashing later inside a mis-indexed call. The duplicate reference
        // QueryInterface returns is released immediately; the identity check is all that is wanted.
        private static void RequireInterface(IntPtr instance, Guid interfaceId, string name)
        {
            if (instance == IntPtr.Zero)
                throw new InvalidOperationException("DirectComposition returned a null " + name);

            IntPtr duplicate;
            // Marshal.QueryInterface's IID parameter is `ref Guid` on .NET Framework and `in Guid`
            // on modern .NET, where passing it with `ref` warns CS9191. The two arms are the same
            // call with the same semantics; only the argument modifier differs. Compiled per
            // variant rather than silenced, because Pulsar's from-source build has no csproj and so
            // no NoWarn: Legacy compiles with NETFRAMEWORK defined, Interim with NETCOREAPP, and
            // the SDK defines the same symbols per TFM for the local dual-target build.
#if NETFRAMEWORK
            int result = Marshal.QueryInterface(instance, ref interfaceId, out duplicate);
#else
            int result = Marshal.QueryInterface(instance, in interfaceId, out duplicate);
#endif
            if (result >= 0 && duplicate != IntPtr.Zero)
            {
                Marshal.Release(duplicate);
                return;
            }
            throw new InvalidOperationException(
                "DirectComposition object does not implement " + name
                + " (QueryInterface returned 0x" + result.ToString("X8") + ")");
        }

        private static void Check(int result)
        {
            if (result < 0) Marshal.ThrowExceptionForHR(result);
        }

        private static void Release(ref IntPtr value)
        {
            IntPtr current = value;
            value = IntPtr.Zero;
            if (current == IntPtr.Zero) return;
            try { Marshal.Release(current); }
            catch { }
            finally { HudResourceTelemetry.NativeReleased(); }
        }

        private static void TrackAcquire(IntPtr value)
        {
            if (value != IntPtr.Zero) HudResourceTelemetry.NativeAcquired();
        }
    }
}
