using System;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Thin ISurfaceRaycaster adapter over Unity Physics. PhraseLayer does not assume where the colliders come from:
    /// they may be scene meshes, MRUK-provided environment colliders, or controlled test geometry. Missing colliders
    /// remain a normal projection failure and never cause PhraseLayer to guess a physical text surface.
    /// </summary>
    public sealed class UnityPhysicsSurfaceRaycaster : MonoBehaviour, ISurfaceRaycaster
    {
        [SerializeField] private float maxDistanceMeters = 10f;
        [SerializeField] private LayerMask layerMask = -1;
        [SerializeField] private QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore;

        public float MaxDistanceMeters => maxDistanceMeters;
        public int LayerMaskValue => layerMask.value;

        public bool TryRaycast(SpatialRay ray, out SurfaceHit hit)
        {
            ValidateConfiguration();

            var origin = ToFiniteVector3(ray.Origin, "ray origin");
            var directionMagnitude = Math.Sqrt(ray.Direction.SquaredMagnitude);
            if (double.IsNaN(directionMagnitude) || double.IsInfinity(directionMagnitude) || directionMagnitude <= 0.0)
                throw new ArgumentException("Spatial ray direction must have a finite non-zero magnitude.", nameof(ray));

            var direction = ToFiniteVector3(
                new SpatialVector3(
                    ray.Direction.X / directionMagnitude,
                    ray.Direction.Y / directionMagnitude,
                    ray.Direction.Z / directionMagnitude),
                "normalized ray direction");

            var unityRay = new Ray(origin, direction);
            if (!Physics.Raycast(
                    unityRay,
                    out var unityHit,
                    maxDistanceMeters,
                    layerMask,
                    triggerInteraction))
            {
                hit = default(SurfaceHit);
                return false;
            }

            hit = new SurfaceHit(
                ToSpatial(unityHit.point),
                ToSpatial(unityHit.normal),
                unityHit.distance);
            return true;
        }

        private void OnValidate()
        {
            ValidateConfiguration();
        }

        private void ValidateConfiguration()
        {
            if (float.IsNaN(maxDistanceMeters) || float.IsInfinity(maxDistanceMeters) || maxDistanceMeters <= 0f)
                throw new InvalidOperationException("Surface raycast max distance must be finite and greater than zero meters.");
        }

        private static Vector3 ToFiniteVector3(SpatialVector3 value, string label)
        {
            var x = ToFiniteFloat(value.X, label + " X");
            var y = ToFiniteFloat(value.Y, label + " Y");
            var z = ToFiniteFloat(value.Z, label + " Z");
            return new Vector3(x, y, z);
        }

        private static float ToFiniteFloat(double value, string label)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < -float.MaxValue || value > float.MaxValue)
                throw new ArgumentOutOfRangeException(label, "Spatial coordinate cannot be represented as a finite Unity float.");
            return (float)value;
        }

        private static SpatialVector3 ToSpatial(Vector3 value)
        {
            return new SpatialVector3(value.x, value.y, value.z);
        }
    }

    /// <summary>
    /// Scene-facing bridge from aligned Read Mode output to the platform-neutral projection and physical text-plane policies.
    /// Center projection is followed by an independent four-corner surface fit before an in-place target is considered layout-ready.
    /// </summary>
    public sealed class UnitySpatialProjectionBehaviour : MonoBehaviour
    {
        [SerializeField] private MetaPassthroughCameraBridge rayProvider = default(MetaPassthroughCameraBridge);
        [SerializeField] private UnityPhysicsSurfaceRaycaster surfaceRaycaster = default(UnityPhysicsSurfaceRaycaster);
        [SerializeField] private float maximumPlanarityErrorMeters = 0.03f;
        [SerializeField] private float minimumTextExtentMeters = 0.005f;
        [SerializeField] private float minimumSurfaceNormalDot = 0.80f;

        private SpatialProjectionPlanner projectionPlanner;
        private WorldTextLayoutPlanner layoutPlanner;

        public SpatialProjectionPlan LastPlan { get; private set; }
        public WorldTextLayoutPlan LastWorldTextLayout { get; private set; }
        public MetaPassthroughCameraBridge RayProvider => rayProvider;
        public UnityPhysicsSurfaceRaycaster SurfaceRaycaster => surfaceRaycaster;

        public void SetSceneReferences(
            MetaPassthroughCameraBridge viewportRayProvider,
            UnityPhysicsSurfaceRaycaster worldSurfaceRaycaster)
        {
            rayProvider = viewportRayProvider ?? throw new ArgumentNullException(nameof(viewportRayProvider));
            surfaceRaycaster = worldSurfaceRaycaster ?? throw new ArgumentNullException(nameof(worldSurfaceRaycaster));
            projectionPlanner = null;
            layoutPlanner = null;
            LastPlan = null;
            LastWorldTextLayout = null;
        }

        public SpatialProjectionPlan Project(ReadModeAlignedResult aligned)
        {
            if (aligned == null) throw new ArgumentNullException(nameof(aligned));
            EnsurePlanners();
            LastPlan = projectionPlanner.Project(aligned.SpatialAssistance);
            LastWorldTextLayout = null;
            return LastPlan;
        }

        public WorldTextLayoutPlan ProjectAndFitWorldText(ReadModeAlignedResult aligned)
        {
            var projection = Project(aligned);
            LastWorldTextLayout = layoutPlanner.Fit(projection);
            return LastWorldTextLayout;
        }

        public WorldTextLayoutPlan FitWorldText(SpatialProjectionPlan projection)
        {
            if (projection == null) throw new ArgumentNullException(nameof(projection));
            EnsurePlanners();
            LastPlan = projection;
            LastWorldTextLayout = layoutPlanner.Fit(projection);
            return LastWorldTextLayout;
        }

        private void OnValidate()
        {
            ValidateLayoutConfiguration();
            projectionPlanner = null;
            layoutPlanner = null;
        }

        private void EnsurePlanners()
        {
            if (projectionPlanner != null && layoutPlanner != null) return;
            if (rayProvider == null)
                throw new InvalidOperationException("Assign MetaPassthroughCameraBridge before projecting Read Mode assistance.");
            if (surfaceRaycaster == null)
                throw new InvalidOperationException("Assign UnityPhysicsSurfaceRaycaster before projecting Read Mode assistance.");

            ValidateLayoutConfiguration();
            projectionPlanner = new SpatialProjectionPlanner(rayProvider, surfaceRaycaster);
            layoutPlanner = new WorldTextLayoutPlanner(
                rayProvider,
                surfaceRaycaster,
                maximumPlanarityErrorMeters,
                minimumTextExtentMeters,
                minimumSurfaceNormalDot);
        }

        private void ValidateLayoutConfiguration()
        {
            if (!IsFinitePositive(maximumPlanarityErrorMeters))
                throw new InvalidOperationException("Maximum text-surface planarity error must be finite and greater than zero meters.");
            if (!IsFinitePositive(minimumTextExtentMeters))
                throw new InvalidOperationException("Minimum world text extent must be finite and greater than zero meters.");
            if (float.IsNaN(minimumSurfaceNormalDot) || float.IsInfinity(minimumSurfaceNormalDot) ||
                minimumSurfaceNormalDot < 0f || minimumSurfaceNormalDot > 1f)
            {
                throw new InvalidOperationException("Minimum surface normal dot must be finite and within [0,1].");
            }
        }

        private static bool IsFinitePositive(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }
    }

    /// <summary>
    /// Reference visualization for Quest verification. It draws fitted OCR text envelopes in world space without
    /// claiming to be the final replacement renderer. Only layout-ready in-place targets are visualized.
    /// </summary>
    public sealed class UnityWorldTextLayoutDebugBehaviour : MonoBehaviour
    {
        private WorldTextLayoutPlan plan;

        public WorldTextLayoutPlan Plan => plan;

        public void Present(WorldTextLayoutPlan worldTextLayout)
        {
            plan = worldTextLayout ?? throw new ArgumentNullException(nameof(worldTextLayout));
        }

        public void Clear()
        {
            plan = null;
        }

        private void Update()
        {
            if (plan == null) return;
            foreach (var target in plan.Targets)
            {
                if (!target.IsReady) continue;
                DrawSurface(target.Surface.Value);
            }
        }

        private static void DrawSurface(WorldTextSurface surface)
        {
            var center = ToUnity(surface.Center);
            var right = Scale(ToUnity(surface.Right), (float)(surface.WidthMeters * 0.5));
            var up = Scale(ToUnity(surface.Up), (float)(surface.HeightMeters * 0.5));

            var p0 = Add(Subtract(center, right), up);
            var p1 = Add(Add(center, right), up);
            var p2 = Subtract(Add(center, right), up);
            var p3 = Subtract(Subtract(center, right), up);

            Debug.DrawLine(p0, p1);
            Debug.DrawLine(p1, p2);
            Debug.DrawLine(p2, p3);
            Debug.DrawLine(p3, p0);
        }

        private static Vector3 ToUnity(SpatialVector3 value) =>
            new Vector3((float)value.X, (float)value.Y, (float)value.Z);

        private static Vector3 Add(Vector3 a, Vector3 b) =>
            new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);

        private static Vector3 Subtract(Vector3 a, Vector3 b) =>
            new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);

        private static Vector3 Scale(Vector3 value, float scale) =>
            new Vector3(value.x * scale, value.y * scale, value.z * scale);
    }
}
