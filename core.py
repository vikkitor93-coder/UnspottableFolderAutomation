"""Standard-library runner primitives. Never persist or upload raw command output."""
from __future__ import annotations

import base64
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
import uuid
import zipfile
from datetime import datetime, timezone

VERSION = "0.1.0"
REPOSITORY = "vikkitor93-coder/UnspottableFolderAutomation"
CODE_BRANCH = "main"
REPORT_BRANCH = "runner-reports"
MAX_ARCHIVE = 100 * 1024 * 1024
SHA = re.compile(r"^[0-9a-f]{40}$")
ERROR_CODE = re.compile(rb"\b(?:CS\d{4}|MSB\d{4}|NU\d{4})\b")


class RunnerError(Exception):
    """Only fixed categories escape this boundary, never raw exception details."""


class Cancelled(RunnerError):
    pass


def stopped(state):
    return (Path(state) / "STOP").exists()


def check_stop(state):
    if stopped(state):
        raise Cancelled("cancelled")


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    os.replace(tmp, path)


def helper_event(state, event):
    allowed = {"starting", "update-ready", "setup-or-update-failed", "cancelled",
               "testing", "uploading", "uploaded", "run-or-upload-failed"}
    if event not in allowed:
        raise ValueError("invalid-helper-event")
    atomic_json(Path(state) / "latest-helper.json", {"runner_version": VERSION, "event": event})


class InstanceLock:
    """OS-owned lock: closing the window or a crash automatically releases it."""
    def __init__(self, state):
        self.path = Path(state) / "runner.lock"
        self.file = None

    def __enter__(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.file = self.path.open("a+b")
        self.file.seek(0)
        self.file.write(b"0")
        self.file.flush()
        self.file.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self.file.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.file, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            self.file.close()
            raise RunnerError("already-running") from None
        return self

    def __exit__(self, *args):
        self.file.close()


def kill_tree(process):
    if os.name == "nt":
        if process.poll() is None:
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           timeout=10, check=False)
    else:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()


