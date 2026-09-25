# Unspottable Modding — GitHub folder helper

This helps the **Branch · Unspottable Modding** chat test the Unity/BepInEx mod on
your Windows PC. The chat publishes code to `main`; your PC downloads an exact
commit, builds it, runs H2, restores the previously installed DLL, and sends a
structured result to `runner-reports`. No browser extension is required for this
transport. The helper does not itself write AI fixes or start a ChatGPT turn.

## First run

1. Install **Python 3.11+** from <https://www.python.org/downloads/> (include the
   Python launcher) and **GitHub CLI** from <https://cli.github.com/> if missing.
   The mod also requires **.NET 8 SDK+**, your installed Unspottable game, and the
   existing compatible BepInEx setup. No game DLLs or paid game files are supplied.
2. Download the source ZIP, **extract the entire folder**, and double-click
   **SETUP.cmd** once. Sign into the GitHub account with write access to this repo.
   GitHub CLI manages sign-in; don't paste a token into any file or chat.
3. Close the game and stop the other extension/QA runner. Double-click
   **START.cmd**, choose **1**, and paste your game folder when asked. The folder
   is used only in memory for this window and is never included in reports.
4. The helper fetches `main`, builds the included mod, temporarily installs its
   DLL, runs the real headless H2 test, restores the previous DLL, and uploads the
   result. If the build fails it doesn't install anything. A failed or skipped
   H2 test is reported as such, not converted to PASS.

GitHub repository: <https://github.com/vikkitor93-coder/UnspottableFolderAutomation>

Results, after the first upload:
<https://github.com/vikkitor93-coder/UnspottableFolderAutomation/tree/runner-reports/reports>

## Manual first; automatic later

Every launch begins in manual mode. After a successful manual run and result
upload, choose **2** and type **AUTO**. It checks once a minute and tests each new
`main` commit once. Report commits go to a different branch, so they cannot create
an update/test loop. Ten new revisions or four hours ends that automatic session;
a running job has its own timeout. Any failed/skipped job or upload error also
ends it. Opening another window is blocked by an operating-system lock.

**STOP.cmd** cancels downloads, builds, tests, and polling. **Ctrl+C** also stops
the session. It never installs a scheduled task, startup service, or auto-restart.
Reopening START is an explicit new manual session. Cancellation cannot undo an
upload GitHub already accepted. Closing the window forcibly or losing power may
interrupt DLL restoration: close the game and run **RECOVER.cmd** in that case.
The next test refuses to replace an unresolved backup. Keep the original folder
so STOP and the running helper share the same state directory.

Windows child processes are contained using kill-on-close Job Objects and
cancelled by their owned process tree. An unrelated already-running game is never
killed. H2 temporarily launches its own game process. Only the test DLL is restored;
the game/BepInEx may independently write their normal logs/configuration files.

## Developer tools and diagnostics

Press **D** in the menu. It provides the latest source ZIP download link, opens the
exact ZIP downloaded for the most recent revision, and opens the latest sanitized
session JSON. Local helper data is in `.local`, which is excluded from Git.

Reports contain only schema/version, random run ID, tested commit, UTC start time,
step number, duration, exit code, fixed error categories, compiler diagnostic codes,
and allowlisted H2 assertion IDs with PASS/FAIL/SKIP and critical flags. They omit
raw stdout, arbitrary reason/evidence strings, local paths, names, tokens, device
information, network information, and full game logs. H2's intermediate files are
deleted after the summary is extracted. Game-created logs remain game-owned.

The repository is public. GitHub itself records the authenticated account and its
normal commit metadata; the report payload contains no account/device identifiers.
Failed uploads remain in `.local/pending`; menu **3** retries them without rerunning
the game. A retry recognizes an already-uploaded identical report.

## Continue in the modding chat

Give that chat [MODDING_CHAT_HANDOFF.md](MODDING_CHAT_HANDOFF.md), or tell it to read
that file in this repository. It should inspect results matching the code commit,
make the next justified mod change under `project`, update its handoff, and publish
the next complete revision to `main`. Your running automatic session can then test
it. **Uploading a report does not automatically wake that chat.** An AI-side trigger
is a separate step; this release supplies the PC-to-GitHub execution/results loop.

The bundled baseline is the retrieved v0.9.6 QA verification source, with its
provenance in `project/README.md`. If the modding chat has produced a newer source
ZIP, import that current ZIP before advancing gameplay work. The other extension
and runner project is independent and must not run a game test concurrently.

## Verification

Run `python -m unittest discover -s tests -v` from this folder. See TESTING.md for
what was verified and what still needs a real Windows/game run. No gameplay PASS
is claimed by the source checks. The root automation.json configures the real
mod build/H2 job, not a simulation.
