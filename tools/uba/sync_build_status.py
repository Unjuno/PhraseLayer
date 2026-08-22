#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any, Iterable

API_BASE_DEFAULT = "https://build-api.cloud.unity3d.com/api/v1"
ACTIVE_STATUSES = {"queued", "senttobuilder", "started", "restarted"}
SUCCESS_STATUSES = {"success"}
FAILURE_STATUSES = {"failure", "canceled", "cancelled", "unknown"}
CS_ERROR_RE = re.compile(r"^.*error CS\d{4}.*$", re.MULTILINE | re.IGNORECASE)
REVISION_KEYS = {
    "lastbuiltrevision", "revision", "commit", "commitsha", "commitid", "changeset",
    "changesets", "scmrevision", "sourceversion", "sha",
}


class ApiError(RuntimeError):
    pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # type: ignore[override]
        return None


class UnityBuildAutomationApi:
    def __init__(self, api_key: str, api_base: str = API_BASE_DEFAULT, timeout: float = 30.0):
        if not api_key.strip():
            raise ValueError("Unity Build Automation API key is empty")
        self.api_key = api_key.strip()
        self.api_base = api_base.rstrip("/")
        self.timeout = timeout
        self.opener = urllib.request.build_opener(NoRedirect())

    def _request(self, url: str, *, authenticated: bool = True) -> tuple[int, dict[str, str], bytes]:
        headers = {"Accept": "application/json, text/plain;q=0.9, */*;q=0.1"}
        if authenticated:
            headers["Authorization"] = "Basic " + self.api_key
        request = urllib.request.Request(url, headers=headers, method="GET")
        try:
            with self.opener.open(request, timeout=self.timeout) as response:
                return response.status, dict(response.headers.items()), response.read()
        except urllib.error.HTTPError as exc:
            body = exc.read()
            if exc.code == 303:
                return exc.code, dict(exc.headers.items()), body
            preview = body.decode("utf-8", errors="replace")[:500]
            raise ApiError(f"UBA API GET {url} returned HTTP {exc.code}: {preview}") from exc
        except urllib.error.URLError as exc:
            raise ApiError(f"UBA API GET {url} failed: {exc.reason}") from exc

    def get_json(self, path: str, query: dict[str, str] | None = None) -> Any:
        url = self.api_base + "/" + path.lstrip("/")
        if query:
            url += "?" + urllib.parse.urlencode(query)
        status, _, body = self._request(url)
        if status == 303:
            raise ApiError(f"Unexpected redirect from JSON endpoint: {url}")
        try:
            return json.loads(body.decode("utf-8"))
        except json.JSONDecodeError as exc:
            raise ApiError(f"UBA API endpoint did not return JSON: {url}") from exc

    def get_text_or_redirect(self, path: str) -> str:
        url = self.api_base + "/" + path.lstrip("/")
        status, headers, body = self._request(url)
        if status == 303:
            try:
                signed_url = json.loads(body.decode("utf-8"))
            except json.JSONDecodeError as exc:
                raise ApiError(f"UBA redirect body was not a signed URL: {url}") from exc
            if not isinstance(signed_url, str) or not signed_url.startswith(("https://", "http://")):
                raise ApiError(f"UBA redirect body did not contain a URL: {url}")
            _, _, redirected = self._request(signed_url, authenticated=False)
            return redirected.decode("utf-8", errors="replace")

        content_type = headers.get("Content-Type", "")
        text = body.decode("utf-8", errors="replace")
        if "application/json" in content_type:
            try:
                decoded = json.loads(text)
            except json.JSONDecodeError:
                return text
            if isinstance(decoded, str) and decoded.startswith(("https://", "http://")):
                _, _, redirected = self._request(decoded, authenticated=False)
                return redirected.decode("utf-8", errors="replace")
            if isinstance(decoded, dict):
                for key in ("log", "text", "content"):
                    value = decoded.get(key)
                    if isinstance(value, str):
                        return value
        return text


def _normalize_key(key: str) -> str:
    return re.sub(r"[^a-z0-9]", "", key.lower())


def _walk_revision_values(value: Any, parent_key: str = "") -> Iterable[str]:
    if isinstance(value, dict):
        for key, child in value.items():
            normalized = _normalize_key(str(key))
            if normalized in REVISION_KEYS or any(token in normalized for token in ("commit", "revision", "changeset")):
                if isinstance(child, str):
                    yield child
                else:
                    yield from _walk_revision_values(child, normalized)
            elif parent_key in REVISION_KEYS:
                yield from _walk_revision_values(child, parent_key)
    elif isinstance(value, list):
        for child in value:
            yield from _walk_revision_values(child, parent_key)
    elif isinstance(value, str) and parent_key in REVISION_KEYS:
        yield value


