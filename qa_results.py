"""Project results reduced to an exact allowlist; no free-text evidence leaves PC."""
IDS = {
    "player.p1.ownership", "player.p2.ownership", "move.p1.injected", "move.p1.consumed",
    "move.p1.world", "move.p1.isolated-from-p2", "move.p2.injected", "move.p2.consumed",
    "move.p2.world", "move.p2.isolated-from-p1", "move.p2.independent", "punch.prepare-target",
    "punch.execution", "punch.impact", "punch.monitor", "punch.injected", "punch.consumed",
    "adapter.infrastructure", "cleanup.final",
}
STATES = {"PASS", "FAIL", "SKIP", "NOT_RUN"}
ERRORS = {"none", "prerequisites-missing", "game-already-running", "build-failed",
          "bootstrap-failed", "adapter-missing", "adapter-failed", "cancelled",
          "restore-failed", "internal-error", "recovery-required"}


def validate_summary(value):
    if not (set(value) == {"build", "bootstrap", "gameplay", "assertions", "error", "restored"}):
        raise ValueError("invalid-schema")
    if not (all(value[key] in STATES for key in ("build", "bootstrap", "gameplay"))):
        raise ValueError("invalid-schema")
    if not (value["error"] in ERRORS and type(value["restored"]) is bool):
        raise ValueError("invalid-schema")
    if not (isinstance(value["assertions"], list) and len(value["assertions"]) <= len(IDS)):
        raise ValueError("invalid-schema")
    seen = set()
    for item in value["assertions"]:
        if not (set(item) == {"id", "status", "critical"}):
            raise ValueError("invalid-schema")
        if not (item["id"] in IDS and item["id"] not in seen):
            raise ValueError("invalid-schema")
        seen.add(item["id"])
        if not (item["status"] in {"PASS", "FAIL", "SKIP"} and type(item["critical"]) is bool):
            raise ValueError("invalid-schema")
    return value


def summarize_adapter(raw):
    if raw.get("schemaVersion") != "ue.qa.adapter.v1" or raw.get("status") not in {"PASS", "FAIL", "SKIP"}:
        raise ValueError("invalid-adapter")
    assertions = []
    for item in raw["assertions"]:
        if item.get("id") not in IDS:
            raise ValueError("unknown-assertion")
        assertions.append({"id": item["id"], "status": item["status"], "critical": item["critical"]})
    return raw["status"], assertions
