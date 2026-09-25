# UnspottableExpanded QA adapter — v0.9.9

This adapter is the boundary between the Windows runner and the mod's opt-in QA instrumentation. It is not a supervisor, browser extension, web server, or replacement launcher. The runner owns orchestration. The adapter talks only to the mod's existing loopback bridge on `127.0.0.1:24783`.

## Compatibility

The existing bridge stays protocol **4**. Existing commands remain valid: `PING`, `INFO`, the default state snapshot request, and all `INPUT ...` commands (`SETAXIS`, `PRESS`, `PROBEAXIS`, `PROBEBUTTON`, `CLEAR`, `CLEARALL`). `INFO` now adds `qaVerification:true`; no existing field was removed.

New extension name: `qa-verification-v1`.

## Runner command

With an already-running QA game process:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Invoke-QAAdapter.ps1 `
  -Test Gameplay -PlayerId 0 -SecondPlayerId 1 -TimeoutSeconds 18 `
  -OutputDir .\qa-results
```

`Invoke-QAAdapter.ps1` does **not** launch or stop Unspottable. It assumes the runner launched the game with `UE_QA_MODE=1`, `UE_QA_INPUT=1`, and `UE_QA_GAMEPLAY=1`. It always attempts `INPUT CLEARALL` and `QA CLEANUP` before it exits.

Supported parameters:

| Parameter | Default | Meaning |
|---|---:|---|
| `-Test` | `Gameplay` | Adapter test set. v1 supports `Gameplay`. |
| `-PlayerId` | `0` | Rewired player expected to own P1. |
| `-SecondPlayerId` | `1` | Rewired player used for independent P2 testing when deterministic ownership exists. |
| `-TimeoutSeconds` | `18` | Upper bound used for action/result polling. Individual bridge calls are bounded to 4 s. |
| `-OutputDir` | `./qa-results` | Directory for the machine-readable result. |
| `-MovementThreshold` | `0.10` | Minimum XZ displacement accepted as world movement. |
| `-IsolationThreshold` | `0.05` | Maximum XZ drift accepted for the non-injected player. |
| `-PunchTargetDistance` | `0.85` | QA-only bot placement distance in the verified movement direction. Clamped in the mod to 0.55–1.25. |

Exit codes: `0` = PASS, `2` = FAIL (an assertion failed), `3` = SKIP/PARTIAL (a critical capability was unavailable, so no PASS is emitted), `4` = adapter/infrastructure/protocol error.

## Result schema

Each invocation writes `qa-adapter-gameplay-<timestamp>.json` with schema `ue.qa.adapter.v1`:

```json
{
  "schemaVersion": "ue.qa.adapter.v1",
  "test": "Gameplay",
  "status": "PASS|FAIL|SKIP",
  "exitCode": 0,
  "startedUtc": "...",
  "finishedUtc": "...",
  "durationMs": 1234,
  "protocol": 4,
  "extension": "qa-verification-v1",
  "assertions": [
    {
      "id": "punch.execution",
      "layer": "punch execution",
      "status": "PASS|FAIL|SKIP",
      "critical": true,
      "reason": "...",
      "evidence": {}
    }
  ],
  "cleanup": {
    "inputCleared": true,
    "worldRestored": true
  }
}
```

The overall result is FAIL if any critical assertion fails. It is SKIP if there are no critical failures but at least one critical assertion could not be tested. Unsupported P2 in the current one-player H2 bootstrap is a non-critical SKIP; no independent-P2 claim is made.

## New bridge commands

`QA CAPABILITIES` returns mapped actor counts and whether deterministic ownership, P2, `PlayerPunch`, and a bot target are currently observable.

`QA ACTORS` returns focused gameplay actor records: instance ID, resolved Rewired player ID (or `-1`), ownership-resolution reason, and position. It does not export player names.

`QA SNAPSHOT <rewiredPlayerId>` returns one mapped actor's position, `PlayerPunch` state, and per-action evidence counters. This distinguishes acknowledged injections from synthetic getter reads.

`QA RESETCOUNTERS` clears only the QA evidence counters. It does not change gameplay state.

`QA PREPAREPUNCH <rewiredPlayerId> <dirX> <dirZ> [distance]` moves the nearest live bot to a bounded position in a **verified movement direction**. The original transform/active state are retained for `QA CLEANUP`. This command is QA-only.

`QA ARMPUNCH <rewiredPlayerId>` requires an exact PlayMaker FSM named `PlayerPunch` on the deterministically mapped actor. If unavailable, it returns `skip:true`; the adapter will not substitute a generic getter count.

`QA PUNCHSTATUS <rewiredPlayerId>` reports: synthetic punch consumed, correlated `PlayerPunch` FSM transition, target prepared, impact observed, impact evidence, and weak target displacement.

`QA CLEANUP` restores any QA-positioned bot and clears the punch monitor. It reports failure if a prepared target cannot be restored. The adapter also sends `INPUT CLEARALL` separately, and incomplete final cleanup is a critical FAIL rather than a successful result.

## Assertion semantics

The evidence layers are deliberately separate:

1. **Input injection** — bridge accepted `SETAXIS`/`PRESS`.
2. **Input consumption** — the game called a patched Rewired getter while that synthetic override was active. Neutralized real input is counted separately.
3. **World movement** — the deterministically mapped gameplay actor changed XZ position by the configured threshold.
4. **Punch execution** — after a synthetic punch was consumed, the exact `PlayerPunch` FSM changed state inside a 2.0 s correlation window. A general getter/intercept counter is never accepted here.
5. **Punch impact** — within 2.0 s after punch execution, the prepared target changed active state or showed a token-matched reaction-like PlayMaker transition (`hit`, `punch`, `knock`, `fall`, `stun`, `dead/death`, or `down`). Ordinary target displacement is recorded but **never** passes impact because an AI bot can walk by itself.

## Deterministic player ownership

For multi-player claims the mod only accepts a unique direct `Rewired.Player` field reference found on the gameplay actor hierarchy. The single-player H2 bootstrap may also use the unambiguous case of exactly one live gameplay actor plus exactly one Rewired player with `isPlaying=true`. It does not infer P1/P2 from array order, GameObject name, spawn position, or a general input counter.

## Output and sanitization

The adapter result contains no username, absolute game/user path, account ID, device/network identifier, secret, or unrelated log content. Human H1/H2 wrappers place sanitized evidence ZIPs in the user's Downloads folder, but the path itself is printed to the console rather than embedded in the evidence result.
