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
    /// small depth/normal jitter. When four viewport-corner rays can intersect that same verified plane, the physical
    /// OCR width, height and vertical tangent drive the world label's size and orientation. A previously verified
    /// surface may survive only a bounded raycast miss, and all world-surface state and label objects are discarded on
    /// encounter change so stale real-world text cannot leak or accumulate across the session.
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
        [SerializeField] private float fittedTextHeightFraction = 0.85f;
        [SerializeField] private float fittedTextWidthFraction = 0.95f;

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
            DestroyAllLabels();
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
            var layoutProjector = new SurfacePlaneTextLayoutProjector(cameraBridge);
            var targets = result.SpatialAssistance.Targets;
            var exactCandidates = 0;
            var surfaceMisses = 0;
            var retainedOcrDropouts = 0;
            var retainedSurfaceMisses = 0;
            var fittedSurfaceLayouts = 0;
            var surfaceLayoutFailures = 0;

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
                        var heldLayout = TryBuildSurfaceLayout(layoutProjector, envelope, heldSurface, ref fittedSurfaceLayouts, ref surfaceLayoutFailures);
                        ApplyWorldPlacement(heldLabel, target.Segment.DisplayText, heldSurface, heldLayout);
                        SetGameObjectActive(heldLabel.gameObject, true);
                        renderedUnitIds.Add(unit.Id);
                        retainedSurfaceMisses++;
                    }
                    continue;
                }

                var stabilizedSurface = surfaceStabilizer.Stabilize(unit.Id, projected.Surface.Value);
                var surfaceLayout = TryBuildSurfaceLayout(layoutProjector, envelope, stabilizedSurface, ref fittedSurfaceLayouts, ref surfaceLayoutFailures);
                var label = GetOrCreateLabel(unit.Id);
                ApplyWorldPlacement(label, target.Segment.DisplayText, stabilizedSurface, surfaceLayout);
                SetGameObjectActive(label.gameObject, true);
                renderedUnitIds.Add(unit.Id);
            }

            readAssistance.SetWorldRenderedTargets(renderedUnitIds);
            status = string.Format(
                "World overlay: candidates={0}, rendered={1}, retained-ocr-dropouts={2}, surface-misses={3}, retained-surface-misses={4}, fitted-layouts={5}, layout-failures={6}, environment-api={7}.",
                exactCandidates,
                renderedUnitIds.Count,
                retainedOcrDropouts,
                surfaceMisses,
                retainedSurfaceMisses,
                fittedSurfaceLayouts,
                surfaceLayoutFailures,
                questRaycaster != null && questRaycaster.HasEnvironmentDepthApi);
        }

        private static SurfaceTextLayout? TryBuildSurfaceLayout(
            SurfacePlaneTextLayoutProjector projector,
            ViewportEnvelope envelope,
            SurfaceHit surface,
            ref int successes,
            ref int failures)
        {
            if (projector.TryProject(envelope, surface, out var layout, out _))
            {
                successes++;
                return layout;
            }

            failures++;
            return null;
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

        private void ApplyWorldPlacement(TextMesh label, string displayText, SurfaceHit surface, SurfaceTextLayout? layout)
        {
            var normal = Normalize(ToUnity(layout.HasValue ? layout.Value.Normal : surface.Normal));
            var point = ToUnity(layout.HasValue ? layout.Value.Center : surface.Point);
            var up = layout.HasValue ? Normalize(ToUnity(layout.Value.Up)) : BuildFallbackUp(normal);
            var offset = (float)Math.Max(0.0, surfaceOffsetMeters);

            label.text = displayText;
            label.fontSize = Math.Max(1, fontSize);
            label.characterSize = layout.HasValue
                ? ComputeFittedCharacterSize(displayText, layout.Value)
                : (float)Math.Max(0.001, characterSizeMeters);
            label.transform.localPosition = new Vector3(
                point.x + (normal.x * offset),
                point.y + (normal.y * offset),
                point.z + (normal.z * offset));

            // Unity TextMesh faces local -Z in the standard orientation. The plane-derived vertical tangent is used
            // as the up hint so wall, tilted and floor text do not silently inherit global-up orientation.
            label.transform.localRotation = BuildSurfaceRotation(normal, up);
        }

        private float ComputeFittedCharacterSize(string displayText, SurfaceTextLayout layout)
        {
            var heightFraction = Math.Max(0.05, Math.Min(1.0, fittedTextHeightFraction));
            var widthFraction = Math.Max(0.05, Math.Min(1.0, fittedTextWidthFraction));
            return (float)SurfaceTextSizing.ComputeCharacterSizeMeters(
                displayText,
                layout,
                heightFraction,
                widthFraction,
                minimumCharacterSizeMeters: 0.001);
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
            DestroyAllLabels();
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

        private void DestroyAllLabels()
        {
            foreach (var pair in labels)
            {
                if (pair.Value != null && pair.Value.gameObject != null)
                    Destroy(pair.Value.gameObject);
            }

            labels.Clear();
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

        private static Quaternion BuildSurfaceRotation(Vector3 normal, Vector3 up)
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
            var value = method.Invoke(null, new object[] { forward, up });
            if (value is Quaternion rotation)
                return rotation;
            throw new InvalidOperationException("Quaternion.LookRotation did not return a Quaternion.");
        }

        private static Vector3 BuildFallbackUp(Vector3 normal)
        {
            var worldUp = new Vector3(0f, 1f, 0f);
            var dot = Math.Abs((normal.x * worldUp.x) + (normal.y * worldUp.y) + (normal.z * worldUp.z));
            return dot < 0.95 ? worldUp : new Vector3(0f, 0f, 1f);
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
                throw new InvalidOperationException("Projected surface vector must be non-zero.");
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
