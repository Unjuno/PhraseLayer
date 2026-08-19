using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Spatial;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class CameraAndProjectionTests
    {
        [Fact]
        public async Task CameraCoordinatorRequestsPermissionStartsStreamAndCapturesFrame()
        {
            var permission = new FakePermissionService(CameraPermissionState.Unknown, CameraPermissionState.Granted);
            var frame = new ImageFrame(new byte[4], 2, 2, 99, ImagePixelFormat.Gray8);
            var stream = new FakeCameraStream(frame);
            var coordinator = new CameraCaptureCoordinator(permission, stream);

            var captured = await coordinator.CaptureAsync();

            Assert.Equal(CameraCaptureState.Ready, coordinator.State);
            Assert.Equal(1, permission.RequestCount);
            Assert.Equal(1, stream.StartCount);
            Assert.Equal(1, stream.CaptureCount);
            Assert.Same(frame, captured);
            Assert.Null(coordinator.FailureReason);
        }

        [Fact]
        public async Task CameraCoordinatorStopsWhenPermissionIsDenied()
        {
            var permission = new FakePermissionService(CameraPermissionState.Unknown, CameraPermissionState.Denied);
            var stream = new FakeCameraStream(new ImageFrame(new byte[4], 2, 2, 0));
            var coordinator = new CameraCaptureCoordinator(permission, stream);

            var state = await coordinator.EnsureReadyAsync();

            Assert.Equal(CameraCaptureState.Failed, state);
            Assert.Equal(1, permission.RequestCount);
            Assert.Equal(0, stream.StartCount);
            Assert.Equal("Camera permission was not granted.", coordinator.FailureReason);
        }

        [Fact]
        public async Task CameraCoordinatorRejectsBackendThatNeverStartsPlaying()
        {
            var permission = new FakePermissionService(CameraPermissionState.Granted, CameraPermissionState.Granted);
            var stream = new FakeCameraStream(new ImageFrame(new byte[4], 2, 2, 0)) { StartMakesPlaying = false };
            var coordinator = new CameraCaptureCoordinator(permission, stream);

            var state = await coordinator.EnsureReadyAsync();

            Assert.Equal(CameraCaptureState.Failed, state);
            Assert.Equal(1, stream.StartCount);
            Assert.Equal("Camera stream did not enter the playing state.", coordinator.FailureReason);
        }

        [Fact]
        public async Task CameraCoordinatorDoesNotRestartAlreadyPlayingStream()
        {
            var permission = new FakePermissionService(CameraPermissionState.Granted, CameraPermissionState.Granted);
            var stream = new FakeCameraStream(new ImageFrame(new byte[4], 2, 2, 0)) { IsPlaying = true };
            var coordinator = new CameraCaptureCoordinator(permission, stream);

            var state = await coordinator.EnsureReadyAsync();

            Assert.Equal(CameraCaptureState.Ready, state);
            Assert.Equal(0, permission.RequestCount);
            Assert.Equal(0, stream.StartCount);
        }

        [Fact]
        public void ExactCoverageProjectsToInPlaceReplacement()
        {
            var target = BuildSpatialTarget(SpatialAssistanceCoverage.Exact, new ViewportEnvelope(0.2, 0.3, 0.4, 0.5));
            var planner = new SpatialProjectionPlanner(new FakeRayProvider(true), new FakeRaycaster(true));

            var projected = Assert.Single(planner.Project(new SpatialAssistancePlan(new[] { target })).Targets);

            Assert.Equal(OverlayPlacementKind.InPlaceReplacement, projected.PlacementKind);
            Assert.Equal(SpatialProjectionFailure.None, projected.Failure);
            Assert.True(projected.Surface.HasValue);
            Assert.Equal(0.3, projected.ViewportAnchor!.Value.U, 6);
            Assert.Equal(0.4, projected.ViewportAnchor!.Value.V, 6);
        }

        [Fact]
        public void PartialCoverageProjectsToAdjacentLabel()
        {
            var target = BuildSpatialTarget(SpatialAssistanceCoverage.Partial, new ViewportEnvelope(0.2, 0.3, 0.4, 0.5));
            var projected = Assert.Single(new SpatialProjectionPlanner(new FakeRayProvider(true), new FakeRaycaster(true))
                .Project(new SpatialAssistancePlan(new[] { target })).Targets);

            Assert.Equal(OverlayPlacementKind.AdjacentLabel, projected.PlacementKind);
            Assert.Equal(SpatialProjectionFailure.None, projected.Failure);
        }

        [Fact]
        public void UnresolvedCoverageIsSkippedWithoutCallingProjectionBackends()
        {
            var rays = new FakeRayProvider(true);
            var raycaster = new FakeRaycaster(true);
            var target = BuildSpatialTarget(SpatialAssistanceCoverage.Unresolved, null);

            var projected = Assert.Single(new SpatialProjectionPlanner(rays, raycaster)
                .Project(new SpatialAssistancePlan(new[] { target })).Targets);

            Assert.Equal(OverlayPlacementKind.Skip, projected.PlacementKind);
            Assert.Equal(SpatialProjectionFailure.NoReliableGeometry, projected.Failure);
            Assert.Equal(0, rays.CallCount);
            Assert.Equal(0, raycaster.CallCount);
        }

        [Fact]
        public void MissingSurfaceDoesNotPretendWorldPlacementSucceeded()
        {
            var target = BuildSpatialTarget(SpatialAssistanceCoverage.Exact, new ViewportEnvelope(0.1, 0.1, 0.2, 0.2));
            var projected = Assert.Single(new SpatialProjectionPlanner(new FakeRayProvider(true), new FakeRaycaster(false))
                .Project(new SpatialAssistancePlan(new[] { target })).Targets);

            Assert.Equal(OverlayPlacementKind.Skip, projected.PlacementKind);
            Assert.Equal(SpatialProjectionFailure.SurfaceNotFound, projected.Failure);
            Assert.False(projected.CanRenderInWorld);
        }

        private static SpatialAssistanceTarget BuildSpatialTarget(SpatialAssistanceCoverage coverage, ViewportEnvelope? envelope)
        {
            var unit = new SemanticUnit("mwe:0:8", SemanticUnitKind.MultiwordExpression, 0, 8, "keep off", 2);
            var segment = new MixedLanguageSegment("keep off", "立ち入らない", true, unit);
            return new SpatialAssistanceTarget(segment, Array.Empty<OcrTextRegionSpan>(), coverage, envelope);
        }

        private sealed class FakePermissionService : ICameraPermissionService
        {
            private readonly CameraPermissionState requestResult;

            public FakePermissionService(CameraPermissionState initialState, CameraPermissionState requestResult)
            {
                State = initialState;
                this.requestResult = requestResult;
            }

            public CameraPermissionState State { get; private set; }
            public int RequestCount { get; private set; }

            public Task<CameraPermissionState> RequestAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequestCount++;
                State = requestResult;
                return Task.FromResult(State);
            }
        }

        private sealed class FakeCameraStream : ICameraStreamBackend
        {
            private readonly ImageFrame frame;

            public FakeCameraStream(ImageFrame frame)
            {
                this.frame = frame;
            }

            public bool IsPlaying { get; set; }
            public bool StartMakesPlaying { get; set; } = true;
            public int StartCount { get; private set; }
            public int StopCount { get; private set; }
            public int CaptureCount { get; private set; }

            public Task StartAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                StartCount++;
                if (StartMakesPlaying) IsPlaying = true;
                return Task.CompletedTask;
            }

            public Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                StopCount++;
                IsPlaying = false;
                return Task.CompletedTask;
            }

            public Task<ImageFrame?> CaptureAsync(CancellationToken cancellationToken = default(CancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CaptureCount++;
                return Task.FromResult<ImageFrame?>(frame);
            }
        }

        private sealed class FakeRayProvider : IViewportRayProvider
        {
            private readonly bool succeeds;

            public FakeRayProvider(bool succeeds)
            {
                this.succeeds = succeeds;
            }

            public int CallCount { get; private set; }

            public bool TryCreateRay(ViewportPoint point, out SpatialRay ray)
            {
                CallCount++;
                ray = new SpatialRay(
                    new SpatialVector3(0, 0, 0),
                    new SpatialVector3(point.U, point.V, 1));
                return succeeds;
            }
        }

        private sealed class FakeRaycaster : ISurfaceRaycaster
        {
            private readonly bool succeeds;

            public FakeRaycaster(bool succeeds)
            {
                this.succeeds = succeeds;
            }

            public int CallCount { get; private set; }

            public bool TryRaycast(SpatialRay ray, out SurfaceHit hit)
            {
                CallCount++;
                hit = new SurfaceHit(
                    new SpatialVector3(0, 0, 2),
                    new SpatialVector3(0, 0, -1),
                    2.0);
                return succeeds;
            }
        }
    }
}
