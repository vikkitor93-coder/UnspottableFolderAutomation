# AI handoff — UnspottableExpanded v0.9.9 H2.5 Local menu submit

## Project identity

This is the Unity/BepInEx mod for the commercial PC game Unspottable, not the separate browser social-stealth project. Own only opt-in mod QA instrumentation and deterministic game tests. Do not rebuild the Windows runner, browser extension, or dashboard here.

## Evidence carried forward

The v0.9.6 real Windows run built successfully but H2 stopped in `menu_start_main` at stage 4 with `menu-player-timeout` and zero `PlayerUnspottable` objects. v0.9.7 H2.3 then tried native controller-manager initialization plus debug keyboard assignment. The user rejected that direction and required the full game to use its normal lifecycle without skipped player selection or gameplay. The first v0.9.8 batch/nographics run built successfully but exited from `menu_post_start_main` before H2 stage 1. The rendered v0.9.8 follow-up then visibly reached the Local/Online menu and timed out at stage 0 in `menu_post_start_main` with build PASS and zero players. That proved rendering/lifecycle startup works and isolated the missing pre-Local menu transition.

## v0.9.9 H2.5 architecture

H2.5 does not perform any direct scene load. It does not invoke ControlerManager lifecycle or assignment helpers, set Rewired `isPlaying`, spawn players, move transforms, or send PlayMaker start events. The game owns all scene transitions and player creation.

At `menu_post_start_main`, before any local player exists, the QA harness locates the visible Local Unity UI control and invokes its normal UI submit/click path. It does not choose Online and does not load a scene directly. Once the game enters the Local flow, the harness supplies process-local synthetic values through the existing Rewired getter patches. It waits for the normal support scenes, joins P1 then P2 via mapped actions, uses MoveX/MoveY to search for the physical native START area, accepts the highlighted level through menu input, then waits for a real `level_*_main` gameplay scene containing both selected players. Only then does the existing deterministic QA adapter run.

The verification layer still separates injection, consumption, world movement, punch execution (`PlayerPunch` FSM evidence), and punch impact (reaction-state evidence). Getter activity or target displacement alone is insufficient. Unsupported deterministic P2 ownership remains SKIP, not PASS. Cleanup remains critical.

## Privacy / dependencies

Evidence remains sanitized and bounded. No new dependencies are downloaded. Proprietary game binaries are not included.

## NEXT ACTION

Run one revision-isolated Windows `mod-qa` manual job for rendered v0.9.9 H2.5. Inspect the matching report. The most useful first result is which normal-lifecycle stage succeeds/fails: boot, P1 join, P2 join, native START traversal, level selection, gameplay actors, or the deterministic adapter assertions. Do not reintroduce direct scene loads or controller-manager initialization to force a pass.
