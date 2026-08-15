# CleanFeed User Guide

You see your HUD. Your recording does not.

This guide is for players. It assumes you installed CleanFeed from Pulsar and you record or stream
with OBS or the NVIDIA overlay. Other recording software should work fine, but is not tested. 
For the architecture, the Harmony patch surface, and the maintainer detail, see [README.md](README.md). 
For data handling and the trust model, see [SECURITY.md](SECURITY.md).

---

## 1. What CleanFeed does

CleanFeed splits HUD drawing into two destinations. Anything you filter is drawn onto a separate
**player surface** that sits on top of the game window on your desktop, and it is never written into
the picture your recorder grabs from the game. The world itself, meaning ships, asteroids, effects,
and the sky, is untouched and records normally.

### What it can hide

- **Native game HUD:** the terminal and other menus, chat, the toolbar and the info strip around it,
  the speed gauge, GPS and signal markers, and the two bottom status panels.
- **Supported mod HUDs:** HUD LCD, WeaponCore reload and targeting UI, WC Radar, Flight Vector,
  Flight HUD, Thrust Beacon, Flip and Burn, GameCore's Hand Terminal, and BuildInfo's Toolbar Info
  panel, background plate included.

### What it cannot hide

- **World-space geometry drawn by mods:** lines, rings, gates, spheres, triangles, and custom
  projections. These are part of the 3D scene, not the HUD layer, so they stay in the recording.
- **The Shift+F11 timing overlay.** The game emits it from the render thread straight into its debug
  sprite queue, and it never carries the stamp CleanFeed rewrites, so it cannot be moved onto the
  player surface and there is no key for it. Press Shift+F11 again to close it before recording
  anything you would rather not publish about your connection.
- **Anything a mod draws that CleanFeed cannot attribute to a known source.** CleanFeed lists these
  in the settings screen as possible leaks rather than pretending they are handled.

### Requirements

- **Windows.** CleanFeed puts the player HUD on a Windows desktop composition layer, and there is no
  Linux or Proton equivalent for that. The plugin declares Windows in its manifest, so Pulsar builds
  that read that field will not offer it on Linux or Proton at all. A from-source Linux build is
  expected to load and simply decline to redirect, but that has never been tested on a real Linux
  machine and is not a supported setup.
- **Fullscreen Window** or **Window** mode (Options, Display). Exclusive fullscreen is not supported;
  CleanFeed will refuse to redirect, or will stop redirecting, if you switch to it.
- Your recorder must use **Game Capture** (OBS) or the normal game-recording path (NVIDIA, Steam, etc), pointed at
  the Space Engineers window.

### Display Capture is not supported

**Display Capture, Monitor Capture, and NVIDIA Desktop Capture record your whole screen, so they
record the player surface too.** There is no way around this. Those modes copy the desktop, and your
player-only HUD is on the desktop. Use Game Capture. In OBS, also leave **Capture third-party
overlays** off.

**Discord's screen and application sharing is the same class of capture.** Discord Go Live and Share
Your Screen use Windows Graphics Capture, which reads the composed desktop output, so whoever is
watching sees the player-only HUD exactly as you do. Sharing the Space Engineers window from Discord
directly is not a clean feed. There is a way to stream to Discord cleanly, by relaying through OBS -
see section 10.

---

## 2. Quick start

1. In Pulsar, enable **CleanFeed** in the plugin list and start Space Engineers.
2. Set the game to **Fullscreen Window** or **Window**.
3. Load into a world and wait until you are actually in the cockpit or on foot. CleanFeed does nothing
   on the main menu or a loading screen.
4. Turn on every recording filter: `/cf hud all on`.
5. Start the redirect: `/cf redirect`.
6. You get a brief notification, your HUD may blink once while the redirect arms, and then it looks
   exactly as it did before. **That is correct.** The change is only visible to the recorder.
7. Record a ten second test clip with your normal setup and play it back. The world should be there
   and the HUD should not.
8. Back to the vanilla HUD at any time: `/cf hud off`.

