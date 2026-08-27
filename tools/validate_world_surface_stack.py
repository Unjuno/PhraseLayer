#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
SCRIPTS = UNITY / "Assets" / "Scripts"
MANIFEST = UNITY / "Assets" / "Plugins" / "Android" / "AndroidManifest.xml"
violations: list[str] = []


def read(path: Path, label: str) -> str:
    if not path.is_file():
        violations.append(f"missing file: {path.relative_to(ROOT)}")
        return ""
    return path.read_text(encoding="utf-8")


def require(path: Path, markers: tuple[str, ...], label: str) -> str:
    text = read(path, label)
    for marker in markers:
        if text and marker not in text:
            violations.append(f"{label} missing reviewed marker: {marker}")
    return text


native = require(
    SCRIPTS / "MetaEnvironmentDepthSurfaceRaycaster.cs",
    (
        'ScenePermission = "com.oculus.permission.USE_SCENE"',
        'NativeFuncsTypeName = "Meta.XR.MRUtilityKit.MRUKNativeFuncs"',
        'HitInfoTypeName = "MrukEnvironmentRaycastHitPointGetInfo"',
        'HitPointTypeName = "MrukEnvironmentRaycastHitPoint"',
        'GetNativeDelegateField("CreateEnvironmentRaycaster")',
        'GetNativeDelegateField("DestroyEnvironmentRaycaster")',
        'GetNativeDelegateField("EnvironmentRaycasterStatus")',
        'GetNativeDelegateField("RaycastEnvironment")',
        "HasUserAuthorizedPermission(ScenePermission)",
        "ownsEnvironmentRaycaster",
        "public void Dispose()",
        "RaycasterCreating",
        "RaycasterReady",
        "RaycastStatusHit",
        "hit = default(SurfaceHit)",
        "Creation is asynchronous",
    ),
    "Meta native environment raycaster",
)
if native:
    for forbidden in (
        'ManagerTypeName = "Meta.XR.EnvironmentRaycastManager"',
        'owner.AddComponent(managerType)',
        'owner.AddComponent(environmentDepthManagerType)',
        'EnvironmentDepthManagerTypeName',
        'GetMethod("Raycast", BindingFlags.Instance',
    ):
        if forbidden in native:
            violations.append(
                "Meta native environment raycaster must not instantiate the telemetry manager or OVRCameraRig-bound "
                f"depth manager: {forbidden}"
            )

quest = require(
    SCRIPTS / "QuestSurfaceRaycaster.cs",
    (
        "sealed class QuestSurfaceRaycaster : ISurfaceRaycaster, IDisposable",
        "MetaEnvironmentDepthSurfaceRaycaster environmentDepth",
        "UnityPhysicsSurfaceRaycaster physics",
        "environmentDepth.TryRaycast(ray, out hit)",
        "return physics.TryRaycast(ray, out hit)",
        "environmentDepth.Dispose()",
    ),
    "Quest surface fallback chain",
)

overlay = require(
    SCRIPTS / "QuestReadWorldOverlayBehaviour.cs",
    (
        "ISurfaceRaycaster raycaster",
        "new QuestSurfaceRaycaster(gameObject, maxDistance, surfaceLayerMask)",
        "EnsureSpatialPermissionRequested();",
        "Permission.RequestUserPermissions(",
        "HandleSpatialPermissionGranted",
        "HandleSpatialPermissionDenied",
        "collider/viewport fallback",
        "DisposeRaycaster();",
        "questRaycaster.Dispose();",
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
        violations.append("Quest manifest must declare local Spatial Data permission for native environment placement")
    if "com.oculus.permission.USE_ANCHOR_API" in manifest:
        violations.append("Quest Read surface stack must not request unused anchor-persistence permission")
    for permission in ("android.permission.INTERNET", "android.permission.ACCESS_NETWORK_STATE"):
        if permission in manifest:
            violations.append(f"Quest Read surface stack must remain local-only; forbidden permission: {permission}")

if violations:
    raise SystemExit("\n".join(violations))

print(
    "PASS: Read world placement uses permission-gated MRUK native environment raycasting without instantiating "
    "Meta's telemetry-emitting EnvironmentRaycastManager or OVRCameraRig-bound EnvironmentDepthManager, falls back "
    "to Unity collider geometry, and leaves unresolved surface misses to the viewport overlay without network access"
)
