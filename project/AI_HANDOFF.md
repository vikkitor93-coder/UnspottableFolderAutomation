# AI handoff — UnspottableExpanded v0.9.7 H2.3 native manager init

> Provenance: `AI_HANDOFF.md`, `MILESTONES.md`, and `TESTING.md` were **absent** from the v0.9.5 H2.2 source package recovered from the newest available upgrade bundle. These files are newly created from the verified v0.9.5 source and the v0.9.6 changes; no missing prior handoff text was invented.

## Project identity and ownership

This is the Unity/BepInEx mod for the commercial PC game **Unspottable**, not the separate browser social-stealth project. This package owns only opt-in mod QA instrumentation and deterministic game tests. The Windows runner/supervisor, Chrome extension, and dashboard are owned by other chats/components.

Normal gameplay must stay untouched unless QA environment flags are explicitly set. Do not add OS-wide keyboard/mouse/controller automation, a second supervisor, a web server, proprietary game binaries, or any assertion that equates a general Rewired getter count with actual gameplay.

## Verified v0.9.5 bootstrap lifecycle

`src/Plugin.cs` uses `Assembly-CSharp` runtime reflection and Rewired. In QA gameplay mode it loads `menu_start_main`, waits for the game's own `global_player_ui` and `menu_start_ia` support scenes, enables native join, obtains Rewired player 0, calls native `AssignKeyboardDebug(ReInput.controllers.Keyboard)` once (fallback `AssignKeyboardToPlayer(Player1)`), waits for a real `PlayerUnspottable`, lets the native FSMs settle, calls `DisableJoinGame`, then loads `level_meadow_main`. Gameplay-ready requires at least one real `PlayerUnspottable`; bots are counted from runtime type `Bot`.

The v0.9.5 H2 script then injected movement across **all** Rewired players and considered punch successful when the general intercept counter increased. That punch assertion was invalid: it proved only that a patched getter was polled. It also could not establish independent P1/P2 ownership.

## v0.9.6 architecture

`src/Plugin.cs` remains the bootstrap/core and is now a partial class. `src/QaVerification.cs` adds the QA verification extension without changing the normal launch path. Bridge protocol remains 4.

Input evidence now tracks per Rewired player/action: injection count, synthetic getter reads, synthetic active reads, and neutralized real-input reads. World assertions use a focused snapshot of the mapped gameplay actor rather than a scene-wide per-frame scan.

Gameplay actor ownership resolves only from a unique direct `Rewired.Player` field reference on the actor hierarchy. A singleton fallback is allowed only when there is one live gameplay actor and one `isPlaying` Rewired player. P2 is skipped if deterministic ownership is not available.

Punch verification is correlated: a synthetic punch must first be returned active to gameplay; then an exact PlayMaker FSM named `PlayerPunch` must transition within 2.0 s. Impact requires target active-state/reaction-FSM evidence within 2.0 s of execution. Target displacement alone is explicitly insufficient.

`QA PREPAREPUNCH` can temporarily place the nearest live bot in the verified movement direction. `QA CLEANUP` restores the bot transform/active state, and the adapter clears synthetic input in `finally`.

## Evidence/privacy

Evidence helpers whitelist relevant QA/log lines and redact absolute local paths, credentials/tokens, and network addresses. Rewired player names were removed from metadata and lifecycle probes. `graphicsDevice` keeps only Unity's graphics API family rather than the hardware model. Session evidence no longer stores the game path or user-local paths.

## Build/runtime dependencies

No new dependency is downloaded by v0.9.6: **0 B / 0 B (100%)**. Building still requires the user's installed Unspottable/BepInEx assemblies and .NET 8 SDK or newer, exactly as the existing build script expects. Proprietary game DLLs are not included in this ZIP.

## Current limitation

The recovered v0.9.5 package contains no game assemblies, installed game, Windows PowerShell runtime, or Unity runtime in this execution environment. Therefore v0.9.6 has only static/source-policy validation and simulated assertion-policy validation here. No claim is made that H2 passed against the real game. A real Windows/game run is required next.

## NEXT ACTION

On the Windows game machine, build/deploy v0.9.6 with `build.bat --no-launch`, then run `tools\Run-H2-Gameplay-SelfTest.ps1`. Inspect the generated adapter JSON. The key expected outcome is that `punch.execution` only passes on a correlated `PlayerPunch` transition and `punch.impact` only passes on reaction-state evidence. If ownership is unresolved or P2 is not present, preserve the SKIP and capture the sanitized evidence ZIP rather than weakening the assertion.


## Real Windows evidence after runner isolation

The revision-isolated Windows runner is now uploading reliable bounded reports. The first clean report on revision `4aaa4eed...` showed:

- build: PASS;
- bootstrap: FAIL at stage 4;
- reason: `menu-player-timeout`;
- active scene: `menu_start_main`;
- PlayerUnspottable count: 0;
- bot count: 0;
- installed mod restoration: PASS.

This narrows the failure to native local-player creation, not compilation, support-scene loading, or result upload.

## v0.9.7 H2.3 change

After the game itself has loaded `global_player_ui` and `menu_start_ia`, H2.3 now invokes
`ControlerManager.InitStartScene()` and static `resetAllControler()` exactly once before
the existing native keyboard assignment. This is evidence-backed by the earlier native fast-boot path,
which used those manager initialization calls before `AssignKeyboardDebug`.

H2.3 still does not manually load support scenes and does not pair `AssignKeyboardDebug` with a
second raw `loadPlayer` for P1.

## NEXT ACTION (v0.9.7)

Run the revision-isolated Windows `mod-qa` job. If stage 4 now passes, inspect deterministic
ownership/movement/punch assertions. If stage 4 still times out, do not add a second player or guess
PlayMaker events; add a narrow bounded controller-manager result field or targeted one-time probe.
