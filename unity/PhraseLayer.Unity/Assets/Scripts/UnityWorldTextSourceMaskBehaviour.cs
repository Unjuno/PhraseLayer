using System;
using System.Collections.Generic;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Covers only currently re-observed physical source-text envelopes that pass the conservative Core mask policy.
    ///
    /// The mask mesh is created directly and never receives a Collider, so it cannot feed back into the physical
    /// surface raycast used to place subsequent text. The assigned material must be explicitly reviewed for the target
    /// render pipeline; in particular it should be opaque and visible from the headset-facing side.
    /// </summary>
    public sealed class UnityWorldTextSourceMaskBehaviour : MonoBehaviour
    {
        [SerializeField] private Material maskMaterial = default(Material);
        [SerializeField] private float horizontalPaddingFraction = 0.06f;
        [SerializeField] private float verticalPaddingFraction = 0.12f;
        [SerializeField] private float surfaceOffsetMeters = 0.0015f;
        [SerializeField] private int minimumObservationCount = 2;
        [SerializeField] private float maximumPlanarityErrorMeters = 0.01f;

        private readonly Dictionary<long, MaskView> views = new Dictionary<long, MaskView>();
        private WorldTextMaskPolicy policy;

        public Material MaskMaterial => maskMaterial;
        public bool IsConfigured => maskMaterial != null;
        public int ActiveMaskCount => views.Count;
        public int LastEligibleMaskCount { get; private set; }
        public int LastSuppressedMaskCount { get; private set; }
        public WorldTextTrackingPlan LastPlan { get; private set; }

        public void SetMaskMaterial(Material reviewedMaskMaterial)
        {
            maskMaterial = reviewedMaskMaterial ?? throw new ArgumentNullException(nameof(reviewedMaskMaterial));
            RefreshMaterial();
        }

        public bool TryPresent(WorldTextTrackingPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            EnsurePolicy();
            LastPlan = plan;
            LastEligibleMaskCount = 0;
            LastSuppressedMaskCount = 0;

            if (maskMaterial == null)
            {
                LastSuppressedMaskCount = plan.Tracks.Count;
                ClearViews();
                return false;
            }

            var liveTrackIds = new HashSet<long>();
            foreach (var track in plan.Tracks)
            {
                var decision = policy.Evaluate(track);
                if (!decision.CanMask)
                {
                    LastSuppressedMaskCount++;
                    continue;
                }

                liveTrackIds.Add(track.TrackId);
                LastEligibleMaskCount++;
                var view = GetOrCreateView(track.TrackId);
                UpdateView(view, track);
            }

            RemoveMissingViews(liveTrackIds);
            return true;
        }

        public void Clear()
        {
            ClearViews();
            LastPlan = null;
            LastEligibleMaskCount = 0;
            LastSuppressedMaskCount = 0;
        }

        private void OnValidate()
        {
            ValidateConfiguration();
            policy = null;
            RefreshMaterial();
        }

        private void OnDestroy()
        {
            Clear();
        }

        private MaskView GetOrCreateView(long trackId)
        {
            if (views.TryGetValue(trackId, out var existing))
                return existing;

            var root = new GameObject("PhraseLayer Source Mask " + trackId);
            root.transform.SetParent(transform, false);
            var meshFilter = root.AddComponent<MeshFilter>();
            var meshRenderer = root.AddComponent<MeshRenderer>();
            var mesh = CreateUnitQuadMesh(trackId);
            meshFilter.sharedMesh = mesh;
            meshRenderer.sharedMaterial = maskMaterial;

            var created = new MaskView(root, mesh, meshRenderer);
            views.Add(trackId, created);
            return created;
        }

        private void UpdateView(MaskView view, WorldTextTrackState track)
        {
            var surface = track.Surface;
            var width = (float)(surface.WidthMeters * (1.0 + (2.0 * horizontalPaddingFraction)));
            var height = (float)(surface.HeightMeters * (1.0 + (2.0 * verticalPaddingFraction)));
            var position = ToUnity(surface.Center);
            var forward = ToUnity(surface.Normal);
            var up = ToUnity(surface.Up);

            var cameraToSurface = track.Source.Source.Ray;
            if (cameraToSurface.HasValue && TryNormalize(ToUnity(cameraToSurface.Value.Direction), out var rayDirection))
            {
                position = Subtract(position, Scale(rayDirection, surfaceOffsetMeters));
                // A fitted plane normal has no intrinsic front side. Orient the mask toward the camera so a
                // conventional single-sided opaque material remains visible without mirroring the text axes.
                if (Dot(forward, rayDirection) > 0f)
                    forward = Scale(forward, -1f);
            }

            view.Renderer.sharedMaterial = maskMaterial;
            view.Root.transform.position = position;
            view.Root.transform.rotation = Quaternion.LookRotation(forward, up);
            view.Root.transform.localScale = new Vector3(
                Math.Max(0.0001f, width),
                Math.Max(0.0001f, height),
                1f);
        }

        private void RemoveMissingViews(HashSet<long> liveTrackIds)
        {
            var stale = new List<long>();
            foreach (var pair in views)
            {
                if (!liveTrackIds.Contains(pair.Key))
                    stale.Add(pair.Key);
            }

            for (var index = 0; index < stale.Count; index++)
                DestroyView(stale[index]);
        }

        private void ClearViews()
        {
            var ids = new List<long>(views.Keys);
            for (var index = 0; index < ids.Count; index++)
                DestroyView(ids[index]);
        }

        private void DestroyView(long trackId)
        {
            if (!views.TryGetValue(trackId, out var view))
                return;

            views.Remove(trackId);
            if (view.Mesh != null)
                Destroy(view.Mesh);
            if (view.Root != null)
                Destroy(view.Root);
        }

        private void RefreshMaterial()
        {
            if (maskMaterial == null) return;
            foreach (var pair in views)
                pair.Value.Renderer.sharedMaterial = maskMaterial;
        }

        private void EnsurePolicy()
        {
            if (policy != null) return;
            ValidateConfiguration();
            policy = new WorldTextMaskPolicy(minimumObservationCount, maximumPlanarityErrorMeters);
        }

        private void ValidateConfiguration()
        {
            if (!IsFiniteNonNegative(horizontalPaddingFraction))
                throw new InvalidOperationException("Source mask horizontal padding must be finite and non-negative.");
            if (!IsFiniteNonNegative(verticalPaddingFraction))
                throw new InvalidOperationException("Source mask vertical padding must be finite and non-negative.");
            if (!IsFiniteNonNegative(surfaceOffsetMeters))
                throw new InvalidOperationException("Source mask surface offset must be finite and non-negative meters.");
            if (minimumObservationCount <= 0)
                throw new InvalidOperationException("Source mask minimum observation count must be greater than zero.");
            if (!IsFiniteNonNegative(maximumPlanarityErrorMeters))
                throw new InvalidOperationException("Source mask maximum planarity error must be finite and non-negative meters.");
        }

        private static Mesh CreateUnitQuadMesh(long trackId)
        {
            var mesh = new Mesh();
            mesh.name = "PhraseLayer Source Mask Mesh " + trackId;
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f)
            };
            mesh.normals = new[]
            {
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 1f)
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f)
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static bool IsFiniteNonNegative(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
        }

        private static Vector3 ToUnity(SpatialVector3 value)
        {
            return new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        }

        private static bool TryNormalize(Vector3 value, out Vector3 normalized)
        {
            var squared = Dot(value, value);
            if (float.IsNaN(squared) || float.IsInfinity(squared) || squared <= 1e-12f)
            {
                normalized = default(Vector3);
                return false;
            }

            normalized = Scale(value, 1f / (float)Math.Sqrt(squared));
            return true;
        }

        private static float Dot(Vector3 a, Vector3 b)
        {
            return (a.x * b.x) + (a.y * b.y) + (a.z * b.z);
        }

        private static Vector3 Scale(Vector3 value, float scale)
        {
            return new Vector3(value.x * scale, value.y * scale, value.z * scale);
        }

        private static Vector3 Subtract(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        }

        private sealed class MaskView
        {
            public MaskView(GameObject root, Mesh mesh, MeshRenderer renderer)
            {
                Root = root;
                Mesh = mesh;
                Renderer = renderer;
            }

            public GameObject Root { get; }
            public Mesh Mesh { get; }
            public MeshRenderer Renderer { get; }
        }
    }
}
