using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using CleanFeed.Profiles;
using CleanFeed.Render;
using HarmonyLib;

namespace CleanFeed.Compatibility
{
    internal static class HudAdapterCoordinator
    {
        // Everything a scoped Draw needs, in one entry. ScopePrefix runs on every patched draw call,
        // so its read path must stay a single lock-free dictionary lookup.
        private sealed class ScopeDescriptor
        {
            internal readonly Func<object, HudRouteTag> Resolver;
            internal readonly bool AlwaysScope;
            internal readonly bool SealRetained;
            // Set only on the RichHud client emitters and the client TextBoard draws: the scoped
            // call is also a capture boundary, and the geometry it appends to the framework's shared
            // 2D buffer is projected onto the player layer when it returns.
            internal readonly bool CaptureRichHud;

            internal ScopeDescriptor(
                Func<object, HudRouteTag> resolver, bool alwaysScope, bool sealRetained,
                bool captureRichHud)
            {
                Resolver = resolver;
                AlwaysScope = alwaysScope;
                SealRetained = sealRetained;
                CaptureRichHud = captureRichHud;
            }

            internal ScopeDescriptor Merge(bool alwaysScope, bool sealRetained, bool captureRichHud)
                => alwaysScope == AlwaysScope && sealRetained == SealRetained
                   && captureRichHud == CaptureRichHud
                    ? this
                    : new ScopeDescriptor(
                        Resolver, AlwaysScope || alwaysScope, SealRetained || sealRetained,
                        CaptureRichHud || captureRichHud);
        }

        // A value type: Harmony stores __state in a local of the declared type, so a struct keeps the
        // per-scoped-draw allocation out of the render path entirely.
        internal struct ScopeState
        {
            internal HudRouteTag Previous;
            internal HudRouteTag Current;
            internal bool SealRetained;
            internal bool Active;
            // Harmony stores one __state local per patched method and shares it across that method's
            // prefixes and postfixes, so the capture window's bookkeeping rides here rather than in
            // a second state type. Capturing is separate from CaptureStart because the struct's
            // default leaves the index at a valid-looking zero.
            internal bool Capturing;
            internal int CaptureStart;
            // Carried so the finalizer can re-run the backing-object walk over the same instance
            // once the body has actually built its HUD objects. Null whenever the walk does not
            // apply to this scope, which is how the finalizer decides without re-reading the
            // descriptor.
            internal object BackingOwner;
        }

        private sealed class StatHostInfo
        {
            internal bool IsPanel;
            internal bool IsToolbar;
        }

        // Stat IDs carried by the StatControl entries of the two bottom HUD panels in the definition:
        // the bottom-left suit panel and the bottom-right controlled-grid panel. The presence of any
        // of these identifies a MyStatControls instance as one of those panels, so the whole instance
        // is scoped and its untagged plate art and readouts follow the panel's route instead of
        // defaulting to player-only.
        //
        // The environment and gravity readouts belong to the toolbar-side info cluster instead, and
        // are listed in ToolbarClusterIds below.
        //
        // Targeting/crosshair stat IDs - targeting_circle, target_locked, target_in_focus,
        // offscreen_target_circle, controlled_can_target, controlled_is_turret, controlled_reloading -
        // must stay out of this set: MyHudCrosshair and MyHudTargetingMarkers build their own
        // MyStatControls instances from the same class and must never be scoped as panels.
        private static readonly HashSet<string> StatPanelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "player_helmet", "player_jetpack", "player_magboots", "player_flashlight",
            "player_broadcasting", "player_radiation", "controlled_dampeners", "controlled_speed",
            "player_health", "player_energy", "player_oxygen", "player_hydrogen", "player_food",
            "player_hydrogen_bottles", "player_oxygen_bottles",
            "controlled_power_usage", "controlled_reactors", "controlled_hydrogen_capacity",
            "controlled_estimated_time_remaining_energy", "controlled_mass", "controlled_is_static",
            "controlled_handbreak", "controlled_broadcasting", "controlled_remote_access"
        };