def run_process(argv, cwd, state, timeout=60, capture=False, limit=1024 * 1024,
                input_bytes=None, inherit=False, cancel_grace=0):
    """Drain bounded output in memory; only fixed compiler codes survive."""
    check_stop(state)
    env = os.environ.copy()
    env.update(GH_PROMPT_DISABLED="1", GIT_TERMINAL_PROMPT="0", GH_HOST="github.com")
    options = {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == "nt" else {"start_new_session": True}
    try:
        process = subprocess.Popen(argv, cwd=cwd, env=env,
                                   stdin=subprocess.PIPE if input_bytes is not None else (None if inherit else subprocess.DEVNULL),
                                   stdout=None if inherit else subprocess.PIPE,
                                   stderr=None if inherit else subprocess.STDOUT, **options)
    except OSError:
        raise RunnerError("command-unavailable") from None
    job = None
    if os.name == "nt":
        try:
            from windows_job import OwnedJob
            job = OwnedJob(process)
        except OSError:
            kill_tree(process)
            raise RunnerError("process-containment-failed") from None
    collected = bytearray()
    codes = set()
    oversized = threading.Event()

    def drain():
        tail = b""
        while True:
            chunk = process.stdout.read(8192)
            if not chunk:
                break
            codes.update(code.decode("ascii") for code in ERROR_CODE.findall(tail + chunk))
            tail = chunk[-16:]
            if capture:
                if len(collected) + len(chunk) > limit:
                    oversized.set()
                elif not oversized.is_set():
                    collected.extend(chunk)

    thread = None
    if not inherit:
        thread = threading.Thread(target=drain, daemon=True)
        thread.start()
    if input_bytes is not None:
        def feed():
            try:
                process.stdin.write(input_bytes)
                process.stdin.close()
            except (BrokenPipeError, OSError):
                pass
        threading.Thread(target=feed, daemon=True).start()
    start = time.monotonic()
    try:
        while process.poll() is None:
            check_stop(state)
            if time.monotonic() - start > timeout:
                raise RunnerError("timeout")
            if oversized.is_set():
                raise RunnerError("output-too-large")
            time.sleep(0.1)
        if thread:
            thread.join(timeout=2)
            if thread.is_alive():
                raise RunnerError("background-process-not-supported")
        check_stop(state)
        if oversized.is_set():
            raise RunnerError("output-too-large")
        return process.returncode, bytes(collected), sorted(codes)[:100]
    except (Cancelled, KeyboardInterrupt):
        (Path(state) / "STOP").touch()
        if cancel_grace:
            try:
                process.wait(timeout=cancel_grace)
            except subprocess.TimeoutExpired:
                pass
        raise
    finally:
        # On POSIX also clean descendants left behind by a completed command.
        if process.poll() is None or os.name != "nt":
            kill_tree(process)
        if job:
            job.close()
        if thread:
            thread.join(timeout=2)
            if not thread.is_alive():
                process.stdout.close()


def api(state, endpoint, payload=None, binary=False):
    args = ["gh", "api", "--hostname", "github.com", endpoint]
    data = None
    if payload is not None:
        args += ["--method", "POST" if endpoint.endswith("/git/refs") else "PUT", "--input", "-"]
        data = json.dumps(payload).encode()
    rc, out, _ = run_process(args, state, state, timeout=90, capture=True,
                             limit=MAX_ARCHIVE if binary else 2 * 1024 * 1024,
                             input_bytes=data)
    if rc:
        raise RunnerError("github-request-failed")
    if binary:
        return out
    try:
        return json.loads(out)
    except (ValueError, UnicodeError):
        raise RunnerError("invalid-github-response") from None


def latest_revision(state):
    result = api(state, f"repos/{REPOSITORY}/commits/{CODE_BRANCH}")
    revision = result.get("sha", "")
    if not SHA.fullmatch(revision):
        raise RunnerError("invalid-revision")
    return revision


def extract_archive(data, destination):
    """Reject traversal, links, zip bombs, duplicates, and Windows path tricks."""
    destination = Path(destination)
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        members = archive.infolist()
        if len(members) > 10000 or sum(m.file_size for m in members) > MAX_ARCHIVE:
            raise RunnerError("archive-too-large")
        roots = set()
        seen = set()
        for member in members:
            # Windows ZipInfo normalizes backslashes; validate before that rewrite.
            name = member.orig_filename
            parts = PurePosixPath(name).parts
            if not parts or name.startswith("/") or "\\" in name or ":" in name or any(
                p in (".", "..") or p.rstrip(" .") != p or p.split(".")[0].upper() in
                {"CON", "PRN", "AUX", "NUL", *[f"COM{i}" for i in range(10)], *[f"LPT{i}" for i in range(10)]}
                for p in parts
            ) or (member.external_attr >> 16) & 0o170000 == 0o120000:
                raise RunnerError("unsafe-archive")
            roots.add(parts[0])
            relative = Path(*parts[1:])
            key = str(relative).casefold()
            if len(parts) > 1 and key in seen:
                raise RunnerError("duplicate-archive-path")
            seen.add(key)
        if len(roots) != 1:
            raise RunnerError("invalid-archive-root")
        destination.mkdir(parents=True, exist_ok=False)
        for member in members:
            relative = Path(*PurePosixPath(member.filename).parts[1:])
            target = destination / relative
            if member.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(member) as source, target.open("xb") as output:
                    shutil.copyfileobj(source, output)


def download_revision(state, revision):
    if not SHA.fullmatch(revision):
        raise RunnerError("invalid-revision")
    # Fresh directory each time; builds never alter an update cache or user source.
    runs = Path(state) / "runs"
    runs.mkdir(exist_ok=True)
    target = runs / uuid.uuid4().hex
    data = api(state, f"repos/{REPOSITORY}/zipball/{revision}", binary=True)
    check_stop(state)
    try:
        extract_archive(data, target)
        for filename in ("app.py", "worker.py", "core.py", "automation.json"):
            if not (target / filename).is_file():
                raise RunnerError("incomplete-update")
        # Source ZIP is the exact GitHub archive, before any build modifications.
        temp = Path(state) / "latest-source.zip.tmp"
        temp.write_bytes(data)
        os.replace(temp, Path(state) / "latest-source.zip")
        return target
    except Exception:
        shutil.rmtree(target, ignore_errors=True)
        raise


def load_manifest(source):
    try:
        manifest = json.loads((Path(source) / "automation.json").read_text(encoding="utf-8"))
        if not (manifest["schema"] == 1):
            raise ValueError("invalid-schema")
        if not (manifest["profile"] in ("runner-self-test", "mod-build", "mod-qa", "custom")):
            raise ValueError("invalid-schema")
        steps = manifest["steps"]
        if not (isinstance(steps, list) and 1 <= len(steps) <= 20):
            raise ValueError("invalid-schema")
        for step in steps:
            argv = step["argv"]
            if not (isinstance(argv, list) and 1 <= len(argv) <= 64):
                raise ValueError("invalid-schema")
            if not (all(isinstance(arg, str) and arg and "\x00" not in arg and len(arg) <= 4096 for arg in argv)):
                raise ValueError("invalid-schema")
            if not (type(step["timeout_seconds"]) is int and 1 <= step["timeout_seconds"] <= 3600):
                raise ValueError("invalid-schema")
        return manifest
    except (OSError, ValueError, AssertionError, KeyError, TypeError):
        raise RunnerError("invalid-job-manifest") from None


STATUSES = {"passed", "failed", "cancelled", "skipped"}
CATEGORIES = {"none", "cancelled", "timeout", "command-unavailable", "command-failed",
              "output-too-large", "background-process-not-supported", "process-containment-failed", "internal-error", "invalid-job-manifest", "capability-unavailable"}


def validate_report(report):
    # Exact schema prevents a pending-file edit or worker output adding raw data.
    if not (set(report) == {"schema", "runner_version", "run_id", "revision", "started_utc", "profile", "status", "steps", "error", "qa"}):
        raise ValueError("invalid-schema")
    if not (report["schema"] == 1 and report["runner_version"] == VERSION):
        raise ValueError("invalid-schema")
    if not (re.fullmatch(r"[0-9a-f]{32}", report["run_id"])):
        raise ValueError("invalid-schema")
    if not (SHA.fullmatch(report["revision"])):
        raise ValueError("invalid-schema")
    if not (re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", report["started_utc"])):
        raise ValueError("invalid-schema")
    if not (report["profile"] in ("runner-self-test", "mod-build", "mod-qa", "custom")):
        raise ValueError("invalid-schema")
    if not (report["status"] in STATUSES and report["error"] in CATEGORIES):
        raise ValueError("invalid-schema")
    if not (isinstance(report["steps"], list) and len(report["steps"]) <= 20):
        raise ValueError("invalid-schema")
    if report["qa"] is not None:
        from qa_results import validate_summary
        validate_summary(report["qa"])
    for step in report["steps"]:
        if not (set(step) == {"number", "status", "exit_code", "duration_seconds", "compiler_codes", "error"}):
            raise ValueError("invalid-schema")
        if not (type(step["number"]) is int and 1 <= step["number"] <= 20):
            raise ValueError("invalid-schema")
        if not (step["status"] in STATUSES and step["error"] in CATEGORIES):
            raise ValueError("invalid-schema")
        if not (step["exit_code"] is None or type(step["exit_code"]) is int):
            raise ValueError("invalid-schema")
        if not (type(step["duration_seconds"]) in (int, float) and 0 <= step["duration_seconds"] <= 3700):
            raise ValueError("invalid-schema")
        if not (isinstance(step["compiler_codes"], list) and len(step["compiler_codes"]) <= 100):
            raise ValueError("invalid-schema")
        if not (all(isinstance(c, str) and re.fullmatch(r"(?:CS\d{4}|MSB\d{4}|NU\d{4})", c) for c in step["compiler_codes"])):
            raise ValueError("invalid-schema")
    return report


def run_job(source, state, revision):
    report = dict(schema=1, runner_version=VERSION, run_id=uuid.uuid4().hex,
                  revision=revision, started_utc=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
                  profile="custom", status="failed", steps=[], error="none", qa=None)
    try:
        manifest = load_manifest(source)
        report["profile"] = manifest["profile"]
        report["status"] = "passed"
        for index, spec in enumerate(manifest["steps"], 1):
            check_stop(state)
            print(f"Step {index}/{len(manifest['steps'])} running...", flush=True)
            start = time.monotonic()
            step = dict(number=index, status="failed", exit_code=None,
                        duration_seconds=0, compiler_codes=[], error="none")
            try:
                argv = [sys.executable if arg == "$PYTHON" else str(state) if arg == "$STATE" else arg for arg in spec["argv"]]
                rc, _, codes = run_process(argv, source, state, spec["timeout_seconds"], cancel_grace=4)
                step.update(exit_code=rc, compiler_codes=codes,
                            status="passed" if rc == 0 else "skipped" if rc == 3 and manifest["profile"] == "mod-qa" else "failed",
                            error="none" if rc == 0 else "capability-unavailable" if rc == 3 and manifest["profile"] == "mod-qa" else "command-failed")
            except (RunnerError, KeyboardInterrupt) as exc:
                category = str(exc) if isinstance(exc, RunnerError) else "cancelled"
                step.update(status="cancelled" if category == "cancelled" else "failed", error=category)
            finally:
                step["duration_seconds"] = round(min(3700, time.monotonic() - start), 2)
                report["steps"].append(step)
            print(f"Step {index}: {step['status']}; compiler codes: {', '.join(step['compiler_codes']) or 'none'}", flush=True)
            if step["status"] != "passed":
                report.update(status=step["status"], error=step["error"])
                break
    except (RunnerError, KeyboardInterrupt) as exc:
        category = str(exc) if isinstance(exc, RunnerError) else "cancelled"
        report.update(status="cancelled" if category == "cancelled" else "failed", error=category)
    except Exception:
        report.update(status="failed", error="internal-error")
    qa_file = Path(state) / "qa-summary.json"
    if report["profile"] == "mod-qa" and qa_file.is_file():
        try:
            from qa_results import validate_summary
            report["qa"] = validate_summary(json.loads(qa_file.read_text(encoding="utf-8")))
            if report["status"] == "passed" and (report["qa"]["gameplay"] != "PASS" or not report["qa"]["restored"]):
                report.update(status="failed", error="command-failed")
        except Exception:
            report.update(qa=None, status="failed", error="internal-error")
    elif report["profile"] == "mod-qa" and report["status"] == "passed":
        report.update(status="failed", error="internal-error")
    validate_report(report)
    atomic_json(Path(state) / "latest-session.json", report)
    atomic_json(Path(state) / "pending" / (report["run_id"] + ".json"), report)
    return report


def upload_report(state, report):
    validate_report(report)
    check_stop(state)
    try:
        api(state, f"repos/{REPOSITORY}/git/ref/heads/{REPORT_BRANCH}")
    except Cancelled:
        raise
    except RunnerError:
        try:
            api(state, f"repos/{REPOSITORY}/git/refs", {"ref": f"refs/heads/{REPORT_BRANCH}", "sha": report["revision"]})
        except RunnerError:
            # Another machine may have created the branch. A read verifies that.
            api(state, f"repos/{REPOSITORY}/git/ref/heads/{REPORT_BRANCH}")
    endpoint = f"repos/{REPOSITORY}/contents/reports/{report['run_id']}.json"
    body = json.dumps(report, indent=2) + "\n"
    try:
        api(state, endpoint, {"message": "Add structured runner result", "branch": REPORT_BRANCH,
                              "content": base64.b64encode(body.encode()).decode()})
    except Cancelled:
        raise
    except RunnerError:
        # An interrupted response may still have committed: recognize exact retry.
        existing = api(state, endpoint + f"?ref={REPORT_BRANCH}")
        if base64.b64decode(existing.get("content", "")).decode() != body:
            raise RunnerError("report-upload-failed") from None
    (Path(state) / "pending" / (report["run_id"] + ".json")).unlink(missing_ok=True)


def wait_cancellable(state, seconds):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        check_stop(state)
        time.sleep(min(0.2, max(0, end - time.monotonic())))
