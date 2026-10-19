using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Spatial;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Runtime permission adapter for the two camera permissions used by the current Meta Passthrough Camera samples.
    /// Only one request is allowed per service instance at a time.
    /// </summary>
    public sealed class MetaPassthroughCameraPermissionService : ICameraPermissionService
    {
#if UNITY_ANDROID
        private static readonly string[] RequiredPermissions =
        {
            "android.permission.CAMERA",
            "horizonos.permission.HEADSET_CAMERA"
        };

        private Task<CameraPermissionState> inFlightRequest;
        private CameraPermissionState state = CameraPermissionState.Unknown;

        public CameraPermissionState State
        {
            get
            {
                if (HasAllPermissions()) return CameraPermissionState.Granted;
                return state;
            }
        }

        public Task<CameraPermissionState> RequestAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasAllPermissions())
            {
                state = CameraPermissionState.Granted;
                return Task.FromResult(state);
            }

            if (inFlightRequest != null && !inFlightRequest.IsCompleted)
                return inFlightRequest;

            var completion = new TaskCompletionSource<CameraPermissionState>();
            inFlightRequest = completion.Task;

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => CompleteIfFinished(completion);
            callbacks.PermissionDenied += _ => Complete(completion, CameraPermissionState.Denied);

            if (cancellationToken.CanBeCanceled)
                cancellationToken.Register(() => completion.TrySetCanceled());

            Permission.RequestUserPermissions(RequiredPermissions, callbacks);
            return inFlightRequest;
        }

        private static bool HasAllPermissions()
        {
            for (var index = 0; index < RequiredPermissions.Length; index++)
            {
                if (!Permission.HasUserAuthorizedPermission(RequiredPermissions[index])) return false;
            }
            return true;
        }

        private void CompleteIfFinished(TaskCompletionSource<CameraPermissionState> completion)
        {
            if (HasAllPermissions()) Complete(completion, CameraPermissionState.Granted);
        }

        private void Complete(TaskCompletionSource<CameraPermissionState> completion, CameraPermissionState result)
        {
            state = result;
            completion.TrySetResult(result);
        }
#else
        public CameraPermissionState State => CameraPermissionState.Granted;

        public Task<CameraPermissionState> RequestAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CameraPermissionState.Granted);
        }
