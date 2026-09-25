# UnspottableExpanded mod source — v0.9.9 H2.5

This is the Unity/BepInEx mod for the commercial PC game Unspottable, not the separate browser social-stealth project.

Read `AI_HANDOFF.md`, `MILESTONES.md`, `TESTING.md`, and `QA_ADAPTER.md` before changing it.

The folder-automation helper builds this source against the locally installed game/BepInEx assemblies using `UE_GAME_DIR`, temporarily deploys the test DLL, invokes the H2 wrapper, and restores the previous DLL. `UE_FOLDER_QA_OUTPUT` selects the runner-owned evidence directory.

H2.5 uses a visible rendered game process. It preserves the game's normal scene lifecycle. At the pre-player Local/Online menu it locates the visible Local Unity UI control and submits that UI choice; after Local is entered, player selection and gameplay are driven through process-local Rewired input. It does not directly load gameplay scenes, spawn/reposition players, initialize/reset ControlerManager, or fire PlayMaker start events.

No proprietary game/BepInEx DLLs, game install, or SDK is included. Do not run the browser-extension QA runner and the folder-automation runner against the same game process at the same time.
