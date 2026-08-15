using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CleanFeed.Profiles;
using CleanFeed.Render;
using Sandbox.ModAPI;
using VRageMath;
using VRageRender;

// The intermediate quad record RichHud writes for every 2D (screen-space) triangle it draws. Every
// member is a VRage/VRageMath type, so CleanFeed can name the shape exactly and read the buffer as a
// strongly typed list rather than reflecting per element. Field meanings, in RichHud's own order:
//   Item1  blend type
//   Item2  X = billboard/pool index, Y = index into the shared matrix buffer
//   Item3  material
//   Item4  Item1 = premultiplied linear colour, Item2 = optional scissor mask in plane space
//   Item5  texture coordinates for the three vertices
//   Item6  plane-space (pixel) positions for the three vertices
using FlatTriangleBillboardData = VRage.MyTuple<
    VRageRender.MyBillboard.BlendTypeEnum,
    VRageMath.Vector2I,
    VRage.Utils.MyStringId,
    VRage.MyTuple<VRageMath.Vector4, System.Nullable<VRageMath.BoundingBox2>>,
    VRage.MyTuple<VRageMath.Vector2, VRageMath.Vector2, VRageMath.Vector2>,
    VRage.MyTuple<VRageMath.Vector2, VRageMath.Vector2, VRageMath.Vector2>>;

namespace CleanFeed.Compatibility
{
    // Projects Flip and Burn's RichHud nav menu onto the player-only layer instead of dropping it.
    //
    // WHY A BUFFER READ RATHER THAN THE BILLBOARDS
    //
    // RichHud never puts geometry on the billboards it hands MyTransparentGeometry. Client and
    // master both queue POOLED MyTriangleBillboards first and write the vertex data into a parallel
    // intermediate buffer; the master transfers buffer to billboards afterwards, in
    // BillBoardUtils.FinishDraw, once every client has queued. A prefix on AddBillboard(s) can
    // therefore only read whatever the pool slot last held. The intermediate buffer, by contrast, is
    // written synchronously by the emitting call, so reading the range it appended is reading this
    // frame's real geometry - the call's own arguments, one transformation later.
    //
    // WHY ONE BUFFER COVERS BOTH THE CHROME AND THE TEXT
    //
    // Chrome (window plates, rows, buttons, scrollbars) comes from QuadBoard.Draw in Flip and Burn's
    // own embedded copy of the framework: BillBoardUtils.AddQuad, in the mod's assembly.
    //
    // Text does not. A client-side Rendering.Client.TextBoard is only a proxy: its Draw invokes a
    // delegate the master handed over at registration, and the glyph quads are built and emitted by
    // the master's Rendering.Server.TextBoard -> master BillBoardUtils.AddTriangleData, in the
    // MASTER's assembly. That is precisely why the fail-closed guard removed the chrome from both
    // outputs while the text stayed on both: the guard keys off a scope pushed by patches on the
    // CLIENT's emitters, and the master-side text emission was never inside one.
    //
    // Both paths nevertheless append to the SAME list. The client's BillBoardUtils does not own its
    // buffers: BillBoardUtils.GetApiData10 hands the client the master's own flatTriangleList,
    // matrixBuf and matrixTable (Item2, the 3D triangleList, is passed as null - "never worked" -
    // which is also why no vID-10 client can use the 3D path at all). So resolving the master's
    // instance once yields the buffer both paths write, and a delta over any scoped call captures
    // whatever that call emitted, wherever the emitting code lives.
    //
    // ATTRIBUTION
    //
    // Menu content is attributed by scopes installed only on types inside the client mod's own
    // assembly - its BillBoardUtils.Add* statics and its Rendering.Client.TextBoard.Draw overloads -
    // and only when that client resolves to Flip and Burn. The master-side text emission is captured
    // because it happens synchronously inside one of those calls, not because anything master-side
    // carries the menu's tag.
    //
    // The master's own emitters are scoped too, but under a SEPARATE cursor tag and only when no
    // client scope is current, which is what pulls the shared cursor onto the same layer as the
    // projected menu instead of leaving it behind the composited surface. That capture is gated on
    // the menu having already projected a quad in this same frame, so with nothing projected the
    // master's UI is untouched and records exactly as it always did. The cursor tag also fails in
    // the opposite direction from the menu tag - open, not closed - because an arrow is not a
    // privacy vector.
    internal static class HudRichHudQuadCapture
    {
        private const string EmitterType = "RichHudFramework.UI.Rendering.BillBoardUtils";
        private const string HudMainType = "RichHudFramework.UI.Server.HudMain";
        private const float Epsilon = 0.01f;
        // Cauchy-Schwarz equality test for "these two axes are parallel", relative.
        private const double ParallelTolerance = 1e-6d;
        // Squared metres. An element plane that sits further than a millimetre off the base plane is
        // not the same plane, and cannot be mapped by a 2D scale-and-offset.
        private const double CoplanarToleranceSq = 1e-6d;

        private static readonly object Gate = new object();
        private static Type _emitters;
        private static FieldInfo _instanceField;
        private static FieldInfo _flatListField;
        private static FieldInfo _matrixBufField;
        private static object _boundInstance;
        private static List<FlatTriangleBillboardData> _flatList;
        private static List<MatrixD> _matrixBuf;
        private static PropertyInfo _basePlaneProperty;
        // HudMain assigns PixelToWorldRef exactly once, from its STATIC constructor
        // (Master\UI\HUD\HudMain.cs:147-150), so the array reference is stable for the process and
        // only its single element is rewritten each frame. Caching the reference makes the base
        // plane a plain array read on the draw path instead of a reflective property call.
        private static MatrixD[] _basePlaneRef;
        private static int _bindGeneration = -1;
        private static int _bindFailureLogged;
        private static int _projectFailureLogged;

