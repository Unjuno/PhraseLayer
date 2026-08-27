using System;
using System.Collections.Generic;
using System.Reflection;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Projects exact Read assistance targets from stabilized OCR viewport geometry onto real Unity collider-backed
    /// surfaces. Targets that cannot be projected remain available to the existing viewport GUI fallback.
    ///
    /// This component is intentionally conservative: it never assumes a fixed depth and never turns an unresolved
    /// OCR target into a world-space replacement. Accurate real-world placement therefore depends on a real surface
    /// provider (for example MRUK/scene geometry with colliders) being present in the running Quest scene.
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

        private readonly Dictionary<string, TextMesh> labels = new Dictionary<string, TextMesh>(StringComparer.Ordinal);
        private readonly HashSet<string> renderedUnitIds = new HashSet<string>(StringComparer.Ordinal);
        private UnityPhysicsSurfaceRaycaster raycaster;
        private string status = "Waiting for projected Read assistance.";

        public int WorldRenderedCount => renderedUnitIds.Count;
        public string Status => status;

        private void OnEnable()
        {
            EnsureReferences();
            RebuildRaycaster();
            readAssistance.ResultPresented += HandleResultPresented;

            if (readAssistance.LastResult != null)
                Refresh(readAssistance.LastResult);
        }

        private void OnDisable()
        {
            if (readAssistance != null)
                readAssistance.ResultPresented -= HandleResultPresented;
            HideAllLabels();
            if (readAssistance != null)
                readAssistance.SetWorldRenderedTargets(Array.Empty<string>());
        }

        private void OnDestroy()
        {
            if (readAssistance != null)
                readAssistance.ResultPresented -= HandleResultPresented;

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

            HideAllLabels();
            renderedUnitIds.Clear();

            var planner = new SpatialProjectionPlanner(cameraBridge, raycaster);
            var targets = result.SpatialAssistance.Targets;
            var exactCandidates = 0;
            var surfaceMisses = 0;

            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                var unit = target.Segment.Unit;
                if (unit == null)
                    continue;
                if (target.Coverage != SpatialAssistanceCoverage.Exact)
                    continue;
                if (string.Equals(target.Segment.SourceText, target.Segment.DisplayText, StringComparison.Ordinal))
                    continue;
                if (!readAssistance.TryGetRenderableEnvelope(target, out var envelope))
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
                    continue;
                }

                var label = GetOrCreateLabel(unit.Id);
                ApplyWorldPlacement(label, target.Segment.DisplayText, projected.Surface.Value);
                SetGameObjectActive(label.gameObject, true);
                renderedUnitIds.Add(unit.Id);
            }

            readAssistance.SetWorldRenderedTargets(renderedUnitIds);
            status = string.Format(
                "World overlay: candidates={0}, rendered={1}, surface-misses={2}.",
                exactCandidates,
                renderedUnitIds.Count,
                surfaceMisses);
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
            var maxDistance = maxSurfaceDistanceMeters > 0f ? maxSurfaceDistanceMeters : 0.01f;
            raycaster = new UnityPhysicsSurfaceRaycaster(maxDistance, surfaceLayerMask);
        }

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
