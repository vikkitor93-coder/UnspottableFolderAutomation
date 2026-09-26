UNSPOTTABLE EXPANDED v0.9.10 — H2.6 NORMAL LIFECYCLE

This is the Unity/BepInEx Unspottable mod, not the separate browser game.

Normal launches preserve the known-good safe lifecycle. QA instrumentation is dormant unless UE_QA_MODE=1.

H2.6 removes the rejected bootstrap shortcuts. The current diagnostic variant is rendered so the real menu/video flow can be observed. In QA gameplay mode it:
- launches the game as a normal visible rendered window (no -batchmode / -nographics);
- never performs a direct QA scene load;
- never initializes/resets ControlerManager or uses debug controller assignment;
- never spawns or repositions a player;
- drives pre-game/menu selection and P1 join by finding the actual Rewired keyboard owners and their enabled Space mappings;
- injects those mapped Actions into the owning Player/System Player, with a direct Rewired.Keyboard Space fallback;
- waits for the game's own menu/support scenes;
- joins P1 and P2 with process-local synthetic Rewired button values;
- reaches the native START flow with ordinary MoveX/MoveY player input;
- accepts the highlighted level with normal menu input;
- declares gameplay-ready only after a real level_*_main scene contains both selected players.

The deterministic verification layer remains intact: injection, consumption, world movement,
punch execution and punch impact are separate assertions. P2 independence remains evidence-gated.

No new dependencies are downloaded.
