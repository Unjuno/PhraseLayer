using System;
using System.Collections.Generic;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// World-space renderer for stabilized in-place assistance tracks.
    ///
    /// A reviewed Japanese-capable Unity Font must be assigned explicitly; PhraseLayer does not silently bundle or
    /// substitute a font asset. This component only owns translated text rendering slightly in front of the fitted
    /// physical text plane. Physical source covering is intentionally owned by UnityWorldTextSourceMaskBehaviour so
    /// its stricter current-observation/planarity policy can fail closed independently and be validated on Quest.
    /// </summary>
    public sealed class UnityWorldTextRendererBehaviour : MonoBehaviour
    {
        [SerializeField] private Font font = default(Font);
        [SerializeField] private int fontSize = 64;
        [SerializeField] private float textHeightFraction = 0.80f;
        [SerializeField] private float surfaceOffsetMeters = 0.003f;
        [SerializeField] private bool renderRetainedTracks = true;

        private readonly Dictionary<long, TrackView> views = new Dictionary<long, TrackView>();

        public Font Font => font;
        public bool IsConfigured => font != null;
        public int ActiveViewCount => views.Count;
        public WorldTextTrackingPlan LastPlan { get; private set; }

        public void SetFont(Font reviewedFont)
        {
            font = reviewedFont ?? throw new ArgumentNullException(nameof(reviewedFont));
            RefreshFontMaterial();
        }

        public bool TryPresent(WorldTextTrackingPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            ValidateConfiguration();
            LastPlan = plan;

            if (font == null)
                return false;

            var liveTrackIds = new HashSet<long>();
            foreach (var track in plan.Tracks)
            {
                if (!track.ObservedThisFrame && !renderRetainedTracks)
                    continue;

                liveTrackIds.Add(track.TrackId);
                var view = GetOrCreateView(track.TrackId);
                UpdateView(view, track);
            }

            RemoveMissingViews(liveTrackIds);
            return true;
        }

        public void Clear()
        {
            foreach (var pair in views)
            {
                if (pair.Value.Root != null)
                    Destroy(pair.Value.Root);
            }
            views.Clear();
            LastPlan = null;
        }

        private void OnValidate()
        {
            ValidateConfiguration();
            RefreshFontMaterial();
        }

        private void OnDestroy()
        {
            Clear();
        }

        private TrackView GetOrCreateView(long trackId)
        {
            if (views.TryGetValue(trackId, out var existing))
                return existing;

            var root = new GameObject("PhraseLayer World Text " + trackId);
            root.transform.SetParent(transform, false);
            var text = root.AddComponent<TextMesh>();
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = fontSize;
            text.font = font;

            var meshRenderer = root.GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                throw new InvalidOperationException("Unity TextMesh did not provide its required MeshRenderer.");
            meshRenderer.sharedMaterial = font.material;

            var created = new TrackView(root, text, meshRenderer);
            views.Add(trackId, created);
            return created;
        }

        private void UpdateView(TrackView view, WorldTextTrackState track)
        {
            var segment = track.Source.Source.Source.Segment;
            var surface = track.Surface;

            view.Text.font = font;
            view.Text.fontSize = fontSize;
            view.Text.text = segment.DisplayText;
            view.Text.characterSize = Math.Max(0.0001f, (float)(surface.HeightMeters * textHeightFraction));
            view.Renderer.sharedMaterial = font.material;

            var position = ToUnity(surface.Center);
            var cameraToSurface = track.Source.Source.Ray;
            if (cameraToSurface.HasValue && TryNormalize(ToUnity(cameraToSurface.Value.Direction), out var rayDirection))
                position = Subtract(position, Scale(rayDirection, surfaceOffsetMeters));

            view.Root.transform.position = position;
            view.Root.transform.rotation = Quaternion.LookRotation(
                ToUnity(surface.Normal),
                ToUnity(surface.Up));
            view.Root.transform.localScale = Vector3.one;
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
            {
                var id = stale[index];
                var view = views[id];
                views.Remove(id);
                if (view.Root != null)
                    Destroy(view.Root);
            }
        }

        private void RefreshFontMaterial()
        {
            if (font == null) return;
            foreach (var pair in views)
            {
                pair.Value.Text.font = font;
                pair.Value.Renderer.sharedMaterial = font.material;
            }
        }

        private void ValidateConfiguration()
        {
            if (fontSize <= 0)
                throw new InvalidOperationException("World text renderer font size must be greater than zero.");
            if (!IsFinitePositive(textHeightFraction))
                throw new InvalidOperationException("World text renderer height fraction must be finite and greater than zero.");
            if (float.IsNaN(surfaceOffsetMeters) || float.IsInfinity(surfaceOffsetMeters) || surfaceOffsetMeters < 0f)
                throw new InvalidOperationException("World text renderer surface offset must be finite and non-negative.");
        }

        private static bool IsFinitePositive(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }

        private static Vector3 ToUnity(SpatialVector3 value)
        {
            return new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        }

        private static bool TryNormalize(Vector3 value, out Vector3 normalized)
        {
            var squared = (value.x * value.x) + (value.y * value.y) + (value.z * value.z);
            if (float.IsNaN(squared) || float.IsInfinity(squared) || squared <= 1e-12f)
            {
                normalized = default(Vector3);
                return false;
            }

            var inverseMagnitude = 1f / (float)Math.Sqrt(squared);
            normalized = Scale(value, inverseMagnitude);
            return true;
        }

        private static Vector3 Scale(Vector3 value, float scale)
        {
            return new Vector3(value.x * scale, value.y * scale, value.z * scale);
        }

        private static Vector3 Subtract(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        }

        private sealed class TrackView
        {
            public TrackView(GameObject root, TextMesh text, MeshRenderer renderer)
            {
                Root = root;
                Text = text;
                Renderer = renderer;
            }

            public GameObject Root { get; }
            public TextMesh Text { get; }
            public MeshRenderer Renderer { get; }
        }
    }
}