        // Per-reason breakdown behind HudBillboardProjector's single richhud-dropped total. Every
        // one of these also increments that total; they exist so a live session can say WHICH way
        // the path is failing instead of only how often, which the first flicker session could not.
        private static long _dropPairMismatch;
        private static long _dropMissingMatrix;
        private static long _dropUnclippableSkew;
        private static long _dropProjectFail;
        private static long _dropBuffersUnresolved;
        private static long _dropEmptyRange;
        private static long _dropFault;
        private static long _dropNoBasePlane;
        private static long _dropNoViewport;
        private static long _dropNotScreenLocked;
        // Cursor-scope outcomes. Kept apart from the menu's drop counters because they mean
        // something different: a cursor quad that does not project is a cosmetic loss on the
        // player's screen, never a leak.
        private static long _cursorProjected;
        private static long _cursorLost;
        private static long _cursorPassed;
        private static long _masterResolverHits;
        private static long _masterResolverTagged;
        private static long _vanillaCursorRouted;

        // Per-frame gate for master-side (cursor) capture, expressed as a high-water mark in the
        // shared 2D buffer rather than a frame counter, which needs no extra patch and cannot drift
        // out of step with the framework's own frame.
        //
        // Set to flatTriangleList.Count the moment a MENU scope projects at least one quad. The list
        // only grows within a frame and is emptied once per frame in BillBoardUtils.FinishDraw
        // (master TreeManager.DrawUI:238), so "count >= mark" means "still inside the frame in which
        // the menu was projected". The first scope of the next frame sees a count below the mark and
        // disarms, so a stale mark cannot survive into a frame where the menu is not being projected.
        private static int _gateMark;
        // Not a drop: the scissor mask removed the quad outright, which is what the master's own
        // masking pass would have done too. Counted only so a mask that is wrong in the OTHER
        // direction - eating content that should be visible - is visible as a large number here.
        private static long _maskClipped;

        // Thread affine, like the route context it rides on: a capture window opens and closes
        // inside one scoped draw call, on whichever thread made that call. That is the game's
        // update/worker draw path - RichHud emits from session-component draw callbacks, not from
        // MyRender11 - so these claim per-thread affinity only, which is exactly what
        // [ThreadStatic] gives them; no statement here assumes the render thread.
        [ThreadStatic] private static bool _active;
        [ThreadStatic] private static bool _cursorScope;
        [ThreadStatic] private static int _suppressedEmissions;

        // Cursor sprites are computed at their own EndScope but NOT emitted there. They are held
        // here and flushed once, at RichHud's frame end, so they always enter the layer's bucket
        // after every menu sprite - see FlushCursor for why submission order could not be relied on.
        //
        // An array rather than a List because MyRenderProxy.DrawSprite takes its destination by ref;
        // an array element's field is a variable that can be passed directly, with no copy and no
        // per-sprite allocation.
        private const int MaxPendingCursorSprites = 64;
        [ThreadStatic] private static PendingSprite[] _pendingCursor;
        [ThreadStatic] private static int _pendingCursorCount;

        private struct PendingSprite
        {
            internal string Texture;
            internal RectangleF Destination;
            internal Rectangle Source;
            internal Color Color;
            internal float Rotation;
        }

        // True while an enclosing scoped call is collecting geometry for projection. The billboard
        // prefixes read it to tell "swallowed because it is being projected" from "swallowed because
        // nothing could be done with it", which are the same suppression but different accounting.
        internal static bool CaptureActive
        {
            get { return _active; }
        }

        // Resolves the shared intermediate buffers from the installed master. The master is the
        // single owner of both; every embedded client copy is handed these same list instances.
        internal static void Bind(Assembly master)
        {
            if (master == null) return;
            lock (Gate)
            {
                Type emitters;
                try { emitters = master.GetType(EmitterType, false, false); }
                catch { emitters = null; }
                if (emitters == null || ReferenceEquals(emitters, _emitters)) return;

                _emitters = emitters;
                const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags instances = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _instanceField = emitters.GetField("instance", statics);
                _flatListField = emitters.GetField("flatTriangleList", instances);
                _matrixBufField = emitters.GetField("matrixBuf", instances);

                Type hudMain;
                try { hudMain = master.GetType(HudMainType, false, false); }
                catch { hudMain = null; }
                _basePlaneProperty = hudMain == null
                    ? null
                    : hudMain.GetProperty("PixelToWorldRef", statics);
                _basePlaneRef = null;
                if (_basePlaneProperty == null)
                    NoteBindFailure("RichHud master HudMain.PixelToWorldRef not found; nav menu"
                                    + " content will be dropped rather than projected");

                _boundInstance = null;
                _flatList = null;
                _matrixBuf = null;
                _bindGeneration = -1;

                if (_instanceField == null || _flatListField == null || _matrixBufField == null)
                    NoteBindFailure("RichHud master billboard buffers not found ("
                                    + (_instanceField == null ? "instance " : string.Empty)
                                    + (_flatListField == null ? "flatTriangleList " : string.Empty)
                                    + (_matrixBufField == null ? "matrixBuf" : string.Empty)
                                    + "); nav menu content will be dropped rather than projected");
            }
        }

        internal static void Reset()
        {
            lock (Gate)
            {
                _emitters = null;
                _instanceField = null;
                _flatListField = null;
                _matrixBufField = null;
                _basePlaneProperty = null;
                _basePlaneRef = null;
                _boundInstance = null;
                _flatList = null;
                _matrixBuf = null;
                _bindGeneration = -1;
                _gateMark = 0;
            }
            _active = false;
            _cursorScope = false;
            _suppressedEmissions = 0;
            _pendingCursorCount = 0;
            _pendingCursor = null;
        }

