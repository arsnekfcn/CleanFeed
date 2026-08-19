using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace CleanFeed.Compatibility
{
    // Answers one question for the RichHud client resolver: is GameCore's Hand Terminal (the M-key
    // PDA) open right now.
    //
    // WHY THE ROUTE IS CONDITIONAL
    //
    // GameCore draws its whole RichHud tree through one registered client, and RichHud attributes a
    // draw to a client by which assembly emitted it, not by which window the node belongs to - the
    // master flattens every client's subtree into one interleaved pass. So the finest attribution
    // available is "GameCore is drawing", and the narrowest honest condition on top of it is a state
    // read from the mod itself. The PDA is the window whose hangar submenu lists GPS names, so it is
    // the window the route exists for; while it is open every other piece of GameCore RichHud UI -
    // mission cards, quest log, territory indicator, shop and item terminal windows - follows the
    // same route, because nothing on the emission path can tell them apart.
    //
    // WHY IT FAILS CLOSED
    //
    // The reflection below names fields of someone else's mod, which can be renamed or restructured
    // by an update at any time. A gate that answered "closed" on drift would silently put the GPS
    // list back into the recording, which is the one outcome this route exists to prevent - so a
    // broken schema or a throwing read answers OPEN instead, and GameCore's UI is over-hidden until
    // the schema is understood again. Over-hiding costs the operator a menu on camera; under-hiding
    // costs them the coordinates.
    //
    // The one null that is not drift: Session.Hud before the mod's session init has assigned it. No
    // Hud means no PDA, so that reads closed rather than fail-closed open. An UNBOUND gate that is
    // nevertheless asked is drift too - only a client registered as "GameCore" ever asks, so absence
    // of the mod cannot reach the read path - and it fails closed the same way.
    internal static class GameCorePdaGate
    {
        internal const string SessionType = "GameCore.Session";
        private const string HudFieldName = "Hud";
        private const string ControllerFieldName = "_controller";
        private const string ViewPropertyName = "Current";

        // Backstop only. Invalidate is driven from RichHud's own frame end, so in a live frame the
        // cache is at most one frame old; this bounds the staleness if that postfix ever stops
        // running - a patch that failed to install, or a frame that threw before FinishDraw - at
        // which point a stale "PDA open" would keep hiding UI that has since closed.
        private static readonly long StalenessTicks = Stopwatch.Frequency / 8;

        private static readonly object Gate = new object();
        private static Assembly _assembly;
        private static FieldInfo _hudField;
        private static FieldInfo _controllerField;
        private static PropertyInfo _viewProperty;
        private static bool _bound;
        private static bool _schemaBroken;
        private static int _schemaLogged;
        private static int _readFaultLogged;
        // Names the copy a logged fault belongs to. Under SeamlessClient several copies of the mod
        // are loaded at once, so "the gate broke" is only actionable if the line says which binding
        // broke; without it two lines from two transfers are indistinguishable.
        private static string _assemblyName;

        private static long _reads;
        private static long _failClosed;

        // Read by StatusLine from whichever thread ran the command, hence the interlocked mirror of
        // the cached value. Everything else here is touched only from the master draw path.
        private static int _lastValue;

        // The cache the emitter path actually reads. A scoped draw asks PdaOpen once per patched
        // emitter call - hundreds of times per frame - and each miss is three reflective member
        // reads, so a computed answer is held until the frame ends or the backstop expires.
        private static bool _cached;
        private static bool _cacheValid;
        private static long _cachedAt;

        // Called from the RichHud client scan, with the assembly that declares both GameCore.Session
        // and GameCore's embedded RichHud client. Idempotent: the scan re-runs whenever the loaded
        // assembly set changes, and a rebind of the same assembly must not reset the latched schema
        // verdict or the counters behind it.
        internal static void Bind(Assembly gameCoreAssembly)
        {
            if (gameCoreAssembly == null) return;
            lock (Gate)
            {
                if (ReferenceEquals(gameCoreAssembly, _assembly)) return;
                _assembly = gameCoreAssembly;
                _bound = true;
                _schemaBroken = false;
                _hudField = null;
                _controllerField = null;
                _viewProperty = null;
                // Both log latches are per BINDING, not per process. They exist to keep a persistent
                // fault off a per-frame path, and clearing _schemaBroken above without clearing them
                // meant the first binding to break was the only one that could ever say so: every
                // later copy - a SeamlessClient transfer, a mod update mid-session - failed silently,
                // visible only as gamecore-pda= in a status line nobody had a reason to run. That is
                // the one path where over-hiding costs the user their PDA outright, so it has to be
                // able to announce itself more than once per session.
                Interlocked.Exchange(ref _schemaLogged, 0);
                Interlocked.Exchange(ref _readFaultLogged, 0);
                string name;
                try { name = gameCoreAssembly.GetName().Name; }
                catch { name = "<unnamed>"; }
                Volatile.Write(ref _assemblyName, name);
                Invalidate();

                Type session;
                try { session = gameCoreAssembly.GetType(SessionType, false, false); }
                catch { session = null; }
                FieldInfo hud = session?.GetField(HudFieldName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                // The declared field type, not the instance's: the controller and its view property
                // have to be resolvable at bind time, before the mod has assigned Session.Hud.
                Type hudType = hud?.FieldType;
                FieldInfo controller = hudType?.GetField(ControllerFieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo view = controller?.FieldType.GetProperty(ViewPropertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (view != null && view.GetIndexParameters().Length != 0) view = null;

                if (session == null || hud == null || controller == null || view == null)
                {
                    _schemaBroken = true;
                    NoteSchemaBreak("GameCore PDA gate could not resolve "
                                    + (session == null ? SessionType
                                        : hud == null ? SessionType + "." + HudFieldName
                                        : controller == null
                                            ? (hudType?.FullName ?? "Hud") + "." + ControllerFieldName
                                            : (controller.FieldType.FullName ?? "controller")
                                              + "." + ViewPropertyName)
                                    + "; GameCore RichHud UI will be routed player-only whenever the"
                                    + " mod draws");
                    return;
                }

                _hudField = hud;
                _controllerField = controller;
                _viewProperty = view;
            }
        }

        // Called from the RichHud frame-end postfix. The PDA cannot open and close within one
        // RichHud draw pass, so one read per frame is exact rather than merely cheap.
        internal static void Invalidate() => _cacheValid = false;

        internal static bool PdaOpen
        {
            get
            {
                long now = Stopwatch.GetTimestamp();
                if (_cacheValid && now - _cachedAt <= StalenessTicks) return _cached;

                bool open = Read();
                _cached = open;
                _cacheValid = true;
                _cachedAt = now;
                Volatile.Write(ref _lastValue, open ? 1 : 0);
                return open;
            }
        }

        private static bool Read()
        {
            Interlocked.Increment(ref _reads);
            // Only a resolver whose client registered with RichHud as "GameCore" ever asks, and the
            // scan binds this gate from the assembly that declares GameCore.Session - which is the
            // same assembly that embeds the mod's RichHud client today. Being asked while unbound
            // therefore means a future GameCore build split the two apart: drift, not absence, and
            // it fails closed like any other drift.
            if (!Volatile.Read(ref _bound))
            {
                NoteSchemaBreak("GameCore PDA gate asked while unbound: the registered GameCore"
                                + " client and " + SessionType + " no longer share an assembly;"
                                + " GameCore RichHud UI will be routed player-only whenever the mod"
                                + " draws");
                return FailClosed();
            }
            if (Volatile.Read(ref _schemaBroken)) return FailClosed();
            try
            {
                object hud = _hudField.GetValue(null);
                // Session init has not run yet. Nothing is drawing a PDA that does not exist.
                if (hud == null) return false;
                object controller = _controllerField.GetValue(hud);
                if (controller == null) return FailClosed();
                object view = _viewProperty.GetValue(controller, null);
                if (view == null) return FailClosed();
                // HandTerminalView.None is the zero member, and the enum's ordering is the mod's to
                // change; only the "no view" member is load bearing here, so the value is compared
                // as an integer rather than parsed against member names.
                return Convert.ToInt32(view) != 0;
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _readFaultLogged, 1) == 0)
                    Plugin.Log("GameCore PDA gate read failed: " + ex.GetType().Name + " - "
                               + ex.Message + "; GameCore RichHud UI is being routed player-only"
                               + " while the mod draws [binding="
                               + (Volatile.Read(ref _assemblyName) ?? "unbound") + "]");
                return FailClosed();
            }
        }

        private static bool FailClosed()
        {
            Interlocked.Increment(ref _failClosed);
            return true;
        }

        private static void NoteSchemaBreak(string message)
        {
            if (Interlocked.Exchange(ref _schemaLogged, 1) != 0) return;
            Plugin.Log(message + " [binding=" + (Volatile.Read(ref _assemblyName) ?? "unbound") + "]");
        }

        internal static string StatusLine()
            => "gamecore-pda=" + (!Volatile.Read(ref _bound)
                   ? "unbound"
                   : Volatile.Read(ref _schemaBroken) ? "schema-broken" : "bound")
               + ", reads:" + Interlocked.Read(ref _reads)
               + ", fail-closed:" + Interlocked.Read(ref _failClosed)
               + ", open=" + (Volatile.Read(ref _lastValue) != 0 ? "yes" : "no");
    }
}
