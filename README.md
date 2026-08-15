# CleanFeed

A Space Engineers client plugin that splits your HUD in two: **the HUD you see** and **the HUD your
recorder sees**. Keep speed, GPS, chat, and the rest on screen while the capture gets the world
without them. Or any number of other combinations.

Player-facing instructions live in **[USERGUIDE.md](USERGUIDE.md)**. Trust model, data handling, and
vulnerability reporting live in **[SECURITY.md](SECURITY.md)**.

---

## What it does

- **Removes chosen HUD items from the recording, not from the game.** You still fly with a full HUD.
  The recorder gets the scene without the items you filtered.
- **Two independent switches per item.** `/cf hud <item> on|off` controls what the recording sees.
  `/cf player <item> show|hide` controls what *you* see. Hiding an item from yourself does not change
  its recording filter.
- **Covers the vanilla HUD and named mod HUDs.** Terminal and menus, chat, toolbar and info strip,
  speed, GPS and signal markers, both bottom status panels, plus HUD LCD, WeaponCore, WC Radar,
  Flight Vector, Flight HUD, Thrust Beacon, Flip and Burn, GameCore's Hand Terminal, and BuildInfo's
  toolbar info panel, background plate included.
- **Reports what it cannot cover.** Unknown and partially routable HUD sources are listed in the
  settings screen as possible leaks rather than quietly assumed handled.
- **Fails open.** Any renderer, composition, GPU-copy, or commit fault hides the player visual and
  restores the vanilla HUD by the next rendered frame. The deliberate exception is the two projected
  RichHud screens, the Flip and Burn navigation menu and the GameCore Hand Terminal, whose content is
  GPS lists: those fail closed, withheld from both outputs.

## What it does not do

- **It is not a security boundary.** It is a capture convenience. Recorder behavior changes between
  releases. Verify with a test clip.
- **Display Capture is not a clean-feed route.** Display Capture, Monitor Capture, and NVIDIA Desktop
  Capture copy the desktop, and the player HUD lives on the desktop. Use **Game Capture** or the
  normal NVIDIA game-recording path.
- **Discord's own screen and application sharing is not a clean-feed route either.** Discord Go Live
  and Share Your Screen capture through Windows Graphics Capture, which reads the composed desktop
  output, so a Discord viewer sees the player-only HUD. It is the same class of capture as Display
  Capture. The supported route is OBS as a relay, since the player surface is attached to the Space
  Engineers window only and any capture of a different window is clean; **[USERGUIDE.md](USERGUIDE.md)**
  has the two setups under "Streaming to Discord". There are future plans to improve this behavior.
- **It does not set a Windows capture-protection flag.** The NVIDIA and OBS routes rely on their
  game-capture paths reading the game swapchain without the DirectComposition visual. Nothing is
  marked protected, because NVIDIA is known to reject protected windows.
- **It does not work in exclusive fullscreen.** There is nowhere to put a desktop-composed player
  visual. CleanFeed refuses to start there and stops immediately if you switch to it mid-session.

## How it works

Space Engineers draws its HUD into the same backbuffer as the world, which is exactly what a game
capture grabs. CleanFeed intercepts the sprite-render seams and sends the filtered producers
somewhere else: they render into a reserved GPU target, copy once into a **DirectComposition** logical
surface, and commit straight onto the Space Engineers window. That surface is a desktop composition
layer sitting **over** the game window, so you see it, while the recorder is still reading the game's
own swapchain underneath and never sees it.

The consequences fall out of that one design choice:

- No second swapchain, no `Present`, no staging texture, no CPU readback, no managed full-frame
  buffer, no WPF bitmap upload, no independent UI-frame queue. HUD and world move as one frame.
- The route owns no top-level window, so it cannot receive mouse or keyboard input.
- Anything that copies the desktop copies the player layer too.
- Exclusive fullscreen bypasses desktop composition, so the route cannot exist there.

## Requirements