Both `/cf` and `/cleanfeed` work as the command prefix. Every example below uses `/cf`.
You can also open settings via the plugins menu.

You can change filters while the redirect is running. You do not need to stop and restart it.

---

## 3. Filters explained

There are **two independent switches per HUD item**, and the difference matters before you start
flipping things.

### Recording filters: `/cf hud <item> on|off`

This is the one you want most of the time.

- **`on`** means the filter is on, so the item is **player-only**. You see it, the recording does not.
- **`off`** means the filter is off, so the item is **recorded** normally, exactly like vanilla.

```
/cf hud all on          turn on every recording filter
/cf hud gps on          hide GPS markers from the recording only
/cf hud chat off        put chat back into the recording
/cf hud all status      list the current state of every filter
/cf hud speed status    show the state of one filter
```

### Player visibility: `/cf player <item> show|hide`

A plain HUD declutter switch. It has nothing to do with recording.

- **`show`** is normal.
- **`hide`** suppresses the item from **both** outputs. You do not see it and the recording does not
  either.

```
/cf player toolbar hide   remove the toolbar from your own view and the recording
/cf player all show       bring everything back
/cf player status         list current player visibility
```

### The difference, in one line

`/cf hud <item> on` hides it from the recording only. `/cf player <item> hide` hides it from
everything. The two settings are stored separately, so hiding an item from yourself does not touch its
recording filter. Show it again and it returns to whatever filter you had set.

### The filter keys

| Key | What it covers |
| --- | --- |
| `terminal` | The control panel / K menu, terminal screens, inventory and block screens, tooltips, and other interactive menus drawn over the game. |
| `chat` | The chat window and chat history. |
| `toolbar` | The hotbar and the whole info strip around it: the plate art, the natural and artificial gravity readouts, the environment oxygen and temperature readouts, build-mode hints (grid size, symmetry, mountpoint align), and BuildInfo's Toolbar Info panel - its labels and its background plate, strips and corners, not just the text. |
| `speed` | The speed gauge. It lives inside the bottom-left panel but has its own filter, which overrides the panel's, so you can keep or drop speed independently. |
| `gps` | GPS markers and signal markers, including their names and distances. Also covers the Flip and Burn target selector (see section 8). |
| `battery-fuel` | Both bottom status panels as whole objects. Bottom-left: the plate, the helmet / jetpack / magboots / flashlight / broadcast icon row, radiation and broadcast readouts, the dampeners indicator, and the health / energy / oxygen / hydrogen bars. Bottom-right: power, reactors, hydrogen, remaining energy time, mass, and the static / handbrake / broadcast / remote-access flags. |
| `hudlcd` | Text drawn by the HUD LCD mod. Supports per-panel markers that can further restrict a single panel (see section 7). |
| `weaponcore.reload` | WeaponCore's reload and weapon status HUD, including its fading text. |
| `weaponcore.radar` | WeaponCore's targeting UI: the target selector, drone notices, active marks, and lead indicators. |
| `wc-radar` | The separate WC Radar mod (missile and contact draws, radar rollup). |
| `flight-vector` | The Flight Vector mod's HUD. |
| `flight-hud` | The Flight HUD / compass mod, including the compass ticker. |
| `thrust-beacon` | The Thrust Beacon mod's HUD text.|
| `flip-burn` | The Flip and Burn mod's HUD elements (see section 8 for the parts that behave differently). |
| `gamecore` | The GameCore mod's Hand Terminal (the M-key PDA) and, while it is open, the rest of GameCore's on-screen UI (see section 9). |

Keys are forgiving. Underscores work like hyphens, and these aliases are accepted: `k`, `k-menu`,
`menu` for `terminal`; `chat-window`, `chat-history` for `chat`; `battery`, `fuel`, `batteryfuel` for
`battery-fuel`; `flightvector`, `flighthud`; `wc-reload`, `weaponcore-reload`; `weaponcore-radar`,
`wc-hud-radar`; `wcradar` for `wc-radar`; `thrustbeacon`, `beacon`; `flipburn`, `flipandburn`,
`flip-and-burn`; `pda`, `hand-terminal`, `handterminal`, `game-core` for `gamecore`.