        // Called by the billboard prefixes when they swallow an emission while capture is open, so a
        // scope that emitted but produced no capturable geometry can still be reported as a drop
        // instead of silently vanishing from the counters.
        internal static void NoteSuppressedEmission()
        {
            if (_active) _suppressedEmissions++;
        }

        // Opens a capture window over one scoped client call. Returns false - meaning "no capture,
        // the fail-closed backstop applies" - for every case that is not a projectable RichHud draw:
        // a different tag, interception off, a route that is not PlayerOnly, unresolvable buffers, or
        // an already-open window (whose range subsumes this one).
        //
        // The recorded start index stays meaningful until EndScope reads it because the master only
        // clears and rotates these buffers in BillBoardUtils.FinishDraw, after every client has
        // queued for the frame - so nothing inside a scoped call can truncate the list or renumber
        // it underneath the window, and the range [start, Count) at EndScope is exactly what this
        // call appended.
        internal static bool BeginScope(HudRouteTag tag, out int start)
        {
            start = -1;
            if (_active) return false;

            bool menu = HudAdapterCoordinator.IsRichHudEmitterTag(tag);
            bool cursor = !menu && HudAdapterCoordinator.IsRichHudCursorTag(tag);
            if (!menu && !cursor) return false;
            if (!HudRedirector.SelectiveInterceptionActive) return false;
            if (HudRedirector.ResolveRoute(tag) != HudEffectiveRoute.PlayerOnly) return false;

            List<FlatTriangleBillboardData> list = Buffers();
            if (list == null) return false;

            // Frame-boundary disarm. Runs for every scope, including ones that go on to decline, so
            // the very first RichHud emission of a frame - which always sees a nearly empty buffer -
            // clears a mark left by the previous frame before anything can read it.
            int mark = _gateMark;
            if (mark > 0 && list.Count < mark)
            {
                _gateMark = 0;
                // Frame boundary. Anything still buffered belongs to the previous frame and can only
                // be here because that frame's flush did not run - an exception between EndScope and
                // FinishDraw. Discard rather than emit: a stale cursor drawn into this frame's
                // bucket would be a visibly lagging arrow.
                DiscardPendingCursor();
            }

            // Master-side content is only pulled onto the projected layer while there is projected
            // menu content for it to sit on top of. Two independent signals, because neither alone
            // covers the frame:
            //
            //   _gateMark   the menu has already projected EARLIER IN THIS FRAME. Exact, but only
            //               true for master emitters that draw after the menu.
            //   layer       the richhud composition layer is currently holding published content.
            //               One frame stale - the render thread writes MayHaveContent, this reads it
            //               - and that is fine in both directions: as the menu opens, at most one
            //               frame of cursor records with it; as it closes, at most one frame of
            //               cursor is projected onto a layer that is about to clear.
            //
            // The mark alone was the previous design and it disarmed permanently whenever the
            // master's subtree sort put the cursor BEFORE the menu, which is exactly the ordering
            // unknown documented on FlushCursor. The layer signal does not depend on intra-frame
            // order at all, so the two together cover the cursor whichever side of the menu it
            // draws on. With both shut - flip-burn unfiltered, or the menu closed - the cursor is
            // left entirely alone and records with everything else.
            if (cursor && _gateMark == 0 && !HudNamedSpriteRoutes.RichHudLayerHasContent)
                return false;

            // Preconditions are checked HERE, before anything is suppressed, rather than being
            // discovered halfway through Project. That is what makes the cursor's fail-open real:
            // declining now means the billboard prefixes never swallow the emission, so a cursor
            // that cannot be projected still reaches both outputs intact. For menu content the same
            // decline means the prefixes fail closed instead, which is the correct direction there.
            if (!PreconditionsMet()) return false;

            start = list.Count;
            _active = true;
            _cursorScope = cursor;
            _suppressedEmissions = 0;
            return true;
        }

        // Everything Project needs that is not per-quad. Cheap: two property reads and a cached
        // array reference.
        private static bool PreconditionsMet()
        {
            if (_matrixBuf == null) return false;
            MatrixD basePlane;
            if (!TryGetBasePlane(out basePlane)) return false;
            if (basePlane.Right.LengthSquared() <= 0d || basePlane.Up.LengthSquared() <= 0d)
                return false;
            var camera = MyAPIGateway.Session?.Camera;
            if (camera == null) return false;
            Vector2 viewport = camera.ViewportSize;
            return viewport.X > 0f && viewport.Y > 0f;
        }

        // Projects everything the scoped call appended. Runs from the scope finalizer, so it also
        // runs when the call threw - in which case the range is whatever it managed to append, which
        // is exactly what was queued and then suppressed.
        internal static void EndScope(int start, HudRouteTag tag)
        {
            try
            {
                Project(start, tag);
            }
            catch (Exception ex)
            {
                // The emission was already suppressed by the billboard prefixes and cannot be
                // re-issued - the pooled billboards still hold no geometry. For menu content that is
                // the right direction anyway: it is the GPS-name dox vector, and a fault must never
                // become a leak into the recording. For the cursor it is only a lost arrow, counted
                // separately so it can never be mistaken for a privacy event.
                if (_cursorScope) Interlocked.Increment(ref _cursorLost);
                else Drop(ref _dropFault);
                if (Interlocked.Exchange(ref _projectFailureLogged, 1) == 0)
                    Plugin.Log("RichHud quad projection faulted: "
                               + ex.GetType().Name + " - " + ex.Message);
            }
            finally
            {
                _active = false;
                _cursorScope = false;
                _suppressedEmissions = 0;
            }
        }