- **Windows.** Windows-only by mechanism: the player surface is a DirectComposition visual on the
  desktop compositor, and there is no Wine or Proton equivalent. `CleanFeed.xml` declares
  `<Platforms>Windows</Platforms>`, which on Pulsar builds that read the field excludes native Linux
  and Proton outright. I will investigate compatibility down the line, but I am very confident this
  will not work in its current state on Native Linux, or Wine/Proton.
- **Pulsar** (Legacy or Interim).
- **Fullscreen Window** or **Window** display mode. Not exclusive fullscreen.
- A recorder using **Game Capture** (such as OBS) or the normal game-recording path (NVIDIA, Steam), pointed at the
  Space Engineers window. In OBS, leave **Capture third-party overlays** off.

## Install

Install CleanFeed from Pulsar's plugin list, enable it, and restart Space Engineers.

For development, launch Pulsar with `-sources`, add this repository as a local plugin source,
associate `CleanFeed.xml`, and select a Release build. That exercises the same source compiler used
for marketplace installs.

`CleanFeed.xml`'s `<Commit>` intentionally lags the implementation it describes: a manifest cannot pin
the commit that contains its own updated pin. The submission candidate is the descriptor in the
PluginHub branch, which is bumped to the public `main` HEAD at release time.

## Quick start

1. Set the game to **Fullscreen Window** or **Window**.
2. Load into a world. CleanFeed does nothing on the main menu or a loading screen.
3. `/cf hud all on` turns on every recording filter.
4. `/cf redirect` starts the routing. Your HUD should look unchanged. That is correct.
5. Record a ten second test clip and watch it back. World present, filtered HUD absent.
6. `/cf hud off` returns to vanilla rendering immediately.

`/cf` and `/cleanfeed` are interchangeable. Filters can be changed while the redirect runs.
**[USERGUIDE.md](USERGUIDE.md)** covers the filters, the settings screen, per-panel HUD LCD markers,
Flip and Burn, streaming to Discord, performance, and troubleshooting in depth.

## Commands

- `/cf settings` - open the privacy, routing, source-discovery, and reporting screen.
- `/cf sources` - registry and discovery status. `/cf rescan` restarts the discovery window.
- `/cf hud all on|off|status` - every recording filter at once, or the current state of all of them.
- `/cf hud <item> on|off|status` - one item. `on` means player-only, `off` means recorded.
- `/cf player all show|hide|status` - show or suppress every supported category for the local player.
- `/cf player <item> show|hide|status` - one category. Suppressed means absent from both outputs.
- `/cf privacy unfocused on|off|status` - the focus-loss privacy guard. Defaults to on.
- `/cf auto on|off|status` - start the selective redirect each session. Defaults to off.
- `/cf redirect` or `/cf hud redirect` - start selective, compatibility-aware routing.
- `/cf hud whole` or `/cf whole` - move the complete native sprite HUD to the player surface.
- `/cleanfeed hud off` - stop redirecting, restore vanilla HUD rendering.
- `/cleanfeed hud status` - GPU lifecycle, surface, timing, and fault telemetry.
- `/cleanfeed profile <item> on|off|status` - advanced raw recording-visibility control.
- `/cleanfeed off` - stop the redirect.
- `/cleanfeed status` - GPU HUD lifecycle and telemetry in one line.

## Filters

Current filter keys: `terminal`, `chat`, `toolbar`, `speed`, `gps`, `battery-fuel`,
`hudlcd`, `weaponcore.reload`, `weaponcore.radar`, `wc-radar`, `flight-vector`, `flight-hud`,
`thrust-beacon`, `flip-burn`, and `gamecore`. Underscores work like hyphens and each key accepts the
obvious aliases.

**The Shift+F11 timing overlay is not covered.** The game emits it from the render thread straight
into its debug sprite queue, without the target stamp the redirect rewrites, so it cannot be moved
onto a composition layer. Press Shift+F11 again to close it before recording anything sensitive
about your connection.

