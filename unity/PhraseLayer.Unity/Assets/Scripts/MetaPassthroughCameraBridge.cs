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
            callbacks.PermissionDeniedAndDontAskAgain += _ => Complete(completion, CameraPermissionState.Denied);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            }

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
    /// It resolves the public IsPlaying, GetTexture and ViewportPointToRay APIs once and then exposes Core interfaces.
    /// Keeping Meta types behind reflection prevents them from leaking into PhraseLayer.Core and makes SDK drift fail loudly at the bridge boundary.
    /// </summary>
    public sealed class MetaPassthroughCameraBridge : MonoBehaviour, ICameraStreamBackend, IViewportRayProvider
    {
        [SerializeField] private Component passthroughCameraAccess;
        [SerializeField] private float startupTimeoutSeconds = 8f;

        private Behaviour cameraBehaviour;
        private PropertyInfo isPlayingProperty;
        private MethodInfo getTextureMethod;
        private MethodInfo viewportPointToRayMethod;
        private bool apiResolved;

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

        public void SetPassthroughCameraAccess(Component component)
        {
            passthroughCameraAccess = component;
            apiResolved = false;
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

        public Task<ImageFrame?> CaptureAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureApi();
            if (!IsPlaying) return Task.FromResult<ImageFrame?>(null);

            var texture = getTextureMethod.Invoke(passthroughCameraAccess, null) as Texture;
            if (texture == null) return Task.FromResult<ImageFrame?>(null);

            // This is the local observation time, not yet the camera hardware timestamp.
            // Hardware timestamp alignment will be added only after the exact PCA timestamp contract is verified in a real Unity/Quest build.
            var localTimestampMicroseconds = checked((long)(Time.realtimeSinceStartupAsDouble * 1_000_000.0));
            var frame = new ImageFrame(
                new UnityTextureFramePayload(texture),
                texture.width,
                texture.height,
                localTimestampMicroseconds,
                ImagePixelFormat.Unknown);
            return Task.FromResult<ImageFrame?>(frame);
        }

        public bool TryCreateRay(ViewportPoint point, out SpatialRay ray)
        {
            EnsureApi();
            var value = viewportPointToRayMethod.Invoke(
                passthroughCameraAccess,
                new object[] { new Vector2((float)point.U, (float)point.V) });

            if (value is Ray unityRay)
            {
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
            getTextureMethod = type.GetMethod("GetTexture", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            viewportPointToRayMethod = type.GetMethod(
                "ViewportPointToRay",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Vector2) },
                null);

            if (isPlayingProperty == null || isPlayingProperty.PropertyType != typeof(bool))
                throw MissingApi(type, "bool IsPlaying");
            if (getTextureMethod == null || !typeof(Texture).IsAssignableFrom(getTextureMethod.ReturnType))
                throw MissingApi(type, "Texture GetTexture()");
            if (viewportPointToRayMethod == null || viewportPointToRayMethod.ReturnType != typeof(Ray))
                throw MissingApi(type, "Ray ViewportPointToRay(Vector2)");

            apiResolved = true;
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
    }
}
