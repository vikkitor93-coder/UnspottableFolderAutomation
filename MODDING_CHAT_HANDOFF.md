# Handoff for Branch · Unspottable Modding

The user requested this GitHub helper specifically to automate testing for your
Unity/BepInEx UnspottableExpanded work. It is not the browser social-stealth game.

Repository: https://github.com/vikkitor93-coder/UnspottableFolderAutomation

1. Read `project/AI_HANDOFF.md`, `project/MILESTONES.md`, `project/TESTING.md`, and
   `project/QA_ADAPTER.md`. Current mod source under `project/` is v0.9.10 H2.6 normal-lifecycle QA.
   Compare with the latest source ZIP in the modding chat; if that ZIP is newer, replace
   this baseline with that source first, preserving the helper integration.
2. Code belongs on `main`. Test evidence is on the separate `runner-reports` branch
   under `reports/<random-run-id>.json`. Fetch branch contents explicitly; do not
   search only the main branch. Associate every result with its full `revision`.
3. Reports distinguish build, bootstrap, gameplay, per-assertion PASS/FAIL/SKIP,
   and restoration. `punch.execution` is FSM evidence and `punch.impact` is reaction
   evidence, not getter counts or bot displacement. Preserve all critical SKIPs.
4. Fix only issues justified by actual evidence and the user's current milestone.
   A green build does not prove gameplay, and this helper's tests aren't game tests.
   Do not create repeated no-op commits to make the PC rerun a failing job.
5. Publish complete coherent updates to `main` once ready for the user to test,
   respecting any branch rules. Never force-push. Include the latest complete source
   ZIP in the chat when code changes. Don't put proprietary assemblies or private
   diagnostic data into GitHub. Don't commit runner-reports back to main.
6. If the QA adapter schema/assertion IDs change, update `qa_results.py`, its
   allowlist tests, and the wrapper together; unrecognized fields fail closed.
7. The runner asks for the game folder per session, builds with `UE_GAME_DIR`,
   temporarily deploys the DLL, invokes the existing H2 wrapper, and restores the
   old DLL. The wrapper accepts `UE_FOLDER_QA_OUTPUT` for a per-run output directory.
   Preserve this integration when importing a new source ZIP.

No AI-side schedule or webhook was configured by this helper. The user can request
another turn manually; a future supported AI trigger may consume report commits.
The helper never fabricates an AI response, clicks ChatGPT, or opens tab loops.
Do not run the Chrome-extension runner and this helper against the same game at
the same time. Manual test success is required before enabling automatic mode.

NEXT ACTION: user performs one revision-isolated Windows manual run of v0.9.10 H2.6, then inspect the matching `runner-reports` result. The previous rendered run stopped at `menu_post_start_main` (Local/Online). H2.5 now submits the visible Local UI choice before Rewired player automation. The key evidence is the first normal-lifecycle stage that succeeds/fails. Do not restore direct scene loads or ControlerManager bootstrap shortcuts.