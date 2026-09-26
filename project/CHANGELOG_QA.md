# Focused change list — v0.9.6

- Added `src/QaVerification.cs` and kept normal gameplay opt-in behavior unchanged.
- Kept bridge protocol 4 and existing commands; added backward-compatible `QA ...` verification commands.
- Added per-player/action injection vs synthetic-consumption counters.
- Added deterministic actor ownership resolution and explicit P2 SKIP when not supported.
- Replaced H2's invalid “intercept counter increased = punch” check with correlated `PlayerPunch` FSM execution evidence using a 2.0 s execution window.
- Added QA-only prepared bot target plus reaction-state impact evidence; movement alone cannot pass impact.
- Added focused snapshots, bounded polling, explicit cleanup (with cleanup failure promoted to FAIL), PASS/FAIL/SKIP result schema, and runner-facing `Invoke-QAAdapter.ps1`.
- Sanitized evidence collection and removed Rewired player names/hardware model/absolute local paths from packaged evidence.
- Added `QA_ADAPTER.md`, `AI_HANDOFF.md`, `MILESTONES.md`, and `TESTING.md` because those docs were absent from the recovered v0.9.5 package.


# Focused change list — v0.9.7 H2.3

- Used the first reliable revision-isolated Windows runner report as source of truth.
- Confirmed build PASS and bootstrap stage-4 `menu-player-timeout` with zero gameplay players.
- Restored `ControlerManager.InitStartScene()` + `resetAllControler()` exactly once after native support scenes are loaded.
- Kept native `AssignKeyboardDebug` as the single P1 join path; no second raw `loadPlayer` call.
- Kept support-scene loading entirely owned by Unspottable.
- Removed obsolete H2 fields that were producing CS0169/CS0414 warning noise.


# Focused change list — v0.9.8 H2.4

- Replaced H2 direct scene loading with observation of the game's real boot/menu/gameplay lifecycle.
- Removed H2 calls that initialized/reset the controller manager, assigned keyboard players, disabled join, or otherwise manufactured lifecycle state.
- Player selection now requires two real menu PlayerUnspottable objects created after player-like Rewired join input.
- Native START is exercised with ordinary MoveX/MoveY input rather than transform movement or PlayMaker events.
- Level selection is accepted with ordinary menu input; gameplay-ready accepts any real level_*_main scene and requires both selected players.
- Preserved deterministic ownership/movement/punch evidence and privacy-safe cleanup/evidence rules.


## Rendered diagnostic follow-up

- The first v0.9.8 batch/nographics run built successfully but exited from `menu_post_start_main` before H2 stage 1.
- H2 now launches a normal visible rendered game window with only `-logFile`; `-batchmode` and `-nographics` are removed.
- `UE_QA_HEADLESS=0`, low-impact throttling is disabled for this diagnostic, and BelowNormal process priority is no longer forced.
- Lifecycle/player-input assertions are otherwise unchanged.


# Focused change list — v0.9.9 H2.5

- Confirmed a rendered v0.9.8 Windows run reaches the visible Local/Online menu in `menu_post_start_main`, then times out at H2 stage 0 with build PASS and zero players.
- Added an explicit pre-player Local-menu step: H2 finds the visible Unity UI object that identifies itself as Local and submits that control through its normal UI submit/click path.
- The helper refuses to classify a UI target as Local when its label/path also contains Online.
- No direct scene load, player spawning, ControlerManager initialization, transform movement, or PlayMaker start event was added.
- After Local is entered, the existing Rewired-based P1/P2 selection, native START traversal, level selection, gameplay-actor gate, deterministic assertions, and cleanup remain unchanged.


# Focused change list — v0.9.10 H2.6

- Confirmed the revision-isolated v0.9.9 rendered run still timed out at stage 0 in `menu_post_start_main`; direct Unity UI submission did not prove a valid Local-menu input path.
- Removed the direct Unity UI-submit fallback from H2.
- Stopped treating Rewired Player 0 as the keyboard player before P1 exists.
- Added QA-only Harmony postfixes for `Rewired.Keyboard.GetKey/GetKeyDown/GetKeyUp` using both `KeyboardKeyCode` and Unity `KeyCode` overloads when present.
- Pre-game selection and P1 join now emulate the official Space control at the shared Rewired keyboard-controller layer, allowing the game's actual keyboard ownership/maps (including possible System Player UI ownership) to decide who receives it.
- Kept direct scene loads, ControlerManager lifecycle mutation, debug assignment, player manufacturing/repositioning, and PlayMaker start events prohibited.