def _looks_like_sha(value: str) -> bool:
    return re.fullmatch(r"[0-9a-fA-F]{7,64}", value.strip()) is not None


def revision_matches(build: dict[str, Any], target_sha: str) -> bool:
    target = target_sha.strip().lower()
    if len(target) < 7:
        raise ValueError("target commit SHA must contain at least 7 hex characters")
    for candidate in _walk_revision_values(build):
        candidate = candidate.strip().lower()
        if _looks_like_sha(candidate) and (target.startswith(candidate) or candidate.startswith(target)):
            return True
    return False


def build_number(build: dict[str, Any]) -> int | None:
    for key in ("build", "buildNumber", "number"):
        value = build.get(key)
        if isinstance(value, int):
            return value
        if isinstance(value, str) and value.isdigit():
            return int(value)
    return None


def build_status(build: dict[str, Any]) -> str:
    for key in ("buildStatus", "status", "buildState"):
        value = build.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return "unknown"


def normalized_status(value: str) -> str:
    return re.sub(r"[^a-z]", "", value.lower())


def find_matching_build(builds: Any, target_sha: str, branch: str | None = None) -> dict[str, Any] | None:
    if isinstance(builds, dict):
        for key in ("builds", "results", "items"):
            if isinstance(builds.get(key), list):
                builds = builds[key]
                break
    if not isinstance(builds, list):
        return None
    candidates: list[dict[str, Any]] = []
    for build in builds:
        if not isinstance(build, dict) or not revision_matches(build, target_sha):
            continue
        scm_branch = build.get("scmBranch") or build.get("branch")
        if branch and isinstance(scm_branch, str):
            if scm_branch.removeprefix("refs/heads/") != branch.removeprefix("refs/heads/"):
                continue
        candidates.append(build)
    return max(candidates, key=lambda item: build_number(item) or -1) if candidates else None


def flatten_strings(value: Any) -> Iterable[str]:
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        preferred = ("message", "error", "description", "title", "label", "name")
        emitted: set[str] = set()
        for key in preferred:
            child = value.get(key)
            if isinstance(child, str):
                emitted.add(key)
                yield child
        for key, child in value.items():
            if key not in emitted:
                yield from flatten_strings(child)
    elif isinstance(value, list):
        for child in value:
            yield from flatten_strings(child)


def extract_diagnostics(log_text: str, failures: Any = None, limit: int = 20) -> list[str]:
    diagnostics: list[str] = []
    seen: set[str] = set()

    def add(text: str) -> None:
        line = re.sub(r"\s+", " ", text.strip())
        if line and line not in seen:
            seen.add(line)
            diagnostics.append(line)

    for match in CS_ERROR_RE.finditer(log_text or ""):
        add(match.group(0))
        if len(diagnostics) >= limit:
            return diagnostics
    for text in flatten_strings(failures):
        if re.search(r"\berror\b|\bfail", text, re.IGNORECASE):
            add(text)
            if len(diagnostics) >= limit:
                return diagnostics
    if not diagnostics:
        for line in (log_text or "").splitlines():
            if re.search(r"\berror\b|exception|script compiler", line, re.IGNORECASE):
                add(line)
                if len(diagnostics) >= limit:
                    break
    return diagnostics


def dashboard_url(build: dict[str, Any]) -> str | None:
    links = build.get("links")
    if not isinstance(links, dict):
        return None
    for key in ("dashboard_log", "dashboard_summary", "dashboard_project", "dashboard_url"):
        item = links.get(key)
        href = item.get("href") if isinstance(item, dict) else item
        if isinstance(href, str) and href:
            if href.startswith("https://"):
                return href
            if href.startswith("/"):
                return "https://cloud.unity.com" + href
    return None


def _project_matches(project: dict[str, Any], project_id: str) -> bool:
    target = project_id.strip().lower()
    return any(isinstance(project.get(key), str) and project[key].strip().lower() == target for key in ("guid", "projectGuid", "projectid", "projectId", "upid"))


def _project_org_id(project: dict[str, Any]) -> str | None:
    for key in ("orgFk", "orgForeignKey", "orgid", "orgId", "organizationId"):
        value = project.get(key)
        if isinstance(value, (str, int)) and str(value).strip():
            return str(value).strip()
    return None


