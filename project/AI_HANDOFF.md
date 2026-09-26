# AI handoff — UnspottableExpanded v0.9.10 H2.6 keyboard Space

## Project identity

This is the Unity/BepInEx mod for the commercial PC game Unspottable, not the separate browser social-stealth project. Own only opt-in mod QA instrumentation and deterministic game tests. Do not rebuild the Windows runner, browser extension, or dashboard here.

## Evidence carried forward

The v0.9.6 real Windows run built successfully but H2 stopped in `menu_start_main` at stage 4 with `menu-player-timeout` and zero `PlayerUnspottable` objects. v0.9.7 H2.3 tried native controller-manager initialization plus debug keyboard assignment; that direction was rejected in favor of the game's normal lifecycle.

v0.9.8 removed those shortcuts. A batch/nographics run exited from `menu_post_start_main`; a rendered follow-up visibly reached Local/Online and timed out at stage 0. v0.9.9 H2.5 then tried submitting the visible Local Unity UI control directly, but the revision-isolated rendered report still ended at stage 0 in `menu_post_start_main` with build PASS and zero players. Therefore direct UI submission is not considered proven.

Earlier H1 evidence did prove the process-local Rewired Player getter injection layer (axis/button values can be injected and observed through Rewired), but it did not prove that Unspottable consumed those values to produce menu/player/world behavior.

## v0.9.10 H2.6 architecture

H2.6 tests the official keyboard path instead of assuming Rewired Player 0 is the keyboard player. Pre-game selection and P1 join call `PressQaOfficialKeyboardSpace`. It enumerates the System Player plus normal Players with `controllers.hasKeyboard`, scans enabled Keyboard Maps for `ActionElementMap.keyboardKeyCode == Space`, and injects each mapped Action into the owning Player. It also injects Space at `Rewired.Keyboard.GetKey/GetKeyDown/GetKeyUp` through QA-only Harmony postfixes as a fallback for direct keyboard-controller reads. Both `KeyboardKeyCode.Space` and Unity `KeyCode.Space` overloads are patched when present.

The direct Unity UI-submit fallback has been removed. H2.6 still performs no direct scene load, ControlerManager initialization/reset, debug controller assignment, player spawn/reposition, Rewired `isPlaying` mutation, or PlayMaker start event.

After a real P1 appears through the keyboard path, the existing normal-lifecycle P2, native START traversal, level-selection, gameplay actor gate, deterministic movement/punch assertions, and cleanup continue unchanged.

## Privacy / dependencies

Evidence remains sanitized and bounded. No new dependencies are downloaded. Proprietary game binaries are not included.

## NEXT ACTION

Run one revision-isolated Windows `mod-qa` manual job for rendered v0.9.10 H2.6. Watch whether Space advances the visible Local/Online menu. Then inspect the matching `runner-reports` result. If it advances to Local/player selection, the first key question is whether a real P1 appears after the same keyboard Space path. Do not restore direct UI submit, direct scene loads, or controller-manager bootstrap shortcuts.