You can also drop the `hud` word for a single key: `/cf gps on` does the same as `/cf hud gps on`.

If a mod is not installed or not detected, its key still exists and still accepts commands. It just
has nothing to do until the mod shows up.

### Other useful commands

```
/cf status              full state report (HUD routing plus diagnostics)
/cf hud status          redirect lifecycle, display mode, counters, last fault
/cf sources             one-line summary of the source registry and discovery window
/cf rescan              restart the 30 second HUD source discovery window
/cf privacy unfocused on|off|status   see section 5
/cf auto on|off|status  see section 6
/cf profile <item> on|off|status      advanced raw control, see below
/cf settings            open the settings screen
```

`/cf profile` is the low-level version of `/cf hud`. It speaks in recording **visibility** rather than
filter state, so its polarity is inverted: `/cf profile gps off` is the same as `/cf hud gps on`. Use
`/cf hud` unless you have a reason not to. `/cf profile status` prints everything at once, including
which values are per-item overrides and which are inherited from the global default.

### Where your settings live

Settings save automatically to `CleanFeed.profile.ini` in **`%APPDATA%\CleanFeed`** and reload next
time you play.

---

## 4. The settings screen (`/cf settings`)

Type `/cf settings`, or use CleanFeed's Settings action in Pulsar. You can also navigate here with the gear icon on CleanFeed in the plugins menu.

**While a redirect is running the screen is forced player-only, so opening it does not show up in your
recording.** Without an active redirect there is nothing to redirect it into: the screen draws
normally and your recorder captures it like any other menu. If you want to change settings off camera,
start the redirect first.

**Header.** The top line tells you whether a redirect is running, which mode it is in, and whether it
is ready or currently suppressed by the privacy guard. Next to it is a summary of the source registry.

**The four toggle buttons.**

- **Unfocused privacy: ON / OFF.** The alt-tab privacy guard (section 5).
- **Auto-start redirect: ON / OFF.** Start the redirect automatically each session (section 6).
- **Recording default: REC / FILTER.** The default for anything without its own setting. Pressing it
  also sets every listed source to that value, so it is a fast "filter everything" or "record
  everything" button.
- **Player default: SHOW / HIDE.** Same idea for player visibility.

**The source list.** Scrollable, grouped into three sections:

- **VERIFIED SOURCES.** CleanFeed's explicit adapters and the native HUD categories, meaning the keys
  from the table above. They are always listed, even when the matching mod is not loaded, in which
  case the row shows `inactive`.
- **DETECTED BEST-EFFORT.** Providers CleanFeed found at runtime that it was not shipped with. Left
  alone by default. Where CleanFeed can attribute their output, it offers partial controls you can opt
  into. Treat them as unproven until you have checked a test recording.
- **UNSUPPORTED / UNATTRIBUTED.** Things CleanFeed can see but cannot route. Shown so you know they
  will appear in your recording, and so you can file a compatibility request.

Each row shows the source name and its capability (`partitioned`, `partitioned-partial`, `inactive`,
or observe-only) on the first line, then the full description wrapped across two lines below it: its
provenance, its current route, and what may still leak. Hovering a row still shows a tooltip with the
internal ID, provider version, and the same detail. The `gamecore` row additionally carries a red
**THIS EXPOSES GPS** warning beside its buttons, because the Hand Terminal's hangar submenu is a list
of GPS names. Each row has three buttons:

- **REC / FILTER**: the recording filter for that source. FILTER keeps the supported portions
  player-only.
- **SHOW / HIDE**: player visibility. HIDE suppresses the supported portions everywhere.
- **REPORT**: copies a sanitized compatibility report for that one source and opens CleanFeed's GitHub
  issue form.

Rows CleanFeed cannot control have their REC and SHOW buttons greyed out, with a tooltip explaining
why.