        private static void Project(int start, HudRouteTag tag)
        {
            bool cursor = _cursorScope;
            List<FlatTriangleBillboardData> list = _flatList;
            List<MatrixD> matrices = _matrixBuf;
            if (list == null || matrices == null || start < 0 || start > list.Count)
            {
                Lose(cursor, ref _dropBuffersUnresolved);
                return;
            }

            int end = list.Count;
            if (end == start)
            {
                // The call emitted billboards but wrote nothing into the 2D buffer: 3D geometry, or
                // a shape this capture does not understand.
                if (_suppressedEmissions > 0) Lose(cursor, ref _dropEmptyRange);
                return;
            }

            // Projected onto the dedicated RichHud layer, not the tag's default player layer: Flip
            // and Burn hides the vanilla HUD while its menu is open (ShowHud(false)), which engages
            // the hud-hidden clear on the main selective layer and would drain these sprites every
            // frame. That route is unsealed with plain retention and its own idle clear, which is
            // what this producer needs: the menu is drawn from a session component, a different
            // phase than the screen-manager draw the sealed routes' completion markers come from,
            // so a sealed route would publish and clear in alternating frames and flicker.
            string target = HudNamedSpriteRoutes.RichHudTargetName;

            // NO CAMERA IS USED BELOW, and that is the point.
            //
            // These quads live on a plane the master anchors to the camera at the near plane
            // (Master\UI\HUD\HudMain.cs:276-285: a local matrix with Right/Up = FovScale/ScreenHeight
            // and Translation = -NearPlaneDistance on Z, times Camera.WorldMatrix). Pushing those
            // world points back through the current view*projection asks the near plane to invert an
            // operation it is numerically degenerate at, using a matrix the master may have built
            // from the previous frame's camera - so the clip-space depth test rejects quads
            // intermittently as the camera moves, which is what a project-fail count in the hundreds
            // of thousands looks like.
            //
            // The plane never needed the camera. RichHud defines its units to BE screen pixels, and
            // states the exact conversion in HudCursor.UpdateCursorPos
            // (Master\UI\HUD\HudCursor.cs:281-297), which turns a native cursor position into plane
            // coordinates with:
            //     screenPos.Y *= -1;  screenPos += (-ScreenWidth/2, ScreenHeight/2)
            // Inverted, that is the whole mapping, with a scale of exactly one:
            //     screenX = W/2 + planeX
            //     screenY = H/2 - planeY
            // W and H are Camera.ViewportSize, the same source the master reads in
            // UpdateScreenScaling (HudMain.cs:324-325) and the same one the world path used, so a
            // mid-session resolution change is picked up here on the next frame.
            var camera = MyAPIGateway.Session?.Camera;
            if (camera == null)
            {
                Lose(cursor, ref _dropNoViewport);
                return;
            }
            Vector2 viewport = camera.ViewportSize;
            if (viewport.X <= 0f || viewport.Y <= 0f)
            {
                Lose(cursor, ref _dropNoViewport);
                return;
            }
            float halfWidth = viewport.X * 0.5f, halfHeight = viewport.Y * 0.5f;

            MatrixD basePlane;
            if (!TryGetBasePlane(out basePlane))
            {
                Lose(cursor, ref _dropNoBasePlane);
                return;
            }
            Vector3D baseOrigin = basePlane.Translation;
            Vector3D baseRight = basePlane.Right;
            Vector3D baseUp = basePlane.Up;
            double rr = baseRight.LengthSquared(), uu = baseUp.LengthSquared();
            if (rr <= 0d || uu <= 0d)
            {
                Lose(cursor, ref _dropNoBasePlane);
                return;
            }

            // Element matrices are not always the base plane. The nav menu hangs off HighDpiRoot, a
            // ScaledSpaceNode, which copies its parent's matrix and multiplies Right and Up by
            // PlaneScale - ResScale, i.e. ScreenHeight/1080 above 1080p
            // (Shared\UI\HUD\HudSpaceNodes\ScaledSpaceNode.cs:44-46, client HudMain wiring
            // UpdateScaleFunc = () => ResScale). Translation is left alone.
            //
            // So an element matrix is the base plane with its axes rescaled, and the quad's
            // coordinates in BASE plane space are recovered by projecting the element's axes onto
            // the base's. That is done once per matrixID rather than per corner: the decomposition
            // yields a per-axis scale and offset, after which each corner costs one multiply-add.
            // Working from the axes rather than from transformed world points also keeps every
            // quantity small and camera-relative, so no step subtracts two large world coordinates.
            int cachedId = -1;
            bool cachedValid = false;
            float scaleX = 1f, scaleY = 1f, offsetX = 0f, offsetY = 0f;

            int i = start;
            while (i < end)
            {
                if (i + 1 >= end)
                {
                    // Trailing odd triangle. Drop just it - everything already projected stands.
                    Lose(cursor, ref _dropPairMismatch);
                    return;
                }

                FlatTriangleBillboardData left = list[i];
                FlatTriangleBillboardData right = list[i + 1];
                if (!Pairs(ref left, ref right))
                {
                    // A lone triangle - PolyBoard's radial geometry, or a shape whose two halves do
                    // not describe one quad. Drop only this triangle and advance by one, which
                    // re-aligns the cursor onto the next real pair rather than letting one anomaly
                    // shift the parity of every quad behind it.
                    Lose(cursor, ref _dropPairMismatch);
                    i++;
                    continue;
                }
                i += 2;

                int matrixId = left.Item2.Y;
                if (matrixId < 0 || matrixId >= matrices.Count)
                {
                    Lose(cursor, ref _dropMissingMatrix);
                    continue;
                }
                if (matrixId != cachedId)
                {
                    cachedId = matrixId;
                    MatrixD elementPlane = matrices[matrixId];
                    cachedValid = TryDecomposePlane(
                        ref elementPlane, ref baseOrigin, ref baseRight, ref baseUp, rr, uu,
                        out scaleX, out scaleY, out offsetX, out offsetY);
                }
                if (!cachedValid)
                {
                    // Not a screen-locked plane - a RichHud element attached to a block or otherwise
                    // living in world space. It has no 2D mapping, and the world path it would need
                    // is the degenerate one this replaced, so it is dropped rather than guessed at.
                    Lose(cursor, ref _dropNotScreenLocked);
                    continue;
                }

                // Plane-space corners, wound so that A->B is the material's +U edge and A->D its +V
                // edge. RichHud emits the pair as (P0,P1,P2) and (P0,P2,P3) with P0 at the bounds
                // maximum, so the quad's top-left corner is the second triangle's third vertex.
                Vector2 a = right.Item6.Item3, uvA = right.Item5.Item3;
                Vector2 b = left.Item6.Item1, uvB = left.Item5.Item1;
                Vector2 c = left.Item6.Item2, uvC = left.Item5.Item2;
                Vector2 d = left.Item6.Item3, uvD = left.Item5.Item3;

                BoundingBox2? mask = left.Item4.Item2;
                if (mask.HasValue)
                {
                    bool dropped;
                    if (!Clip(mask.Value, ref a, ref b, ref c, ref d,
                            ref uvA, ref uvB, ref uvC, ref uvD, out dropped))
                    {
                        if (dropped) Lose(cursor, ref _dropUnclippableSkew);
                        else Interlocked.Increment(ref _maskClipped);
                        continue;
                    }
                }

                // Element plane pixels straight to screen pixels. The mask clip above ran in the same
                // element plane space the mask itself is expressed in, so it composes with this
                // without any change of frame.
                Vector2 sa = ToScreen(ref a, scaleX, scaleY, offsetX, offsetY, halfWidth, halfHeight);
                Vector2 sb = ToScreen(ref b, scaleX, scaleY, offsetX, offsetY, halfWidth, halfHeight);
                Vector2 sc = ToScreen(ref c, scaleX, scaleY, offsetX, offsetY, halfWidth, halfHeight);
                Vector2 sd = ToScreen(ref d, scaleX, scaleY, offsetX, offsetY, halfWidth, halfHeight);

                // Rotation and extents come from the quad's own edges in 2D. A->B is the +U edge and
                // A->D the +V edge, so an axis-aligned quad yields rotation zero; the screen Y flip
                // is already baked into ToScreen and therefore into the edge directions.
                Vector2 widthEdge = sb - sa;
                Vector2 heightEdge = sd - sa;
                float width = widthEdge.Length();
                float height = heightEdge.Length();
                if (float.IsNaN(width) || float.IsNaN(height)
                    || width < 0.01f || height < 0.01f)
                {
                    Lose(cursor, ref _dropProjectFail);
                    continue;
                }

                Vector2 uvMin = new Vector2(
                    Math.Min(Math.Min(uvA.X, uvB.X), Math.Min(uvC.X, uvD.X)),
                    Math.Min(Math.Min(uvA.Y, uvB.Y), Math.Min(uvC.Y, uvD.Y)));
                Vector2 uvMax = new Vector2(
                    Math.Max(Math.Max(uvA.X, uvB.X), Math.Max(uvC.X, uvD.X)),
                    Math.Max(Math.Max(uvA.Y, uvB.Y), Math.Max(uvC.Y, uvD.Y)));

                string texture;
                Rectangle source;
                if (!HudBillboardProjector.TryResolveSpriteSource(
                        left.Item3, uvMin, uvMax, out texture, out source))
                {
                    Lose(cursor, ref _dropProjectFail);
                    continue;
                }

                Vector2 center = (sa + sb + sc + sd) * 0.25f;
                var destination = new RectangleF(
                    center.X - width * 0.5f, center.Y - height * 0.5f, width, height);
                float rotation = (float)Math.Atan2(widthEdge.Y, widthEdge.X);

                if (cursor)
                {
                    // Buffered, not drawn. See FlushCursor.
                    if (!EnqueueCursorSprite(texture, ref destination, ref source,
                            ToSpriteColor(left.Item4.Item1), rotation))
                        Interlocked.Increment(ref _cursorLost);
                }
                else
                {
                    MyRenderProxy.DrawSprite(texture, ref destination, source,
                        ToSpriteColor(left.Item4.Item1), rotation, true, false, target, null, 0f);
                    HudBillboardProjector.NoteRichHudProjected();
                    // Arm the master-side gate for the rest of THIS frame. Taken after the sprite so
                    // it can only be set by a quad that actually reached the layer, and taken from
                    // the live buffer count so the frame-boundary disarm in BeginScope has a mark
                    // that this frame's own growth can never fall below.
                    _gateMark = list.Count;
                }
            }
        }

