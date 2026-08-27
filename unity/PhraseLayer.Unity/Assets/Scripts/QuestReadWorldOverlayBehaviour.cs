using System;
using System.Collections.Generic;
using System.Reflection;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Projects exact Read assistance targets from stabilized OCR viewport geometry onto physical surfaces.
    /// Meta's native environment raycaster is preferred when Spatial Data permission and device support are available;
    /// ordinary Unity collider geometry is the secondary surface source. Targets that cannot be projected remain
    /// available to the existing viewport GUI fallback.
    ///
    /// This component is intentionally conservative: it never assumes a fixed depth and never turns an unresolved
    /// OCR target into a new world-space replacement. Verified surface hits are stabilized per semantic unit to reduce
    /// small depth/normal jitter. A previously verified surface may survive only a bounded raycast miss, and all
    /// world-surface state is discarded when the Read encounter changes.
    /// </summary>
    public sealed class QuestReadWorldOverlayBehaviour : MonoBehaviour
    {
        [SerializeField] private QuestReadAssistanceDebugBehaviour readAssistance = default(QuestReadAssistanceDebugBehaviour);
        [SerializeField] private MetaPassthroughCameraBridge cameraBridge = default(MetaPassthroughCameraBridge);
        [SerializeField] private int surfaceLayerMask = -1;
        [SerializeField] private float maxSurfaceDistanceMeters = 10f;
        [SerializeField] private float surfaceOffsetMeters = 0.01f;
        [SerializeField] private float characterSizeMeters = 0.02f;
        [SerializeField] private int fontSize = 64;
        [SerializeField] private float surfaceBlendFactor = 0.35f;
        [SerializeField] private float surfaceResetPointDistanceMeters = 0.20f;
        [SerializeField] private float surfaceResetNormalAngleDegrees = 20f;
        [SerializeField] private int surfaceMaxMissingObservations = 1;

        private readonly Dictionary<string, TextMesh> labels = new Dictionary<string, TextMesh>(StringComparer.Ordinal);
        private readonly HashSet<string> renderedUnitIds = new HashSet<string>(StringComparer.Ordinal);
        private ISurfaceRaycaster raycaster;
        private QuestSurfaceRaycaster questRaycaster;
        private SurfaceHitStabilizer surfaceStabilizer;
        private string stabilizedSurfaceEncounterId = string.Empty;
        private string status = "Waiting for projected Read assistance.";
#if UNITY_ANDROID && !UNITY_EDITOR
        private UnityEngine.Android.PermissionCallbacks spatialPermissionCallbacks;
        private bool spatialPermissionRequested;
#endif

        public int WorldRenderedCount => renderedUnitIds.Count;
        public string Status => status;

        private void OnEnable()
        {
            EnsureReferences();
            surfaceStabilizer = BuildSurfaceStabilizer();
            stabilizedSurfaceEncounterId = string.Empty;
            RebuildRaycaster();
            readAssistance.ResultPresented += HandleResultPresented;

            if (readAssistance.LastResult != null)
                Refresh(readAssistance.LastResult);
        }

        private void OnDisable()
        {
            if (readAssistance != null)
                readAssistance.ResultPresented -= HandleResultPresented;
            DetachSpatialPermissionCallbacks();
            DisposeRaycaster();
            ResetSurfaceStability();
            HideAllLabels();
            if (readAssistance != null)
                readAssistance.SetWorldRenderedTargets(Array.Empty<string>());
        }

        private void OnDestroy()
        {
            if (readAssistance != null)
                readAssistance.ResultPresented -= HandleResultPresented;
            DetachSpatialPermissionCallbacks();
            DisposeRaycaster();
            ResetSurfaceStability();
            surfaceStabilizer = null;

            foreach (var pair in labels)
            {
                if (pair.Value != null && pair.Value.gameObject != null)
                    Destroy(pair.Value.gameObject);
            }

            labels.Clear();
            renderedUnitIds.Clear();
            if (readAssistance != null)
                readAssistance.SetWorldRenderedTargets(Array.Empty<string>());
        }

        private void HandleResultPresented(ReadModeSpatialResult result)
        {
            if (result == null)
                return;
            Refresh(result);
        }

        private void Refresh(ReadModeSpatialResult result)
        {
            if (raycaster == null)
                RebuildRaycaster();
            if (surfaceStabilizer == null)
                surfaceStabilizer = BuildSurfaceStabilizer();
            EnsureSpatialPermissionRequested();
            EnsureSurfaceEncounter();

            var previouslyRendered = new HashSet<string>(renderedUnitIds, StringComparer.Ordinal);
            HideAllLabels();

            var planner = new SpatialProjectionPlanner(cameraBridge, raycaster);
            var targets = result.SpatialAssistance.Targets;
            var exactCandidates = 0;
            var surfaceMisses = 0;
            var retainedOcrDropouts = 0;
            var retainedSurfaceMisses = 0;

            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                var unit = target.Segment.Unit;
                if (unit == null)
                    continue;
                if (string.Equals(target.Segment.SourceText, target.Segment.DisplayText, StringComparison.Ordinal))
                    continue;

                var hasRenderableEnvelope = readAssistance.TryGetRenderableEnvelope(target, out var envelope);
                TextMesh retainedLabel = null;
                var isRetainedOcrDropout =
                    !target.Envelope.HasValue &&
                    hasRenderableEnvelope &&
                    previouslyRendered.Contains(unit.Id) &&
                    labels.TryGetValue(unit.Id, out retainedLabel) &&
                    retainedLabel != null;

                if (isRetainedOcrDropout)
                {
                    SetGameObjectActive(retainedLabel.gameObject, true);
                    renderedUnitIds.Add(unit.Id);
                    retainedOcrDropouts++;
                    continue;
                }

                if (target.Coverage != SpatialAssistanceCoverage.Exact)
                    continue;
                if (!hasRenderableEnvelope)
                    continue;

                exactCandidates++;
                var stabilizedTarget = new SpatialAssistanceTarget(
                    target.Segment,
                    target.Regions,
                    SpatialAssistanceCoverage.Exact,
                    envelope);
                var projected = planner.Project(new SpatialAssistancePlan(new[] { stabilizedTarget })).Targets[0];
                if (!projected.CanRenderInWorld || !projected.Surface.HasValue)
                {
                    surfaceMisses++;
                    if (previouslyRendered.Contains(unit.Id) &&
                        labels.TryGetValue(unit.Id, out var heldLabel) &&
                        heldLabel != null &&
                        surfaceStabilizer.TryHoldMissing(unit.Id, out var heldSurface))
                    {
                        ApplyWorldPlacement(heldLabel, target.Segment.DisplayText, heldSurface);
                        SetGameObjectActive(heldLabel.gameObject, true);
                        renderedUnitIds.Add(unit.Id);
                        retainedSurfaceMisses++;
                    }
                    continue;
                }

                var stabilizedSurface = surfaceStabilizer.Stabilize(unit.Id, projected.Surface.Value);
                var label = GetOrCreateLabel(unit.Id);
                ApplyWorldPlacement(label, target.Segment.DisplayText, stabilizedSurface);
                SetGameObjectActive(label.gameObject, true);
                renderedUnitIds.Add(unit.Id);
            }

            readAssistance.SetWorldRenderedTargets(renderedUnitIds);
            status = string.Format(
                "World overlay: candidates={0}, rendered={1}, retained-ocr-dropouts={2}, surface-misses={3}, retained-surface-misses={4}, environment-api={5}.",
                exactCandidates,
                renderedUnitIds.Count,
                retainedOcrDropouts,
                surfaceMisses,
                retainedSurfaceMisses,
                questRaycaster != null && questRaycaster.HasEnvironmentDepthApi);
        }

        private TextMesh GetOrCreateLabel(string unitId)
        {
            if (labels.TryGetValue(unitId, out var existing) && existing != null)
                return existing;

            var gameObject = new GameObject("PhraseLayer World Label " + unitId);
            var textMesh = gameObject.AddComponent<TextMesh>();
            textMesh.anchor = TextAnchor.MiddleCenter;
            textMesh.alignment = TextAlignment.Center;
            textMesh.fontStyle = FontStyle.Bold;
            labels[unitId] = textMesh;
            return textMesh;
        }

        private void ApplyWorldPlacement(TextMesh label, string displayText, SurfaceHit surface)
        {
            var normal = Normalize(ToUnity(surface.Normal));
            var point = ToUnity(surface.Point);
            var offset = (float)Math.Max(0.0, surfaceOffsetMeters);
            var characterSize = (float)Math.Max(0.001, characterSizeMeters);

            label.text = displayText;
            label.fontSize = Math.Max(1, fontSize);
            label.characterSize = characterSize;
            label.transform.localPosition = new Vector3(
                point.x + (normal.x * offset),
                point.y + (normal.y * offset),
                point.z + (normal.z * offset));

            // Unity TextMesh faces local -Z in the standard orientation. Align that front face with the outward
            // surface normal so text is readable from the same side of the surface that the camera ray reached.
            label.transform.localRotation = BuildSurfaceRotation(normal);
        }

        private SurfaceHitStabilizer BuildSurfaceStabilizer()
        {
            return new SurfaceHitStabilizer(new SurfaceHitStabilizerOptions
            {
                BlendFactor = Math.Max(0.01, Math.Min(1.0, surfaceBlendFactor)),
                ResetPointDistanceMeters = Math.Max(0.0, surfaceResetPointDistanceMeters),
                ResetNormalAngleDegrees = Math.Max(0.0, Math.Min(180.0, surfaceResetNormalAngleDegrees)),
                MaxMissingObservations = Math.Max(0, surfaceMaxMissingObservations),
            });
        }

        private void EnsureSurfaceEncounter()
        {
            var encounterId = readAssistance.CurrentEncounterId ?? string.Empty;
            if (string.Equals(stabilizedSurfaceEncounterId, encounterId, StringComparison.Ordinal))
                return;

            surfaceStabilizer.Reset();
            stabilizedSurfaceEncounterId = encounterId;
        }

        private void ResetSurfaceStability()
        {
            surfaceStabilizer?.Reset();
            stabilizedSurfaceEncounterId = string.Empty;
        }

        private void HideAllLabels()
        {
            foreach (var pair in labels)
            {
                if (pair.Value != null && pair.Value.gameObject != null)
                    SetGameObjectActive(pair.Value.gameObject, false);
            }
            renderedUnitIds.Clear();
        }

        private void RebuildRaycaster()
        {
            DisposeRaycaster();
            var maxDistance = maxSurfaceDistanceMeters > 0f ? maxSurfaceDistanceMeters : 0.01f;
            questRaycaster = new QuestSurfaceRaycaster(gameObject, maxDistance, surfaceLayerMask);
            raycaster = questRaycaster;
        }

        private void DisposeRaycaster()
        {
            if (questRaycaster != null)
                questRaycaster.Dispose();
            questRaycaster = null;
            raycaster = null;
        }

        private void EnsureSpatialPermissionRequested()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (questRaycaster == null || !questRaycaster.HasEnvironmentDepthApi)
                return;
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(MetaEnvironmentDepthSurfaceRaycaster.ScenePermission))
                return;
            if (spatialPermissionRequested)
                return;

            spatialPermissionRequested = true;
            spatialPermissionCallbacks = new UnityEngine.Android.PermissionCallbacks();
            spatialPermissionCallbacks.PermissionGranted += HandleSpatialPermissionGranted;
            spatialPermissionCallbacks.PermissionDenied += HandleSpatialPermissionDenied;
            UnityEngine.Android.Permission.RequestUserPermissions(
                new[] { MetaEnvironmentDepthSurfaceRaycaster.ScenePermission },
                spatialPermissionCallbacks);