**Staged Apply.** Every toggle and every row button is **staged**. Nothing takes effect until you press
**Apply**. **Cancel** throws your edits away. The four footer buttons are immediate and are not
staged: **Start redirect / Stop redirect**, **Rescan sources**, **Copy diagnostics**, and **Request
support**.

Two details worth knowing:

- Turning **Auto-start redirect** on in this screen only saves the preference. It deliberately does not
  start a redirect while you are sitting in the settings screen. Use `/cf auto on` if you want it to
  start now.
- If the list of HUD sources changes while you have the screen open with unsaved edits, Apply refuses
  once and asks you to review the refreshed list. Press Apply again after checking it.

**Sanitized reports.** The **Copy diagnostics** and **Request support** buttons, and each row's REPORT
button, produce a report that excludes local file paths, world and server names, entity identity, GPS
data, chat, and authentication data. It is safe to paste into a public issue.

---

## 5. Privacy features

### Unfocused privacy guard (default: on)

When you alt-tab away, minimize, or the Space Engineers window otherwise stops being the foreground
window, the guard engages on that very frame: the filtered HUD is **drained off-screen entirely**,
published neither to the player surface nor to the recording. The same drain covers the moment the
redirect is first arming.

What it does not do any more is tear the whole thing down. A momentary foreground blip is absorbed,
and a real one **parks** the player surface: it is cleared to transparent, committed once so nothing
is left on the glass, and then simply held. Alt-tab back and CleanFeed resumes committing on the next
frame, with no rebuild and no waiting - the second-long HUD dropouts older builds had on every
alt-tab are gone. Minimizing the window is different: that is a genuine loss of the surface, so it is
rebuilt when you return, which takes a fraction of a second.

The point of the guard is that there is no window in which your filtered HUD can appear in the
recording because the player surface briefly went away.

### The Steam overlay (Shift+Tab)

The Steam overlay draws inside the game itself, underneath CleanFeed's player surface, so without
handling it your filtered HUD would sit on top of the overlay and make it unusable. CleanFeed
listens for the overlay opening and parks the player surface while it is open - your HUD disappears
under the overlay, exactly as it does in vanilla, and returns the moment you close it. Filters keep
working the whole time, so nothing extra appears in the recording while the overlay is up.

```
/cf privacy unfocused on
/cf privacy unfocused off
/cf privacy unfocused status
```

or the **Unfocused privacy** button in the settings screen.

### With the guard off: focus-pause retention

Turn the guard off and plain focus loss behaves differently. Instead of draining, CleanFeed pauses
commits and keeps the composition surfaces alive, so the last HUD frame stays on the player surface.
Alt-tabbing back restores exactly the same HUD instantly, with no rebuild.

The practical difference is what is on the player surface while you are away: with the guard on it is
blank, with the guard off your last HUD frame is still sitting there. Off is the right choice if you
alt-tab constantly and are confident nothing sensitive is on screen. It is less private.
**Keep the guard on if you are live.**

---

## 6. Auto-start

If you want CleanFeed running every time without typing anything:

```
/cf auto on
/cf auto off
/cf auto status
```

or use **Auto-start redirect** in the settings screen. Remember that it is staged behind Apply there,
and that it only saves the preference rather than starting immediately.

`/cf auto on` from chat saves the preference and, if a redirect is not already running, starts one
right away.

**The redirect is world-scoped.** This matters:

- It does not arm on the main menu or during a loading screen. CleanFeed waits until the world
  actually reports as ready.
- When you leave a world, the redirect is suspended automatically. When you load the next world, it
  resumes with the mode you were last using.
- With auto-start off, run `/cf redirect` yourself once you are in the world.

The reason for the world scoping is practical: a redirect left running against a loading screen would
divert the loading screen itself onto the player surface, giving you a fragmentary or black loader.

Your filter and player settings are saved separately from auto-start and persist regardless.

---

## 7. HUD LCD per-panel overrides

