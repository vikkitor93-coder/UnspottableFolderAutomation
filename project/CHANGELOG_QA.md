# Focused change list — v0.9.6

- Added `src/QaVerification.cs` and kept normal gameplay opt-in behavior unchanged.
- Kept bridge protocol 4 and existing commands; added backward-compatible `QA ...` verification commands.
- Added per-player/action injection vs synthetic-consumption counters.
- Added deterministic actor ownership resolution and explicit P2 SKIP when not supported.
- Replaced H2's invalid “intercept counter increased = punch” check with correlated `PlayerPunch` FSM execution evidence using a 2.0 s execution window.
- Added QA-only prepared bot target plus reaction-state impact evidence; movement alone cannot pass impact.
- Added focused snapshots, bounded polling, explicit cleanup (with cleanup failure promoted to FAIL), PASS/FAIL/SKIP result schema, and runner-facing `Invoke-QAAdapter.ps1`.
- Sanitized evidence collection and removed Rewired player names/hardware model/absolute local paths from packaged evidence.
- Added `QA_ADAPTER.md`, `AI_HANDOFF.md`, `MILESTONES.md`, and `TESTING.md` because those docs were absent from the recovered v0.9.5 package.
