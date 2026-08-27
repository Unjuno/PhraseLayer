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
    /// Scene-facing bridge from aligned Read Mode output to the platform-neutral projection policy. It computes
    /// world surface hits only; rendering, smoothing, tracking, and source-text masking remain separate concerns.
    /// </summary>
    public sealed class UnitySpatialProjectionBehaviour : MonoBehaviour
    {
        [SerializeField] private MetaPassthroughCameraBridge rayProvider = default(MetaPassthroughCameraBridge);
        [SerializeField] private UnityPhysicsSurfaceRaycaster surfaceRaycaster = default(UnityPhysicsSurfaceRaycaster);

        private SpatialProjectionPlanner planner;

        public SpatialProjectionPlan LastPlan { get; private set; }
        public MetaPassthroughCameraBridge RayProvider => rayProvider;
        public UnityPhysicsSurfaceRaycaster SurfaceRaycaster => surfaceRaycaster;

        public void SetSceneReferences(
            MetaPassthroughCameraBridge viewportRayProvider,
            UnityPhysicsSurfaceRaycaster worldSurfaceRaycaster)
        {
            rayProvider = viewportRayProvider ?? throw new ArgumentNullException(nameof(viewportRayProvider));
            surfaceRaycaster = worldSurfaceRaycaster ?? throw new ArgumentNullException(nameof(worldSurfaceRaycaster));
            planner = null;
            LastPlan = null;
        }

        public SpatialProjectionPlan Project(ReadModeAlignedResult aligned)
        {
            if (aligned == null) throw new ArgumentNullException(nameof(aligned));
            EnsurePlanner();
            LastPlan = planner.Project(aligned.SpatialAssistance);
            return LastPlan;
        }

        private void EnsurePlanner()
        {
            if (planner != null) return;
            if (rayProvider == null)
                throw new InvalidOperationException("Assign MetaPassthroughCameraBridge before projecting Read Mode assistance.");
            if (surfaceRaycaster == null)
                throw new InvalidOperationException("Assign UnityPhysicsSurfaceRaycaster before projecting Read Mode assistance.");

            planner = new SpatialProjectionPlanner(rayProvider, surfaceRaycaster);
        }
    }
}
