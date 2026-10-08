"""Trusted inputs only: an external review digest, never approval asserted by a PR."""
import hashlib
import json
from pathlib import Path
import re

API_ID = 1360432948
CONTROL_ID = 1337465220
SHA = re.compile(r"[0-9a-f]{40}")
DIGEST = re.compile(r"[0-9a-f]{64}")
KINDS = {"portable", "portable-coverage", "mutation-benchmark"}
FINGERPRINT_KEYS = {
    "api", "tree", "base", "platform", "quality", "control", "image", "sdk",
    "scripts", "dependencyInputs", "testSelection", "coverageProfile", "environment", "baselines", "testInventory",
    "os", "architecture", "kind", "resourceProfile",
}


def require(value, message):
    if not value:
        raise ValueError(message)


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def load(path, approved_digest):
    require(isinstance(approved_digest, str) and DIGEST.fullmatch(approved_digest), "External approval digest required")
    raw = Path(path).read_bytes()
    require(hashlib.sha256(raw).hexdigest() == approved_digest, "Manifest differs from reviewed bytes")
    value = json.loads(raw)
    require(set(value) == {"version", "repositoryId", "owner", "sources", "tree", "base", "sdk", "image", "inputDigests", "tasks", "resourceProfile"}, "Unknown or incomplete manifest")
    require(value["resourceProfile"] in {"zero-used-swap-v1", "operational-stable-swap-v1"}, "Unknown resource profile")
    require(type(value["version"]) is int and value["version"] == 1 and value["repositoryId"] == API_ID and value["owner"] == "ctwoodwa", "Wrong API/owner contract")
    require(set(value["sources"]) == {"api", "platform", "quality", "control"}, "Incomplete source pins")
    require(all(isinstance(v, str) and SHA.fullmatch(v) for v in value["sources"].values()), "Sources must be immutable full SHAs")
    require(all(isinstance(value[k], str) and SHA.fullmatch(value[k]) for k in ("tree", "base")), "Missing exact tree/base")
    require(value['base']==value['sources']['api'], 'Snapshot profile requires base equal to API head')
    require(re.fullmatch(r"sha256:[0-9a-f]{64}", value["image"]), "Immutable image ID required")
    require(isinstance(value["sdk"], str) and value["sdk"], "SDK required")
    require(set(value["inputDigests"]) == {"scripts", "dependencyInputs", "testSelection", "coverageProfile", "environment", "baselines", "testInventory"}, "Incomplete equivalence inputs")
    require(all(DIGEST.fullmatch(v) for v in value["inputDigests"].values()), "Malformed input digest")
    require(isinstance(value["tasks"], list) and 1 <= len(value["tasks"]) <= 2, "At most two explicit tasks")
    ids = set()
    for task in value["tasks"]:
        require(set(task) == {"id", "kind"} and re.fullmatch(r"[a-z][a-z0-9-]{0,39}", task["id"]), "Invalid task identity")
        require(task["id"] not in ids and task["kind"] in KINDS, "Duplicate or unsupported task")
        ids.add(task["id"])
    # Mutation has separate memory/process behavior. It cannot inherit portable pair qualification.
    if any(t["kind"] == "mutation-benchmark" for t in value["tasks"]):
        require(len(value["tasks"]) == 1, "Mutation benchmark must reserve the machine exclusively")
        require(value['resourceProfile']=='zero-used-swap-v1', 'Mutation benchmark requires absolute-zero profile')
    return value


def admit_event(event, env, approved_workflow_sha):
    """Future private orchestration hook; public API events cannot reach a deep listener."""
    require(SHA.fullmatch(approved_workflow_sha or ""), "Reviewed private workflow SHA required")
    expected = {"GITHUB_REPOSITORY_ID": str(CONTROL_ID), "GITHUB_REPOSITORY": "Harborline-Software/harborline-control",
                "GITHUB_REF": "refs/heads/main", "GITHUB_WORKFLOW_SHA": approved_workflow_sha,
                "GITHUB_ACTOR_ID": "1328090", "GITHUB_ACTOR": "ctwoodwa", "GITHUB_TRIGGERING_ACTOR": "ctwoodwa", "GITHUB_RUN_ATTEMPT": "1"}
    require(all(env.get(k) == v for k, v in expected.items()), "Untrusted orchestration identity")
    require(env.get("GITHUB_EVENT_NAME") in {"schedule", "workflow_dispatch"}, "Deep work cannot be triggered by PR/push/workflow_run")
    repository = event.get("repository", {})
    require(repository.get("id") == CONTROL_ID and repository.get("private") is True and repository.get("fork") is False, "Private trusted repository required")
    require(not any(key in event for key in ("pull_request", "merge_group", "workflow_run")), "Untrusted event payload")
    require(not event.get("inputs"), "Caller inputs cannot select arbitrary deep source or commands")


def fingerprint(value, task):
    return {**value["sources"], "tree": value["tree"], "base": value["base"], "sdk": value["sdk"],
            "image": value["image"], **value["inputDigests"], "kind": task["kind"],
            "os": "linux", "architecture": "arm64", "resourceProfile":value["resourceProfile"]}


def classify(paths):
    """Conservative pilot: no source/path class is allowed to skip critical hosted proof."""
    require(isinstance(paths, list) and all(isinstance(p, str) and p and not p.startswith("/") and ".." not in p.split("/") for p in paths), "Invalid changed-path inventory")
    return {"hostedCritical": True, "deepEligible": False, "nativeReleaseRequired": True,
            "mutationCandidate": any(p.endswith(".cs") and "/tests/" not in p for p in paths)}
