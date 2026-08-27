#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
ASSETS = UNITY / "Assets"
SCRIPTS = ASSETS / "Scripts"
MANIFEST = ASSETS / "Plugins" / "Android" / "AndroidManifest.xml"
LINK_XML = ASSETS / "link.xml"
LINK_META = ASSETS / "link.xml.meta"
OPENXR_SETTINGS = ASSETS / "XR" / "Settings" / "OpenXR Package Settings.asset"
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
        "TryGetDelegate(",
        "TrySetField(",
        "TryReadField(",
        "TryConvertToInt32(",
        "IsRecoverableBoundaryException(",
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

projection = require(
    CORE / "SpatialProjection.cs",
    (
        "Verified surface normals are canonicalized to face back toward the camera ray origin",
        "hit = OrientSurfaceTowardRayOrigin(ray, hit);",
        "private static SurfaceHit OrientSurfaceTowardRayOrigin(SpatialRay ray, SurfaceHit hit)",
        "if (Dot(ray.Direction, hit.Normal) <= 0.0)",
        "new SpatialVector3(-hit.Normal.X, -hit.Normal.Y, -hit.Normal.Z)",
    ),
    "camera-facing verified surface orientation",
)
if projection:
    for forbidden in ("UnityEngine", "Meta.XR", "Oculus", "Android.Permission"):
        if forbidden in projection:
            violations.append(
                f"Core spatial projection must stay platform/runtime independent: {forbidden}"
            )

surface_stabilizer = require(
    CORE / "SurfaceHitStabilizer.cs",
    (
        "sealed class SurfaceHitStabilizerOptions",
        "BlendFactor = 0.35",
        "ResetPointDistanceMeters = 0.20",
        "ResetNormalAngleDegrees = 20.0",
        "MaxMissingObservations = 1",
        "sealed class SurfaceHitStabilizer",
        "public SurfaceHit Stabilize(string key, SurfaceHit observed)",
        "pointDistance > options.ResetPointDistanceMeters",
        "normalAngle > options.ResetNormalAngleDegrees",
        "public bool TryHoldMissing(string key, out SurfaceHit hit)",
        "states.Remove(key)",
        "public void Reset()",
    ),
    "verified world surface stabilizer",
)
if surface_stabilizer:
    for forbidden in (
        "UnityEngine",
        "Meta.XR",
        "Oculus",
        "Android.Permission",
    ):
        if forbidden in surface_stabilizer:
            violations.append(
                f"Core surface stabilizer must stay platform/runtime independent: {forbidden}"
            )

surface_layout = require(
    CORE / "SurfacePlaneTextLayout.cs",
    (
        "enum SurfacePlaneLayoutFailure",
        "ImplausibleExtent = 5",
        "sealed class SurfacePlaneTextLayoutProjectorOptions",
        "MaxCornerOffsetMultiplier = 2.0",
        "MaxCornerOffsetPaddingMeters = 0.5",
        "readonly struct SurfaceTextLayout",
        "sealed class SurfacePlaneTextLayoutProjector",
        "IViewportRayProvider rayProvider",
        "new ViewportPoint(envelope.MinU, envelope.MinV)",
        "new ViewportPoint(envelope.MaxU, envelope.MaxV)",
        "TryIntersectPlane(",
        "RayParallelToSurface",
        "SurfaceBehindRay",
        "DegenerateExtent",
        "Distance(worldCorners[index], surface.Point) > maxCornerOffset",
        "Normalize(horizontal)",
        "Normalize(vertical)",
    ),
    "verified surface-plane text layout",
)
if surface_layout:
    for forbidden in ("UnityEngine", "Meta.XR", "Oculus", "Android.Permission"):
        if forbidden in surface_layout:
            violations.append(
                f"Core surface-plane layout must stay platform/runtime independent: {forbidden}"
            )

require(
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

require(
    SCRIPTS / "QuestReadWorldOverlayBehaviour.cs",
    (
        "ISurfaceRaycaster raycaster",
        "SurfaceHitStabilizer surfaceStabilizer",
        "surfaceBlendFactor = 0.35f",
        "surfaceResetPointDistanceMeters = 0.20f",
        "surfaceResetNormalAngleDegrees = 20f",
        "surfaceMaxMissingObservations = 1",
        "fittedTextHeightFraction = 0.85f",
        "fittedTextWidthFraction = 0.95f",
        "new QuestSurfaceRaycaster(gameObject, maxDistance, surfaceLayerMask)",
        "new SurfaceHitStabilizer(new SurfaceHitStabilizerOptions",
        "new SurfacePlaneTextLayoutProjector(cameraBridge)",
        "TryBuildSurfaceLayout(",
        "ComputeFittedCharacterSize(",
        "layout.Value.Up",
        "BuildFallbackUp(normal)",
        "EnsureSurfaceEncounter();",
        "readAssistance.CurrentEncounterId",
        "surfaceStabilizer.Reset();",
        "surfaceStabilizer.Stabilize(unit.Id, projected.Surface.Value)",
        "surfaceStabilizer.TryHoldMissing(unit.Id, out var heldSurface)",
        "previouslyRendered.Contains(unit.Id)",
        "EnsureSpatialPermissionRequested();",
        "Permission.RequestUserPermissions(",
        "HandleSpatialPermissionGranted",
        "HandleSpatialPermissionDenied",
        "collider/viewport fallback",
        "DisposeRaycaster();",
        "questRaycaster.Dispose();",
        "target.Coverage != SpatialAssistanceCoverage.Exact",
        "!projected.CanRenderInWorld",
        "retained-surface-misses",
        "fitted-layouts",
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

openxr = read(OPENXR_SETTINGS, "OpenXR feature contract")
if openxr:
    android_meta_sections = [
        section
        for section in openxr.split("--- !u!114")
        if "m_Name: MetaXRFeature Android" in section
    ]
    if len(android_meta_sections) != 1:
        violations.append("OpenXR settings must contain exactly one Android Meta XR Feature section")
    else:
        android_meta = android_meta_sections[0]
        if "m_enabled: 1" not in android_meta:
            violations.append("Android Meta XR Feature must remain enabled for Quest native environment raycast")
        if "XR_META_environment_raycast" not in android_meta:
            violations.append("Android Meta XR Feature must expose XR_META_environment_raycast")

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
    "PASS: Read world placement keeps Android Meta XR environment-raycast support enabled, preserves reflected MRUK "
    "native interop for IL2CPP, fails closed across the optional reflection boundary, requires an identity tracking "
    "origin, destroys only a ready raycaster it owns, avoids Meta telemetry/depth-manager coupling, canonicalizes each "
    "verified surface normal toward its camera ray origin, stabilizes only verified world hits with bounded miss "
    "retention reset per encounter, derives physical label extent/orientation only by intersecting viewport-corner "
    "rays with that verified plane, rejects implausibly distant corner intersections, falls back to Unity colliders, "
    "and keeps unresolved misses on the local-only viewport path"
)