**`toolbar` covers the hotbar and the whole info strip around it**, including the gravity,
environment, and build-mode readouts beside it, and BuildInfo's Toolbar Info panel. The BuildInfo
panel rides the key whole: its label text and its background plate, strips, and corners. Those plates
are persistent Text HUD billboards the panel builds once rather than draws per frame, so CleanFeed
tags them where they are created and routes them with the rest of the key.

**`battery-fuel` covers both bottom status panels** as whole objects. Bottom-left, the suit panel: the
plate itself, the helmet / jetpack / magboots / flashlight / broadcast icon row, the radiation and
broadcast readouts, the dampeners indicator, and the health / energy / oxygen / hydrogen bars.
Bottom-right, the controlled-grid panel: power, reactors, hydrogen, remaining energy time, mass, and
the static / handbrake / broadcast / remote-access flags. **`speed` stays separately controllable**
and overrides inside the panel, so the speed gauge follows its own filter rather than the panel's.

Every key also supports `/cf player <key> show|hide`. Hidden content is suppressed from both the
player and the recording while its recording filter is left untouched.

**HUD LCD panels can carry a per-panel marker** in Custom Data: an optional seventh-or-later
colon-separated field of `cleanfeed=player`, `cleanfeed=record`, `cleanfeed=hidden`, or
`cleanfeed=shown`.

**Instance markers are subtractive.** They may only restrict a panel relative to its category, never
widen it. A block's Custom Data is writable by other players on a server, so `cleanfeed=player` can
take one panel out of the recording, but **`cleanfeed=record` cannot force a panel into the recording**
while the `hudlcd` category filter hides it. It therefore only matters when the category is
unfiltered, which is already the default there: accepted, but effectively inert. The player axis
follows the same rule. `cleanfeed=hidden` suppresses one panel, and `cleanfeed=shown` only undoes an
earlier `hidden` on the same panel. It cannot defeat a category-wide player hide.

**Flip and Burn is handled in three pieces.** Its Text HUD elements route under `flip-burn`. Its
target-selector visuals around GPS and signal targets, meaning the corner brackets, travel time, and
offscreen arrow, follow the native `gps` category instead, so hiding GPS from the recording also hides
the selector pointing at it. Its RichHud navigation menu draws through the RichHud master mod and is
**projected onto the player-only layer** under the `flip-burn` key: with `flip-burn` filtered the menu
stays fully usable and pixel-faithful to the player while the recording never contains it, and with
`flip-burn` unfiltered it renders and records exactly as without CleanFeed. The projection fails
closed - if any part of the capture is not ready, the menu's content is withheld from both outputs
rather than leaked to the recording.

**`gamecore` routes GameCore's Hand Terminal, and only while it is open.** The M-key PDA draws
through the RichHud master mod, and its hangar submenu lists GPS names, so CleanFeed routes
GameCore's RichHud content onto the player-only layer for as long as the PDA is open and lets it
record normally the rest of the time.

**While the PDA is open, all of GameCore's RichHud UI follows the same route** - mission cards, quest
log, territory indicator, shop and item terminal windows. RichHud attributes a draw to a client
assembly, not to a window: the master flattens every client's UI into one interleaved draw pass, so
"GameCore is drawing" is the finest attribution available and the PDA's state is the narrowest
honest condition on top of it. Closing the PDA restores everything to the recording.

**The hangar preview hologram stays in the recording.** It is a spawned grid entity plus a
world-space backdrop billboard, not HUD content, and nothing on the HUD routing path can reach it.
It carries no GPS text, only the shape of the ship being previewed. The `gamecore` row is
`partitioned-partial` for exactly this reason.

**The open/closed gate fails closed.** CleanFeed reads the PDA's view state out of the mod by
reflection. If a GameCore update renames or restructures what it reads, or the read throws, the gate
answers "open" and GameCore's RichHud UI is routed player-only whenever the mod draws - over-hiding
rather than leaking the coordinates. `/cf hud status` reports `gamecore-pda=` with the bind state,
the read count, and the fail-closed count.

## Settings and runtime discovery