#endif
    }

    /// <summary>
    /// Thin Unity bridge around a Meta PassthroughCameraAccess component.
    ///
    /// The reviewed v85 contract is resolved through reflection so Meta types stay behind this adapter boundary:
    /// IsPlaying, GetTexture(), Timestamp, GetCameraPose(), and ViewportPointToRay(Vector2, Pose?). Capture retains
    /// a stable Timestamp/GetCameraPose pair in UnityTextureFramePayload. Downstream Read Mode can then generate
    /// center and corner rays from the camera pose that accompanied the OCR frame instead of the headset pose after
    /// language processing finishes.
    /// </summary>
    public sealed class MetaPassthroughCameraBridge : MonoBehaviour, ICameraStreamBackend, IViewportRayProvider
    {
        private const int CaptureMetadataAttempts = 3;

        [SerializeField] private Component passthroughCameraAccess;
        [SerializeField] private float startupTimeoutSeconds = 8f;

        private Behaviour cameraBehaviour;
        private PropertyInfo isPlayingProperty;
        private PropertyInfo timestampProperty;
        private MethodInfo getTextureMethod;
        private MethodInfo getCameraPoseMethod;
        private MethodInfo viewportPointToRayMethod;
        private bool apiResolved;
        private long stableCaptureMetadataCount;
        private long unstableCaptureMetadataCount;
        private long capturedPoseRayCount;

        public bool IsPlaying
        {
            get
            {
                EnsureApi();
                var value = isPlayingProperty.GetValue(passthroughCameraAccess, null);
                return value is bool playing && playing;
            }
        }

        public Component PassthroughCameraAccess => passthroughCameraAccess;
        public long StableCaptureMetadataCount => stableCaptureMetadataCount;
        public long UnstableCaptureMetadataCount => unstableCaptureMetadataCount;
        public long CapturedPoseRayCount => capturedPoseRayCount;
        public bool LastCaptureMetadataStable { get; private set; }
        public DateTime? LastCameraTimestamp { get; private set; }

        public void SetPassthroughCameraAccess(Component component)
        {
            passthroughCameraAccess = component;
            apiResolved = false;
            ResetApi();
            EnsureApi();
        }

        public async Task StartAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            EnsureApi();
            if (IsPlaying) return;

            cameraBehaviour.enabled = true;
            var startedAt = Time.realtimeSinceStartupAsDouble;
            while (!IsPlaying)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Time.realtimeSinceStartupAsDouble - startedAt > startupTimeoutSeconds)
                    throw new TimeoutException("Passthrough camera did not start before the configured timeout.");
                await Task.Yield();
            }
        }

        public Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureApi();
            cameraBehaviour.enabled = false;
            return Task.CompletedTask;
        }

        public Task<ImageFrame> CaptureAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureApi();
            if (!IsPlaying) return Task.FromResult<ImageFrame>(null);

            Texture lastTexture = null;
            var lastTimestamp = default(DateTime);
            for (var attempt = 0; attempt < CaptureMetadataAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var timestampBefore = ReadTimestamp();
                var cameraPose = ReadCameraPose();
                var texture = getTextureMethod.Invoke(passthroughCameraAccess, null) as Texture;
                var timestampAfter = ReadTimestamp();
                if (texture == null) return Task.FromResult<ImageFrame>(null);

                lastTexture = texture;
                lastTimestamp = timestampAfter;
                if (timestampBefore == timestampAfter)
                {
                    LastCaptureMetadataStable = true;
                    LastCameraTimestamp = timestampBefore;
                    stableCaptureMetadataCount++;
                    return Task.FromResult(new ImageFrame(
                        new UnityTextureFramePayload(texture, timestampBefore, cameraPose),
                        texture.width,
                        texture.height,
                        ToTimestampMicroseconds(timestampBefore),
                        ImagePixelFormat.Unknown));
                }
            }

            // A frame boundary raced all bounded attempts. The texture may still be used for OCR, but its pose is
            // deliberately not trusted for world registration. Quest Read Mode smoke therefore cannot pass from it.
            LastCaptureMetadataStable = false;
            LastCameraTimestamp = lastTimestamp.Ticks > 0 ? lastTimestamp : (DateTime?)null;
            unstableCaptureMetadataCount++;
            if (lastTexture == null || lastTimestamp.Ticks <= 0)
                return Task.FromResult<ImageFrame>(null);

            return Task.FromResult(new ImageFrame(
                new UnityTextureFramePayload(lastTexture),
                lastTexture.width,
                lastTexture.height,
                ToTimestampMicroseconds(lastTimestamp),
                ImagePixelFormat.Unknown));
        }

        public bool TryCreateFrameRayProvider(ImageFrame frame, out IViewportRayProvider provider)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            EnsureApi();

            var payload = frame.NativePayload as UnityTextureFramePayload;
            if (payload != null && payload.HasCameraCaptureMetadata)
            {
                var expectedTimestamp = ToTimestampMicroseconds(payload.CameraTimestamp);
                if (frame.TimestampMicroseconds != expectedTimestamp)
                {
                    throw new InvalidOperationException(
                        "ImageFrame timestamp does not match its retained PassthroughCameraAccess.Timestamp metadata.");
                }

                provider = new CapturedPoseRayProvider(this, payload.CameraPose);
                return true;
            }

            provider = this;
            return false;
        }

        public bool TryCreateRay(ViewportPoint point, out SpatialRay ray)
        {
            return TryCreateRay(point, null, out ray);
        }

        private bool TryCreateRay(ViewportPoint point, Pose? cameraPose, out SpatialRay ray)
        {
            EnsureApi();
            var value = viewportPointToRayMethod.Invoke(
                passthroughCameraAccess,
                new object[] { new Vector2((float)point.U, (float)point.V), cameraPose });

            if (value is Ray unityRay)
            {
                if (cameraPose.HasValue) capturedPoseRayCount++;
                ray = new SpatialRay(ToSpatial(unityRay.origin), ToSpatial(unityRay.direction));
                return true;
            }

            ray = default(SpatialRay);
            return false;
        }

        private void Awake()
        {
            if (passthroughCameraAccess != null) EnsureApi();
        }

        private void EnsureApi()
        {
            if (apiResolved) return;
            if (passthroughCameraAccess == null)
                throw new InvalidOperationException("Assign the Meta PassthroughCameraAccess component before using the bridge.");

            cameraBehaviour = passthroughCameraAccess as Behaviour;
            if (cameraBehaviour == null)
                throw new InvalidOperationException("PassthroughCameraAccess must derive from UnityEngine.Behaviour.");

            var type = passthroughCameraAccess.GetType();
            isPlayingProperty = type.GetProperty("IsPlaying", BindingFlags.Instance | BindingFlags.Public);
            timestampProperty = type.GetProperty("Timestamp", BindingFlags.Instance | BindingFlags.Public);
            getTextureMethod = type.GetMethod("GetTexture", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            getCameraPoseMethod = type.GetMethod("GetCameraPose", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            viewportPointToRayMethod = type.GetMethod(
                "ViewportPointToRay",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Vector2), typeof(Pose?) },
                null);

            if (isPlayingProperty == null || isPlayingProperty.PropertyType != typeof(bool))
                throw MissingApi(type, "bool IsPlaying");
            if (timestampProperty == null || timestampProperty.PropertyType != typeof(DateTime))
                throw MissingApi(type, "DateTime Timestamp");
            if (getTextureMethod == null || !typeof(Texture).IsAssignableFrom(getTextureMethod.ReturnType))
                throw MissingApi(type, "Texture GetTexture()");
            if (getCameraPoseMethod == null || getCameraPoseMethod.ReturnType != typeof(Pose))
                throw MissingApi(type, "Pose GetCameraPose()");
            if (viewportPointToRayMethod == null || viewportPointToRayMethod.ReturnType != typeof(Ray))
                throw MissingApi(type, "Ray ViewportPointToRay(Vector2, Pose?)");

            apiResolved = true;
        }

        private DateTime ReadTimestamp()
        {
            var value = timestampProperty.GetValue(passthroughCameraAccess, null);
            if (!(value is DateTime timestamp) || timestamp.Ticks <= 0)
                throw new InvalidOperationException("PassthroughCameraAccess.Timestamp returned an uninitialized value.");
            return timestamp;
        }

        private Pose ReadCameraPose()
        {
            var value = getCameraPoseMethod.Invoke(passthroughCameraAccess, null);
            if (!(value is Pose pose))
                throw new InvalidOperationException("PassthroughCameraAccess.GetCameraPose() did not return UnityEngine.Pose.");
            return pose;
        }

        private void ResetApi()
        {
            cameraBehaviour = null;
            isPlayingProperty = null;
            timestampProperty = null;
            getTextureMethod = null;
            getCameraPoseMethod = null;
            viewportPointToRayMethod = null;
        }

        private static long ToTimestampMicroseconds(DateTime timestamp)
        {
            // DateTime ticks are 100 ns. The resulting value is an opaque camera-source timestamp used for ordering
            // and frame identity; it is not presented as Unix time.
            return checked(timestamp.Ticks / 10L);
        }

        private static InvalidOperationException MissingApi(Type type, string expected)
        {
            return new InvalidOperationException(
                "The assigned component '" + type.FullName + "' does not expose the expected Meta Passthrough Camera API: " + expected + ".");
        }

        private static SpatialVector3 ToSpatial(Vector3 value)
        {
            return new SpatialVector3(value.x, value.y, value.z);
        }

        private sealed class CapturedPoseRayProvider : IViewportRayProvider
        {
            private readonly MetaPassthroughCameraBridge owner;
            private readonly Pose cameraPose;

            public CapturedPoseRayProvider(MetaPassthroughCameraBridge owner, Pose cameraPose)
            {
                this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
                this.cameraPose = cameraPose;
            }

            public bool TryCreateRay(ViewportPoint point, out SpatialRay ray)
            {
                return owner.TryCreateRay(point, cameraPose, out ray);
            }
        }
    }
}