        private static bool EnqueueCursorSprite(
            string texture, ref RectangleF destination, ref Rectangle source,
            Color color, float rotation)
        {
            PendingSprite[] buffer = _pendingCursor;
            if (buffer == null)
            {
                buffer = new PendingSprite[MaxPendingCursorSprites];
                _pendingCursor = buffer;
            }
            // A cursor is three quads. A buffer sixty-four deep that still overflows is not a cursor,
            // so the overflow is discarded and counted rather than grown into.
            if (_pendingCursorCount >= buffer.Length) return false;

            int index = _pendingCursorCount++;
            buffer[index].Texture = texture;
            buffer[index].Destination = destination;
            buffer[index].Source = source;
            buffer[index].Color = color;
            buffer[index].Rotation = rotation;
            return true;
        }

        // Emits the frame's buffered cursor sprites, in the order they were captured.
        //
        // WHY THIS IS DEFERRED RATHER THAN DRAWN IN PLACE
        //
        // Within one named sprite target, later messages paint over earlier ones, so the cursor has
        // to be enqueued after the menu. Reading the master's draw order to establish that turned
        // out to be the wrong approach twice over: subtreeOrder is an ascending sort of
        // (distanceRank << 48) | (InnerZLayer << 32) | subtreeIndex (TreeManager.UpdateInnerLayerSort
        // :304-321), the cursor and the menu tie on distance because both sit on the pixel plane at
        // the origin the rank is measured from, and the tiebreak is InnerZLayer - a SUBTREE-level
        // byte taken from the root node's recomputed FullZOffset (FlatTreeData.cs:116), not the
        // node-level ZOffset the cursor sets in its constructor, and not the order subtrees were
        // appended in. DrawNodes can also drop entries from the index list mid-pass when a node
        // throws (HudNodeIterator.DrawNodes:436). The order is therefore an implementation detail of
        // someone else's sort, and any argument built on it is one refactor from being wrong again.
        //
        // So the capture stops depending on it. Cursor sprites are fully resolved to final
        // screen-space values at their own EndScope and held; this runs from a postfix on the
        // master's BillBoardUtils.FinishDraw, which TreeManager.DrawUI calls at :238 after DrawNodes
        // has completed at :227 - i.e. after every client and master scope in the frame. Whatever
        // order the master drew things in, the cursor's sprites enter the bucket last.
        //
        // Fail-open, like everything else on the cursor path: a flush that throws costs the arrow,
        // never the frame.
        internal static void FlushCursor()
        {
            int count = _pendingCursorCount;
            if (count <= 0) return;
            PendingSprite[] buffer = _pendingCursor;
            _pendingCursorCount = 0;
            if (buffer == null) return;

            string target = HudNamedSpriteRoutes.RichHudTargetName;
            int emitted = 0;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    MyRenderProxy.DrawSprite(
                        buffer[i].Texture, ref buffer[i].Destination, buffer[i].Source,
                        buffer[i].Color, buffer[i].Rotation, true, false, target, null, 0f);
                    emitted++;
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _projectFailureLogged, 1) == 0)
                    Plugin.Log("RichHud cursor flush failed open: "
                               + ex.GetType().Name + " - " + ex.Message);
            }
            finally
            {
                Interlocked.Add(ref _cursorProjected, emitted);
                if (emitted < count) Interlocked.Add(ref _cursorLost, count - emitted);
                Array.Clear(buffer, 0, count);
            }
        }

        private static void DiscardPendingCursor()
        {
            int count = _pendingCursorCount;
            if (count <= 0) return;
            _pendingCursorCount = 0;
            Interlocked.Add(ref _cursorLost, count);
            PendingSprite[] buffer = _pendingCursor;
            if (buffer != null) Array.Clear(buffer, 0, count);
        }

        // A quad that could not be projected. For the menu that is a drop in the privacy sense and
        // feeds the fail-closed total; for the cursor it is only a missing arrow, so it is counted
        // on its own and never logged as a fail-closed event.
        private static void Lose(bool cursor, ref long reason)
        {
            Interlocked.Increment(ref reason);
            if (cursor) Interlocked.Increment(ref _cursorLost);
            else HudBillboardProjector.NoteRichHudDropped();
        }

        // The billboard prefixes call this when they let cursor-tagged content through untouched,
        // which is the whole fail-open path: nothing was captured, so nothing was suppressed.
        internal static void NoteCursorPassed() => Interlocked.Increment(ref _cursorPassed);

        // Called twice per master-emitter draw: once at resolver entry, once more if it produced the
        // cursor tag. Hits at zero means the scope patches are not reaching the resolver at all;
        // hits without tagged means the resolver ran and declined.
        // Ticked each time the vanilla mouse cursor is redirected onto the RichHud layer, i.e. once
        // per frame while a projected menu is showing. Zero with a menu on screen means the
        // MyDX9Gui.DrawMouseCursor scope did not install or the layer never reported content.
        internal static void NoteVanillaCursorRouted()
            => Interlocked.Increment(ref _vanillaCursorRouted);

        internal static void NoteMasterResolver(bool tagged)
        {
            if (tagged) Interlocked.Increment(ref _masterResolverTagged);
            else Interlocked.Increment(ref _masterResolverHits);
        }

        // RichHud bakes billboard colour for the transparent-geometry pipeline: alpha-premultiplied
        // in LINEAR space. The sprite pipeline expects straight-alpha sRGB and premultiplies in the
        // shader, so passing the value through applied alpha twice and darkened mid-tones (reads as
        // extra transparency). Un-premultiply first, then encode to sRGB - the encode is nonlinear,
        // so the order matters.
        private static Color ToSpriteColor(Vector4 premultipliedLinear)
        {
            float a = premultipliedLinear.W;
            Vector3 rgb = new Vector3(
                premultipliedLinear.X, premultipliedLinear.Y, premultipliedLinear.Z);
            if (a > 0.0001f) rgb /= a;
            var straight = new Vector4(rgb.X, rgb.Y, rgb.Z, a);
            return new Color(straight.ToSRGB());
        }

        private static Vector2 ToScreen(
            ref Vector2 plane, float scaleX, float scaleY, float offsetX, float offsetY,
            float halfWidth, float halfHeight)
            => new Vector2(
                halfWidth + (plane.X * scaleX + offsetX),
                halfHeight - (plane.Y * scaleY + offsetY));

        // Expresses an element plane in base plane coordinates as a per-axis scale and offset.
        //
        // Returns false unless the element plane really is the base plane rescaled and shifted
        // within itself - axes parallel to the base's and origin coplanar with it. The parallelism
        // test is Cauchy-Schwarz equality (|v.b|^2 == |v|^2|b|^2 exactly when v is parallel to b),
        // which needs no square roots and no normalisation; the coplanarity test is the residual of
        // the origin offset after removing its in-plane components.
        private static bool TryDecomposePlane(
            ref MatrixD element, ref Vector3D baseOrigin, ref Vector3D baseRight, ref Vector3D baseUp,
            double rr, double uu,
            out float scaleX, out float scaleY, out float offsetX, out float offsetY)
        {
            scaleX = 1f; scaleY = 1f; offsetX = 0f; offsetY = 0f;

            Vector3D right = element.Right, up = element.Up;
            double sx = Vector3D.Dot(right, baseRight) / rr;
            double sy = Vector3D.Dot(up, baseUp) / uu;
            if (!(sx > 0d) || !(sy > 0d)) return false;

            double rightLenSq = right.LengthSquared(), upLenSq = up.LengthSquared();
            if (Math.Abs(rightLenSq - sx * sx * rr) > ParallelTolerance * rightLenSq) return false;
            if (Math.Abs(upLenSq - sy * sy * uu) > ParallelTolerance * upLenSq) return false;

            // Zero for every ScaledSpaceNode, which leaves Translation untouched; carried anyway so
            // a space that only shifts within the plane still maps.
            Vector3D delta = element.Translation - baseOrigin;
            double ox = Vector3D.Dot(delta, baseRight) / rr;
            double oy = Vector3D.Dot(delta, baseUp) / uu;
            Vector3D residual = delta - ox * baseRight - oy * baseUp;
            if (residual.LengthSquared() > CoplanarToleranceSq) return false;

            scaleX = (float)sx; scaleY = (float)sy;
            offsetX = (float)ox; offsetY = (float)oy;
            return true;
        }

        private static bool TryGetBasePlane(out MatrixD basePlane)
        {
            MatrixD[] reference = _basePlaneRef;
            if (reference == null)
            {
                PropertyInfo property = _basePlaneProperty;
                if (property == null) { basePlane = default(MatrixD); return false; }
                try { reference = property.GetValue(null, null) as MatrixD[]; }
                catch { reference = null; }
                if (reference == null || reference.Length < 1)
                {
                    basePlane = default(MatrixD);
                    return false;
                }
                _basePlaneRef = reference;
            }
            basePlane = reference[0];
            return true;
        }

        // Every drop reason feeds both its own counter and the single richhud-dropped total, so the
        // breakdown always sums to the total and neither can drift from the other.
        private static void Drop(ref long reason)
        {
            Interlocked.Increment(ref reason);
            HudBillboardProjector.NoteRichHudDropped();
        }

        // Appended to the retained-route status line, which is where the operator was already
        // reading live counters from.
        internal static string StatusLine()
            => "richhud-capture=pair-mismatch:" + Interlocked.Read(ref _dropPairMismatch)
               + ", no-matrix:" + Interlocked.Read(ref _dropMissingMatrix)
               + ", unclippable-skew:" + Interlocked.Read(ref _dropUnclippableSkew)
               + ", project-fail:" + Interlocked.Read(ref _dropProjectFail)
               + ", buffers-unresolved:" + Interlocked.Read(ref _dropBuffersUnresolved)
               + ", no-base-plane:" + Interlocked.Read(ref _dropNoBasePlane)
               + ", no-viewport:" + Interlocked.Read(ref _dropNoViewport)
               + ", not-screen-locked:" + Interlocked.Read(ref _dropNotScreenLocked)
               + ", empty-range:" + Interlocked.Read(ref _dropEmptyRange)
               + ", fault:" + Interlocked.Read(ref _dropFault)
               + ", mask-clipped:" + Interlocked.Read(ref _maskClipped)
               + ", cursor-projected:" + Interlocked.Read(ref _cursorProjected)
               + ", cursor-lost:" + Interlocked.Read(ref _cursorLost)
               + ", cursor-passed:" + Interlocked.Read(ref _cursorPassed)
               + ", resolver-hits:" + Interlocked.Read(ref _masterResolverHits)
               + ", resolver-tagged:" + Interlocked.Read(ref _masterResolverTagged)
               + ", vanilla-cursor-routed:" + Interlocked.Read(ref _vanillaCursorRouted)
               + ", gate:" + (Volatile.Read(ref _gateMark) > 0 ? "mark" : "shut")
               + "/" + (HudNamedSpriteRoutes.RichHudLayerHasContent ? "layer" : "empty");

        // Two consecutive records describe one quad when they share a material, a matrix, a blend
        // mode, a colour, a mask, and the diagonal RichHud splits the quad along - the first
        // triangle's P0/P2 are the second's first two vertices. Those are the structural invariants
        // and they are the whole test.
        //
        // What used to be here as well, and is deliberately gone: a check that Item2.X equalled the
        // record's own index in the list. Item2.X is written as bbDataBack.Count at insertion, so it
        // IS the list index, and it doubles as the pool index because the master keeps
        // flatTriangleList and flatTriPoolBack[0] parallel (UpdateFlatBillboards indexes the pool
        // with it, and RotatePool swaps which pool list is active without renumbering anything). The
        // invariant was therefore true - and worthless. It could not distinguish a genuinely foreign
        // buffer from any transient the framework might legitimately produce, while a single failure
        // shifted the pairing parity of the whole remaining window, so every subsequent structural
        // test failed too and the entire frame's menu was dropped. That cascade is exactly the
        // all-or-nothing-per-frame flicker the live counters showed; a cross-check that can only
        // ever turn one anomaly into total loss has no place on this path.
        private static bool Pairs(
            ref FlatTriangleBillboardData left, ref FlatTriangleBillboardData right)
            => left.Item2.Y == right.Item2.Y
               && left.Item1 == right.Item1
               && left.Item3 == right.Item3
               && Near(left.Item4.Item1, right.Item4.Item1)
               && SameMask(left.Item4.Item2, right.Item4.Item2)
               && Near(left.Item6.Item1, right.Item6.Item1)
               && Near(left.Item6.Item3, right.Item6.Item2);

        // Applies RichHud's scissor mask in plane space, before projection, so a scrolled list
        // clips against its viewport exactly as the master's own masking pass would.
        //
        // Returns true when the quad should be drawn, with the corners and texture coordinates
        // rewritten to the clipped rectangle. Returns false when there is nothing to draw: either
        // the mask removed the quad entirely - not a fault, and not counted - or the quad is skewed
        // and only partly inside the mask, which this cannot clip and therefore drops.
        private static bool Clip(
            BoundingBox2 mask,
            ref Vector2 a, ref Vector2 b, ref Vector2 c, ref Vector2 d,
            ref Vector2 uvA, ref Vector2 uvB, ref Vector2 uvC, ref Vector2 uvD,
            out bool dropped)
        {
            dropped = false;
            float minX = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
            float maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
            float minY = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
            float maxY = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));

            // Wholly inside: nothing to clip, and the corners are left exactly as emitted, so a
            // skewed quad still projects through its four points.
            if (minX >= mask.Min.X && maxX <= mask.Max.X
                && minY >= mask.Min.Y && maxY <= mask.Max.Y)
                return true;

            float clipMinX = Math.Max(minX, mask.Min.X);
            float clipMaxX = Math.Min(maxX, mask.Max.X);
            float clipMinY = Math.Max(minY, mask.Min.Y);
            float clipMaxY = Math.Min(maxY, mask.Max.Y);
            if (clipMaxX <= clipMinX || clipMaxY <= clipMinY) return false;

            bool axisAligned = Math.Abs(a.Y - b.Y) <= Epsilon && Math.Abs(c.Y - d.Y) <= Epsilon
                               && Math.Abs(a.X - d.X) <= Epsilon && Math.Abs(b.X - c.X) <= Epsilon;
            float sizeX = maxX - minX, sizeY = maxY - minY;
            if (!axisAligned || sizeX <= 0f || sizeY <= 0f)
            {
                dropped = true;
                return false;
            }

            float uMin = Math.Min(Math.Min(uvA.X, uvB.X), Math.Min(uvC.X, uvD.X));
            float uMax = Math.Max(Math.Max(uvA.X, uvB.X), Math.Max(uvC.X, uvD.X));
            float vMin = Math.Min(Math.Min(uvA.Y, uvB.Y), Math.Min(uvC.Y, uvD.Y));
            float vMax = Math.Max(Math.Max(uvA.Y, uvB.Y), Math.Max(uvC.Y, uvD.Y));

            // Plane Y runs up, texture V runs down, so the V remap is taken from the top edge.
            float u0 = Lerp(uMin, uMax, (clipMinX - minX) / sizeX);
            float u1 = Lerp(uMin, uMax, (clipMaxX - minX) / sizeX);
            float v0 = Lerp(vMin, vMax, (maxY - clipMaxY) / sizeY);
            float v1 = Lerp(vMin, vMax, (maxY - clipMinY) / sizeY);

            a = new Vector2(clipMinX, clipMaxY); uvA = new Vector2(u0, v0);
            b = new Vector2(clipMaxX, clipMaxY); uvB = new Vector2(u1, v0);
            c = new Vector2(clipMaxX, clipMinY); uvC = new Vector2(u1, v1);
            d = new Vector2(clipMinX, clipMinY); uvD = new Vector2(u0, v1);
            return true;
        }

        private static float Lerp(float from, float to, float amount)
            => from + (to - from) * amount;

        private static bool SameMask(BoundingBox2? left, BoundingBox2? right)
        {
            if (left.HasValue != right.HasValue) return false;
            if (!left.HasValue) return true;
            return Near(left.Value.Min, right.Value.Min) && Near(left.Value.Max, right.Value.Max);
        }

        private static bool Near(Vector2 left, Vector2 right)
            => Math.Abs(left.X - right.X) <= Epsilon && Math.Abs(left.Y - right.Y) <= Epsilon;

        private static bool Near(Vector4 left, Vector4 right)
            => Math.Abs(left.X - right.X) <= 1e-5f && Math.Abs(left.Y - right.Y) <= 1e-5f
               && Math.Abs(left.Z - right.Z) <= 1e-5f && Math.Abs(left.W - right.W) <= 1e-5f;

        // The master's instance is created at its session start and dropped on unload, so the
        // resolved lists are re-read whenever the redirector's activation generation moves rather
        // than being trusted for the life of the process. Between generations this is a field read
        // and an int compare, off the per-quad path entirely.
        private static List<FlatTriangleBillboardData> Buffers()
        {
            int generation = HudRedirector.ActivationGeneration;
            List<FlatTriangleBillboardData> list = _flatList;
            if (list != null && _matrixBuf != null && _bindGeneration == generation) return list;

            lock (Gate)
            {
                if (_instanceField == null || _flatListField == null || _matrixBufField == null)
                    return null;
                object instance;
                try { instance = _instanceField.GetValue(null); }
                catch { instance = null; }
                if (instance == null)
                {
                    _boundInstance = null;
                    _flatList = null;
                    _matrixBuf = null;
                    return null;
                }

                if (!ReferenceEquals(instance, _boundInstance) || _flatList == null || _matrixBuf == null)
                {
                    try
                    {
                        _flatList = _flatListField.GetValue(instance) as List<FlatTriangleBillboardData>;
                        _matrixBuf = _matrixBufField.GetValue(instance) as List<MatrixD>;
                    }
                    catch
                    {
                        _flatList = null;
                        _matrixBuf = null;
                    }
                    _boundInstance = instance;
                    if (_flatList == null || _matrixBuf == null)
                        NoteBindFailure("RichHud master billboard buffers are not the expected list"
                                        + " types; nav menu content will be dropped rather than projected");
                }

                _bindGeneration = generation;
                return _matrixBuf == null ? null : _flatList;
            }
        }

        private static void NoteBindFailure(string message)
        {
            if (Interlocked.Exchange(ref _bindFailureLogged, 1) != 0) return;
            Plugin.Log(message);
        }
    }
}
