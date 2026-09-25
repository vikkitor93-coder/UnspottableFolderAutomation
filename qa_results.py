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
BOOTSTRAP_REASONS = {
    "not-run", "ready", "game-exited", "start-scene-timeout", "support-scenes-timeout",
    "native-keyboard-failed", "menu-player-timeout", "menu-player-disappeared",
    "meadow-load-timeout", "gameplay-player-timeout", "bootstrap-timeout",
    "bootstrap-exception", "other",
}
BOOTSTRAP_SCENES = {"unknown", "other", "menu_post_start_main", "menu_start_main", "level_meadow_main"}


def summarize_bootstrap(raw):
    """Reduce H2 bootstrap JSON to bounded non-sensitive diagnostic fields."""
    status = raw.get("status") if raw.get("status") in {"PASS", "FAIL", "SKIP"} else "FAIL"
    stage = raw.get("qaGameplayStage")
    if type(stage) is not int or not 0 <= stage <= 20:
        stage = None

    scene = raw.get("scene")
    if not isinstance(scene, str) or not scene:
        scene = "unknown"
    elif scene not in BOOTSTRAP_SCENES:
        scene = "other"

    def bounded_count(name):
        value = raw.get(name)
        return value if type(value) is int and 0 <= value <= 10000 else None

    reason = str(raw.get("reason") or "")
    lower = reason.lower()
    if status == "PASS":
        code = "ready"
    elif "game exited before gameplay-ready" in lower:
        code = "game-exited"
    elif "timeout after" in lower:
        code = "bootstrap-timeout"
    elif "exception:" in lower or lower.startswith("stage "):
        code = "bootstrap-exception"
    elif stage == 1:
        code = "start-scene-timeout"
    elif stage == 2:
        code = "support-scenes-timeout"
    elif stage == 3:
        code = "native-keyboard-failed"
    elif stage == 4:
        code = "menu-player-timeout"
    elif stage == 5:
        code = "menu-player-disappeared"
    elif stage == 7 and scene != "level_meadow_main":
        code = "meadow-load-timeout"
    elif stage == 7:
        code = "gameplay-player-timeout"
    else:
        code = "other"

    return {
        "status": status,
        "stage": stage,
        "reason": code,
        "scene": scene,
        "players": bounded_count("playerCount"),
        "bots": bounded_count("botCount"),
    }


def validate_summary(value):
    expected = {
        "build", "bootstrap", "bootstrap_stage", "bootstrap_reason", "bootstrap_scene",
        "bootstrap_players", "bootstrap_bots", "gameplay", "assertions", "error", "restored",
    }
    if set(value) != expected:
        raise ValueError("invalid-schema")
    if not (all(value[key] in STATES for key in ("build", "bootstrap", "gameplay"))):
        raise ValueError("invalid-schema")
    if not (value["error"] in ERRORS and type(value["restored"]) is bool):
        raise ValueError("invalid-schema")
    if not (value["bootstrap_reason"] in BOOTSTRAP_REASONS and value["bootstrap_scene"] in BOOTSTRAP_SCENES):
        raise ValueError("invalid-schema")
    if not (value["bootstrap_stage"] is None or
            (type(value["bootstrap_stage"]) is int and 0 <= value["bootstrap_stage"] <= 20)):
        raise ValueError("invalid-schema")
    for key in ("bootstrap_players", "bootstrap_bots"):
        if not (value[key] is None or (type(value[key]) is int and 0 <= value[key] <= 10000)):
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
