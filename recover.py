"""Offline recovery after a forced window close or power loss during deployment."""
import os
from pathlib import Path
from core import InstanceLock, RunnerError, run_process


def restore(game):
    target = Path(game) / "BepInEx/plugins/UnspottableExpanded/UnspottableExpanded.dll"
    backup = target.with_name("UnspottableExpanded.uqa-backup")
    marker = target.with_name("UnspottableExpanded.uqa-testing")
    if backup.is_file():
        os.replace(backup, target)
    elif marker.is_file() and marker.read_text(encoding="ascii") == "original-absent":
        target.unlink(missing_ok=True)
    elif marker.exists():
        raise RunnerError("backup-missing-do-not-delete-installed-mod")
    else:
        return False
    marker.unlink(missing_ok=True)
    target.with_name("UnspottableExpanded.uqa-new").unlink(missing_ok=True)
    return True


if __name__ == "__main__":
    state = Path(__file__).resolve().parent / ".local"
    try:
        with InstanceLock(state):
            (state / "STOP").unlink(missing_ok=True)
            game = input("Folder containing Unspottable.exe: ").strip().strip('"')
            if not (Path(game) / "Unspottable.exe").is_file():
                raise RunnerError("game-not-found")
            rc, out, _ = run_process(["tasklist", "/FI", "IMAGENAME eq Unspottable.exe", "/FO", "CSV", "/NH"], state, state, capture=True)
            if rc or b"unspottable.exe" in out.lower():
                raise RunnerError("close-game-first")
            print("Previous mod restored." if restore(game) else "No interrupted deployment to recover.")
    except (RunnerError, OSError, UnicodeError) as exc:
        print("Recovery stopped. Close the runner/game and check the backup. No automatic restart.")
