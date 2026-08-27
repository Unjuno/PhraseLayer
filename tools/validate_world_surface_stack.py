#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
ASSETS = UNITY / "Assets"
SCRIPTS = ASSETS / "Scripts"
MANIFEST = ASSETS / "Plugins" / "Android" / "AndroidManifest.xml"
LINK_XML = ASSETS / "link.xml"
LINK_META = ASSETS / "link.xml.meta"
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
        "creationRequested",
        "public void Dispose()",
        "TryGetRaycasterStatus(out var statusValue)",
        "statusValue == RaycasterReady",
        "IsIdentityTrackingOrigin()",
        'GetProperty("lossyScale"',
        "RaycasterCreating",
        "RaycasterReady",
        "RaycastStatusHit",
        "hit = default(SurfaceHit)",
        "Creation is asynchronous",
        "destroy.DynamicInvoke()",
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

require(
    LINK_XML,
    (
        '<assembly fullname="meta.xr.mrutilitykit">',
        '<type fullname="Meta.XR.MRUtilityKit.MRUKNativeFuncs*" preserve="all" />',
        "EnvironmentRaycastManager",
    ),
    "IL2CPP reflection preservation",
)
require(
    LINK_META,
    (
        "fileFormatVersion: 2",
        "TextScriptImporter:",
    ),
    "IL2CPP linker asset metadata",
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
    "PASS: Read world placement preserves the reflected MRUK native interop for IL2CPP, requires an identity "
    "tracking origin, destroys only a ready raycaster it owns, avoids Meta's telemetry-emitting manager and "
    "OVRCameraRig-bound depth manager, falls back to Unity colliders, and keeps unresolved misses on the local-only "
    "viewport path"
)
