# Testing — v0.9.6

## Evidence levels

H1 is an **input-layer** self-test. It can prove synthetic Rewired values are installed and observable through patched getters. It does not prove world movement, punch execution, or punch impact.

H2 is the deterministic gameplay wrapper. It launches the real game in QA/headless mode, waits for the existing native keyboard bootstrap to reach Meadow, then invokes `Invoke-QAAdapter.ps1`. The adapter separates each gameplay evidence layer and always attempts cleanup.

## Easy Windows run

1. Close Unspottable.
2. Open the v0.9.6 source folder.
3. Double-click `build.bat` for a normal build/deploy/launch, or run `build.bat --no-launch` from Command Prompt to build/deploy without launching.
4. For the real gameplay verification, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-H2-Gameplay-SelfTest.ps1
```

5. The wrapper writes a sanitized `Unspottable-QA-H2-VERIFICATION-<timestamp>.zip` to Downloads. Inside, the authoritative result is `qa-adapter-gameplay-<timestamp>.json` when bootstrap reached gameplay-ready.

For input-only H1:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-H1-Input-SelfTest.ps1
```

## Dependencies and download progress

This update adds no package/runtime download step: **download progress 0 B / 0 B — complete**. The build uses the already-installed game assemblies under `Unspottable_Data\Managed`, BepInEx core assemblies, and an installed .NET 8 SDK or newer. If those prerequisites are missing, the build stops and does not download proprietary or third-party binaries automatically.

## Real-game pass conditions

A real H2 PASS requires all critical supported assertions to pass. P1 ownership, input injection, input consumption, and world movement are critical. Punch input must be consumed and must produce a correlated `PlayerPunch` FSM transition within 2.0 s. If a live target is prepared, impact requires a reaction-like FSM/active-state change within 2.0 s; ordinary displacement does not count. If a critical capability is unavailable, the result is SKIP rather than PASS. P2 independence is tested only when two actors have deterministic unique ownership. Final synthetic-input or prepared-target cleanup failure is itself a critical FAIL.

## Validation performed in this delivery environment

The actual Unspottable installation and proprietary game assemblies were not available here, and neither `dotnet` nor Windows PowerShell was available to execute the real build/wrappers. Therefore **no real-game test ran here**.

Performed locally against the source package:

- C# lexical/delimiter validation for `Plugin.cs` and `QaVerification.cs`.
- Static checks that protocol 4 and legacy input commands remain, the invalid H2 general-counter punch assertion is gone, cleanup is present, and the new verifier does not add scene-wide per-frame object scans.
- Static privacy checks for sanitized evidence paths/log collection.
- Simulated assertion-policy cases: getter-only punch must not pass execution; movement-only target change must not pass impact; a correlated `PlayerPunch` transition plus reaction-state evidence can pass; unsupported P2 remains SKIP.

Machine-readable validation outputs are under `validation/`.

## In-game developer menu

No in-game developer menu/`OnGUI` debug menu exists in the recovered v0.9.5 source, so none was removed or replaced. QA access remains environment-variable + loopback-bridge based. Source is the contents of this ZIP; sanitized logs/results are exported by the H1/H2 wrappers to Downloads. No identifying diagnostics were added.