`/cf settings`, or Pulsar's Settings action, opens the in-game settings screen. **While a redirect is
running the screen is forced player-only, so it is hidden from the recording.** Without an active
redirect there is nothing to redirect it into: it renders normally and is captured like any other
menu. Start the redirect first if you want to change settings off camera.

The screen holds unfocused privacy, recording defaults, player defaults, redirect controls, and a
scrollable source registry grouped as **Verified Sources**, **Detected Best-Effort**, and
**Unsupported / Unattributed**. Each source row carries its name and capability plus a two-line
word-wrapped description, so the whole description is on the row rather than only in the hover
tooltip, and the `gamecore` row carries a red **THIS EXPOSES GPS** warning next to its buttons.
Normal edits are staged until Apply. Starting or stopping a redirect, rescanning sources, copying
diagnostics, and requesting support are immediate.

Settings persist to `%APPDATA%\CleanFeed\CleanFeed.profile.ini`, a fixed per-user path that survives
plugin updates and Pulsar's from-source recompiles. A profile left in the game's older plugin-local
storage folder is **migrated forward once** on first load and then written to the new location.

The registry always lists every explicit CleanFeed category, including inactive compatibility
providers. For unknown providers, CleanFeed passively inventories HUD-like loaded types and Harmony
owners at known render seams and scopes their `Draw` / `Render` methods from that assembly scan.
During a bounded 30-second discovery window it can additionally take at most 96 rate-limited Text HUD
call-stack samples, tag the created backing object, and install an identity-only caller scope for
later messages. It never skips an unknown provider method and does no per-sprite or steady-state stack
tracing.

Newly detected providers are left unchanged by default. Attributed native sprites and representable
Text HUD or billboard portions can be offered as best-effort partial controls. World lines, persistent
billboards, custom projections, and ambiguous output continue to fail open and are shown as possible
leaks. **Rescan sources** reopens the bounded discovery window. Each row can copy a sanitized
compatibility report and open the CleanFeed GitHub issue form; reports exclude local paths, world and
server names, entity identity, GPS, chat, and authentication data.

## Selective route and compatibility

The selective route waits for `ConsumeMainSprites` to join vanilla's asynchronous sprite worker, then
renders the named player queue synchronously. It never invokes the private worker directly, never
races the game's sprite manager, and never reuses a returned message collection. The transport can
redirect either selected HUD producers or the complete native `RenderMainSprites` pass away from the
game backbuffer.

Textured billboard portions of the mod adapters are routable. World lines, triangles, persistent
billboards, custom projections, and the Thrust Beacon `[PRI]` native marker fail open to the
recording. `/cf hud status` reports each discovered adapter as `partitioned`, `partitioned-partial`,
or inactive, with diverted, pass, and failure counters.

## Runtime behavior

**Fail-open.** Unexpected renderer, composition, GPU-copy, or commit errors hide the player visual and
restore the vanilla HUD by the following rendered frame. A CleanFeed failure fails towards your HUD
being recorded, never towards your HUD disappearing. The projected RichHud screens are the deliberate
exception and fail closed, as described under Flip and Burn and `gamecore` above.

**Focus.** With unfocused privacy on, which is the default, a foreground change engages privacy
suppression on the frame it is seen: supported HUD is drained off-backbuffer and published to neither
output. The same drain covers initial arming. The composition itself is not torn down. A short
foreground mismatch is debounced, and one that persists **parks** the composition - transparent,
committed once, surfaces retained - so returning to the game resumes committing with no rearm delay
and no rebuild. Real target loss, meaning the window minimized, hidden, or gone, still suspends and
rebuilds. `/cf privacy unfocused off` trades the drain for plain retention: commits pause, the last
HUD frame stays on the player surface, and alt-tabbing back restores it instantly.

**Steam overlay.** The Shift+Tab overlay renders inside the game's swapchain, underneath the composed
player surface, so an unhandled overlay would sit behind the filtered HUD. CleanFeed listens for the
game's overlay-activation event and parks the player surface (transparent, one commit, surfaces
retained) while the overlay is open, resuming on the frame it closes. Producers keep diverting for
the duration, so filtered content stays out of the recording while the overlay is up. Status field:
`steam-overlay=`.