#endif
        }

        private void DetachSpatialPermissionCallbacks()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (spatialPermissionCallbacks == null)
                return;
            spatialPermissionCallbacks.PermissionGranted -= HandleSpatialPermissionGranted;
            spatialPermissionCallbacks.PermissionDenied -= HandleSpatialPermissionDenied;
            spatialPermissionCallbacks = null;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void HandleSpatialPermissionGranted(string permission)
        {
            if (!string.Equals(permission, MetaEnvironmentDepthSurfaceRaycaster.ScenePermission, StringComparison.Ordinal))
                return;

            RebuildRaycaster();
            status = "Spatial Data permission granted; native environment placement enabled when supported.";
            if (LastResultAvailable())
                Refresh(readAssistance.LastResult);
        }

        private void HandleSpatialPermissionDenied(string permission)
        {
            if (!string.Equals(permission, MetaEnvironmentDepthSurfaceRaycaster.ScenePermission, StringComparison.Ordinal))
                return;
            status = "Spatial Data permission denied; using collider/viewport fallback.";
        }

        private bool LastResultAvailable()
        {
            return readAssistance != null && readAssistance.LastResult != null;
        }
#endif

        private void EnsureReferences()
        {
            if (readAssistance == null)
                throw new InvalidOperationException("Assign QuestReadAssistanceDebugBehaviour to QuestReadWorldOverlayBehaviour.");
            if (cameraBridge == null)
                throw new InvalidOperationException("Assign MetaPassthroughCameraBridge to QuestReadWorldOverlayBehaviour.");
        }

        private static Quaternion BuildSurfaceRotation(Vector3 normal)
        {
            var method = typeof(Quaternion).GetMethod(
                "LookRotation",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(Vector3), typeof(Vector3) },
                null);
            if (method == null)
                throw new MissingMethodException(typeof(Quaternion).FullName, "LookRotation(Vector3, Vector3)");

            var forward = new Vector3(-normal.x, -normal.y, -normal.z);
            var value = method.Invoke(null, new object[] { forward, new Vector3(0f, 1f, 0f) });
            if (value is Quaternion rotation)
                return rotation;
            throw new InvalidOperationException("Quaternion.LookRotation did not return a Quaternion.");
        }

        private static void SetGameObjectActive(GameObject gameObject, bool active)
        {
            var method = typeof(GameObject).GetMethod(
                "SetActive",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(bool) },
                null);
            if (method == null)
                throw new MissingMethodException(typeof(GameObject).FullName, "SetActive(bool)");
            method.Invoke(gameObject, new object[] { active });
        }

        private static Vector3 Normalize(Vector3 value)
        {
            var magnitude = Math.Sqrt((value.x * value.x) + (value.y * value.y) + (value.z * value.z));
            if (magnitude <= 0.0)
                throw new InvalidOperationException("Projected surface normal must be non-zero.");
            return new Vector3(
                (float)(value.x / magnitude),
                (float)(value.y / magnitude),
                (float)(value.z / magnitude));
        }

        private static Vector3 ToUnity(SpatialVector3 value)
        {
            return new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        }
    }
}