def _target_id(target: dict[str, Any]) -> str | None:
    for key in ("buildtargetid", "buildTargetId", "id"):
        value = target.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return None


def _target_name(target: dict[str, Any]) -> str | None:
    for key in ("name", "buildTargetName", "targetName"):
        value = target.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return None


def _target_branch(target: dict[str, Any]) -> str | None:
    stack = [target]
    while stack:
        value = stack.pop()
        if isinstance(value, dict):
            for key, child in value.items():
                normalized = _normalize_key(str(key))
                if normalized in {"scmbranch", "branch"} and isinstance(child, str):
                    return child.removeprefix("refs/heads/")
                if isinstance(child, (dict, list)):
                    stack.append(child)
        elif isinstance(value, list):
            stack.extend(value)
    return None


def resolve_scope(api: UnityBuildAutomationApi, project_id: str, org_id: str, target_selector: str, branch: str) -> tuple[str, str]:
    resolved_org = org_id.strip()
    if not resolved_org:
        projects = api.get_json("projects")
        if isinstance(projects, dict):
            projects = projects.get("projects") or projects.get("results") or projects.get("items")
        if not isinstance(projects, list):
            raise ApiError("UBA /projects did not return a project list; set UNITY_UBA_ORG_ID explicitly")
        project = next((item for item in projects if isinstance(item, dict) and _project_matches(item, project_id)), None)
        if project is None:
            raise ApiError("Unable to discover UBA organization for the configured project GUID; set UNITY_UBA_ORG_ID explicitly")
        resolved_org = _project_org_id(project) or ""
        if not resolved_org:
            raise ApiError("UBA project metadata did not expose orgFk; set UNITY_UBA_ORG_ID explicitly")

    quote = lambda value: urllib.parse.quote(value, safe="")
    targets = api.get_json(f"orgs/{quote(resolved_org)}/projects/{quote(project_id)}/buildtargets")
    if isinstance(targets, dict):
        targets = targets.get("buildtargets") or targets.get("results") or targets.get("items")
    if not isinstance(targets, list):
        raise ApiError("UBA build-target endpoint did not return a list")
    targets = [item for item in targets if isinstance(item, dict)]

    selector = target_selector.strip()
    if selector:
        lowered = selector.lower()
        matches = [item for item in targets if (_target_id(item) or "").lower() == lowered or (_target_name(item) or "").lower() == lowered]
    else:
        normalized_branch = branch.removeprefix("refs/heads/") if branch else ""
        matches = [item for item in targets if normalized_branch and _target_branch(item) == normalized_branch]
        if not matches and len(targets) == 1:
            matches = targets
    if len(matches) != 1:
        available = [f"{_target_id(item) or '?'}:{_target_name(item) or '?'}:{_target_branch(item) or '?'}" for item in targets]
        raise ApiError("Unable to select exactly one UBA build target. Set UNITY_UBA_BUILD_TARGET to its ID or name. Available: " + ", ".join(available))
    resolved_target = _target_id(matches[0])
    if not resolved_target:
        raise ApiError("Selected UBA build target does not expose an ID")
    return resolved_org, resolved_target


