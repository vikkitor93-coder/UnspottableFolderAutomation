# QA milestones

## Completed in v0.9.6 source

- **Q3 Evidence separation:** injection, consumption, world movement, punch execution, and punch impact are distinct assertions.
- **Q4 Ownership safety:** deterministic actor ↔ Rewired mapping; no array/name/order guess for P1/P2.
- **Q5 Focused snapshots:** bridge queries target mapped actors and cached bots; the verifier does not add whole-scene scans every frame.
- **Q6 Machine results:** adapter emits `ue.qa.adapter.v1` PASS/FAIL/SKIP assertions and bounded exit codes.
- **Q7 Cleanup/timeouts:** bridge calls/polling are bounded; input and QA-positioned target cleanup run in `finally`.
- **Q8 Evidence privacy:** sanitized state/metadata/log collection, no player names, absolute personal paths, secrets, or network addresses in packaged evidence.
- **Q9 Backward compatibility:** protocol 4 and existing bridge/input commands are retained; new commands live under `QA ...`.

## Pending real-game validation

- **Q10 Real build:** compile v0.9.6 against the user's exact installed Unity/Rewired/BepInEx assemblies.
- **Q11 P1 movement:** verify mapped Rewired player 0 consumes only its injection and moves the mapped world actor.
- **Q12 P2 independence:** only when two uniquely mapped gameplay actors exist, inject P1 then P2 separately and verify the other actor remains below drift threshold.
- **Q13 Punch execution:** verify a consumed synthetic punch causes a `PlayerPunch` FSM transition.
- **Q14 Punch impact:** verify the prepared bot produces reaction-state evidence. Movement-only evidence must remain a failure.
- **Q15 Failure cleanup:** intentionally fail/timeout one run and confirm synthetic input is cleared and prepared target state is restored.

A milestone is not complete merely because a getter counter increases.


## Real-run progress

- **Q10 Real build:** COMPLETE — v0.9.6 built successfully against the user's installed game/BepInEx assemblies.
- **H2 bootstrap support scenes:** COMPLETE — runner reached `menu_start_main` with native support scenes loaded.
- **H2 native player creation:** ACTIVE — v0.9.6 stopped at stage 4 with zero `PlayerUnspottable`; v0.9.7 H2.3 restores the native ControlerManager init/reset sequence before keyboard assignment.
