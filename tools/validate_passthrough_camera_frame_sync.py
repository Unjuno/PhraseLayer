#!/usr/bin/env python3
"""Static contract for PassthroughCameraAccess frame timestamp/camera-pose binding.

This gate distinguishes two claims deliberately:
1. implemented: a stable Meta Timestamp/GetCameraPose pair is retained with the OCR frame and reused for world rays,
   and the detector submits the matching passthrough texture directly to an Inference Engine tensor without a CPU
   image readback in between;
2. not yet verified: exact end-to-end pixel/pose synchronization, because real Quest timing evidence has not yet been
   captured for the Meta texture producer, Unity graphics submission, detector inference, and projection sequence.
"""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BRIDGE = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/MetaPassthroughCameraBridge.cs"
PAYLOAD = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityTextureFramePayload.cs"
PROJECTION = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnitySpatialProjection.cs"
SMOKE = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/QuestReadModeSmokeTestBehaviour.cs"
DETECTOR = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityPaddleOcrDetectorRuntime.cs"
DEVICE_RUNNER = ROOT / "tools/run_quest_read_mode_smoke.py"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def forbid(text: str, fragment: str, label: str) -> None:
    if fragment in text:
        raise GateError(f"{label} contains forbidden stale marker: {fragment}")


def validate() -> dict[str, object]:
    bridge = BRIDGE.read_text(encoding="utf-8")
    payload = PAYLOAD.read_text(encoding="utf-8")
    projection = PROJECTION.read_text(encoding="utf-8")
    smoke = SMOKE.read_text(encoding="utf-8")
    detector = DETECTOR.read_text(encoding="utf-8")
    runner = DEVICE_RUNNER.read_text(encoding="utf-8")

    for fragment in (
        'type.GetProperty("Timestamp", BindingFlags.Instance | BindingFlags.Public)',
        'type.GetMethod("GetCameraPose", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)',
        'new[] { typeof(Vector2), typeof(Pose?) }',
        'CaptureMetadataAttempts = 3',
        'var timestampBefore = ReadTimestamp()',
        'var cameraPose = ReadCameraPose()',
        'var texture = getTextureMethod.Invoke(passthroughCameraAccess, null) as Texture',
        'var timestampAfter = ReadTimestamp()',
        'if (timestampBefore == timestampAfter)',
        'new UnityTextureFramePayload(texture, timestampBefore, cameraPose)',
        'ToTimestampMicroseconds(timestampBefore)',
        'TryCreateFrameRayProvider(ImageFrame frame, out IViewportRayProvider provider)',
        'new CapturedPoseRayProvider(this, payload.CameraPose)',
        'new object[] { new Vector2((float)point.U, (float)point.V), cameraPose }',
        'capturedPoseRayCount++',
    ):
        require(bridge, fragment, "Meta passthrough camera bridge")

    forbid(
        bridge,
        'checked((long)(Time.realtimeSinceStartupAsDouble * 1_000_000.0))',
        "Meta passthrough camera bridge",
    )

    for fragment in (
        'public bool HasCameraCaptureMetadata { get; }',
        'public DateTime CameraTimestamp { get; }',
        'public Pose CameraPose { get; }',
    ):
        require(payload, fragment, "Unity frame payload")

    for fragment in (
        'BindFrameRayProvider(aligned.Spatial.Frame)',
        'UsesCapturedCameraPose = rayProvider.TryCreateFrameRayProvider(frame, out activeViewportRayProvider)',
        'new SpatialProjectionPlanner(viewportRayProvider, surfaceRaycaster)',
        'new WorldTextLayoutPlanner(',
        'public bool UsesCapturedCameraPose { get; private set; }',
        'public long? LastProjectionFrameTimestampMicroseconds { get; private set; }',
    ):
        require(projection, fragment, "Unity spatial projection")

    for fragment in (
        'projection.UsesCapturedCameraPose',
        'projection.RayProvider.CapturedPoseRayCount > capturedPoseRaysBefore',
        'camera_timestamp_source=MetaPassthroughCameraAccess.Timestamp',
        'captured_pose_projection=',
        'pixel_pose_sync_verified=false',
    ):
        require(smoke, fragment, "Quest Read Mode smoke")

    # The detector must consume the camera texture immediately through the reviewed Inference Engine 2.2.1
    # texture-to-tensor path. Reintroducing a CPU image readback here would reopen the frame/pose race.
    for fragment in (
        'UsesGpuTexturePreprocessing => true',
        '.SetTensorLayout(TensorLayout.NCHW)',
        '.SetCoordOrigin(flipReadbackRows ? CoordOrigin.TopLeft : CoordOrigin.BottomLeft)',
        '.SetChannelSwizzle(ChannelSwizzle.BGRA)',
        'TextureConverter.ToTensor(texture, inputTensor, textureTransform)',
        'worker.Schedule(inputTensor)',
    ):
        require(detector, fragment, "PP-OCR detector")

    for fragment in (
        'Graphics.Blit(',
        '.ReadPixels(',
        '.GetPixels32(',
        'RenderTexture.active',
        'RenderTexture.GetTemporary(',
    ):
        forbid(detector, fragment, "PP-OCR detector")

    for fragment in (
        'CAPTURED_POSE_MARKER = "captured_pose_projection=true"',
        '"captured_pose_projection_observed": CAPTURED_POSE_MARKER in logcat',
        '"camera_timestamp_source": "MetaPassthroughCameraAccess.Timestamp"',
        '"camera_pose_source": "MetaPassthroughCameraAccess.GetCameraPose"',
        '"camera_timestamp_pose_binding_implemented": True',
        '"camera_pixel_pose_sync_verified": False',
    ):
        require(runner, fragment, "Quest device runner")

    return {
        "status": "pass",
        "meta_timestamp_used": True,
        "meta_camera_pose_cached": True,
        "stable_timestamp_pose_pair_required": True,
        "center_and_corner_rays_share_capture_pose": True,
        "quest_smoke_requires_capture_pose": True,
        "detector_input_gpu_texture_to_tensor": True,
        "detector_cpu_image_readback_forbidden": True,
        "pixel_pose_sync_verified": False,
        "real_quest_timing_evidence_still_required": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