If you use the HUD LCD mod, you can restrict CleanFeed's behavior for one specific panel from that
panel's **Custom Data**, without touching the global `hudlcd` filter.

Add one of these as an extra colon-separated field on the `hudlcd` line, in the **seventh position or
later**:

- `cleanfeed=player` : this panel is player-only, never recorded.
- `cleanfeed=record` : this panel follows the `hudlcd` category. Accepted, but see below: it cannot
  put a panel back into the recording.
- `cleanfeed=hidden` : this panel is hidden from you and from the recording.
- `cleanfeed=shown`  : this panel is visible to you (undoes an earlier `hidden` on the same panel).

Example Custom Data line:

```
hudlcd:-0.98:0.85:0.6:255,255,255:mono:cleanfeed=player
```

The marker must land in field 7 or beyond, counting `hudlcd` itself as field 1. A shorter line means
the marker is ignored.

### Precedence

**Markers only ever subtract.** A marker can take one panel out of the recording or out of your view.
It can never add a panel back into either one. The reason: Custom Data is writable by whoever owns the
block, which on a server can be somebody else. If a marker could widen what gets recorded, another
player could push their own text into your capture after you had filtered HUD LCD out. So the rule is
that **the category decides the maximum and the marker can only narrow it.**

1. **A category-wide player hide always wins.** After `/cf player hudlcd hide`, every HUD LCD panel is
   suppressed and no marker brings it back. `cleanfeed=shown` cannot defeat it.
2. **`cleanfeed=hidden` on a panel wins over the category being shown.** That one panel disappears from
   both outputs. `cleanfeed=shown` only cancels a `hidden` marker on the same panel.
3. **`cleanfeed=player` narrows the recording for that panel only.** If the `hudlcd` filter is off, so
   panels are recorded, this one panel is still kept out of the recording.
4. **`cleanfeed=record` cannot widen it.** If the `hudlcd` filter is on, the panel stays out of the
   recording regardless of the marker. It only has meaning while the category is unfiltered, and that
   is already what an unmarked panel does there, so in practice it is accepted but inert.
5. **No marker means the panel follows the `hudlcd` category**, exactly as before.
6. If a panel somehow carries more than one marker, the last one read wins.

If you actually want a panel back in the recording, turn the category filter off with
`/cf hud hudlcd off` and mark the panels you want kept private with `cleanfeed=player`.

---

## 8. Flip and Burn specifics

**Its HUD elements follow `flip-burn`.** The burn timer, readouts, and other text follow
`/cf hud flip-burn on|off` like any other mod key. Its world-space visuals do not: the gates, rings,
and spheres, and the additively blended dots and lines it draws out in the world, are part of the 3D
scene and stay in the recording.

**The GPS / signal target selector follows `gps`, not `flip-burn`.** The corner brackets, travel time,
and offscreen arrow that point at your selected target follow the native `gps` filter. This is
deliberate: if you have hidden GPS markers from your recording, a bracket labeled with the marker's
name and distance would defeat the point. Hide GPS from the recording and the selector goes with it.

---

## 9. GameCore Hand Terminal specifics

**The PDA follows `gamecore`, and only while it is open.** The M-key Hand Terminal has a hangar
submenu that lists GPS names, so `/cf hud gamecore on` keeps it off the recording while you are
reading it. The moment you close it, GameCore goes back to recording normally. `/cf hud pda on` is
the same command.

**While the PDA is open, the rest of GameCore's on-screen UI goes with it.** Mission cards, the quest
log, the territory indicator, and the shop and item terminal windows are all drawn by the same
framework GameCore draws the PDA with, and that framework hands CleanFeed one signal for the whole
mod rather than one per window. So the filter covers everything GameCore is drawing for as long as
the terminal is up. This is deliberate and it is the honest limit of what can be attributed. If you
need those elements on camera, close the PDA.

**The hangar preview hologram stays in the recording.** The rotating ship preview and the panel
behind it are real objects in the world, not HUD, so no HUD filter can move them. They show the shape
of the ship being previewed and nothing else - no GPS names, no coordinates, no station names. The
settings screen marks `gamecore` as `partitioned-partial` for this reason.