        // Stat IDs that identify the toolbar-side info cluster entries of the HUD definition: the plate
        // art behind the toolbar, the natural/artificial gravity readouts, the oxygen and temperature
        // readouts and the build-mode hints (grid size, symmetry, mountpoint align, colour hint).
        // They route with the toolbar category, so hiding the toolbar hides the whole strip.
        private static readonly HashSet<string> ToolbarClusterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "environment_oxygen_level", "environment_temperature_level", "natural_gravity",
            "artificial_gravity", "toolbar_grid_size", "toolbar_align_to_mountpoint", "toolbar_symmetry"
        };

        private sealed class OwnerTagStamp
        {
            internal int Generation;
            internal string Key;
            internal bool? RecordingOverride;
            internal bool? PlayerVisibleOverride;
            internal int Walks;
            internal int BarrenWalks;
            internal long NextBarrenWalk;
        }

        // Walks allowed per owner per route per activation generation: one before the scoped body
        // and one after it. The second is not a retry of the first. A method whose job is to BUILD
        // the provider's HUD objects - a TextAPI-detected handler, a first render - owns none of
        // them yet when the prefix runs, so an entry-only walk can never see what that very call
        // created, and those objects stay unattributed until something else happens to re-arm the
        // stamp. Past two the owner's graph is settled and further walks are pure cost on a
        // per-draw path.
        private const int WalksPerOwnerRoute = 2;

        // How long a barren walk stands before the owner is tried again. Long enough that an owner
        // which will never own a backing object costs a walk a second rather than a walk a draw,
        // short enough that a provider whose HUD objects arrive on a mod-to-mod handshake is picked
        // up within a second of them existing.
        private static readonly long BarrenWalkRetryTicks = Stopwatch.Frequency;

        // How far the barren interval may double out. The first live run measured about sixty
        // always-scoped owners retrying every second for the whole session - correct behaviour, and
        // the reason the toolbar panel was finally attributed, but a permanent floor of work for
        // owners that will never hold a backing object. Doubling from one second to a thirty-two
        // second ceiling keeps the early window dense, where a mod-to-mod handshake actually lands,
        // and lets the steady state fall away to nothing.
        private const int BarrenWalkBackoffShifts = 5;

        private const string BuildInfoToolbarRenderType =
            "Digi.BuildInfo.Features.ToolbarInfo.ToolbarLabelRender";

        // The panel's five persistent background plates, by field name, plus the list they are also
        // held in. Named rather than walked so the backstop cannot be defeated by budget or ordering.
        private static readonly NamedMemberCache[] BuildInfoBackgroundMembers =
        {
            new NamedMemberCache("Background"),
            new NamedMemberCache("BackgroundBottom"),
            new NamedMemberCache("CornerBottomLeft"),
            new NamedMemberCache("BackgroundTop"),
            new NamedMemberCache("CornerTopRight")
        };
        private static readonly NamedMemberCache BuildInfoBackgroundList =
            new NamedMemberCache("Backgrounds");

        private static long _backingTagged;
        private static long _barrenWalks;
        private static long _buildInfoBackgroundsTagged;

        // RichHudFramework ships twice over: once as the installed master mod, and once as a source
        // copy embedded in every mod that draws through it. Only the master carries its
        // MySessionComponent, and only an embedded copy carries the client registrar, so each edition
        // has a type the other never has.
        private const string RichHudMasterType = "RichHudFramework.Server.RichHudMaster";
        private const string RichHudClientType = "RichHudFramework.Client.RichHudClient";
        private const string RichHudEmitterType = "RichHudFramework.UI.Rendering.BillBoardUtils";
        private const string RichHudHandlerType = "RichHudFramework.Internal.ExceptionHandler";
        // The client-side proxy whose Draw calls straight through to the master's own TextBoard, and
        // therefore the only client-side stack frame every glyph quad passes through.
        private const string RichHudClientTextBoardType =
            "RichHudFramework.UI.Rendering.Client.TextBoard";
        // GameCore embeds its framework copy, so its session type and its RichHud client registrar
        // are declared by the same assembly and one pass over the client list can bind both.
        private const string GameCoreSessionType = GameCorePdaGate.SessionType;

        private static readonly object Gate = new object();

        // The two ConditionalWeakTables below are replaced wholesale by Shutdown, so they cannot be
        // their own lock objects: a reader would hold the monitor of the old table while a writer
        // took the monitor of the new one. These immutable gates guard both the tables' contents and
        // the fields that reference them.
        private static readonly object StatHostGate = new object();
        private static readonly object OwnerTagGate = new object();
        private static readonly HashSet<MethodBase> Patched = new HashSet<MethodBase>();
        private static readonly Dictionary<string, HudRouteTag> DefaultTags =
            new Dictionary<string, HudRouteTag>(StringComparer.OrdinalIgnoreCase);
        private const int BackingWalkMaxDepth = 4;
        private const int BackingWalkMaxNodes = 2048;

        // Reflection caches for members read on a per-draw path.
        private static readonly NamedMemberCache GpsPointType =
            new NamedMemberCache("POIType", "Type", "m_type", "PointType");
        private static readonly NamedMemberCache HudLcdPanel =
            new NamedMemberCache("thisTextPanel", "TextPanel", "m_textPanel");
        private static readonly NamedMemberCache PanelEntityId = new NamedMemberCache("EntityId");
        private static readonly NamedMemberCache PanelCustomData = new NamedMemberCache("CustomData");
        private static readonly NamedMemberCache BackingObject =
            new NamedMemberCache("BackingObject", "m_backingObject");

        private static Dictionary<MethodBase, ScopeDescriptor> _scopes =
            new Dictionary<MethodBase, ScopeDescriptor>();
        private static Harmony _harmony;
        private static ConditionalWeakTable<object, StatHostInfo> _statHosts =
            new ConditionalWeakTable<object, StatHostInfo>();
        private static ConditionalWeakTable<object, OwnerTagStamp> _ownerTags =
            new ConditionalWeakTable<object, OwnerTagStamp>();

        internal static void Initialize(Harmony harmony)
        {
            _harmony = harmony;
            HudModalState.Reset();
            HudSourceRegistry.Initialize();
            Scan(AppDomain.CurrentDomain.GetAssemblies());
            HudSourceDiscovery.Initialize();
        }

        // assemblies is only meaningful when assembliesChanged is set; the caller passes null on the
        // ticks between polls.
        internal static void Update(Assembly[] assemblies, bool assembliesChanged)
        {
            if (_harmony == null) return;
            bool changed = assembliesChanged && assemblies != null;
            if (changed) Scan(assemblies);
            HudSourceDiscovery.Update(assemblies, changed);
        }

        internal static void Shutdown()
        {
            HudRouteContext.Reset();
            HudModalState.Reset();
            lock (Gate)
            {
                Volatile.Write(ref _scopes, new Dictionary<MethodBase, ScopeDescriptor>());
                Patched.Clear();
                DefaultTags.Clear();
            }
            HudSourceDiscovery.Shutdown();
            HudSourceRegistry.Shutdown();
            HudRichHudQuadCapture.Reset();
            _harmony = null;
            lock (StatHostGate)
                _statHosts = new ConditionalWeakTable<object, StatHostInfo>();
            lock (OwnerTagGate)
                _ownerTags = new ConditionalWeakTable<object, OwnerTagStamp>();
        }

        internal static string StatusLine()
            => HudSourceRegistry.StatusLine() + "; " + HudSourceDiscovery.StatusLine()
               + "; " + GameCorePdaGate.StatusLine()
               + "; backing-walk=tagged:" + Interlocked.Read(ref _backingTagged)
               + ", barren:" + Interlocked.Read(ref _barrenWalks)
               + ", buildinfo-bg-tagged:" + Interlocked.Read(ref _buildInfoBackgroundsTagged)
               + ", provider-faults:" + Interlocked.Read(ref _providerFaults);

        private static void Scan(Assembly[] assemblies)
        {
            if (_harmony == null) return;

            TryPatchNative(assemblies);
            TryPatchInteractiveScreens(assemblies);
            TryPatchTextHud(assemblies);
            TryPatchBuildInfo(assemblies);
            TryPatchHudLcd(assemblies);
            TryPatchFlipBurn(assemblies, TryPatchRichHud(assemblies));
            TryPatchProvider(assemblies, "flight-vector", "FlightVector.Session", "Draw");
            TryPatchProvider(assemblies, "flight-hud", "HudCompassMod.HudCompassSession", "Draw");
            TryPatchProviderMethods(assemblies, "thrust-beacon", "ThrustBeacon.Session", "ClientTasks", "Draw");

            TryPatchProviderMethods(assemblies, "wc-radar", "WCRadar.Session",
                "DrawMissile", "ExpandedDraw", "ProcessDraws", "RollupData");
            TryPatchProvider(assemblies, "weaponcore.reload",
                "WeaponCore.Data.Scripts.CoreSystems.Ui.Hud.Hud", "DrawHudOnce");
            TryPatchProvider(assemblies, "weaponcore.reload",
                "WeaponCore.Data.Scripts.CoreSystems.Ui.Hud.Hud", "AgingTextDraw");
            TryPatchProviderMethods(assemblies, "weaponcore.radar",
                "WeaponCore.Data.Scripts.CoreSystems.Ui.Targeting.TargetUi",
                "DrawSelector", "DrawDroneNotice", "DrawActiveMarks", "DrawActiveLeads", "DrawTarget");
        }

        private static void TryPatchNative(Assembly[] assemblies)
        {
            Type terminal = FindType(assemblies, "Sandbox.Game.Gui.MyGuiScreenTerminal");
            PatchAllNamed("terminal", terminal, "Draw", _ => DefaultTag("terminal"));

            Type chat = FindType(assemblies, "Sandbox.Game.GUI.HudViewers.MyHudControlChat");
            PatchAllNamed("chat", chat, "Draw", _ => DefaultTag("chat"), false, true);

            Type toolbar = FindType(assemblies, "Sandbox.Game.Screens.Helpers.MyGuiControlToolbar");
            PatchAllNamed("toolbar", toolbar, "Draw", _ => DefaultTag("toolbar"), false, true);

            int gpsTargets = 0;
            foreach (Type type in SafeTypes(assemblies.Where(IsGameAssembly)).Where(t => t.Name == "PointOfInterest"))
                gpsTargets += PatchAllNamed("gps", type, "Draw", ResolveGps);
            SetStatus("gps", gpsTargets > 0 ? "partitioned" : "inactive", gpsTargets,
                gpsTargets > 0 ? "poi-type=10" : "target-missing");

            Type statHost = FindType(assemblies, "Sandbox.Game.GUI.MyStatControls");
            bool statHostPatched = PatchScope(
                FindMethod(statHost, "Draw", m => m.GetParameters().Length == 2),
                ResolveStatPanel, false, false);
            int statTargets = 0;
            foreach (Type type in SafeTypes(assemblies.Where(IsGameAssembly)).Where(t =>
                         t.Name.IndexOf("StatControl", StringComparison.OrdinalIgnoreCase) >= 0
                         && t.Namespace != null
                         && t.Namespace.IndexOf("Sandbox", StringComparison.OrdinalIgnoreCase) >= 0))
                statTargets += PatchAllNamed(null, type, "Draw", ResolveTagged);
            bool statsReady = statHostPatched && statTargets > 0;
            SetStatus("speed", statsReady ? "partitioned" : "inactive", statTargets,
                statsReady ? "binding-scoped" : "target-missing");
            SetStatus("battery-fuel", statsReady ? "partitioned" : "inactive", statTargets,
                statsReady ? "binding-scoped" : "target-missing");
        }

        private static void TryPatchInteractiveScreens(Assembly[] assemblies)
        {
            Type screenBase = FindType(assemblies, "Sandbox.Graphics.GUI.MyGuiScreenBase");
            if (screenBase == null)
            {
                SetStatus("interactive-ui", "inactive", 0, "screen-base-missing");
                return;
            }

            int count = 0;
            foreach (Type type in SafeTypes(assemblies.Where(IsGuiAssembly)))
            {
                if (type == screenBase || !screenBase.IsAssignableFrom(type)
                    || IsGameplayRenderScreen(type))
                    continue;

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance
                                                               | BindingFlags.Public
                                                               | BindingFlags.NonPublic
                                                               | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != "Draw" || method.IsAbstract || method.ContainsGenericParameters)
                        continue;
                    if (PatchScope(method, ResolveInteractiveScreen, false, false)) count++;
                }
            }

            // The base implementation owns controls, tooltips, cursor, and background sprites.
            // Derived screens commonly call it from their own Draw override, so scope it too.
            MethodInfo baseDraw = FindMethod(screenBase, "Draw", method =>
                !method.IsStatic && method.GetParameters().Length == 0);
            if (PatchScope(baseDraw, ResolveInteractiveScreen, false, false)) count++;

            // MyDX9Gui.DrawMouseCursor(string) - Sandbox.Game\Sandbox\Graphics\GUI_UPPER\MyDX9Gui.cs
            // :557-564 - wraps exactly one MyGuiManager.DrawSpriteBatch of the cursor texture and
            // nothing else, so scoping it moves the cursor and only the cursor.
            //
            // Phase order, which is what lets this land on top without a deferred flush:
            // MyDX9Gui.Draw calls MyScreenManager.Draw() at :584 and DrawMouseCursor at :627. The
            // RichHud master is a session component, and session components draw from
            // MyGuiScreenGamePlay.Draw (:1592) - a screen, and therefore inside that
            // MyScreenManager.Draw call. The cursor sprite is thus enqueued into the layer's bucket
            // after every projected menu sprite in the same frame, and later messages paint over
            // earlier ones.
            Type dx9Gui = FindType(assemblies, "Sandbox.Graphics.GUI.MyDX9Gui");
            int cursorTargets = PatchAllNamed(null, dx9Gui, "DrawMouseCursor", ResolveVanillaCursor);
            SetStatus("vanilla-cursor", cursorTargets > 0 ? "partitioned" : "inactive", cursorTargets,
                cursorTargets > 0 ? "top-layer while richhud projects" : "target-missing");

            Type screenManager = FindType(assemblies, "Sandbox.Graphics.GUI.MyScreenManager");
            MethodInfo drawTooltips = FindMethod(screenManager, "DrawTooltips", method =>
                method.IsStatic && method.GetParameters().Length == 1);
            if (PatchScope(drawTooltips, _ => DefaultTag("terminal"), false, false)) count++;
            MethodInfo drawScreens = FindMethod(screenManager, "Draw", method =>
                method.IsStatic && method.GetParameters().Length == 0);
            if (PatchMenuFrameSeal(drawScreens)) count++;

            SetStatus("interactive-ui", count > 0 ? "partitioned" : "inactive", count,
                count > 0 ? "explicit-screen+tooltip-scopes; frame-sealed" : "draw-targets-missing");
        }

        private static HudRouteTag ResolveInteractiveScreen(object instance)
        {
            if (instance == null || IsGameplayRenderScreen(instance.GetType())) return null;
            HudModalState.ObserveInteractiveScreen();
            if (instance.GetType().FullName == "CleanFeed.Screens.CleanFeedSettingsScreen")
                return SettingsTag();
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
                if (type.FullName == "Sandbox.Game.Gui.MyGuiScreenTerminal")
                    return DefaultTag("terminal");
            return DefaultTag("interactive");
        }

        private static bool IsGameplayRenderScreen(Type type)
        {
            for (; type != null; type = type.BaseType)
            {
                string name = type.FullName;
                if (name == "Sandbox.Game.Gui.MyGuiScreenGamePlay"
                    || name == "Sandbox.Game.Gui.MyGuiScreenHudBase"
                    || name == "Sandbox.Game.Gui.MyGuiScreenHudSpace")
                    return true;
            }
            return false;
        }

        private static void TryPatchTextHud(Assembly[] assemblies)
        {
            bool factoryOk = false;
            foreach (Type factoryType in FindTypes(assemblies, "UIFun.FontTexture"))
            {
                MethodInfo factory = FindMethod(factoryType, "Factory", m =>
                    m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));
                if (PatchFactory(factory)) factoryOk = true;
            }

            int draws = 0;
            int ctors = 0;
            string[] types =
            {
                "UIFun.Messagesv2.ModHUDMessage",
                "UIFun.Messagesv2.ModBillboardHUDMessage",
                "UIFun.Messagesv2.ModBillboardTriHUDMessage"
            };
            foreach (string typeName in types)
            foreach (Type type in FindTypes(assemblies, typeName))
            {
                draws += PatchAllNamed("texthud-backend", type, "Draw", ResolveTagged);
                ctors += PatchConstructors(type, "Text HUD backend ctor patch");
            }
            SetStatus("texthud", factoryOk && draws > 0 ? "partitioned" : "inactive",
                draws + (factoryOk ? 1 : 0),
                factoryOk
                    ? "tagged-billboards" + (ctors > 0 ? "; ctor-tagged" : string.Empty)
                    : "factory-missing");
        }

        private static void TryPatchFlipBurn(Assembly[] assemblies, int richHudTargets)
        {
            int count = 0;
            foreach (Type hud in FindTypes(assemblies, "FlipAndBurn.Hud"))
            {
                count += PatchAllNamed(null, hud, "Draw", _ => DefaultTag("flip-burn"), true);

                // The GPS/signal target selector - corner brackets, travel time, offscreen arrow -
                // follows the native gps category, so hiding gps from the recording also hides the
                // selector that points at it. DrawSelectedSignal runs from the Spectrum render hook,
                // outside Hud.Draw's scope, hence its own alwaysScope patch.
                count += PatchAllNamed(null, hud, "DrawGpsDestination", _ => DefaultTag("gps"), true);
                count += PatchAllNamed(null, hud, "DrawSelectedSignal", _ => DefaultTag("gps"), true);
            }

            // The nav menu is a RichHud UI tree. Its chrome is emitted by Flip and Burn's own
            // embedded copy of the framework and its text by the master on the client's behalf; both
            // are scoped by TryPatchRichHud and reprojected onto the player layer, so the menu
            // follows the flip-burn filter like the rest of the mod's HUD instead of being gated shut.
            SetStatus("flip-burn", count > 0 ? "partitioned-partial" : "inactive",
                count + richHudTargets,
                count > 0
                    ? "text-hud scoped; gps-target selector follows gps;"
                      + " world gates/rings/spheres pass; nav menu "
                      + (richHudTargets > 0 ? "routed via RichHud" : "unrouted")
                    : "provider-missing");
        }

        // RichHud draws every registered client in one interleaved master pass: TreeManager.DrawUI
        // calls HudNodeIterator.DrawNodes once over the flattened subtrees of all clients, sorted by
        // HUD-space distance and z-layer, and invokes each node's draw callback from that single loop.
        // The master therefore has no per-client draw method to scope, and its only per-client draw
        // boundaries - TreeClient.Update and the Before/AfterDrawCallback invocations - emit nothing.
        //
        // Two kinds of scope have to be installed, because a client's UI reaches the render queue by
        // two different routes.
        //
        // Chrome - window plates, rows, buttons, scrollbars - is drawn by QuadBoard/MatBoard inside
        // the client's own embedded framework copy, so the MyTransparentGeometry.AddBillboard(s)
        // calls are made by that client's own BillBoardUtils, from the client's own assembly, while
        // the master's DrawNodes callback is on the stack. Scoping those Add* statics attributes it.
        //
        // Text is not. A client-side Rendering.Client.TextBoard is a proxy over an API delegate: its
        // Draw invokes the master's Rendering.Server.TextBoard.Draw, which builds the glyph quads and
        // emits them through the MASTER's BillBoardUtils.AddTriangleData, in the master's assembly.
        // Nothing client-side is on that emission's stack, so scoping the emitters alone left every
        // glyph unattributed - which is exactly the observed split where the chrome disappeared under
        // a filtered route and the text kept rendering into the recording. Scoping the client
        // TextBoard.Draw overloads closes it: the master-side emission happens synchronously inside
        // the client call, so it inherits the client's scope without patching anything master-side
        // and without touching any other RichHud client.
        //
        // FindType is deliberately not used for any of these types: it returns the first match across
        // all assemblies, and with several mods shipping the framework that match is arbitrary.
        //
        // The billboards both routes queue are the master's pooled MyTriangleBillboards. They are
        // constructed with CustomViewProjection -1 and the default LocalTypeEnum.Custom, and queued
        // with isPersistent false, so they pass HudBillboardProjector's supported-primitive test. What
        // they do not carry at queue time is geometry: BillBoardUtils writes the vertex data into a
        // parallel list and the master transfers it into the pooled billboards afterwards, in
        // BillBoardUtils.FinishDraw, once every client has queued. A projector that reads
        // billboard.Position* in the AddBillboard prefix therefore reads whatever the pool slot held
        // when it was last used, not this frame's geometry - which is why these scopes are also
        // capture boundaries, and HudRichHudQuadCapture reads that parallel list instead.
        private static int TryPatchRichHud(Assembly[] assemblies)
        {
            // LAST match, not first. Under SeamlessClient every server join loads a FRESH copy of
            // the master mod's script assembly without unloading the old one, so several assemblies
            // declare the master type at once and only the newest is the one the live session
            // registered with. Binding the first match wired the capture and the master emitter
            // patches to a dead copy whose buffers are never written: clients tagged their draws,
            // the projector swallowed them fail-closed, and every menu vanished from both outputs
            // with all capture counters at zero. AppDomain.GetAssemblies returns load order, so the
            // last declaring assembly is the newest copy; the scan re-runs on every assembly-set
            // change, so each transfer re-binds to the copy that just loaded. Stale patches on
            // older copies are inert - their emitters are never called again.
            Assembly master = null;
            int masterCopies = 0;
            foreach (Assembly assembly in assemblies)
            {
                if (SafeGetType(assembly, RichHudMasterType) == null) continue;
                master = assembly;
                masterCopies++;
            }
            if (master == null)
            {
                SetStatus("richhud-nav", "inactive", 0, "master-missing");
                Plugin.Log("RichHud scan: no assembly declares " + RichHudMasterType
                           + "; nav menu and cursor capture both inactive");
                return 0;
            }
            if (masterCopies > 1)
                Plugin.Log("RichHud scan: " + masterCopies + " assemblies declare the master"
                           + " (SeamlessClient reloads); binding the newest");

            // The master owns the intermediate 2D buffer and the matrix buffer that every embedded
            // client copy is handed a reference to, so one binding covers both emission routes.
            HudRichHudQuadCapture.Bind(master);

            // The master's own emitter statics, scoped as capture boundaries exactly like the
            // clients'. This is what brings the shared cursor onto the projected layer: TreeManager
            // draws it from the same BeginDraw/FinishDraw window as the client trees
            // (TreeManager.cs:184-238) but from the master's own subtree, so it is emitted with no
            // client frame on the stack and was previously unattributed - passing straight to the
            // backbuffer, where the composited RichHud layer then covered it.
            //
            // ResolveRichHudMaster declines whenever any scope is already current, so the master
            // emitters that run inside a client call keep the client's tag and its fail-closed
            // policy. HudRichHudQuadCapture additionally gates master capture on the menu having
            // actually projected this frame, so with nothing projected these patches resolve to a
            // pass-through and the cursor records with the menu as before.
            //
            // Both lookups here are assembly-qualified through SafeGetType, never FindType. That is
            // deliberate and load bearing: every client mod embeds a type with the SAME full name
            // RichHudFramework.UI.Rendering.BillBoardUtils, and FindType returns the first match
            // across all assemblies, so using it would patch some arbitrary client's statics a
            // second time instead of the master's.
            int masterEmitters = 0;
            int masterCandidates = 0;
            Type masterEmitterType = SafeGetType(master, RichHudEmitterType);
            bool finishDrawPatched = false;
            if (masterEmitterType != null)
            {
                foreach (MethodInfo method in masterEmitterType.GetMethods(
                             BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.DeclaredOnly))
                {
                    if (!method.Name.StartsWith("Add", StringComparison.Ordinal)
                        || method.ContainsGenericParameters) continue;
                    masterCandidates++;
                    if (PatchScope(method, ResolveRichHudMaster, false, false, true)) masterEmitters++;
                }

                // RichHud's frame-end boundary. TreeManager.DrawUI calls BillBoardUtils.FinishDraw
                // at :238, after DrawNodes has finished at :227 - so this postfix runs once per
                // frame, after every client and master draw callback. It is where the captured
                // cursor sprites are flushed, which is what makes the cursor land on top of the
                // projected menu without depending on the master's internal subtree draw order.
                MethodInfo finishDraw = FindMethod(masterEmitterType, "FinishDraw",
                    m => m.IsStatic && m.GetParameters().Length == 0);
                lock (Gate)
                {
                    finishDrawPatched = TryPatchLocked(finishDraw, null,
                        Hook(nameof(RichHudFrameEndPostfix)), null, "richhud frame-end patch");
                }
                if (finishDrawPatched) masterEmitters++;
            }

            // Scan-time, once per scan, on a cold path. The cursor path has now failed twice for
            // reasons that were indistinguishable from the outside, so the installation states it
            // reached rather than leaving the next live run to infer them: whether the master
            // assembly was identified and which one, whether its emitter type resolved, and how many
            // of its Add* statics actually took a patch. Read alongside the resolver-hits counter in
            // richhud-capture=, this separates "patch never installed" from "resolver declined".
            string masterName;
            try { masterName = master.GetName().Name; }
            catch { masterName = "<unnamed>"; }
            Plugin.Log("RichHud scan: master=" + masterName
                       + ", emitter-type=" + (masterEmitterType != null ? "found" : "MISSING")
                       + ", master-add-statics=" + masterEmitters + "/" + masterCandidates
                       + ", finish-draw=" + (finishDrawPatched ? "patched" : "MISSING"));

            int clients = 0;
            int count = masterEmitters;
            int textBoards = 0;
            bool gameCoreBound = false;
            int gameCoreTargets = 0;
            // The GameCore copy the gate binds, resolved the same way the master is above: LAST
            // match, i.e. newest under SeamlessClient. Deferred out of the loop deliberately.
            // Binding inside it rebound the gate once per copy, so with several copies loaded the
            // gate ended up describing whichever copy the enumeration happened to visit last while
            // every earlier copy's schema verdict was resolved and then thrown away - wasted
            // reflection, and a schema-broken state that did not necessarily belong to the binding
            // the draw path actually reads.
            Assembly gameCoreAssembly = null;
            foreach (Assembly assembly in assemblies)
            {
                if (assembly == master || SafeGetType(assembly, RichHudClientType) == null) continue;
                clients++;
                var scope = new RichHudClientScope(assembly);
                // Assembly-qualified for the same reason every other lookup here is: the client copy
                // of the framework has the same type names in every mod that ships it, and this one
                // has to be the assembly that also declares GameCore's session state.
                bool gameCore = SafeGetType(assembly, GameCoreSessionType) != null;
                if (gameCore)
                {
                    gameCoreAssembly = assembly;
                    gameCoreBound = true;
                }
                int assemblyTargets = 0;

                Type emitters = SafeGetType(assembly, RichHudEmitterType);
                if (emitters != null)
                {
                    foreach (MethodInfo method in emitters.GetMethods(
                                 BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.DeclaredOnly))
                    {
                        if (!method.Name.StartsWith("Add", StringComparison.Ordinal)
                            || method.ContainsGenericParameters) continue;
                        if (PatchScope(method, scope.Resolve, false, false, true)) assemblyTargets++;
                    }
                }

                Type textBoard = SafeGetType(assembly, RichHudClientTextBoardType);
                if (textBoard != null)
                {
                    foreach (MethodInfo method in textBoard.GetMethods(
                                 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.DeclaredOnly))
                    {
                        if (method.Name != "Draw" || method.IsAbstract
                            || method.ContainsGenericParameters) continue;
                        if (PatchScope(method, scope.Resolve, false, false, true))
                        {
                            assemblyTargets++;
                            textBoards++;
                        }
                    }
                }

                count += assemblyTargets;
                if (gameCore) gameCoreTargets += assemblyTargets;
            }

            // One bind, against the newest copy, after every copy has been seen.
            if (gameCoreAssembly != null) GameCorePdaGate.Bind(gameCoreAssembly);

            SetStatus("richhud-nav", count > 0 ? "partitioned" : "inactive", count,
                count > 0
                    ? "client-emitters+textboards scoped; clients=" + clients
                      + "; text-draws=" + textBoards
                      + "; master-emitters=" + masterEmitters + "; layer-projected"
                    : clients > 0 ? "emitter-missing" : "no-clients");

            // The PDA route is the same client scopes read through a different resolver answer, so
            // it is active only when both halves are in place: the gate bound to the mod's view
            // state, and the client's own emitters and text draws actually patched.
            bool gameCoreReady = gameCoreBound && gameCoreTargets > 0;
            SetStatus("gamecore", gameCoreReady ? "partitioned-partial" : "inactive", gameCoreTargets,
                gameCoreReady
                    ? "client scopes gated on PDA-open; all GameCore RichHud UI follows while open;"
                      + " hangar hologram is world geometry"
                    : gameCoreBound ? "emitter-missing"
                    : clients > 0 ? "session-type-missing" : "no-clients");
            SetStatus("richhud-pda", gameCoreReady ? "partitioned" : "inactive", gameCoreTargets,
                gameCoreReady ? "pda-gated client scopes" : "gate-unbound");
            return count;
        }

        // Maps one embedded framework copy to a route tag. The emitters are static, so a resolver has
        // no instance to walk back to the master's ModClient list; the name is read from the client
        // registrar in the same assembly instead, where it is the same string the master registers the
        // client under - RichHudClient.Init's modName, forwarded as ClientData.Item1.
        //
        // The read is deferred to the first scoped draw and latched only once the client reports
        // Registered. Reading it at patch time would be too early: CleanFeed scans an assembly as it
        // appears, and RichHudClient.Init runs later, from the mod's own session start. That is also
        // why ExceptionHandler.ModName is only a fallback - it is initialised to the session
        // component's debug name and only becomes the mod name once Init has run.
        private sealed class RichHudClientScope
        {
            private readonly Type _client;
            private readonly Type _handler;
            private HudRouteTag _tag;
            private bool _gameCore;
            private bool _resolved;

            internal RichHudClientScope(Assembly assembly)
            {
                _client = SafeGetType(assembly, RichHudClientType);
                _handler = SafeGetType(assembly, RichHudHandlerType);
            }

            internal HudRouteTag Resolve(object instance)
            {
                if (!Volatile.Read(ref _resolved))
                {
                    if (!(ReadStaticMember(_client, "Registered") as bool? ?? false)) return null;

                    string name = ReadClientName();
                    if (string.IsNullOrEmpty(name)) return null;

                    // Every other client passes through untagged, exactly as before this patch
                    // existed, so its primitives keep following whatever global route is in force.
                    if (string.Equals(name, "FlipAndBurn", StringComparison.OrdinalIgnoreCase))
                        _tag = RichHudEmitterTag();
                    else if (string.Equals(name, "GameCore", StringComparison.OrdinalIgnoreCase))
                        _gameCore = true;
                    Volatile.Write(ref _resolved, true);
                }

                // The latch settles WHICH client this assembly is, never whether it routes. Flip and
                // Burn's answer is fixed once and cached in _tag; GameCore's is conditional on the
                // PDA being open, so the gate is asked on every scoped draw and the route closes
                // again the moment the menu does.
                if (_gameCore) return GameCorePdaGate.PdaOpen ? GameCorePdaTag() : null;
                return _tag;
            }

            private string ReadClientName()
            {
                object registrar = ReadStaticMember(_client, "Instance");
                object clientData = ReadNamedMember(ReadNamedMember(registrar, "regMessage"), "Item1");
                return ReadNamedMember(clientData, "Item1") as string
                       ?? ReadStaticMember(_handler, "ModName") as string;
            }
        }

        // Fire-and-forget Text HUD clients - Flip and Burn among them - build their messages fresh
        // every frame with TimeToLive 2, so a backing object exists only for the frame that drew it
        // and the owner-field walk in EnsureOwnedBackingObjects never sees it. Tagging in the ctor
        // catches the instance while the provider's scoped Draw is still on the stack, which is the
        // only moment its route is knowable.
        private static int PatchConstructors(Type type, string label)
        {
            if (type == null) return 0;
            int count = 0;
            lock (Gate)
            {
                foreach (ConstructorInfo ctor in type.GetConstructors(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (TryPatchLocked(ctor, null, Hook(nameof(BackendCreatedPostfix)), null, label))
                        count++;
            }
            return count;
        }

        private static void BackendCreatedPostfix(object __instance)
        {
            if (__instance == null) return;
            HudRouteTag tag = HudRouteContext.Current;
            if (tag != null) HudRouteContext.Tag(__instance, tag);
        }

        private static void TryPatchHudLcd(Assembly[] assemblies)
        {
            int count = 0;
            foreach (Type type in FindTypes(assemblies, "Jawastew.HudLcd.HudLcd"))
                count += PatchAllNamed(null, type, "UpdateHUDMessage", ResolveHudLcd, true);
            SetStatus("hudlcd", count > 0 ? "partitioned" : "inactive", count,
                count > 0 ? "classic+marker" : "provider-missing");
        }

        // The whole Toolbar Info panel rides the toolbar key, background included. What that takes is
        // worth stating, because the panel has no per-frame draw of its own to scope.
        //
        // ToolbarLabelRender registers UPDATE_AFTER_SIM only. It never draws: it builds five
        // persistent Text HUD billboards - the plate, the top and bottom strips and the two rounded
        // corners, materials BuildInfo_UI_Square and BuildInfo_UI_Corner - alongside the label text
        // packages, and leaves them Visible for the Text HUD API mod's own render loop to emit. So
        // the three methods below are patched for what they CREATE and MUTATE, not for what they
        // paint: TextAPIDetected builds the panel, UpdateAfterSim carries the visibility, position
        // and opacity passes, UpdateRender sizes the plates against the measured text. The backing
        // objects those calls produce inherit the toolbar tag, and the shared backend's own scoped
        // Draw resolves it back when the panel is finally emitted.
        //
        // The background is genuinely routable and is expected to follow the filter, not merely to
        // be attempted: the backend emits each plate through the singular
        // MyTransparentGeometry.AddBillboard with isPersistent false, LocalType Custom and
        // CustomViewProjection -1, on a camera-anchored screen-space quad, blended PostPP - the
        // supported shape all the way down, and not the additive family the projector declines.
        //
        // TextAPIDetected in particular is why the backing-object walk runs on scope EXIT as well as
        // entry. Its entire job is to construct the panel, so at prefix time every field holding it
        // is still null.
        private static void TryPatchBuildInfo(Assembly[] assemblies)
        {
            int count = 0;
            foreach (Type type in FindTypes(
                         assemblies, "Digi.BuildInfo.Features.ToolbarInfo.ToolbarLabelRender"))
            {
                count += PatchAllNamed(null, type, "TextAPIDetected", _ => DefaultTag("toolbar"), true);
                count += PatchAllNamed(null, type, "UpdateAfterSim", _ => DefaultTag("toolbar"), true);
                count += PatchAllNamed(null, type, "UpdateRender", _ => DefaultTag("toolbar"), true);
            }
            SetStatus("buildinfo-toolbar", count > 0 ? "partitioned" : "inactive", count,
                count > 0 ? "toolbar-profile; persistent-texthud; labels+background" : "provider-missing");
        }

        private static void TryPatchProvider(Assembly[] assemblies, string key, string typeName, string method)
            => TryPatchProviderMethods(assemblies, key, typeName, method);

        private static void TryPatchProviderMethods(
            Assembly[] assemblies, string key, string typeName, params string[] methods)
        {
            int count = 0;
            foreach (Type type in FindTypes(assemblies, typeName))
            foreach (string name in methods)
                count += PatchAllNamed(
                    null, type, name, _ => DefaultTag(key), true,
                    string.Equals(key, "flight-hud", StringComparison.OrdinalIgnoreCase));
            SetStatus(key, count > 0 ? "partitioned-partial" : "inactive", count,
                count > 0 ? "source-scoped; world-lines/triangles pass" : "provider-missing");
        }

        private static int PatchAllNamed(
            string statusKey, Type type, string methodName, Func<object, HudRouteTag> resolver,
            bool alwaysScope = false, bool sealRetained = false)
        {
            if (type == null) return 0;
            int count = 0;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static
                                                           | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != methodName || method.IsAbstract || method.ContainsGenericParameters) continue;
                if (PatchScope(method, resolver, alwaysScope, sealRetained)) count++;
            }
            if (statusKey != null)
                SetStatus(statusKey, count > 0 ? "partitioned" : "inactive", count,
                    count > 0 ? "source-scoped" : "target-missing");
            return count;
        }

        // Single registration path for every kind of adapter patch. Callers differ only in which
        // Harmony methods they install and what they log.
        private static bool TryPatchLocked(
            MethodBase method, HarmonyMethod prefix, HarmonyMethod postfix, HarmonyMethod finalizer,
            string label)
        {
            if (method == null || _harmony == null) return false;
            if (Patched.Contains(method)) return true;
            try
            {
                _harmony.Patch(method, prefix: prefix, postfix: postfix, finalizer: finalizer);
                Patched.Add(method);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log(label + " skipped " + method.DeclaringType?.FullName + "." + method.Name
                           + ": " + ex.GetType().Name + " - " + ex.Message);
                return false;
            }
        }

        private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(HudAdapterCoordinator), name);

        private static bool PatchScope(
            MethodBase method,
            Func<object, HudRouteTag> resolver,
            bool alwaysScope,
            bool sealRetained,
            bool captureRichHud = false)
        {
            if (method == null) return false;
            lock (Gate)
            {
                if (Patched.Contains(method))
                {
                    MergeScopeLocked(method, alwaysScope, sealRetained, captureRichHud);
                    return true;
                }

                PublishScopeLocked(method,
                    new ScopeDescriptor(resolver, alwaysScope, sealRetained, captureRichHud));
                if (TryPatchLocked(method, Hook(nameof(ScopePrefix)), null,
                        Hook(nameof(ScopeFinalizer)), "compat patch"))
                    return true;
                PublishScopeLocked(method, null);
                return false;
            }
        }

        private static bool PatchFactory(MethodBase method)
        {
            lock (Gate)
                return TryPatchLocked(method, null, Hook(nameof(FactoryPostfix)), null,
                    "Text HUD factory patch");
        }

        private static bool PatchMenuFrameSeal(MethodBase method)
        {
            lock (Gate)
                return TryPatchLocked(method, null, null, Hook(nameof(MenuFrameFinalizer)),
                    "interactive frame seal patch");
        }

        // Copy-on-write so ScopePrefix can read without a lock. Always called under Gate.
        private static void PublishScopeLocked(MethodBase method, ScopeDescriptor descriptor)
        {
            var copy = new Dictionary<MethodBase, ScopeDescriptor>(Volatile.Read(ref _scopes));
            if (descriptor == null) copy.Remove(method);
            else copy[method] = descriptor;
            Volatile.Write(ref _scopes, copy);
        }

        private static void MergeScopeLocked(
            MethodBase method, bool alwaysScope, bool sealRetained, bool captureRichHud)
        {
            if (!Volatile.Read(ref _scopes).TryGetValue(method, out ScopeDescriptor existing)) return;
            ScopeDescriptor merged = existing.Merge(alwaysScope, sealRetained, captureRichHud);
            if (!ReferenceEquals(merged, existing)) PublishScopeLocked(method, merged);
        }

        private static Exception MenuFrameFinalizer(Exception __exception)
        {
            if (__exception == null)
            {
                HudModalState.CompleteGuiFrame();
                HudNamedSpriteRoutes.SealInteractiveFrame();
            }
            return __exception;
        }

        // Fails open. The resolvers read arbitrary mod objects through reflection and the walk below
        // enumerates arbitrary mod collections, so nothing on this path is provably non-throwing -
        // and it runs on every patched draw. An escaping exception would be raised out of the
        // patched method itself, breaking the very HUD source CleanFeed exists to route, so a fault
        // instead leaves __state inactive: the finalizer no-ops and the original call runs
        // unscoped, exactly as it did before the patch was installed.
        private static void ScopePrefix(MethodBase __originalMethod, object __instance, out ScopeState __state)
        {
            __state = default(ScopeState);
            try
            {
                if (!Volatile.Read(ref _scopes).TryGetValue(__originalMethod, out ScopeDescriptor descriptor))
                    return;

                bool intercepting = HudRedirector.SelectiveInterceptionActive;
                if (!intercepting && !descriptor.AlwaysScope) return;
                HudRouteTag tag = descriptor.Resolver?.Invoke(__instance);
                if (tag == null) return;
                // MyGuiScreenTerminal.Draw is patched first by the native category scan, so its
                // resolver is the terminal-specific one and never reaches
                // ResolveInteractiveScreen's observation. Static tooltip scopes have no instance.
                if (__instance != null
                    && string.Equals(tag.Key, "terminal", StringComparison.OrdinalIgnoreCase))
                    HudModalState.ObserveInteractiveScreen();
                if (descriptor.AlwaysScope && intercepting)
                {
                    EnsureOwnedBackingObjects(__instance, tag);
                    __state.BackingOwner = __instance;
                }

                __state.Previous = HudRouteContext.Push(tag);
                __state.Current = tag;
                __state.SealRetained = descriptor.SealRetained;
                __state.Active = true;
                if (descriptor.CaptureRichHud)
                    __state.Capturing = HudRichHudQuadCapture.BeginScope(tag, out __state.CaptureStart);
            }
            catch (Exception ex)
            {
                // A push that already happened is undone here rather than left to the finalizer:
                // the state is about to be cleared, so nothing else would ever restore it and the
                // thread would carry a stale tag for the rest of the frame.
                if (__state.Active)
                {
                    try { HudRouteContext.Restore(__state.Previous); }
                    catch { }
                }
                __state = default(ScopeState);
                NoteScopeFault(ref _prefixFaultLogged, "scope prefix", ex);
            }
        }

        // Restoring the route context is the one thing that must happen, so it runs in a finally
        // and the three calls before it are guarded individually. Each of them reaches code that
        // can fault on foreign data - RichHud's shared buffers, a held triangle, the retained-route
        // seal - and losing the restore would leave this thread's [ThreadStatic] tag pointing at a
        // finished scope, silently misrouting every later draw on the frame.
        private static Exception ScopeFinalizer(
            MethodBase __originalMethod, Exception __exception, ScopeState __state)
        {
            // The exception is handed straight back to Harmony, which rethrows it with a plain
            // `throw` - and that RESETS the stack trace to the rethrow site inside the generated
            // replacement. Everything the provider's own body was doing when it faulted is erased,
            // and what the game logs instead is a one-frame trace naming <Method>_Patch1, which
            // reads as "CleanFeed's patch threw" when CleanFeed only carried the exception past.
            // That misattribution cost a whole investigation on 2026-08-14: 2327 Text HUD faults
            // logged with ModHUDMessage.Draw_Patch1 as the innermost frame while 7505 instances of
            // the SAME fault, on an unpatched sibling path, carried the full trace and named the
            // real site. The trace is intact HERE, before the rethrow, so it is recorded once per
            // distinct method and exception type. Bounded because a faulting provider faults at
            // HUD rates; the counter carries the volume.
            if (__exception != null) NoteProviderFault(__originalMethod, __exception);

            if (__state.Active)
            {
                try
                {
                    // Before the route context is restored, and on the exception path too: whatever
                    // the call appended to RichHud's shared buffer was already queued and swallowed,
                    // so it has to be projected or explicitly accounted as dropped either way.
                    if (__state.Capturing)
                    {
                        try { HudRichHudQuadCapture.EndScope(__state.CaptureStart, __state.Current); }
                        catch (Exception ex)
                        { NoteScopeFault(ref _captureEndFaultLogged, "richhud capture end", ex); }
                    }

                    try { HudBillboardProjector.FlushPendingTriangle(HudRouteContext.Current); }
                    catch (Exception ex)
                    { NoteScopeFault(ref _triangleFlushFaultLogged, "pending triangle flush", ex); }

                    // Second half of the walk pair, and the only chance to attribute anything this
                    // call CREATED. The bridged BuildInfo toolbar panel is the case that made this
                    // necessary: its background plates and corners are persistent Text HUD
                    // billboards built inside the scoped TextAPI-detected handler, so at prefix
                    // time the fields holding them are still null and the entry walk attributes
                    // nothing. The stamp caps the pair, so this costs one walk per owner and route
                    // rather than one per draw. Guarded like its siblings: the walk reads arbitrary
                    // mod fields and a fault here must not escape into the provider's own method.
                    if (__state.BackingOwner != null)
                    {
                        try { EnsureOwnedBackingObjects(__state.BackingOwner, __state.Current); }
                        catch (Exception ex)
                        { NoteScopeFault(ref _backingWalkFaultLogged, "backing walk", ex); }
                    }

                    if (__state.SealRetained)
                    {
                        try { HudNamedSpriteRoutes.Seal(__state.Current); }
                        catch (Exception ex)
                        { NoteScopeFault(ref _sealFaultLogged, "retained route seal", ex); }
                    }
                }
                finally { HudRouteContext.Restore(__state.Previous); }
            }
            return __exception;
        }

        // Swallowed faults on the scope path are bounded capability losses, not faults the user can
        // act on, and they can recur at HUD rates - so each site reports once per session and the
        // counters in the source registry carry the volume, matching HudBillboardProjector's
        // once-per-session drop notice.
        private static int _prefixFaultLogged;
        private static int _captureEndFaultLogged;
        private static int _triangleFlushFaultLogged;
        private static int _sealFaultLogged;
        private static int _enumerationFaultLogged;
        private static int _backingWalkFaultLogged;

        private static void NoteScopeFault(ref int latch, string stage, Exception ex)
        {
            if (Interlocked.Exchange(ref latch, 1) != 0) return;
            Plugin.Log("compat " + stage + " failed and was swallowed: "
                       + ex.GetType().Name + " - " + ex.Message);
        }

        // Distinct method-and-exception pairs already reported, capped so a mod that throws every
        // frame from many methods cannot turn the log into its own crash dump.
        private const int ProviderFaultReportCap = 6;
        private static readonly object ProviderFaultGate = new object();
        private static readonly HashSet<string> ProviderFaultsReported =
            new HashSet<string>(StringComparer.Ordinal);
        private static long _providerFaults;

        private static void NoteProviderFault(MethodBase method, Exception ex)
        {
            Interlocked.Increment(ref _providerFaults);
            string site;
            try
            {
                site = (method?.DeclaringType?.FullName ?? "<unknown>") + "."
                       + (method?.Name ?? "<unknown>") + " / " + ex.GetType().FullName;
            }
            catch { site = "<unknown> / " + ex.GetType().Name; }

            lock (ProviderFaultGate)
            {
                if (ProviderFaultsReported.Count >= ProviderFaultReportCap) return;
                if (!ProviderFaultsReported.Add(site)) return;
            }

            string trace;
            try { trace = ex.StackTrace; }
            catch { trace = null; }
            Plugin.Log("patched provider method threw and the exception is being returned to it"
                       + " unchanged (CleanFeed neither raised nor swallowed it); Harmony's rethrow"
                       + " will reset the trace, so the original is recorded here once: " + site
                       + " - " + ex.Message
                       + (string.IsNullOrEmpty(trace) ? string.Empty : Environment.NewLine + trace));
        }

        private static void FactoryPostfix(object __result)
        {
            if (__result == null) return;
            HudRouteTag tag = HudRouteContext.Current;
            if (tag != null) HudRouteContext.Tag(__result, tag);
            else HudSourceDiscovery.TryAttributeCurrentCall(__result);
        }

        private static HudRouteTag ResolveTagged(object instance) => HudRouteContext.Lookup(instance);

        private static HudRouteTag ResolveGps(object instance)
        {
            object value = GpsPointType.Read(instance);
            if (value == null) return null;
            try { return Convert.ToInt32(value) == 10 ? DefaultTag("gps") : null; }
            catch { return value.ToString().IndexOf("GPS", StringComparison.OrdinalIgnoreCase) >= 0
                    ? DefaultTag("gps") : null; }
        }

        // Scopes a whole player-panel MyStatControls.Draw under battery-fuel, so everything the panel
        // draws - plate art, icon row, broadcast readout, dampeners, and the bars that carry no stat
        // ID CleanFeed recognises - routes as one object. Individual controls that do carry a mapped
        // stat ID still push their own tag from their own Draw, so `speed` keeps overriding inside the
        // panel rather than being swallowed by it.
        private static HudRouteTag ResolveStatPanel(object instance)
        {
            if (instance == null) return null;
            StatHostInfo info;
            lock (StatHostGate)
            {
                if (!_statHosts.TryGetValue(instance, out info))
                {
                    info = new StatHostInfo();
                    InspectStatHost(instance, info);
                    _statHosts.Add(instance, info);
                }
            }
            // Toolbar wins if an instance somehow carried both.
            if (info.IsToolbar) return DefaultTag("toolbar");
            return info.IsPanel ? DefaultTag("battery-fuel") : null;
        }

        private static void InspectStatHost(object instance, StatHostInfo info)
        {
            object bindings = ReadNamedMember(instance, "m_bindings", "Bindings");
            if (!(bindings is IEnumerable enumerable)) return;
            // The enumeration itself is guarded, not just the per-element reads: this is a
            // collection reached by reflection off an object whose shape CleanFeed only assumes,
            // and GetEnumerator/MoveNext on it are foreign code. A fault leaves the partial
            // classification collected so far, which is the same fail-quiet answer an unrecognised
            // host already gets.
            try
            {
                foreach (object binding in enumerable)
                {
                    object style = ReadNamedMember(binding, "Style");
                    object control = ReadNamedMember(binding, "Control");
                    string id = ReadNamedMember(style, "StatId")?.ToString();
                    if (string.IsNullOrEmpty(id)) continue;
                    if (StatPanelIds.Contains(id)) info.IsPanel = true;
                    if (ToolbarClusterIds.Contains(id)) info.IsToolbar = true;
                    HudRouteTag tag = ResolveStatId(id);
                    if (control != null && tag != null) HudRouteContext.Tag(control, tag);
                }
            }
            catch (Exception ex)
            { NoteScopeFault(ref _enumerationFaultLogged, "stat host enumeration", ex); }
        }

        private static HudRouteTag ResolveStatId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            id = id.ToLowerInvariant();
            if (id == "controlled_speed") return DefaultTag("speed");
            switch (id)
            {
                case "controlled_estimated_time_remaining_energy":
                case "controlled_power_usage":
                case "controlled_hydrogen_capacity":
                case "player_energy":
                case "player_hydrogen":
                    return DefaultTag("battery-fuel");
                default: return null;
            }
        }

        private static HudRouteTag ResolveHudLcd(object instance)
        {
            object panel = HudLcdPanel.Read(instance);
            string id = PanelEntityId.Read(panel)?.ToString();
            string customData = PanelCustomData.Read(panel) as string;
            ParseHudLcdMarker(customData, out bool? recording, out bool? playerVisible);
            return new HudRouteTag("hudlcd", id, recording, playerVisible);
        }

        // Only the stamp check and refresh run under OwnerTagGate. The walk itself reads arbitrary
        // mod fields and enumerates arbitrary mod IEnumerables - foreign code of unbounded duration
        // running on the render thread - and must never execute while a lock every scoped draw can
        // contend on is held.
        //
        // Stamping before the walk means a second thread arriving between the stamp and the walk's
        // completion sees a current stamp and skips, while a thread that arrived just before the
        // stamp can walk the same owner concurrently. Both outcomes are safe: HudRouteContext.Tag is
        // idempotent, so a duplicate walk only re-tags the same backing objects with the same tag,
        // and a skipped walk is one that another thread is already performing for the same tag.
        // A walk that attributed NOTHING has not settled anything, and must not spend the owner's
        // walk allowance - this is what made the toolbar panel's background evade routing entirely.
        //
        // The provider's HUD objects do not exist when its component registers. BuildInfo's
        // ToolbarLabelRender builds the panel from its TextAPI-detected handler, which fires once per
        // session on a handshake with a separate mod, while the UpdateAfterSim this walk rides runs
        // from the tick the component registers. Both walks were therefore spent on the first scoped
        // tick, against an instance whose every message field was still null, and nothing ever
        // re-armed the stamp: a stamp keyed on the activation generation only resets when the
        // redirect is re-enabled. The persistent background billboards are created exactly once and
        // never again, so nothing else could recover them either.
        //
        // So a barren walk sets a retry deadline instead of counting, and the pair is only spent on
        // walks that actually tagged something. The deadline is what bounds the cost: an owner that
        // genuinely owns no backing objects is re-walked at most once per BarrenWalkRetry, not once
        // per draw, and the interception gate above means none of this runs at all until a redirect
        // is armed.
        private static void EnsureOwnedBackingObjects(object owner, HudRouteTag tag)
        {
            if (owner == null || tag == null) return;
            int generation = HudRedirector.ActivationGeneration;
            long now = Stopwatch.GetTimestamp();
            lock (OwnerTagGate)
            {
                if (!_ownerTags.TryGetValue(owner, out OwnerTagStamp stamp))
                {
                    stamp = new OwnerTagStamp();
                    _ownerTags.Add(owner, stamp);
                }

                bool sameRoute = stamp.Generation == generation
                                 && string.Equals(stamp.Key, tag.Key, StringComparison.OrdinalIgnoreCase)
                                 && stamp.RecordingOverride == tag.RecordingOverride
                                 && stamp.PlayerVisibleOverride == tag.PlayerVisibleOverride;
                if (sameRoute)
                {
                    if (stamp.Walks >= WalksPerOwnerRoute) return;
                    if (stamp.Walks == 0 && stamp.NextBarrenWalk != 0 && now < stamp.NextBarrenWalk)
                        return;
                }
                else
                {
                    stamp.Generation = generation;
                    stamp.Key = tag.Key;
                    stamp.RecordingOverride = tag.RecordingOverride;
                    stamp.PlayerVisibleOverride = tag.PlayerVisibleOverride;
                    // A route change starts the pair over: the owner's objects have to be restamped
                    // with the new tag, and both walks are owed again on the new route.
                    stamp.Walks = 0;
                    stamp.BarrenWalks = 0;
                    stamp.NextBarrenWalk = 0;
                }
            }

            // Deliberately outside the lock, as before: the walk reads arbitrary mod fields and
            // enumerates arbitrary mod collections, and holding a shared gate across foreign code is
            // how a deadlock gets built. Tagging is idempotent, so two threads racing the same owner
            // costs a duplicate walk and nothing else.
            int tagged = WalkOwnedFields(owner, tag);
            tagged += TagBuildInfoBackgrounds(owner, tag, out bool backgroundsPending);

            lock (OwnerTagGate)
            {
                if (!_ownerTags.TryGetValue(owner, out OwnerTagStamp stamp)) return;
                // A walk that attributed the labels but not the plates would otherwise settle the
                // owner and leave the background recording forever - the labels are built in the
                // same handler but the toolbar panel is the element whose absence is silent, so it
                // gets an explicit say in whether this owner is finished.
                if (tagged > 0 && !backgroundsPending)
                {
                    stamp.Walks++;
                    stamp.BarrenWalks = 0;
                    stamp.NextBarrenWalk = 0;
                    Interlocked.Add(ref _backingTagged, tagged);
                }
                else
                {
                    int shift = Math.Min(stamp.BarrenWalks, BarrenWalkBackoffShifts);
                    stamp.BarrenWalks++;
                    stamp.NextBarrenWalk = now + (BarrenWalkRetryTicks << shift);
                    Interlocked.Increment(ref _barrenWalks);
                }
            }
        }

        private static int WalkOwnedFields(object owner, HudRouteTag tag)
        {
            var visited = new HashSet<object>(ReferenceComparer.Instance);
            int budget = BackingWalkMaxNodes;
            int tagged = 0;
            Assembly ownerAssembly = owner.GetType().Assembly;
            Type type = owner.GetType();
            while (type != null)
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Static
                                                           | BindingFlags.Public | BindingFlags.NonPublic
                                                           | BindingFlags.DeclaredOnly))
                {
                    object value;
                    try { value = field.GetValue(owner); }
                    catch { continue; }
                    WalkForBacking(value, tag, ownerAssembly, 0, visited, ref budget, ref tagged);
                }
                type = type.BaseType;
            }
            return tagged;
        }

        // Type-targeted backstop for the bridged BuildInfo toolbar panel, and the counter that proves
        // from a log line whether its background was attributed at all.
        //
        // The general walk should reach these - they are direct fields of the scoped owner - but the
        // panel's background is the one element whose failure is silent and whose creation happens
        // once per session, so it gets a path that owes nothing to the node budget, the depth limit
        // or field ordering. Reading six named fields off one known type costs nothing next to the
        // walk it rides along with, and buildinfo-bg-tagged reading zero on a live run says the
        // wrapper objects themselves are absent rather than that the traversal missed them.
        //
        // Gated on the exact type name first, so no other provider is ever probed for these members.
        private static int TagBuildInfoBackgrounds(object owner, HudRouteTag tag, out bool pending)
        {
            pending = false;
            if (!string.Equals(owner.GetType().FullName, BuildInfoToolbarRenderType, StringComparison.Ordinal))
                return 0;

            int tagged = 0;
            foreach (NamedMemberCache member in BuildInfoBackgroundMembers)
                tagged += TagBackingOf(member.Read(owner), tag);

            // The plates are also held in a list, which is what the panel iterates for its opacity
            // and colour passes. Same objects, so this only matters if a future layout moves one out
            // of its own field.
            if (BuildInfoBackgroundList.Read(owner) is IEnumerable list)
            {
                try
                {
                    foreach (object item in list) tagged += TagBackingOf(item, tag);
                }
                catch (Exception ex)
                { NoteScopeFault(ref _enumerationFaultLogged, "backing walk enumeration", ex); }
            }

            // The panel is this owner's whole reason for being bridged, so "the owner exists but its
            // plates do not yet" is a walk that has finished nothing. Reported so the caller can
            // leave the allowance unspent and come back rather than settling on a partial answer.
            pending = tagged == 0;
            if (tagged > 0) Interlocked.Add(ref _buildInfoBackgroundsTagged, tagged);
            return tagged;
        }

        private static int TagBackingOf(object wrapper, HudRouteTag tag)
        {
            if (wrapper == null) return 0;
            object backing = BackingObject.Read(wrapper);
            if (backing == null) return 0;
            HudRouteContext.Tag(backing, tag);
            return 1;
        }

        // Finds the Text HUD backing objects a provider owns, so later draws from the shared backend
        // can be attributed to it. A backing object can sit behind a provider's own wrapper type, not
        // just in a direct field - Flight HUD keeps its compass ticker in a List<CompassDivisionClass>
        // whose elements each hold a HUDMessage - so the search has to walk.
        //
        // The walk is bounded three ways: a node budget, a depth limit, and - for anything that is not
        // a collection - a restriction to types declared in the provider's own assembly, so it can
        // never wander into the engine's object graph. It runs once per owner per activation
        // generation, not per frame.
        private static void WalkForBacking(
            object value, HudRouteTag tag, Assembly ownerAssembly, int depth,
            HashSet<object> visited, ref int budget, ref int tagged)
        {
            if (value == null || budget <= 0 || depth > BackingWalkMaxDepth) return;
            if (value is string) return;
            Type type = value.GetType();
            if (type.IsValueType) return;
            if (!visited.Add(value)) return;
            budget--;

            object backing = BackingObject.Read(value);
            if (backing != null)
            {
                HudRouteContext.Tag(backing, tag);
                tagged++;
                return;
            }

            // Collections are traversed whatever assembly they come from - the interesting case is a
            // BCL List<T> holding the provider's own elements.
            if (value is IEnumerable enumerable && !(value is IDictionary))
            {
                // Guarded around the enumeration, not inside it: this is an arbitrary mod
                // collection whose GetEnumerator/MoveNext are foreign code, and a lazy or
                // self-modifying sequence can throw part way through. The tags already applied
                // stand and the walk simply stops here - the same outcome as running out of budget.
                try
                {
                    foreach (object item in enumerable)
                    {
                        if (budget <= 0) break;
                        WalkForBacking(item, tag, ownerAssembly, depth + 1, visited, ref budget, ref tagged);
                    }
                }
                catch (Exception ex)
                { NoteScopeFault(ref _enumerationFaultLogged, "backing walk enumeration", ex); }
                return;
            }

            if (type.Assembly != ownerAssembly) return;

            // Fields only: property getters on mod types can have side effects.
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance
                                                       | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (budget <= 0) break;
                object child;
                try { child = field.GetValue(value); }
                catch { continue; }
                WalkForBacking(child, tag, ownerAssembly, depth + 1, visited, ref budget, ref tagged);
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static void ParseHudLcdMarker(
            string customData,
            out bool? recording,
            out bool? playerVisible)
        {
            recording = null;
            playerVisible = null;
            if (string.IsNullOrEmpty(customData)) return;
            foreach (string rawLine in customData.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("hudlcd", StringComparison.OrdinalIgnoreCase)) continue;
                string[] fields = line.Split(':');
                for (int i = 6; i < fields.Length; i++)
                {
                    string field = fields[i].Trim();
                    if (field.Equals("cleanfeed=player", StringComparison.OrdinalIgnoreCase)) recording = false;
                    if (field.Equals("cleanfeed=record", StringComparison.OrdinalIgnoreCase)) recording = true;
                    if (field.Equals("cleanfeed=hidden", StringComparison.OrdinalIgnoreCase)) playerVisible = false;
                    if (field.Equals("cleanfeed=shown", StringComparison.OrdinalIgnoreCase)) playerVisible = true;
                }
            }
        }

        // Cold-path member probe. Draw-path members go through a NamedMemberCache instead.
        private static object ReadNamedMember(object instance, params string[] names)
        {
            if (instance == null) return null;
            Type type = instance.GetType();
            while (type != null)
            {
                foreach (string name in names)
                {
                    FieldInfo field = type.GetField(name, NamedMemberCache.Flags);
                    if (field != null)
                    {
                        try { return field.GetValue(instance); } catch { }
                    }
                    PropertyInfo property = type.GetProperty(name, NamedMemberCache.Flags);
                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        try { return property.GetValue(instance, null); } catch { }
                    }
                }
                type = type.BaseType;
            }
            return null;
        }

        // Cold-path static member probe, the counterpart to ReadNamedMember. Kept separate because
        // NamedMemberCache.Flags is instance-only by design: nothing on a draw path reads a static.
        private static object ReadStaticMember(Type type, string name)
        {
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                FieldInfo field = type.GetField(name, flags);
                if (field != null) return field.GetValue(null);
                PropertyInfo property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(null, null);
            }
            catch { }
            return null;
        }

        // Returns the first assembly that declares the type. Callers that must distinguish between
        // several assemblies declaring the same full name have to iterate with SafeGetType instead.
        // LAST declaring assembly wins, for the same reason the RichHud master selection takes the
        // last match: SeamlessClient loads a fresh copy of every mod script assembly on each server
        // join without unloading the old one, so a mod-owned type name resolves in several
        // assemblies at once and only the newest copy is live. First-match patched dead copies -
        // the live producer then ran unpatched, which for a filtered source is a silent recording
        // leak, not just a cosmetic miss. Game types are declared by exactly one assembly, so this
        // changes nothing for them. The scan re-runs on every assembly-set change, and PatchScope
        // dedupes by MethodBase, so each transfer's fresh copy is patched as it appears.
        private static Type FindType(IEnumerable<Assembly> assemblies, string fullName)
        {
            Type newest = null;
            foreach (Assembly assembly in assemblies)
            {
                Type type = SafeGetType(assembly, fullName);
                if (type != null) newest = type;
            }
            return newest;
        }

        // Every declaring assembly, in load order, for the patch sites - as opposed to FindType,
        // which answers the single-binding question ("which copy is live") and is right only where
        // ONE object or assembly has to be chosen.
        //
        // Patching is not that question. A patch on a dead copy is inert (its methods are never
        // called again), while an unpatched live copy is a silent recording leak, so the safe
        // answer for a patch site is "all of them" and newest-only is a gamble on the load-order
        // heuristic being right. It is not always right: the scan runs on an assembly-count poll,
        // and a SeamlessClient session can load two full mod sets between two polls - a lobby
        // server and then the sector server it hands off to. The 2026-08-14 22:26 and 22:28 bursts
        // did exactly that with no scan in between, so the 22:26 copy of every FindType-selected
        // provider was never patched at all: had that copy been the live one, every source it owns
        // would have recorded while the profile said player-only.
        //
        // PatchScope and TryPatchLocked both dedupe by MethodBase, so re-offering an already
        // patched copy on the next scan costs a hash lookup and nothing else.
        private static List<Type> FindTypes(IEnumerable<Assembly> assemblies, string fullName)
        {
            var found = new List<Type>();
            foreach (Assembly assembly in assemblies)
            {
                Type type = SafeGetType(assembly, fullName);
                if (type != null) found.Add(type);
            }
            return found;
        }

        private static Type SafeGetType(Assembly assembly, string fullName)
        {
            try { return assembly.GetType(fullName, false, false); }
            catch { return null; }
        }

        private static IEnumerable<Type> SafeTypes(IEnumerable<Assembly> assemblies)
        {
            foreach (Assembly assembly in assemblies)
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                foreach (Type type in types)
                    if (type != null) yield return type;
            }
        }

        private static bool IsGameAssembly(Assembly assembly)
        {
            string name;
            try { name = assembly.GetName().Name; }
            catch { return false; }
            return name == "Sandbox.Game" || name == "Sandbox.Graphics";
        }

        private static bool IsGuiAssembly(Assembly assembly)
        {
            string name;
            try { name = assembly.GetName().Name; }
            catch { return false; }
            return name == "Sandbox.Game" || name == "Sandbox.Graphics"
                   || name == "SpaceEngineers.Game";
        }

        private static HudRouteTag DefaultTag(string key)
        {
            lock (Gate)
            {
                if (!DefaultTags.TryGetValue(key, out HudRouteTag tag))
                {
                    tag = new HudRouteTag(key);
                    DefaultTags[key] = tag;
                }
                return tag;
            }
        }

        // The tag every RichHud client emitter that resolves to Flip and Burn is scoped under. It
        // carries the same policy key as the mod's own HUD - "flip-burn" - so the nav menu filters
        // with the rest of the mod, but it is deliberately a distinct instance and deliberately not
        // the DefaultTags entry for that key.
        //
        // The distinct instance is what lets the billboard layer tell RichHud-emitted content apart
        // from Flip and Burn's own HudAPI content under the same policy key. The two need different
        // handling under a non-Recorded route: RichHud queues the master's POOLED
        // MyTriangleBillboards and the vertex data is written afterwards, in BillBoardUtils
        // .FinishDraw, so the geometry a prefix can read at queue time is whatever the pool slot last
        // held - unprojectable from the billboard. HudBillboardProjector uses IsRichHudEmitterTag to
        // swallow that emission and let HudRichHudQuadCapture project the same draw from the
        // framework's own intermediate buffer instead, while ordinary flip-burn content keeps its
        // normal billboard projection path.
        //
        // Not cleared by Shutdown. A resolver latched before a shutdown can still hold this instance,
        // and resetting the field would make IsRichHudEmitterTag answer false for it - turning a
        // suppressed-and-projected draw back into the leak this exists to prevent.
        private static HudRouteTag _richHudEmitterTag;

        private static HudRouteTag RichHudEmitterTag()
        {
            lock (Gate)
            {
                HudRouteTag tag = Volatile.Read(ref _richHudEmitterTag);
                if (tag == null)
                {
                    tag = new HudRouteTag("flip-burn");
                    Volatile.Write(ref _richHudEmitterTag, tag);
                }
                return tag;
            }
        }

        // The tag every RichHud client emitter that resolves to GameCore is scoped under while its
        // Hand Terminal is open, under the mod's own policy key "gamecore". A distinct instance from
        // the Flip and Burn emitter tag for the same reason that one is distinct from its default
        // tag: the billboard layer keys the swallow-and-reproject path off tag identity, and the two
        // clients must be able to differ in policy - one filtered, the other not - while both take
        // that path.
        //
        // Not cleared by Shutdown, for the same reason as the emitter tag above: a latched resolver
        // can still hold this instance, and resetting the field would make IsRichHudEmitterTag
        // answer false for it - turning a suppressed-and-projected draw back into the leak this
        // exists to prevent.
        private static HudRouteTag _gameCorePdaTag;

        private static HudRouteTag GameCorePdaTag()
        {
            lock (Gate)
            {
                HudRouteTag tag = Volatile.Read(ref _gameCorePdaTag);
                if (tag == null)
                {
                    tag = new HudRouteTag("gamecore");
                    Volatile.Write(ref _gameCorePdaTag, tag);
                }
                return tag;
            }
        }

        // Both RichHud client emitter tags answer true: they share one capture and projection path,
        // and it is tag identity - not policy key - that tells that path a draw's geometry arrives
        // through the framework's intermediate buffer rather than on the billboards themselves.
        internal static bool IsRichHudEmitterTag(HudRouteTag tag)
            => tag != null
               && (ReferenceEquals(tag, Volatile.Read(ref _richHudEmitterTag))
                   || ReferenceEquals(tag, Volatile.Read(ref _gameCorePdaTag)));

        // The tag for RichHud MASTER content emitted outside any client call - in practice the
        // shared cursor, which TreeManager draws from the same interleaved pass as the client trees
        // but from the master's own subtree, so nothing client-side is ever on its stack.
        //
        // Force-routed PlayerOnly rather than keyed to any one client's policy. Two clients project
        // onto the layer now - the flip-burn nav menu and the GameCore PDA - and the cursor must
        // follow WHICHEVER menu is projected, not one mod's filter: keyed to "flip-burn", a
        // Recorded flip-burn sent the arrow to the backbuffer while a filtered PDA composited over
        // it, so the player lost the cursor and the recording gained it. The force route does not
        // capture the cursor unconditionally - HudRichHudQuadCapture's gate (a menu projected this
        // frame, or the layer holding content) still declines when nothing is projected, and a
        // declined cursor passes through to both outputs exactly as before. It remains a
        // deliberately distinct instance from _richHudEmitterTag because the two need OPPOSITE
        // failure directions. Menu content is the GPS-name dox vector and fails closed. A cursor
        // arrow reveals nothing, so it fails OPEN: if anything about its capture is not ready, it
        // passes through untouched rather than vanishing from the player's screen.
        // HudBillboardProjector keys that difference off which of the two tags is in scope.
        //
        // Not cleared by Shutdown, for the same reason as the emitter tag.
        private static HudRouteTag _richHudCursorTag;

        private static HudRouteTag RichHudCursorTag()
        {
            lock (Gate)
            {
                HudRouteTag tag = Volatile.Read(ref _richHudCursorTag);
                if (tag == null)
                {
                    tag = new HudRouteTag("flip-burn", forceRoute: HudEffectiveRoute.PlayerOnly);
                    Volatile.Write(ref _richHudCursorTag, tag);
                }
                return tag;
            }
        }

        internal static bool IsRichHudCursorTag(HudRouteTag tag)
            => tag != null && ReferenceEquals(tag, Volatile.Read(ref _richHudCursorTag));

        // Scopes the master's own emitters, and only when they are genuinely master-side: a null
        // current tag means no client draw and no other adapter's draw is on the stack.
        //
        // The null test is load bearing, not tidiness. The master's BillBoardUtils.AddTriangleData
        // also runs INSIDE a client TextBoard.Draw - that is how the nav menu's glyphs are emitted -
        // and pushing the fail-open cursor tag over the fail-closed menu tag there would turn a
        // failed menu capture into a pass-through of the menu's text into the recording.
        private static int _masterDeclineLogged;

        private static HudRouteTag ResolveRichHudMaster(object instance)
        {
            // Ticked at entry, before the decision, so a zero hit count in richhud-capture= means
            // the patches are not installed or ScopePrefix never reached the resolver - as opposed
            // to a non-zero hit count with zero tagged, which means the resolver ran and declined
            // because some other scope was already current.
            HudRichHudQuadCapture.NoteMasterResolver(false);
            HudRouteTag current = HudRouteContext.Current;
            if (current != null)
            {
                // One shot, first decline only. The key alone names whatever scope is standing in
                // front of the master's emitters: a detected.* key means dynamic discovery has
                // patched the framework's own draw loop again, which is what defeated the cursor
                // attribution before HudSourceDiscovery started excluding RichHud assemblies.
                if (Interlocked.Exchange(ref _masterDeclineLogged, 1) == 0)
                    Plugin.Log("RichHud master emitter ran inside an existing scope; current tag key="
                               + current.Key + " - the master's own draw loop should not be scoped by"
                               + " anything but its explicit adapter");
                return null;
            }
            HudRichHudQuadCapture.NoteMasterResolver(true);
            return RichHudCursorTag();
        }

        // A postfix rather than a finalizer: this must not run when FinishDraw threw, because a
        // frame that did not complete is one whose sprite bucket may not be in a state worth adding
        // to. The next frame's disarm discards the buffer instead. FlushCursor never throws, and
        // Invalidate is a field write - a frame boundary is the coarsest point at which the PDA's
        // open state can have changed without any draw having observed it yet.
        private static void RichHudFrameEndPostfix()
        {
            HudRichHudQuadCapture.FlushCursor();
            GameCorePdaGate.Invalidate();
        }

        // The VANILLA mouse cursor, which is the arrow the operator actually sees over the nav menu.
        //
        // The RichHud master's own cursor turned out never to emit outside a client scope
        // (resolver-hits in the thousands, resolver-tagged zero), so the cursor apparatus on the
        // master emitters is inert - kept because it still guards master-side text emission, at the
        // cost of one branch. The visible arrow is MyDX9Gui's, drawn as an ordinary GUI sprite with
        // no tag, which lands on the main player layer. The projected RichHud layer composites above
        // main by construction, so the menu covered it.
        //
        // The fix is purely about WHICH layer receives it. The tag forces PlayerOnly - the same
        // route untagged content already takes under a redirect, and the only state this resolver
        // ever fires in - and TargetFor maps it to the RichHud layer so it lands on top of the
        // projected menu. It can never suppress: a forced PlayerOnly route has no suppression path,
        // and the resolver returns null the moment the layer is empty, restoring today's behaviour
        // exactly.
        private static HudRouteTag _vanillaCursorTag;

        private static HudRouteTag VanillaCursorTag()
        {
            lock (Gate)
            {
                HudRouteTag tag = Volatile.Read(ref _vanillaCursorTag);
                if (tag == null)
                {
                    tag = new HudRouteTag("interactive", forceRoute: HudEffectiveRoute.PlayerOnly);
                    Volatile.Write(ref _vanillaCursorTag, tag);
                }
                return tag;
            }
        }

        internal static bool IsVanillaCursorTag(HudRouteTag tag)
            => tag != null && ReferenceEquals(tag, Volatile.Read(ref _vanillaCursorTag));

        // Non-null only while a projected menu is actually on the layer. With nothing projected the
        // cursor is untagged and routes exactly as it always has.
        private static HudRouteTag ResolveVanillaCursor(object instance)
        {
            if (!HudNamedSpriteRoutes.RichHudLayerHasContent) return null;
            HudRichHudQuadCapture.NoteVanillaCursorRouted();
            return VanillaCursorTag();
        }

        private static HudRouteTag SettingsTag()
        {
            lock (Gate)
            {
                const string key = "__cleanfeed-settings";
                if (!DefaultTags.TryGetValue(key, out HudRouteTag tag))
                {
                    tag = new HudRouteTag("terminal", forceRoute: HudEffectiveRoute.PlayerOnly);
                    DefaultTags[key] = tag;
                }
                return tag;
            }
        }

        internal static bool TryPatchDynamicScope(MethodBase method, HudRouteTag tag)
        {
            if (method == null || tag == null || _harmony == null) return false;
            lock (Gate)
            {
                if (Patched.Contains(method)) return false;
            }
            return PatchScope(method, _ => tag, true, false);
        }

        private static MethodInfo FindMethod(Type type, string name, Func<MethodInfo, bool> predicate)
        {
            if (type == null) return null;
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                   | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == name && predicate(m));
        }

        private static void SetStatus(string key, string capability, int targets, string detail)
            => HudSourceRegistry.SetExplicitStatus(key, capability, targets, detail);
    }

    // Resolves a named field or property once per concrete type and holds the MemberInfo. Draw-path
    // members must go through this: the GPS marker path reads one per marker per frame, and an
    // uncached probe walks the type hierarchy per read.
    internal sealed class NamedMemberCache
    {
        internal const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public
                                            | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

        private readonly string[] _names;
        private readonly object _gate = new object();
        private Dictionary<Type, Accessor> _cache = new Dictionary<Type, Accessor>();

        // A cache that resolves nothing silently degrades routing - the caller just sees null and
        // carries on - so the first failure per (type, name-set) is logged. The set is shared by
        // every cache instance and capped, so a hostile or merely unusual object graph cannot turn
        // the probe into a log flood.
        private const int MaxProbeFailureLogs = 64;
        private static readonly object ProbeLogGate = new object();
        private static readonly HashSet<string> ProbeFailures =
            new HashSet<string>(StringComparer.Ordinal);

        internal NamedMemberCache(params string[] names) { _names = names; }

        private sealed class Accessor
        {
            internal FieldInfo Field;
            internal PropertyInfo Property;

            internal object Read(object instance)
            {
                try
                {
                    if (Field != null) return Field.GetValue(instance);
                    if (Property != null) return Property.GetValue(instance, null);
                }
                catch { }
                return null;
            }
        }

        internal object Read(object instance)
        {
            if (instance == null) return null;
            Type type = instance.GetType();
            Dictionary<Type, Accessor> cache = Volatile.Read(ref _cache);
            if (!cache.TryGetValue(type, out Accessor accessor))
            {
                accessor = Resolve(type);
                if (accessor == null) NoteProbeFailure(type);
                lock (_gate)
                {
                    var copy = new Dictionary<Type, Accessor>(Volatile.Read(ref _cache)) { [type] = accessor };
                    Volatile.Write(ref _cache, copy);
                }
            }
            return accessor?.Read(instance);
        }

        // Resolution order is part of the contract: nearest declaring type first, and within a type
        // each candidate name as a field before a property.
        private Accessor Resolve(Type type)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                foreach (string name in _names)
                {
                    FieldInfo field = current.GetField(name, Flags);
                    if (field != null) return new Accessor { Field = field };
                    PropertyInfo property = current.GetProperty(name, Flags);
                    if (property != null && property.GetIndexParameters().Length == 0)
                        return new Accessor { Property = property };
                }
            }
            return null;
        }

        private void NoteProbeFailure(Type type)
        {
            string names = string.Join("/", _names);
            string key = (type.FullName ?? type.Name) + "|" + names;
            lock (ProbeLogGate)
            {
                if (ProbeFailures.Count >= MaxProbeFailureLogs) return;
                if (!ProbeFailures.Add(key)) return;
            }
            Plugin.Log("HUD member probe failed: " + (type.FullName ?? type.Name)
                       + " has none of " + names);
        }
    }
}
