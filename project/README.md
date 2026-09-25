# UnspottableExpanded mod baseline

Imported from `UnspottableExpanded-v0.9.6-qa-verification-source.zip` (SHA-256
`4d04a244382f5775551fb8b72244bd9380c1861a6b0ac1a8726c91c7c5ee7485`).
Read AI_HANDOFF.md, MILESTONES.md, TESTING.md, and QA_ADAPTER.md before changing it.
The modding chat owns gameplay changes; this helper only adds execution/transport.

The only integration change to this baseline is in tools/Run-H2-Gameplay-SelfTest.ps1:
`UE_GAME_DIR` overrides the old default folder, and `UE_FOLDER_QA_OUTPUT` selects a
per-run output directory and bypasses the Downloads ZIP export. C# is unchanged.
The original SHA256SUMS.txt describes the upstream ZIP, not the integrated files.

No proprietary game/BepInEx DLLs, game install, or SDK is included. The helper
builds against the locally installed assemblies with UE_GAME_DIR, temporarily
deploys that build for H2, then restores the previous mod DLL. It does not change
the installed version permanently. Do not run the extension runner simultaneously.