def build_path(org: str, project: str, target: str, suffix: str = "") -> str:
    quote = lambda value: urllib.parse.quote(value, safe="")
    return f"orgs/{quote(org)}/projects/{quote(project)}/buildtargets/{quote(target)}/builds" + suffix


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def sync(args: argparse.Namespace) -> tuple[int, dict[str, Any]]:
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    api = UnityBuildAutomationApi(args.api_key, args.api_base, timeout=args.request_timeout)
    deadline = time.monotonic() + args.timeout
    resolved_org, resolved_target = resolve_scope(api, args.project_id, args.org_id, args.build_target, args.branch)
    base = build_path(resolved_org, args.project_id, resolved_target)
    match: dict[str, Any] | None = None

    while time.monotonic() < deadline:
        builds = api.get_json(base, {"per_page": str(args.per_page), "page": "1"})
        write_json(output_dir / "builds-latest.json", builds)
        match = find_matching_build(builds, args.commit, args.branch)
        if match is not None:
            break
        time.sleep(args.poll_interval)

    if match is None:
        summary = {"outcome": "not_found", "commit": args.commit, "branch": args.branch, "diagnostic": "No Unity Build Automation build matching this commit appeared before timeout."}
        write_json(output_dir / "uba-feedback.json", summary)
        return 2, summary

    number = build_number(match)
    if number is None:
        raise ApiError("Matching UBA build does not expose a build number")

    while True:
        detail = api.get_json(base + f"/{number}")
        if not isinstance(detail, dict):
            raise ApiError("UBA build status endpoint returned a non-object")
        write_json(output_dir / "build.json", detail)
        status_raw = build_status(detail)
        status = normalized_status(status_raw)
        if status in SUCCESS_STATUSES | FAILURE_STATUSES:
            match = detail
            break
        if time.monotonic() >= deadline:
            summary = {"outcome": "timeout", "commit": args.commit, "branch": args.branch, "build_number": number, "build_status": status_raw, "dashboard_url": dashboard_url(detail), "diagnostic": f"UBA build #{number} did not reach a terminal state before timeout (status={status_raw})."}
            write_json(output_dir / "uba-feedback.json", summary)
            return 2, summary
        time.sleep(args.poll_interval)

    status_raw = build_status(match)
    status = normalized_status(status_raw)
    summary: dict[str, Any] = {"commit": args.commit, "branch": args.branch, "build_number": number, "build_status": status_raw, "dashboard_url": dashboard_url(match), "unity_version": match.get("unityVersion"), "last_built_revision": match.get("lastBuiltRevision")}
    if status in SUCCESS_STATUSES:
        summary.update({"outcome": "success", "diagnostic": f"UBA build #{number} succeeded."})
        write_json(output_dir / "uba-feedback.json", summary)
        return 0, summary

    failures: Any = None
    log_text = ""
    try:
        failures = api.get_json(base + f"/{number}/failures")
        write_json(output_dir / "uba-failures.json", failures)
    except ApiError as exc:
        (output_dir / "uba-failures-error.txt").write_text(str(exc) + "\n", encoding="utf-8")
    try:
        log_text = api.get_text_or_redirect(base + f"/{number}/log")
        (output_dir / "uba.log").write_text(log_text, encoding="utf-8")
    except ApiError as exc:
        (output_dir / "uba-log-error.txt").write_text(str(exc) + "\n", encoding="utf-8")

    diagnostics = extract_diagnostics(log_text, failures)
    diagnostic = diagnostics[0] if diagnostics else f"UBA build #{number} ended with status={status_raw}; inspect uploaded UBA diagnostics."
    summary.update({"outcome": "failure", "diagnostic": diagnostic, "diagnostics": diagnostics})
    write_json(output_dir / "uba-feedback.json", summary)
    return 1, summary


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Mirror Unity Build Automation status/log diagnostics for one Git commit.")
    parser.add_argument("--api-key", default=os.getenv("UNITY_UBA_API_KEY", ""))
    parser.add_argument("--org-id", default=os.getenv("UNITY_UBA_ORG_ID", ""), help="Optional; auto-discovered from project metadata when omitted")
    parser.add_argument("--project-id", default=os.getenv("UNITY_UBA_PROJECT_ID", ""), help="Unity Cloud project GUID")
    parser.add_argument("--build-target", default=os.getenv("UNITY_UBA_BUILD_TARGET", ""), help="Optional build target ID or name")
    parser.add_argument("--commit", default=os.getenv("GITHUB_SHA", ""))
    parser.add_argument("--branch", default=os.getenv("GITHUB_REF_NAME", ""))
    parser.add_argument("--api-base", default=os.getenv("UNITY_UBA_API_BASE", API_BASE_DEFAULT))
    parser.add_argument("--poll-interval", type=float, default=float(os.getenv("UNITY_UBA_POLL_INTERVAL", "30")))
    parser.add_argument("--timeout", type=float, default=float(os.getenv("UNITY_UBA_POLL_TIMEOUT", "3000")))
    parser.add_argument("--request-timeout", type=float, default=30.0)
    parser.add_argument("--per-page", type=int, default=50)
    parser.add_argument("--output-dir", default=".ci/uba")
    args = parser.parse_args(argv)
    missing = [name for name, value in (("api-key", args.api_key), ("project-id", args.project_id), ("commit", args.commit)) if not str(value).strip()]
    if missing:
        parser.error("missing required UBA configuration: " + ", ".join(missing))
    if args.poll_interval < 10:
        parser.error("poll interval must be at least 10 seconds to avoid aggressive UBA polling")
    if args.timeout <= 0:
        parser.error("timeout must be positive")
    return args


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        code, summary = sync(args)
    except Exception as exc:
        output_dir = Path(args.output_dir)
        output_dir.mkdir(parents=True, exist_ok=True)
        summary = {"outcome": "error", "commit": args.commit, "branch": args.branch, "diagnostic": str(exc)}
        write_json(output_dir / "uba-feedback.json", summary)
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(summary, sort_keys=True))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
