UNSPOTTABLE EXPANDED v0.9.6 — QA VERIFICATION

This is the Unity/BepInEx Unspottable mod, not the separate browser game.

Normal launches preserve the existing safe lifecycle. QA instrumentation is dormant unless UE_QA_MODE=1.
The H2 native-keyboard Meadow bootstrap is preserved from v0.9.5.

v0.9.6 adds:
- deterministic gameplay actor/Rewired ownership evidence;
- distinct injection, consumption, world movement, punch execution and impact assertions;
- PlayerPunch FSM correlation (general getter counts no longer prove a punch);
- P1/P2 independent tests only when ownership is actually resolved;
- focused snapshots and QA-only punch target preparation/restoration;
- machine-readable PASS/FAIL/SKIP adapter results;
- sanitized evidence collection;
- runner contract in QA_ADAPTER.md.

No new dependencies are downloaded: 0 B / 0 B.
Build still requires the installed game/BepInEx assemblies and .NET 8 SDK or newer.

Windows real-game verification:
  build.bat --no-launch
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-H2-Gameplay-SelfTest.ps1

See TESTING.md before interpreting PASS/SKIP/FAIL.