**Display mode.** Switching to exclusive fullscreen disables redirection immediately, so menus and HUD
remain available.

**World scope.** The redirect does not arm on the main menu or during a loading screen. It suspends
when you leave a world and resumes with the last mode when the next world reports ready. A redirect
left running against a loading screen would divert the loader itself onto the player surface.

**Status.** `/cleanfeed hud status` reports lifecycle state, build identifier, game-HWND readiness,
surface format and dimensions, copy / commit / rebuild counters, sprite-message and discard counts,
native fail-open and rearm counters, focus, privacy and cursor diagnostics, modal Flight HUD state,
and the last fault. A transient empty or sharply partial sprite-message batch retains the last
complete HUD rather than publishing the fragment. The hold window follows the route's own publish
cadence, twice the last gap between publishes, floored at 50 ms and capped at 250 ms; a sustained
lower batch still publishes normally, so genuine HUD removal stays responsive.

**Long-run diagnostics.** Process private, working, and managed memory, handles, and GC counts are
sampled every 10 seconds. While a redirect is active, process-local DXGI video-memory usage and
budget are sampled too, along with borrowed-RTV, DirectComposition, update-reference, pending-copy,
focus, and view-transition counters. A compact health heartbeat is written every five minutes.
Private-memory growth is labeled suspicious only after a complete five-minute window and must be read
alongside the owned resource balances, since normal world loading grows the host process without a
CleanFeed leak. Everything CleanFeed writes to `SpaceEngineers.log` is prefixed `[cleanfeed]`.


## Harmony patch surface

Stated plainly. CleanFeed installs Harmony patches under the owner ID `cleanfeed.hud-redirect` on:

- The game's sprite-render seams: `MyRender11.RenderMainSprites`, its sprite worker,
  `ConsumeMainSprites`, and `Present`.
- The `MyRenderProxy` sprite-draw family: `DrawSprite`, `DrawSpriteAtlas`, `DrawSpriteExt`,
  `DrawString`, `DrawStringAligned`, and the sprite scissor push and pop.
- Render message processing, `ExecuteCommands` deferred-command replay, and the
  `MyTransparentGeometry` billboard entry points.
- `Draw` methods of GUI screens across the game's GUI assemblies.
- Specific third-party HUD provider methods, including Text HUD backend constructors.
- RichHud scopes and capture boundaries: each registered client's billboard emitters and its
  `TextBoard.Draw` overloads, plus the master's frame-end seam. That is what lets the Flip and Burn
  navigation menu and the GameCore Hand Terminal be projected onto the player layer. Nothing gates a
  menu shut.

Discovered HUD-like mod types get their `Draw` / `Render` methods scoped from the assembly scan
itself, not only within the bounded 30-second Text HUD sampling window; that window bounds call-stack
attribution, not scope installation. All patches are installed at load. Routing only activates once a
redirect is running, and until then every patch passes through to vanilla behavior. Teardown removes
only that owner's patches.


## Validation status

The retained-GPU route has been validated end to end with every filter enabled: sharply partial HUD
batches are held rather than published, the redirect stays active without suspensions or render
faults, and a capture taken during the run contains the scene with chat and the supported native and
mod HUD absent. Individual toggles and NVIDIA / OBS motion capture still need separate validation on
the shipping build.

The old `framelock`, `frame-lock`, and `adaptive` modes are retired. They increased full-frame CPU traffic and
cadence variance. All dynamic HUD delivery now uses the GPU-composition path.


## Reporting

Bugs and third-party HUD compatibility requests go to
https://github.com/arsnekfcn/CleanFeed/issues. Include the sanitized report copied from CleanFeed's
settings screen. Do not post world, server, GPS, chat, or authentication data. Security issues go
through the advisory process in [SECURITY.md](SECURITY.md).

## License

MIT. See [LICENSE](LICENSE).
