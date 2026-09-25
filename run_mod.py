"""Build + isolated H2 test for the recovered mod baseline. Restore installed DLL."""
import argparse
import json
import os
from pathlib import Path
import shutil
from core import Cancelled, RunnerError, atomic_json, check_stop, run_process
from qa_results import summarize_adapter, summarize_bootstrap, validate_summary


def run(state):
    source = Path(__file__).resolve().parent / "project"
    game = Path(os.environ.get("UE_GAME_DIR", ""))
    qa = dict(build="NOT_RUN", bootstrap="NOT_RUN", bootstrap_stage=None, bootstrap_reason="not-run",\n              bootstrap_scene="unknown", bootstrap_players=None, bootstrap_bots=None,\n              gameplay="NOT_RUN", assertions=[], error="none", restored=True)
    target = game / "BepInEx" / "plugins" / "UnspottableExpanded" / "UnspottableExpanded.dll"
    backup = target.with_name("UnspottableExpanded.uqa-backup")
    marker = target.with_name("UnspottableExpanded.uqa-testing")
    deployed = False
    had_original = False
    results = state / "h2-current"
    rc = 4
    try:
        if os.name != "nt" or not (game / "Unspottable.exe").is_file() or not shutil.which("dotnet"):
            qa["error"] = "prerequisites-missing"
            return 4
        code, output, _ = run_process(["tasklist", "/FI", "IMAGENAME eq Unspottable.exe", "/FO", "CSV", "/NH"],
                                      source, state, capture=True)
        if code or b"unspottable.exe" in output.lower():
            qa["error"] = "game-already-running"
            return 4
        if backup.exists() or marker.exists():
            # A power loss/forced close may have prevented restoration. Never overwrite backup.
            qa.update(error="recovery-required", restored=False)
            return 4
        for path in (game / "Unspottable_Data/Managed/UnityEngine.CoreModule.dll",
                     game / "Unspottable_Data/Managed/Rewired_Core.dll", game / "BepInEx/core/BepInEx.Core.dll"):
            if not path.is_file():
                qa["error"] = "prerequisites-missing"
                return 4
        code, _, codes = run_process(["dotnet", "build", "UnspottableExpanded.csproj", "-c", "Release", "-o", "output", "--nologo"],
                                     source, state, timeout=600)
        print(" ".join(codes), flush=True)
        if code:
            qa.update(build="FAIL", error="build-failed")
            return code
        built = source / "output/UnspottableExpanded.dll"
        if not built.is_file():
            qa.update(build="FAIL", error="build-failed")
            return 4
        qa["build"] = "PASS"
        check_stop(state)
        target.parent.mkdir(parents=True, exist_ok=True)
        had_original = target.exists()
        if had_original:
            shutil.copyfile(target, backup)
        marker.write_text("original-present" if had_original else "original-absent", encoding="ascii")
        deployed = True
        staged = target.with_name("UnspottableExpanded.uqa-new")
        shutil.copyfile(built, staged)
        os.replace(staged, target)
        if built.read_bytes() != target.read_bytes():
            raise RunnerError("deployment-verification-failed")
        if results.exists():
            shutil.rmtree(results)
        results.mkdir()
        os.environ["UE_FOLDER_QA_OUTPUT"] = str(results)
        rc, _, _ = run_process(["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                                "-File", str(source / "tools/Run-H2-Gameplay-SelfTest.ps1")],
                               source, state, timeout=240)
        bootstrap = results / "H2-bootstrap.json"
        if bootstrap.is_file():
            data = json.loads(bootstrap.read_text(encoding="utf-8-sig"))
            boot = summarize_bootstrap(data)
            qa["bootstrap"] = boot["status"]
            qa["bootstrap_stage"] = boot["stage"]
            qa["bootstrap_reason"] = boot["reason"]
            qa["bootstrap_scene"] = boot["scene"]
            qa["bootstrap_players"] = boot["players"]
            qa["bootstrap_bots"] = boot["bots"]
        adapter_files = list(results.glob("qa-adapter-gameplay-*.json"))
        if len(adapter_files) == 1:
            raw = json.loads(adapter_files[0].read_text(encoding="utf-8-sig"))
            qa["gameplay"], qa["assertions"] = summarize_adapter(raw)
        else:
            qa["error"] = "adapter-missing" if qa["bootstrap"] == "PASS" else "bootstrap-failed"
        if rc == 0 and (qa["bootstrap"] != "PASS" or qa["gameplay"] != "PASS"):
            rc = 4
        if rc and qa["error"] == "none":
            qa["error"] = "adapter-failed"
    except (Cancelled, KeyboardInterrupt):
        qa["error"] = "cancelled"
        rc = 4
    except Exception:
        qa["error"] = "internal-error"
        rc = 4
    finally:
        if deployed:
            try:
                if had_original:
                    os.replace(backup, target)
                else:
                    target.unlink(missing_ok=True)
                marker.unlink(missing_ok=True)
            except OSError:
                qa.update(error="restore-failed", restored=False)
                rc = 4
        # Only the allowlisted summary is retained by this helper.
        shutil.rmtree(results, ignore_errors=True)
        atomic_json(state / "qa-summary.json", validate_summary(qa))
    return rc


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--state", required=True)
    args = parser.parse_args()
    raise SystemExit(run(Path(args.state)))