**If GameCore updates and CleanFeed cannot read it, the filter over-hides rather than leaks.**
CleanFeed asks the mod whether the terminal is open. If a future GameCore version changes what it is
asked, CleanFeed assumes the terminal is open and keeps GameCore's UI off the recording whenever the
mod draws, until the plugin is updated to match. Run `/cf hud status` and look at `gamecore-pda=`: it
reads `bound` normally, `schema-broken` if this has happened, and `unbound` if GameCore is not
loaded.

---

## 10. Streaming to Discord

Discord cannot capture a clean feed from Space Engineers on its own. Go Live and Share Your Screen
both go through Windows Graphics Capture, which reads the composed desktop output, so the player-only
HUD is in the picture whether you share the whole screen or just the game window. That is the same
limitation as Display Capture and it is not something CleanFeed can change from inside the game.

There is a way around it, and it comes from how the player surface is attached: it hangs off the
Space Engineers window and nothing else. **Any capture of a different window is clean.** So run OBS,
point it at the game with Game Capture, and give Discord the OBS output instead of the game.

Set the OBS side up once, either way:

- Add a **Game Capture** source for Space Engineers, with **Capture third-party overlays** off.
- Confirm in the OBS preview that the filtered HUD is absent before you go anywhere near Discord.

**OBS projector plus Go Live.**

1. In OBS, right-click the source and click "open source projector" and select "New Window" or a spare monitor if you want to.
2. In Discord, start **Go Live** or Share Your Screen and pick the **projector**: the projector window
   if it is windowed, or the second monitor if the projector is fullscreen on it. Not Space Engineers,
   and not the monitor Space Engineers is on.

Discord is now capturing an OBS surface, which has no player surface on it, so the stream is clean at
whatever quality Discord will carry. If you want game audio to go out with it, turn on **audio
monitoring** for the game's audio source in OBS (Advanced Audio Properties, Monitor and Output) so
OBS is actually playing the audio Discord is picking up.

Either way, verify once: ask whoever is watching whether they can see the HUD you filtered. Sharing
the wrong window is an easy mistake and it looks exactly like CleanFeed not working.

---

## 11. Troubleshooting

### The HUD is still in my recording

Check these in order.

1. **Is your recorder using Game Capture?** Display Capture, Monitor Capture, and NVIDIA Desktop
   Capture all record the desktop and will always show the player surface. Switch to a Game Capture
   source aimed at the Space Engineers window, and turn **Capture third-party overlays** off. If the
   complaint is coming from a Discord call, that is the same problem: Discord's own sharing captures
   the composed desktop, so relay through OBS as described in section 10.
2. **Is the redirect actually running?** Run `/cf hud status`. Look for `hud=active`. If it says `off`,
   run `/cf redirect`. If it says `faulted`, read the `fault=` field on the same line.
3. **Are the filters on?** Run `/cf hud all status`. Every item you want out of the recording should
   read `on`.
4. **Is the item something CleanFeed can route?** Open `/cf settings` and find it in the source list.
   If it is under UNSUPPORTED / UNATTRIBUTED, or its row says `partitioned-partial` and the leaking
   part is world geometry, it cannot be hidden. File a compatibility request from that row.
5. **Are you in exclusive fullscreen?** See below.

### Exclusive fullscreen is not supported

CleanFeed cannot show a desktop-composed player surface over an exclusive-fullscreen window, so it
refuses to start there, and it stops redirecting immediately if you switch to it mid-session, so that
you are never left without a HUD. Use **Fullscreen Window** or **Window**. `/cf hud status` reports the
current display mode.

### Resolution changes

Changing resolution or resizing the window is supported. The player surface is rebuilt and rescaled to
match. You do not need to restart the redirect. If something cursed happens, please open an issue on GitHub.

### What "fail-open" means

