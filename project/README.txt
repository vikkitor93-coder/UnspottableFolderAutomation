UNSPOTTABLE EXPANDED v0.9.7 — H2.3 NATIVE MANAGER INIT

This is the Unity/BepInEx Unspottable mod, not the separate browser game.

Normal launches preserve the known-good safe lifecycle. QA instrumentation is dormant unless UE_QA_MODE=1.

Real Windows evidence from v0.9.6:
- build PASS;
- menu_start_main and native support scenes loaded;
- H2 stopped at stage 4 with menu-player-timeout;
- PlayerUnspottable count remained 0;
- installed mod was restored successfully.

v0.9.7 changes only the QA bootstrap:
- after Unspottable itself has loaded global_player_ui + menu_start_ia,
  invoke ControlerManager.InitStartScene() and resetAllControler() exactly once;
- then use the native keyboard assignment helper exactly once;
- never manually load the support scenes;
- never combine AssignKeyboardDebug with a second raw loadPlayer for P1;
- remove obsolete H2 fields that produced CS0169/CS0414 warning noise.

The deterministic v0.9.6 verification layer remains intact: injection, consumption,
world movement, punch execution and punch impact are still separate assertions.

No new dependencies are downloaded.
