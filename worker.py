"""One job, no updater recursion and no direct uploads from job commands."""
import argparse
from pathlib import Path
from core import run_job

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--state", required=True)
    parser.add_argument("--revision", required=True)
    args = parser.parse_args()
    result = run_job(Path(__file__).resolve().parent, Path(args.state), args.revision)
    raise SystemExit(0 if result["status"] == "passed" else 1)
