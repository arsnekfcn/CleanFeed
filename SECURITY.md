# Security and privacy

CleanFeed changes where supported HUD draw calls are rendered. It is a capture convenience, not a
security boundary. Recorder behavior can change after an update, Display Capture includes the composed
player HUD, and unsupported third-party render paths intentionally remain visible. Test the exact game,
recorder, display mode, and plugin set before streaming sensitive locations or information.

## Data handling

CleanFeed has no telemetry service and sends no game or user data over the network. It stores only HUD
visibility preferences, written as a plain text file to `%APPDATA%\CleanFeed\CleanFeed.profile.ini`, and
writes renderer/resource health diagnostics to the normal game log. That fixed per-user path is used
instead of the game's plugin-local storage API because the API keys its folder to the compiled module
name, which Pulsar randomises on every from-source recompile; the current location is stable across
plugin updates and recompiles. A profile found in the older game Storage location is migrated forward
once on first load and the old file is left in place. Diagnostics omit entity IDs and do not collect
world names, server names, GPS coordinates, chat contents, or authentication data; logged exception
text has local directory paths scrubbed out.

The compatibility-report buttons are explicit user actions. They copy a sanitized provider report to
the clipboard and can open the public GitHub issue form in the default browser. CleanFeed does not submit
the report automatically.

## Native and runtime integration

CleanFeed is Windows-only. Its active route uses the game's Direct3D 11 device, a DirectComposition
surface, and Win32 window/focus queries. It patches with Harmony under the owner ID
`cleanfeed.hud-redirect`; teardown removes only that owner's patches. The patch surface is: the game's
sprite-render seams (`MyRender11.RenderMainSprites`, its sprite worker, `ConsumeMainSprites`, and
`Present`), the `MyRenderProxy` sprite-draw family (`DrawSprite`, `DrawSpriteAtlas`, `DrawSpriteExt`,
`DrawString`, `DrawStringAligned`, sprite scissor push/pop), render message processing,
`ExecuteCommands` deferred-command replay, and the `MyTransparentGeometry` billboard entry points;
`Draw` methods of GUI screens across the game's GUI assemblies; and specific third-party HUD provider
methods, including Text HUD backend constructors and one blocking gate on Flip and Burn's navigation
menu open while GPS is hidden from the recording. Discovered HUD-like mod types have their
`Draw`/`Render` methods scoped from the assembly scan itself, not only within the bounded 30-second
Text HUD sampling window, which bounds call-stack attribution rather than scope installation. All
patches are installed at load; routing only activates while a redirect is running. Unknown providers
default to recorded/visible until a user explicitly confirms a detected-provider fingerprint.
Renderer, composition, and focus-transition failures are designed to fail open unless the user has
enabled the unfocused-window privacy guard.

## Reporting a vulnerability

Open a private security advisory at https://github.com/arsnekfcn/CleanFeed/security/advisories/new. For
ordinary bugs and compatibility requests, use https://github.com/arsnekfcn/CleanFeed/issues.
