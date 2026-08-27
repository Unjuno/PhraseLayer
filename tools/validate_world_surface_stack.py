#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
SCRIPTS = UNITY / "Assets" / "Scripts"
MANIFEST = UNITY / "Assets" / "Plugins" / "Android" / "AndroidManifest.xml"
violations: list[str] = []


def require(path: Path, markers: tuple[str, ...], label: str) -> None:
    if not path.is_file():
        violations.append(f"missing file: {path.relative_to(ROOT)}")
        return
    text = path.read_text(encoding="utf-8")
    for marker in markers:
        if marker not in text:
            violations.append(f"{label} missing reviewed marker: {marker}")


require(
    SCRIPTS / "MetaEnvironmentDepthSurfaceRaycaster.cs",
    (
        'ScenePermission = "com.oculus.permission.USE_SCENE"',
        'ManagerTypeName = "Meta.XR.EnvironmentRaycastManager"',
        'HitTypeName = "Meta.XR.EnvironmentRaycastHit"',
        'GetProperty("IsSupported"',
        '"Raycast"',
        "HasUserAuthorizedPermission(ScenePermission)",
        "TargetInvocationException",
        "hit = default(SurfaceHit)",
    ),
    "Meta Environment Depth raycaster",
)

require(
    SCRIPTS / "QuestSurfaceRaycaster.cs",
    (
        "MetaEnvironmentDepthSurfaceRaycaster environmentDepth",
        "UnityPhysicsSurfaceRaycaster physics",
        "environmentDepth.TryRaycast(ray, out hit)",
        "return physics.TryRaycast(ray, out hit)",
    ),
    "Quest surface fallback chain",
)

require(
    SCRIPTS / "QuestReadWorldOverlayBehaviour.cs",
    (
        "ISurfaceRaycaster raycaster",
        "new QuestSurfaceRaycaster(gameObject, maxDistance, surfaceLayerMask)",
        "EnsureSpatialPermissionRequested();",
        "Permission.RequestUserPermissions(",
        "HandleSpatialPermissionGranted",
        "HandleSpatialPermissionDenied",
        "collider/viewport fallback",
        "target.Coverage != SpatialAssistanceCoverage.Exact",
        "!projected.CanRenderInWorld",
    ),
    "Quest Read world overlay surface/permission contract",
)

if not MANIFEST.is_file():
    violations.append(f"missing file: {MANIFEST.relative_to(ROOT)}")
else:
    manifest = MANIFEST.read_text(encoding="utf-8")
    if 'android:name="com.oculus.permission.USE_SCENE"' not in manifest:
        violations.append("Quest manifest must declare local Spatial Data permission for Environment Depth placement")
    if "com.oculus.permission.USE_ANCHOR_API" in manifest:
        violations.append("Quest Read surface stack must not request unused anchor-persistence permission")
    for permission in ("android.permission.INTERNET", "android.permission.ACCESS_NETWORK_STATE"):
        if permission in manifest:
            violations.append(f"Quest Read surface stack must remain local-only; forbidden permission: {permission}")

if violations:
    raise SystemExit("\n".join(violations))

print(
    "PASS: Read world placement prefers permission-gated Meta Environment Depth, falls back to Unity collider geometry, "
    "then leaves unresolved surface misses to the viewport overlay without inventing depth or enabling network access"
)
