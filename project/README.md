# UnspottableExpanded mod source — v0.9.10 H2.6

This is the Unity/BepInEx mod for the commercial PC game Unspottable, not the separate browser social-stealth project.

Read `AI_HANDOFF.md`, `MILESTONES.md`, `TESTING.md`, and `QA_ADAPTER.md` before changing it.

The folder-automation helper builds this source against the locally installed game/BepInEx assemblies using `UE_GAME_DIR`, temporarily deploys the test DLL, invokes the H2 wrapper, and restores the previous DLL. `UE_FOLDER_QA_OUTPUT` selects the runner-owned evidence directory.

H2.6 uses a visible rendered game process and preserves the game's normal scene lifecycle. Instead of assuming Rewired Player 0 is the keyboard player, pre-game selection and P1 join now emulate the official keyboard Space control at the shared `Rewired.Keyboard` controller layer. Rewired's own keyboard assignment/maps therefore decide whether the System Player or a game Player receives that key. After P1 exists, the existing normal lifecycle continues.

It does not directly load gameplay scenes, spawn/reposition players, initialize/reset ControlerManager, or fire PlayMaker start events. No proprietary game/BepInEx DLLs, game install, or SDK is included.
