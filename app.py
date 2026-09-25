"""Interactive helper for the Branch · Unspottable Modding workflow.

The interactive controller never validates a newer revision with an older in-memory
schema. If main changes while this window is open, that exact revision is executed
inside its own downloaded app/core/worker set.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import sys
import time
import uuid
from datetime import datetime, timezone
from core import (VERSION, REPOSITORY, REPORT_BRANCH, RunnerError, Cancelled, helper_event,
                  atomic_json, check_stop, download_revision, latest_revision,
                  run_process, upload_report, validate_report, wait_cancellable)


def _bounded_fallback_report(state, revision):
    """Create a schema-valid failure report when the worker result itself is unusable."""
    qa = None
    qa_path = Path(state) / "qa-summary.json"
    if qa_path.is_file():
        try:
            from qa_results import validate_summary
            qa = validate_summary(json.loads(qa_path.read_text(encoding="utf-8")))
        except (ValueError, AssertionError, KeyError, TypeError, OSError, json.JSONDecodeError):
            qa = None
    return validate_report({
        "schema": 1,
        "runner_version": VERSION,
        "run_id": uuid.uuid4().hex,
        "revision": revision,
        "started_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "profile": "mod-qa",
        "status": "failed",
        "steps": [],
        "error": "internal-error",
        "qa": qa,
    })


def _run_exact_from_source(state, revision, source):
    """Run/validate/upload using code from the SAME revision as the job."""
    helper_event(state, "testing")
    print(f"Running code revision {revision[:12]}.", flush=True)
    session = Path(state) / "latest-session.json"
    session.unlink(missing_ok=True)
    (Path(state) / "qa-summary.json").unlink(missing_ok=True)
    rc, _, _ = run_process([sys.executable, str(Path(source) / "worker.py"), "--state", str(state),
                            "--revision", revision], source, state, timeout=22 * 3600,
                           inherit=True, cancel_grace=8)
    try:
        if not session.is_file():
            raise ValueError("worker-did-not-produce-result")
        report = validate_report(json.loads(session.read_text(encoding="utf-8")))
        if report["revision"] != revision:
            raise ValueError("result-revision-mismatch")
        if rc != 0 and report["status"] == "passed":
            raise ValueError("worker-result-mismatch")
    except (ValueError, AssertionError, KeyError, TypeError, OSError, json.JSONDecodeError):
        report = _bounded_fallback_report(state, revision)
        print("Worker result was invalid; uploading bounded diagnostic fallback.", flush=True)

    print(f"Result: {report['status'].upper()} ({report['profile']}).", flush=True)
    helper_event(state, "uploading")
    upload_report(state, report)
    helper_event(state, "uploaded")
    print(f"Result uploaded to GitHub / {REPORT_BRANCH} / reports.", flush=True)
    atomic_json(Path(state) / "last-run.json", {"revision": revision, "status": report["status"]})
    return report


def run_once(state, revision=None, app_revision=None):
    """Run newest revision without ever mixing old controller code with new result schemas."""
    revision = revision or latest_revision(state)
    app_revision = app_revision or revision
    current_source = Path(__file__).resolve().parent

    if revision == app_revision:
        return _run_exact_from_source(state, revision, current_source)

    # Main changed while this interactive app was already open. The old design ran
    # the new worker under this old app, which is what caused the repeated
    # "invalid local result or setup" failures. Delegate the WHOLE run to the
    # downloaded revision's app/core/worker instead.
    print(f"New code revision {revision[:12]} detected; handing off to its matching runner.", flush=True)
    source = download_revision(state, revision)
    try:
        rc, _, _ = run_process([sys.executable, str(source / "app.py"), "--state", str(state),
                                "--revision", revision, "--exact-once"], source, state,
                               timeout=22 * 3600, inherit=True, cancel_grace=8)
        last = Path(state) / "last-run.json"
        if not last.is_file():
            raise RunnerError("delegated-run-did-not-finish")
        summary = json.loads(last.read_text(encoding="utf-8"))
        if summary.get("revision") != revision or summary.get("status") not in ("passed", "failed", "cancelled", "skipped"):
            raise RunnerError("delegated-run-invalid-status")
        # A failed/skipped QA run is still a successfully completed handoff; the
        # structured report has already been uploaded by the matching revision.
        if rc not in (0, 1):
            raise RunnerError("delegated-run-failed")
        return summary
    finally:
        shutil.rmtree(source, ignore_errors=True)


def automatic(state, app_revision, interval=60, max_runs=10, max_hours=4):
    previous = json.loads((Path(state) / "last-run.json").read_text(encoding="utf-8"))
    if previous.get("status") != "passed":
        raise RunnerError("successful-manual-run-required")
    last = previous["revision"]
    deadline = time.monotonic() + max_hours * 3600
    count = 0
    print(f"Automatic mode ON for this window only: every {interval}s; at most {max_runs} new revisions / {max_hours} hours.")
    print("Double-click STOP.cmd or press Ctrl+C to cancel. Any failed/skipped run or upload error stops this mode.", flush=True)
    while count < max_runs and time.monotonic() < deadline:
        wait_cancellable(state, interval)
        if time.monotonic() >= deadline:
            break
        revision = latest_revision(state)
        if revision == last:
            continue
        report = run_once(state, revision, app_revision)
        last = revision
        count += 1
        if report["status"] != "passed":
            break
    print("Automatic mode OFF.", flush=True)


def retry_pending(state):
    count = 0
    for path in sorted((Path(state) / "pending").glob("*.json")):
        if path.stat().st_size > 65536:
            raise RunnerError("invalid-pending-result")
        report = validate_report(json.loads(path.read_text(encoding="utf-8")))
        if path.stem != report["run_id"]:
            raise RunnerError("invalid-pending-result")
        upload_report(state, report)
        count += 1
    print(f"Pending result upload finished ({count} uploaded).")
    return count


def developer(state):
    print(f"Developer tools / build {VERSION}")
    print(f"Source download: https://github.com/{REPOSITORY}/archive/refs/heads/main.zip")
    print("1: Open downloaded source ZIP   2: Latest test log   3: Latest helper status   Enter: Back")
    choice = input("> ").strip()
    path = Path(state) / {"1":"latest-source.zip", "2":"latest-session.json", "3":"latest-helper.json"}.get(choice, "latest-session.json")
    if choice in ("1", "2", "3"):
        if not path.is_file():
            print("Not available yet. Run a manual test first.")
        elif os.name == "nt":
            os.startfile(path)
        else:
            print(path)


def main(state, app_revision):
    state = Path(state)
    print(f"Unspottable Modding helper {VERSION} — manual mode")
    print("Builds and tests the mod; uploads structured results to its GitHub helper repository.")
    print("Stop the extension's other game runner before testing here. Never run both against the game.")

    # Runs that already completed locally should not require another game launch.
    pending = list((state / "pending").glob("*.json"))
    if pending:
        try:
            print(f"Found {len(pending)} pending result(s); retrying upload first...", flush=True)
            retry_pending(state)
        except (RunnerError, ValueError, AssertionError, KeyError, TypeError, OSError, json.JSONDecodeError):
            print("A pending result uses an older/incompatible format; leaving it local and continuing.", flush=True)

    while True:
        check_stop(state)
        print("\n1: Run latest once   2: Automatic mode   3: Retry pending uploads   D: Developer tools   Q: Quit")
        choice = input("> ").strip().lower()
        if choice == "q":
            return
        try:
            if choice in ("1", "2"):
                if not os.environ.get("UE_GAME_DIR"):
                    print("Paste the folder containing Unspottable.exe (used in memory for this session only):")
                    game = input("> ").strip().strip('"')
                    if not (Path(game) / "Unspottable.exe").is_file():
                        print("Game was not found there; nothing started.")
                        continue
                    os.environ["UE_GAME_DIR"] = game
                if choice == "1":
                    run_once(state, app_revision=app_revision)
                else:
                    last = state / "last-run.json"
                    if not last.is_file() or json.loads(last.read_text())["status"] != "passed":
                        print("Complete a successful manual run first.")
                        continue
                    print("Type AUTO to run new code revisions automatically during this session.")
                    if input("> ").strip() == "AUTO":
                        automatic(state, app_revision)
            elif choice == "3":
                retry_pending(state)
            elif choice == "d":
                developer(state)
        except Cancelled:
            raise
        except RunnerError as exc:
            helper_event(state, "run-or-upload-failed")
            print(f"Stopped: {exc}. Automatic mode is OFF. Results remain local if upload failed.")
        except (ValueError, AssertionError, KeyError, TypeError, OSError, json.JSONDecodeError):
            print("Stopped: invalid local result or setup. Automatic mode is OFF.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--state", required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--exact-once", action="store_true")
    args = parser.parse_args()
    try:
        if args.exact_once:
            report = _run_exact_from_source(Path(args.state), args.revision, Path(__file__).resolve().parent)
            raise SystemExit(0 if report["status"] == "passed" else 1)
        main(Path(args.state), args.revision)
    except (Cancelled, KeyboardInterrupt, EOFError):
        helper_event(Path(args.state), "cancelled")
        print("Stopped. Automatic mode is OFF.")
