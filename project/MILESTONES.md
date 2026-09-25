# QA milestones — v0.9.9

## Completed in source

- Q3 evidence separation: injection, consumption, world movement, punch execution, punch impact.
- Q4 ownership safety: deterministic actor ↔ Rewired mapping; no array/name/order guessing.
- Q5 focused snapshots and bounded polling.
- Q6 machine PASS/FAIL/SKIP adapter results.
- Q7 bounded cleanup/timeouts.
- Q8 privacy-safe evidence.
- Q9 protocol 4/backward-compatible bridge commands.
- Q10 real build was proven on v0.9.6 against the user's installed assemblies.
- H2.5 source removes direct scene loads/player manufacturing and uses normal lifecycle + player-like input only.

## Pending real-game validation

- Q10b build v0.9.9 on the Windows game machine.
- Q11a normal P1 join through synthetic player-like input.
- Q11b normal P2 join through synthetic player-like input.
- Q11c native physical START flow reached by ordinary movement input.
- Q11d normal level selection reaches a real gameplay scene with both players.
- Q12 P1 movement and P2 independence when ownership is deterministic.
- Q13 punch execution via correlated PlayerPunch FSM transition.
- Q14 punch impact via reaction-state evidence.
- Q15 failure cleanup restores synthetic input/prepared target state.

A getter counter or a green build never proves gameplay.
