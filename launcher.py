"""Stable entry point. Downloads once, starts app once, never opens browser tabs."""
from pathlib import Path
import shutil
import sys
from core import (InstanceLock, RunnerError, download_revision, latest_revision, helper_event,
                  run_process)


def main():
    if sys.version_info < (3, 11):
        print("Install Python 3.11 or newer, then reopen START.cmd.")
        return 1
    root = Path(__file__).resolve().parent
    state = root / ".local"
    if not shutil.which("gh"):
        print("Install GitHub CLI from https://cli.github.com/ then run SETUP.cmd.")
        return 1
    try:
        with InstanceLock(state):
            (state / "STOP").unlink(missing_ok=True)
            helper_event(state, "starting")
            print("Getting the latest code from the configured GitHub repository...", flush=True)
            revision = latest_revision(state)
            source = download_revision(state, revision)
            helper_event(state, "update-ready")
            try:
                rc, _, _ = run_process([sys.executable, str(source / "app.py"), "--state", str(state),
                                        "--revision", revision], source, state,
                                       timeout=24 * 60 * 60, inherit=True, cancel_grace=12)
                return rc
            finally:
                shutil.rmtree(source, ignore_errors=True)
    except KeyboardInterrupt:
        helper_event(state, "cancelled")
        print("Stopped. Automatic mode is off.")
    except RunnerError as exc:
        if str(exc) != "already-running":
            helper_event(state, "setup-or-update-failed")
        print(f"Stopped: {exc}. If GitHub access failed, run SETUP.cmd and check repository access.")
    except Exception:
        print("Stopped: local setup/update error. No downloaded job was started.")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
