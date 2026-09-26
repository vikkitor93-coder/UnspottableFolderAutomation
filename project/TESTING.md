# Testing — v0.9.10 H2.6

H1 remains an input-layer self-test. H2 is the full normal-lifecycle deterministic gameplay wrapper.

## H2 lifecycle policy

H2 launches the real game as a normal visible rendered process, with no `-batchmode` and no `-nographics`. The mod must not load a scene directly, create/reposition a player, initialize/reset ControlerManager, assign controllers through debug helpers, or fire start FSM events.

The rendered v0.9.9 run still stopped at `menu_post_start_main` (Local/Online), proving the direct Unity-UI submit experiment did not advance the real menu. H2.6 therefore removes that fallback and stops assuming logical Rewired Player 0 is the keyboard player. Pre-game selection and P1 join press Space at the shared `Rewired.Keyboard` controller layer. Rewired then applies the game's actual keyboard ownership and keyboard maps, including the System Player if the UI is configured to use it.

Expected stages are: normal boot → keyboard Space → Local flow → native support scenes → keyboard P1 join → P2 join → native START traversal → normal level selection → real gameplay actors → deterministic adapter. A failure at any stage is evidence; do not bypass it.

## Windows run

Close Unspottable, build/deploy the current source, then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-H2-Gameplay-SelfTest.ps1
```

The wrapper writes sanitized evidence to Downloads when run directly. The folder-automation runner may supply `UE_GAME_DIR` and `UE_FOLDER_QA_OUTPUT`; those integration points are preserved.

A gameplay PASS still requires critical deterministic assertions: ownership, injection, consumption, world movement, correlated `PlayerPunch` execution, qualifying impact when a target is available, and cleanup. P2 independence runs only with deterministic unique ownership.

No new dependencies are downloaded.
