using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CleanFeed.Profiles;
using CleanFeed.Render;
using HarmonyLib;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace CleanFeed.Compatibility
{
    internal enum HudTriangleDecision
    {
        PassThrough,
        Suppress,
        Capture
    }

    [HarmonyPatch]
    internal static class HudBillboardProjector
    {
        // Captured triangle state. A struct in a [ThreadStatic] slot: AddTriangleBillboard arrives at
        // HUD rates, and the pairing buffer must not allocate per call.
        internal struct PendingTriangle
        {
            internal HudRouteTag Tag;
            internal Vector3D P0;
            internal Vector3D P1;
            internal Vector3D P2;
            internal Vector3 N0;
            internal Vector3 N1;
            internal Vector3 N2;
            internal Vector2 Uv0;
            internal Vector2 Uv1;
            internal Vector2 Uv2;
            internal MyStringId Material;
            internal uint ParentId;
            internal Vector3D WorldPosition;
            internal Vector4 Color;
            internal bool HasColor;
            internal MyBillboard.BlendTypeEnum Blend;
        }

        private static long _diverted;
        private static long _passThrough;
        private static long _failed;
        private static long _triangleDiverted;
        private static long _triangleReplayed;
        private static long _trianglePairMisses;
        private static long _suppressed;
        private static long _richHudProjected;
        private static long _richHudDropped;
        private static long _richHudPassed;
        private static long _clipRejected;
        private static long _oversize;
        private static long _blendDeclined;
        private static long _sourceUnresolved;
        private static long _particleDeclined;
        private static int _richHudDropLogged;

        // Particle-path textures that ARE reproducible as sprites, keyed by file name.
        //
        // Square.dds is a uniform white quad. It is what a modder reaches for whenever they want a
        // solid tinted plate, because tinting a white square is how you get one - BuildInfo's
        // toolbar-info panel background is exactly that, a Color_UIBackground plate the operator
        // sees as a flat dark rectangle. For textures like this the sprite pipeline's "uniform
        // tinted rectangle" is the FAITHFUL result, not the degradation the particle guard exists
        // to prevent, so declining them buys nothing and costs a routed element.
        private static readonly HashSet<string> ReproducibleParticleTextures =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Square.dds" };

        private static readonly char[] TexturePathSeparators = { '\\', '/' };

        // Smallest homogeneous w a vertex may carry and still be in front of the eye plane. At or
        // below it the perspective divide produces sign-flipped, unbounded screen coordinates.
        private const double MinimumClipW = 1e-6d;

        // Guard band in NDC, applied after the divide. A vertex this far outside the frustum cannot
        // belong to a quad the player is meant to see, and the depth gate alone does not catch it:
        // a quad straddling the near plane keeps a plausible clip.Z while its X/Y run away.
        private const double ClipGuardBand = 1.5d;

        // Largest destination rectangle accepted, as a multiple of the larger viewport dimension.
        // The sprite goes onto the full-screen player layer with ignoreBounds set, so an unbounded
        // rectangle is a whiteout rather than a misplaced icon. The guard band already bounds each
        // vertex; this is the second net, covering the camera moving between the mod's draw and
        // this projection at high travel speed.
        private const float MaximumViewportSpan = 2f;
        private static long _viewportSpriteLogs;
        [ThreadStatic] private static PendingTriangle _pendingTriangle;
        [ThreadStatic] private static bool _hasPendingTriangle;
        [ThreadStatic] private static bool _replayingTriangle;

        private static MethodBase TargetMethod()
            => AccessTools.Method(typeof(MyTransparentGeometry), nameof(MyTransparentGeometry.AddBillboard),
                new[] { typeof(MyBillboard), typeof(bool) });

        private static bool Prefix(MyBillboard billboard, bool isPersistent)
        {
            HudRouteTag tag = HudRouteContext.Current;
            if (!HudRedirector.SelectiveInterceptionActive || tag == null)
            {
                Interlocked.Increment(ref _passThrough);
                return true;
            }
            HudEffectiveRoute route = HudRedirector.ResolveRoute(tag);
            bool menu = HudAdapterCoordinator.IsRichHudEmitterTag(tag);
            bool cursor = !menu && HudAdapterCoordinator.IsRichHudCursorTag(tag);
            bool richHud = menu || cursor;
            bool supportedBillboard = billboard != null && !isPersistent
                                      && billboard.CustomViewProjection == -1
                                      && billboard.LocalType == MyBillboard.LocalTypeEnum.Custom;
            HudSourceRegistry.RecordActivity(tag,
                isPersistent ? HudPrimitiveFamily.PersistentBillboard
                : billboard != null && billboard.CustomViewProjection != -1
                    ? HudPrimitiveFamily.CustomProjection
                    : HudPrimitiveFamily.Billboard,
                supportedBillboard || (richHud && HudRichHudQuadCapture.CaptureActive));
            if (route == HudEffectiveRoute.Recorded)
            {
                if (richHud) Interlocked.Increment(ref _richHudPassed);
                Interlocked.Increment(ref _passThrough);
                return true;
            }

            // Suppressed is decided before anything else, and before the RichHud fail-closed
            // branch in particular. The player hid this category from both outputs, or privacy is
            // engaged: dropping the primitive is the correct and complete answer whatever kind of
            // primitive it is, so it must not be counted as content that could not be projected.
            // With the two the other way round, hiding flip-burn - or simply alt-tabbing with
            // privacy on - drove the richhud-dropped counter and tripped the once-per-session
            // "could not be projected" warning for content that was never meant to be shown. This
            // also puts the singular prefix in the same order as AllowBatch and ClassifyTriangle,
            // which both already answered Suppressed before inspecting the primitive.
            if (route == HudEffectiveRoute.Suppressed)
            {
                Interlocked.Increment(ref _suppressed);
                return false;
            }

            // RichHud never renders from the billboard it hands over here. This is one of the
            // master's POOLED MyTriangleBillboards: it satisfies every supported-primitive check,
            // but BillBoardUtils writes vertex data into a parallel buffer and the master transfers
            // it into the pool afterwards, in FinishDraw, once every client has queued. Position0..3
            // read at this point are whatever the pool slot last held - roughly six frames stale -
            // so this billboard can never be projected, only swallowed.
            //
            // Under an open capture window the swallow is half of a working path:
            // HudRichHudQuadCapture is collecting the same draw's real geometry from that parallel
            // buffer and will project it onto the player layer when the scoped call returns.
            //
            // Outside one, the two RichHud tags part company, because the content behind them is not
            // equally sensitive. Menu content is the GPS-name dox vector, so an uncaptured emission
            // fails CLOSED: dropped from both outputs rather than leaked into the recording the user
            // believes the flip-burn filter is covering. The shared cursor is an arrow that reveals
            // nothing, so it fails OPEN and passes through untouched - it is far worse to leave the
            // player without a cursor than to let one record.
            if (richHud)
            {
                if (HudRichHudQuadCapture.CaptureActive)
                {
                    HudRichHudQuadCapture.NoteSuppressedEmission();
                    return false;
                }
                if (cursor)
                {
                    HudRichHudQuadCapture.NoteCursorPassed();
                    Interlocked.Increment(ref _passThrough);
                    return true;
                }
                Interlocked.Increment(ref _richHudDropped);
                NoteRichHudDrop();
                return false;
            }

            // PlayerOnly from here down. A primitive with no projection path falls open to the
            // recording, and the RecordActivity call above has already reported it as the leak it
            // is; a suppressed route never reaches this point.
            if (billboard == null || isPersistent || billboard.CustomViewProjection != -1
                || billboard.LocalType != MyBillboard.LocalTypeEnum.Custom)
            {
                Interlocked.Increment(ref _passThrough);
                return true;
            }

            try
            {
                Vector2 uv0 = billboard.UVOffset;
                Vector2 uv1 = uv0 + new Vector2(billboard.UVSize.X, 0f);
                Vector2 uv2 = uv0 + billboard.UVSize;
                Vector2 uv3 = uv0 + new Vector2(0f, billboard.UVSize.Y);
                if (!TryProjectQuad(
                        billboard.Material,
                        ToSpriteColor(billboard.Color, billboard.ColorIntensity),
                        billboard.BlendType,
                        ref billboard.Position0, ref billboard.Position1,
                        ref billboard.Position2, ref billboard.Position3,
                        uv0, uv1, uv2, uv3, HudNamedSpriteRoutes.TargetFor(tag)))
                {
                    Interlocked.Increment(ref _passThrough);
                    return true;
                }
                Interlocked.Increment(ref _diverted);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                Plugin.Log("billboard projection failed open: " + ex.GetType().Name + " - " + ex.Message);
                return true;
            }
        }

        // The batched sibling of the AddBillboard prefix, and the reason it exists: RichHud queues
        // most of its per-frame billboards through
        // MyTransparentGeometry.AddBillboards(IEnumerable<MyBillboard>, bool), not through the
        // singular overload - the nav menu's text glyphs among them, via the master's
        // BillBoardUtils.AddTriangleData. Without this patch that whole path bypasses interception
        // entirely and lands in the recording while the user believes the flip-burn filter covers it.
        //
        // The members of a batch are as unprojectable as the singular billboard is, and for the same
        // reason: they are pooled slots whose geometry has not been written yet. The route decision
        // is therefore made once for the whole call and the batch is passed or swallowed whole; no
        // member is inspected, so the cost on the untagged path is a context read and a branch, and
        // enumerating a caller's collection in a prefix cannot disturb a single-pass source.
        //
        // Returns true to run the original AddBillboards, false to swallow the batch.
        internal static bool AllowBatch(bool isPersistent)
        {
            HudRouteTag tag = HudRouteContext.Current;
            if (!HudRedirector.SelectiveInterceptionActive || tag == null)
            {
                Interlocked.Increment(ref _passThrough);
                return true;
            }

            HudEffectiveRoute route = HudRedirector.ResolveRoute(tag);
            bool menu = HudAdapterCoordinator.IsRichHudEmitterTag(tag);
            bool cursor = !menu && HudAdapterCoordinator.IsRichHudCursorTag(tag);
            bool richHud = menu || cursor;
            // Once per call rather than per billboard. Supported only while a RichHud capture window
            // is open, where the batch's geometry is being projected from the framework's own
            // intermediate buffer; for everything else no batched primitive has a projection path,
            // so the registry reports the family as a leak wherever one actually passes through.
            HudSourceRegistry.RecordActivity(tag, HudPrimitiveFamily.Billboard,
                richHud && HudRichHudQuadCapture.CaptureActive);
            if (route == HudEffectiveRoute.Recorded)
            {
                if (richHud) Interlocked.Increment(ref _richHudPassed);
                Interlocked.Increment(ref _passThrough);
                return true;
            }
            if (route == HudEffectiveRoute.Suppressed)
            {
                Interlocked.Increment(ref _suppressed);
                return false;
            }

            // PlayerOnly. Inside a capture window the swallow is the suppression half of the
            // projection path. Outside one the menu tag fails closed - the batch is dropped from
            // both outputs rather than leaking content the flip-burn filter was meant to cover -
            // while the cursor tag fails open and passes through, since an arrow is not a privacy
            // vector and a missing cursor is the worse outcome. Any other mod's batch falls open to
            // the recording exactly as the other unsupported primitive families do, with the
            // RecordActivity above reporting it as the leak it is.
            if (richHud)
            {
                if (HudRichHudQuadCapture.CaptureActive)
                {
                    HudRichHudQuadCapture.NoteSuppressedEmission();
                    return false;
                }
                if (cursor)
                {
                    HudRichHudQuadCapture.NoteCursorPassed();
                    Interlocked.Increment(ref _passThrough);
                    return true;
                }
                Interlocked.Increment(ref _richHudDropped);
                NoteRichHudDrop();
                return false;
            }
            Interlocked.Increment(ref _passThrough);
            return true;
        }

        internal static void NoteRichHudProjected() => Interlocked.Increment(ref _richHudProjected);

        internal static void NoteRichHudDropped()
        {
            Interlocked.Increment(ref _richHudDropped);
            NoteRichHudDrop();
        }

        // A drop is now the exception rather than the standing behaviour, but it is still a bounded
        // capability gap rather than a fault, and it can occur at HUD rates, so it is reported once
        // per session. The counters in StatusLine carry the volume.
        private static void NoteRichHudDrop()
        {
            if (Interlocked.Exchange(ref _richHudDropLogged, 1) != 0) return;
            Plugin.Log("RichHud content under a filtered route could not be projected onto the"
                       + " player layer and was dropped from both outputs; see richhud counters");
        }

        // Decides what to do with an incoming triangle before its arguments are copied into a
        // PendingTriangle, so a pass-through costs nothing but the branch.
        internal static HudTriangleDecision ClassifyTriangle(out HudRouteTag tag)
        {
            tag = null;
            if (_replayingTriangle) return HudTriangleDecision.PassThrough;

            HudRouteTag current = HudRouteContext.Current;
            if (!HudRedirector.SelectiveInterceptionActive || current == null)
            {
                ReplayPendingTriangle();
                Interlocked.Increment(ref _passThrough);
                return HudTriangleDecision.PassThrough;
            }

            HudEffectiveRoute route = HudRedirector.ResolveRoute(current);
            HudSourceRegistry.RecordActivity(current, HudPrimitiveFamily.Triangle, true);
            if (route == HudEffectiveRoute.Recorded)
            {
                ReplayPendingTriangle();
                Interlocked.Increment(ref _passThrough);
                return HudTriangleDecision.PassThrough;
            }
            if (route == HudEffectiveRoute.Suppressed)
            {
                _hasPendingTriangle = false;
                _pendingTriangle = default(PendingTriangle);
                Interlocked.Increment(ref _suppressed);
                return HudTriangleDecision.Suppress;
            }

            tag = current;
            return HudTriangleDecision.Capture;
        }

        // Returns true to run the original AddTriangleBillboard, false to swallow it.
        internal static bool CaptureTriangle(ref PendingTriangle current)
        {
            if (!_hasPendingTriangle)
            {
                _pendingTriangle = current;
                _hasPendingTriangle = true;
                return false;
            }

            PendingTriangle first = _pendingTriangle;
            if (!CanPair(ref first, ref current))
            {
                ReplayTriangle(ref first);
                _pendingTriangle = current;
                _hasPendingTriangle = true;
                Interlocked.Increment(ref _trianglePairMisses);
                return false;
            }

            _hasPendingTriangle = false;
            _pendingTriangle = default(PendingTriangle);
            try
            {
                MyTransparentMaterial material = MyTransparentMaterials.GetMaterial(first.Material);
                // AddTriangleBillboard carries no intensity of its own, so the neutral 1 is passed:
                // ToSpriteColor is still the one place a colour becomes a sprite colour.
                Color color = ToSpriteColor(
                    first.HasColor ? first.Color : material?.Color ?? Vector4.One, 1f);
                if (TryProjectQuad(
                        first.Material, color, first.Blend,
                        ref first.P0, ref first.P1, ref first.P2, ref current.P1,
                        first.Uv0, first.Uv1, current.Uv1, first.Uv2,
                        HudNamedSpriteRoutes.TargetFor(first.Tag)))
                {
                    Interlocked.Add(ref _triangleDiverted, 2);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                Plugin.Log("triangle projection failed open: " + ex.GetType().Name + " - " + ex.Message);
            }

            ReplayTriangle(ref first);
            Interlocked.Increment(ref _trianglePairMisses);
            return true;
        }

        internal static void FlushPendingTriangle(HudRouteTag scope)
        {
            if (!_hasPendingTriangle) return;
            if (scope != null && !ReferenceEquals(_pendingTriangle.Tag, scope)) return;
            ReplayPendingTriangle();
        }

        private static bool CanPair(ref PendingTriangle first, ref PendingTriangle second)
            => ReferenceEquals(first.Tag, second.Tag)
               && first.Material == second.Material
               && first.ParentId == second.ParentId
               && first.Blend == second.Blend
               && first.HasColor == second.HasColor
               && (!first.HasColor || Near(first.Color, second.Color))
               && Near(first.P0, second.P0)
               && Near(first.P2, second.P2)
               && Near(first.Uv0, second.Uv0)
               && Near(first.Uv2, second.Uv2);

        // World-space quad to one player-layer sprite, via the current camera's view*projection.
        //
        // For world content only. The RichHud flat-quad capture deliberately does NOT come through
        // here: its quads sit on a camera-anchored plane at the near plane, where round-tripping
        // through the projection is numerically degenerate and rejected quads flicker; it maps plane
        // pixels to screen pixels directly instead and shares only TryResolveSpriteSource below.
        //
        // Returning false is a decline, not a suppression: both callers respond by letting the
        // original call run - the singular prefix returns true, the triangle path replays the held
        // pair - so declined content still reaches the player through the game's own transparent
        // geometry pass, correctly blended and correctly clipped, and falls open to the recording
        // with RecordActivity already reporting it. Drawing something wrong on a full-screen,
        // unclipped layer is the worse outcome, so every gate below prefers the decline.
        private static bool TryProjectQuad(
            MyStringId materialId, Color color, MyBillboard.BlendTypeEnum blend,
            ref Vector3D world0, ref Vector3D world1, ref Vector3D world2, ref Vector3D world3,
            Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3,
            string targetTexture)
        {
            // Only the ADDITIVE blends are declined. Standard, LDR/SDR and PostPP are all
            // alpha-over stages, which the premultiplied sprite pass approximates the same way it
            // always has - and PostPP is the Text HUD API's default HUD blend, so declining it
            // would push every routed text glyph (Flip and Burn readouts, HUDLCD panels, the GPS
            // names the filters exist for) into the recording. Coverage there is the point.
            //
            // Additive genuinely cannot be expressed. The premultiplied encoding of an additive
            // draw - RGB pre-scaled by the source alpha with the alpha channel left at 0, which
            // leaves the destination fully weighted - is exact under a colour Src=One equation,
            // and that is the equation this pass uses. It still cannot be reached from here:
            // Primitives/Sprites.hlsl premultiplies the VERTEX colour unconditionally in the
            // vertex shader ("output.color.rgb *= output.color.a", outside the PREMULTIPLY_ALPHA
            // guard, which covers only the texture sample), so a sprite colour submitted with
            // alpha 0 arrives at the blender as pure black and draws nothing at all. The pass is
            // fixed - MySpritesManager.Render binds BlendAlphaPremult for the whole batch and
            // there is no per-sprite blend lever - so an additive billboard drawn through it is
            // painted as ordinary opaque paint. That is the mechanism behind a camera-anchored
            // AdditiveBottom HUD icon arriving as a white slab, and correctness beats coverage
            // for these: declining routes them to the caller's fail direction, counted and
            // reported rather than drawn wrong. Flip and Burn's additive users are its
            // world-space dots and lines, which the docs already declare unroutable.
            if (blend == MyBillboard.BlendTypeEnum.AdditiveBottom
                || blend == MyBillboard.BlendTypeEnum.AdditiveTop)
            {
                Interlocked.Increment(ref _blendDeclined);
                return false;
            }

            // One camera resolution per quad. MatrixD is 128 bytes, so the view/projection product
            // and every vertex transform use the ref/out overloads rather than copying it five times.
            var camera = MyAPIGateway.Session?.Camera;
            if (camera == null) return false;
            Vector2 viewport = camera.ViewportSize;
            if (viewport.X <= 0 || viewport.Y <= 0) return false;

            MatrixD view = camera.ViewMatrix;
            MatrixD projection = camera.ProjectionMatrix;
            MatrixD viewProjection;
            MatrixD.Multiply(ref view, ref projection, out viewProjection);

            Vector2 p0 = Project(ref world0, ref viewProjection, viewport, out bool v0);
            Vector2 p1 = Project(ref world1, ref viewProjection, viewport, out bool v1);
            Vector2 p2 = Project(ref world2, ref viewProjection, viewport, out bool v2);
            Vector2 p3 = Project(ref world3, ref viewProjection, viewport, out bool v3);
            if (!v0 || !v1 || !v2 || !v3)
            {
                Interlocked.Increment(ref _clipRejected);
                return false;
            }

            Vector2 center = (p0 + p1 + p2 + p3) * 0.25f;
            Vector2 widthEdge = p1 - p0;
            Vector2 heightEdge = p3 - p0;
            float width = widthEdge.Length();
            float height = heightEdge.Length();
            if (width < 0.01f || height < 0.01f || float.IsNaN(width) || float.IsNaN(height)) return false;

            // Upper bound as well as a lower one. Nothing the player is meant to read is twice the
            // screen across, and the sprite is drawn unclipped onto the full-screen layer, so an
            // oversized rectangle covers the frame rather than overhanging it.
            float span = Math.Max(viewport.X, viewport.Y) * MaximumViewportSpan;
            if (width > span || height > span)
            {
                Interlocked.Increment(ref _oversize);
                return false;
            }

            float minU = Math.Min(Math.Min(uv0.X, uv1.X), Math.Min(uv2.X, uv3.X));
            float minV = Math.Min(Math.Min(uv0.Y, uv1.Y), Math.Min(uv2.Y, uv3.Y));
            float maxU = Math.Max(Math.Max(uv0.X, uv1.X), Math.Max(uv2.X, uv3.X));
            float maxV = Math.Max(Math.Max(uv0.Y, uv1.Y), Math.Max(uv2.Y, uv3.Y));

            string texture;
            Rectangle source;
            if (!TryResolveSpriteSource(materialId, new Vector2(minU, minV), new Vector2(maxU, maxV),
                    out texture, out source))
                return false;

            var destination = new RectangleF(center.X - width * 0.5f, center.Y - height * 0.5f, width, height);
            float rotation = (float)Math.Atan2(widthEdge.Y, widthEdge.X);

            // Flash forensics. A projected sprite that blankets most of the viewport is either a
            // mod's deliberate fullscreen effect or the next whiteout, and the difference is
            // decided by WHICH material it is - so the first few name themselves instead of
            // leaving the operator to guess from a one-frame flash. Off the hot path by count.
            if (width >= viewport.X * 0.7f && height >= viewport.Y * 0.7f
                && Interlocked.Increment(ref _viewportSpriteLogs) <= 5)
                Plugin.Log("projected sprite covers the viewport: material=" + materialId.String
                           + ", size=" + (int)width + "x" + (int)height
                           + ", color=" + color.R + "/" + color.G + "/" + color.B + "/" + color.A
                           + ", route-key=" + (HudRouteContext.Current?.Key ?? "none"));

            MyRenderProxy.DrawSprite(texture, ref destination, source, color, rotation,
                true, false, targetTexture, null, 0f);
            return true;
        }

        // Material to sprite texture plus the source rectangle for a UV sub-range, in texels.
        // Extracted so the RichHud flat-quad capture - which computes its destination rectangle in
        // screen space without ever touching the camera - resolves materials, atlas offsets and
        // texture sizes through exactly the same code as the world-projection path.
        internal static bool TryResolveSpriteSource(
            MyStringId materialId, Vector2 uvMin, Vector2 uvMax,
            out string texture, out Rectangle source)
        {
            texture = null;
            source = default(Rectangle);

            MyTransparentMaterial material = MyTransparentMaterials.GetMaterial(materialId);
            if (material == null || string.IsNullOrEmpty(material.Texture))
            {
                Interlocked.Increment(ref _sourceUnresolved);
                return false;
            }

            // Particle-class textures carry their falloff in RGB with near-solid alpha, which the
            // sprite pipeline (typed GUI, premultiplied sampling) renders as a uniform tinted
            // rectangle instead of the gradient - RichHud's RadialShadow cursor shadow borrows
            // Textures\Particles\EngineThrustMiddle.dds and drew a hard box. Such quads cannot be
            // reproduced faithfully as sprites; declining here routes them to the caller's existing
            // fail direction rather than drawing something wrong.
            //
            // The path prefix alone was too wide a net, and it cost a real leak: it caught the flat
            // quads in that folder along with the gradients. BuildInfo's toolbar-info background is
            // Textures\Particles\Square.dds, so every plate was declined here, passed through by the
            // caller and recorded - with the panel's LABELS filtering correctly beside it, because
            // they carry a font texture rather than a particle one. That is the whole of the
            // "background records, text does not" report, and it ticked no counter on the way past.
            // Membership is by file name, so the guard now turns on what the texture IS.
            if (IsUnreproducibleParticleTexture(material.Texture))
            {
                Interlocked.Increment(ref _particleDeclined);
                return false;
            }

            Vector2 textureSize = material.TargetSize.X > 0 && material.TargetSize.Y > 0
                ? new Vector2(material.TargetSize.X, material.TargetSize.Y)
                : MyRenderProxy.GetTextureSize(material.Texture);
            if (textureSize.X <= 0 || textureSize.Y <= 0)
            {
                Interlocked.Increment(ref _sourceUnresolved);
                return false;
            }

            // Defensive, not a fix for anything observed: MyTransparentMaterial's constructor
            // defaults UVSize to (1,1), so a material that declares no atlas window already reads as
            // the whole texture. A definition that sets the field to zero outright would otherwise
            // produce a degenerate source rectangle and decline silently, which is the one shape of
            // this bug the counters below could not tell apart from a missing material.
            Vector2 materialUvSize = material.UVSize;
            if (materialUvSize.X <= 0f || materialUvSize.Y <= 0f) materialUvSize = Vector2.One;

            Vector2 uvOffset = material.UVOffset + uvMin * materialUvSize;
            Vector2 uvSize = (uvMax - uvMin) * materialUvSize;
            if (uvSize.X <= 0f || uvSize.Y <= 0f)
            {
                Interlocked.Increment(ref _sourceUnresolved);
                return false;
            }

            texture = material.Texture;
            source = new Rectangle(
                (int)Math.Round(uvOffset.X * textureSize.X),
                (int)Math.Round(uvOffset.Y * textureSize.Y),
                Math.Max(1, (int)Math.Round(uvSize.X * textureSize.X)),
                Math.Max(1, (int)Math.Round(uvSize.Y * textureSize.Y)));
            return true;
        }

        // True only for a particle-folder texture that is NOT on the reproducible list. Matching on
        // the file name rather than the path keeps the decision about the texture's content, which is
        // what the guard is actually about - a mod may point a material anywhere.
        private static bool IsUnreproducibleParticleTexture(string path)
        {
            if (path.IndexOf("Textures\\Particles\\", StringComparison.OrdinalIgnoreCase) < 0
                && path.IndexOf("Textures/Particles/", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            int separator = path.LastIndexOfAny(TexturePathSeparators);
            string file = separator >= 0 ? path.Substring(separator + 1) : path;
            return !ReproducibleParticleTextures.Contains(file);
        }

        // The homogeneous transform is written out rather than delegated to Vector3D.Transform,
        // which performs the perspective divide internally and then discards w. Without w a vertex
        // at or behind the eye plane is indistinguishable from one in front of it: the divide by a
        // negative number flips the sign of X and Y and scales them without bound as w approaches
        // zero, and clip.Z can still land inside the depth gate, so the gate alone lets it pass.
        // That is what turns a quad straddling the near plane into a rectangle tens of thousands of
        // pixels across.
        private static Vector2 Project(
            ref Vector3D world, ref MatrixD viewProjection, Vector2 viewport, out bool valid)
        {
            valid = false;
            double w = world.X * viewProjection.M14 + world.Y * viewProjection.M24
                       + world.Z * viewProjection.M34 + viewProjection.M44;
            // NaN fails every comparison, so it is refused explicitly rather than by the bound.
            if (double.IsNaN(w) || w <= MinimumClipW) return Vector2.Zero;

            double inverseW = 1d / w;
            double x = (world.X * viewProjection.M11 + world.Y * viewProjection.M21
                        + world.Z * viewProjection.M31 + viewProjection.M41) * inverseW;
            double y = (world.X * viewProjection.M12 + world.Y * viewProjection.M22
                        + world.Z * viewProjection.M32 + viewProjection.M42) * inverseW;
            double z = (world.X * viewProjection.M13 + world.Y * viewProjection.M23
                        + world.Z * viewProjection.M33 + viewProjection.M43) * inverseW;

            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) return Vector2.Zero;
            if (z < 0d || z > 1.05d) return Vector2.Zero;
            if (x < -ClipGuardBand || x > ClipGuardBand
                || y < -ClipGuardBand || y > ClipGuardBand) return Vector2.Zero;

            valid = true;
            return new Vector2(
                (float)((x + 1d) * 0.5d * viewport.X),
                (float)((1d - y) * 0.5d * viewport.Y));
        }

        // Billboard colour to sprite colour.
        //
        // MyBillboard.Color is a Vector4 whose components are not bounded to [0, 1] - intensity is
        // how additive HUD content is authored - and ColorIntensity scales it further. new
        // Color(Vector4) packs through PackUNorm, which clamps each component, so every component
        // above 1 lands on 255 and an intensity-scaled tint becomes opaque white across the quad.
        // Scaling first and then dividing RGB by its own maximum when that maximum exceeds 1 keeps
        // the hue and yields the brightest in-gamut colour with the same ratios. Alpha carries
        // coverage rather than intensity, so it is clamped in the ordinary way.
        private static Color ToSpriteColor(Vector4 color, float intensity)
        {
            // ColorIntensity has no field initializer on MyBillboard, so a billboard whose author
            // never set it reads 0. Applying that literally would black out content that renders
            // correctly today, so only a positive finite intensity is honoured.
            float scale = intensity > 0f && !float.IsInfinity(intensity) && !float.IsNaN(intensity)
                ? intensity
                : 1f;
            float r = color.X * scale;
            float g = color.Y * scale;
            float b = color.Z * scale;
            if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b))
            {
                r = 0f;
                g = 0f;
                b = 0f;
            }

            float peak = Math.Max(r, Math.Max(g, b));
            if (peak > 1f)
            {
                float inverse = 1f / peak;
                r *= inverse;
                g *= inverse;
                b *= inverse;
            }

            float a = float.IsNaN(color.W) ? 0f : MathHelper.Clamp(color.W, 0f, 1f);

            // Linear-to-sRGB encode, the projection's other half of faithful colour. Billboard
            // colours are LINEAR - the mod-facing CreateBillboard applies ToLinearRGB to its sRGB
            // input - while the sprite vertex shader runs srgba_to_rgba on whatever it is handed,
            // i.e. it DECODES. Passing linear values through that decode darkens everything
            // nonlinearly (a 0.16 plate lands near 0.02), which read as "substantially darker with
            // the redirect on" the moment a large translucent plate diverted. Encoding here means
            // the shader's decode lands back on the authored linear value. This is the same
            // correction HudRichHudQuadCapture.ToSpriteColor has always applied on the RichHud
            // path, and it retroactively resolves the old HUDLCD colour suspect - diverted
            // billboards were double-decoded from the day this projector shipped.
            var srgb = new Vector4(r, g, b, a).ToSRGB();
            return new Color(new Vector4(srgb.X, srgb.Y, srgb.Z, a));
        }

        private static void ReplayPendingTriangle()
        {
            if (!_hasPendingTriangle) return;
            PendingTriangle pending = _pendingTriangle;
            _hasPendingTriangle = false;
            _pendingTriangle = default(PendingTriangle);
            ReplayTriangle(ref pending);
        }

        // Re-issues the held triangle through the real API. _replayingTriangle makes the prefix wave
        // it straight through, so this can be a direct call rather than a reflective one.
        private static void ReplayTriangle(ref PendingTriangle pending)
        {
            try
            {
                _replayingTriangle = true;
                // AddTriangleBillboard is marked "Only for modders". CleanFeed is re-issuing a call a
                // mod already made, on the same API, so the obsolete marker does not apply here.
#pragma warning disable 618
                if (pending.HasColor)
                    MyTransparentGeometry.AddTriangleBillboard(
                        pending.P0, pending.P1, pending.P2,
                        pending.N0, pending.N1, pending.N2,
                        pending.Uv0, pending.Uv1, pending.Uv2,
                        pending.Material, pending.ParentId, pending.WorldPosition,
                        pending.Color, pending.Blend);
                else
                    MyTransparentGeometry.AddTriangleBillboard(
                        pending.P0, pending.P1, pending.P2,
                        pending.N0, pending.N1, pending.N2,
                        pending.Uv0, pending.Uv1, pending.Uv2,
                        pending.Material, pending.ParentId, pending.WorldPosition,
                        pending.Blend);
#pragma warning restore 618
                Interlocked.Increment(ref _triangleReplayed);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                Plugin.Log("triangle fail-open replay failed: " + ex.GetType().Name + " - " + ex.Message);
            }
            finally { _replayingTriangle = false; }
        }

        private static bool Near(Vector3D left, Vector3D right)
            => (left - right).LengthSquared() <= 1e-12;

        private static bool Near(Vector2 left, Vector2 right)
            => (left - right).LengthSquared() <= 1e-10f;

        private static bool Near(Vector4 left, Vector4 right)
            => Math.Abs(left.X - right.X) <= 1e-6f
               && Math.Abs(left.Y - right.Y) <= 1e-6f
               && Math.Abs(left.Z - right.Z) <= 1e-6f
               && Math.Abs(left.W - right.W) <= 1e-6f;

        internal static string StatusLine()
            => "billboards=diverted:" + Interlocked.Read(ref _diverted)
               + ", pass:" + Interlocked.Read(ref _passThrough)
               + ", triangles:" + Interlocked.Read(ref _triangleDiverted)
               + ", triangle-replay:" + Interlocked.Read(ref _triangleReplayed)
               + ", triangle-miss:" + Interlocked.Read(ref _trianglePairMisses)
               + ", suppressed:" + Interlocked.Read(ref _suppressed)
               + ", richhud-projected:" + Interlocked.Read(ref _richHudProjected)
               + ", richhud-dropped:" + Interlocked.Read(ref _richHudDropped)
               + ", richhud-passed:" + Interlocked.Read(ref _richHudPassed)
               + ", clip-reject:" + Interlocked.Read(ref _clipRejected)
               + ", oversize:" + Interlocked.Read(ref _oversize)
               + ", blend-declined:" + Interlocked.Read(ref _blendDeclined)
               // Both new, and both cover declines that used to leave no trace at all: a tagged
               // billboard whose material would not resolve passed straight through to the recording
               // while every named counter here stayed at zero. Anything that can defeat a filter has
               // to be countable.
               + ", source-unresolved:" + Interlocked.Read(ref _sourceUnresolved)
               + ", particle-declined:" + Interlocked.Read(ref _particleDeclined)
               + ", failed:" + Interlocked.Read(ref _failed);
    }

    // RichHud's client and master emitters both queue through this overload, so it is the batched
    // counterpart to HudBillboardProjector's own AddBillboard patch. A typed prefix, never Harmony's
    // object[] __args: this sits on every batched transparent draw in the game, including while
    // CleanFeed is off. Only isPersistent is injected - the decision is per call, and the enumerable
    // is deliberately not touched, since enumerating a caller's collection in a prefix could disturb
    // a single-pass source.
    //
    // Behavioural summary of the RichHud route as a whole: flip-burn unfiltered (Recorded) leaves
    // the nav menu rendering and recording exactly as before; flip-burn filtered removes it from the
    // recording while HudRichHudQuadCapture reprojects it onto the player-only layer, so the player
    // still sees the menu; anything under that route that cannot be projected is dropped from both
    // outputs rather than leaked; and nothing changes for any other mod's billboards.
    [HarmonyPatch(typeof(MyTransparentGeometry), nameof(MyTransparentGeometry.AddBillboards),
        new[] { typeof(IEnumerable<MyBillboard>), typeof(bool) })]
    internal static class HudBillboardBatchPatch
    {
        private static bool Prefix(bool isPersistent)
            => HudBillboardProjector.AllowBatch(isPersistent);
    }

    // AddTriangleBillboard has exactly two overloads. Each must be patched with a typed prefix, never
    // Harmony's object[] __args, which would box all 13-14 arguments on every transparent triangle in
    // the game - including while CleanFeed is off.
    [HarmonyPatch(typeof(MyTransparentGeometry), nameof(MyTransparentGeometry.AddTriangleBillboard),
        new[]
        {
            typeof(Vector3D), typeof(Vector3D), typeof(Vector3D),
            typeof(Vector3), typeof(Vector3), typeof(Vector3),
            typeof(Vector2), typeof(Vector2), typeof(Vector2),
            typeof(MyStringId), typeof(uint), typeof(Vector3D),
            typeof(MyBillboard.BlendTypeEnum)
        })]
    internal static class HudTriangleBillboardPatch
    {
        private static bool Prefix(
            Vector3D p0, Vector3D p1, Vector3D p2,
            Vector3 n0, Vector3 n1, Vector3 n2,
            Vector2 uv0, Vector2 uv1, Vector2 uv2,
            MyStringId material, uint parentID, Vector3D worldPosition,
            MyBillboard.BlendTypeEnum blendType)
        {
            HudRouteTag tag;
            switch (HudBillboardProjector.ClassifyTriangle(out tag))
            {
                case HudTriangleDecision.PassThrough: return true;
                case HudTriangleDecision.Suppress: return false;
            }

            var current = new HudBillboardProjector.PendingTriangle
            {
                Tag = tag,
                P0 = p0, P1 = p1, P2 = p2,
                N0 = n0, N1 = n1, N2 = n2,
                Uv0 = uv0, Uv1 = uv1, Uv2 = uv2,
                Material = material,
                ParentId = parentID,
                WorldPosition = worldPosition,
                HasColor = false,
                Color = Vector4.One,
                Blend = blendType
            };
            return HudBillboardProjector.CaptureTriangle(ref current);
        }
    }

    [HarmonyPatch(typeof(MyTransparentGeometry), nameof(MyTransparentGeometry.AddTriangleBillboard),
        new[]
        {
            typeof(Vector3D), typeof(Vector3D), typeof(Vector3D),
            typeof(Vector3), typeof(Vector3), typeof(Vector3),
            typeof(Vector2), typeof(Vector2), typeof(Vector2),
            typeof(MyStringId), typeof(uint), typeof(Vector3D),
            typeof(Vector4), typeof(MyBillboard.BlendTypeEnum)
        })]
    internal static class HudTriangleBillboardColorPatch
    {
        private static bool Prefix(
            Vector3D p0, Vector3D p1, Vector3D p2,
            Vector3 n0, Vector3 n1, Vector3 n2,
            Vector2 uv0, Vector2 uv1, Vector2 uv2,
            MyStringId material, uint parentID, Vector3D worldPosition,
            Vector4 color, MyBillboard.BlendTypeEnum blendType)
        {
            HudRouteTag tag;
            switch (HudBillboardProjector.ClassifyTriangle(out tag))
            {
                case HudTriangleDecision.PassThrough: return true;
                case HudTriangleDecision.Suppress: return false;
            }

            var current = new HudBillboardProjector.PendingTriangle
            {
                Tag = tag,
                P0 = p0, P1 = p1, P2 = p2,
                N0 = n0, N1 = n1, N2 = n2,
                Uv0 = uv0, Uv1 = uv1, Uv2 = uv2,
                Material = material,
                ParentId = parentID,
                WorldPosition = worldPosition,
                HasColor = true,
                Color = color,
                Blend = blendType
            };
            return HudBillboardProjector.CaptureTriangle(ref current);
        }
    }
}