CleanFeed is built to never leave you flying blind. If any unexpected renderer, composition, GPU copy,
or commit error occurs, CleanFeed hides the player surface and restores the ordinary vanilla HUD by
the next rendered frame. Same for any adapter that throws. The two deliberate exceptions are the
projected RichHud screens - the Flip and Burn navigation menu and the GameCore Hand Terminal - whose
content is GPS lists: those fail CLOSED, withheld from both outputs, rather than open into the
recording.

The practical consequence for you: **a CleanFeed failure fails towards your HUD being recorded, not
towards your HUD disappearing.** If you are recording something sensitive, check a test clip after any
crash, driver reset, or alt-tab storm rather than assuming the filter held.

### Logs

CleanFeed writes to the normal `SpaceEngineers.log`, and every one of its lines is prefixed with
`[cleanfeed]`. Search the log for that tag. You will find the startup line, filter changes, redirect
start and stop, fault details, and a health heartbeat every five minutes.

### Filing an issue

Report bugs and third-party HUD compatibility requests at
https://github.com/arsnekfcn/CleanFeed/issues.

The fastest path: open `/cf settings`, press **REPORT** on the specific row that is misbehaving, or
**Request support** in the footer for a general problem. That copies a sanitized report to your
clipboard and opens the issue form. Paste it in and describe what you saw.

If you attach log excerpts, take only the `[cleanfeed]` lines, and skim what you paste before you post
it: in rare error cases a line can include a file path from your machine. Do not post world data,
server names, GPS coordinates, chat, or authentication data. The generated report already excludes all
of those.

---

## 12. Performance

These are measured numbers from one machine, not a promise about yours.

- **CleanFeed's own render-thread work is about 0.6 to 0.7 ms per frame.** That covers preparing the
  filtered sprites, one GPU copy, and one commit to the desktop compositor.
- **About 50 MB of extra video memory** at 1080p.
- **An A/B run at 2560x1440, 120 fps cap, modded world:** average frame time was unchanged at 8333 us
  both with the redirect off and with it on, a delta of 0.0%. p95 went from 9072 us to 10015 us. The
  worst single frame in the run was 35.2 ms with the redirect off against 15.1 ms with it on, so the
  tail did not get worse.
- **The cost is fixed per frame, so its share shrinks as your frame rate drops.** Against a 60 fps
  frame budget it is roughly 4%, and roughly 2% at 30 fps. The higher your frame rate, the bigger a
  bite it takes out of each frame.

---

## 13. Known issues

- **Intermittent HUD element flicker under extreme speed on a busy server.** At Flip and Burn travel
  speeds on a heavily loaded multiplayer server, a routed element, most often the toolbar, sometimes
  the chat box, can blink for a frame or two. Several separate causes of this have been found and
  fixed and it is substantially reduced from where it started, but one mechanism is still under
  observation, and counters that detect it are in `/cf hud status`. The failure direction is
  over-hiding: the element goes missing from your own view for a moment rather than appearing in the
  recording. If you see it, an issue with the `[cleanfeed]` log lines from around that moment is
 useful.

---

## 14. Limitations

- **Display Capture sees everything.** This is not a bug that can be fixed. The player surface lives on
  your desktop. Any capture method that copies the desktop copies it too.
- **Capture behavior varies by recorder and version.** OBS and NVIDIA change their capture paths
  between releases, and third-party overlay capture options can pull the player surface in. Always
  verify with a short test recording after updating your recorder, your GPU driver, or CleanFeed.
- **Some mod content is best-effort.** Detected providers, and the partial adapters, do what CleanFeed
  can attribute at runtime. Where it cannot attribute output, it fails open and tells you in the
  settings screen. Read the source list before you trust a new mod
  combination on a live stream. Request new mods via GitHub reporting.
- **It is a capture convenience, not a security boundary.** See [SECURITY.md](SECURITY.md).
- **Verify before you go live.** The whole plugin is a privacy tool, and the only real proof that it is
  working for your particular mod list, recorder, and driver is a test clip you have watched.
