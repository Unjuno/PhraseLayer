using System;
using System.Threading;
using System.Threading.Tasks;

namespace PhraseLayer.Core.Inputs
{
    public enum CameraPermissionState
    {
        Unknown = 0,
        Granted = 1,
        Denied = 2
    }

    public enum CameraCaptureState
    {
        Stopped = 0,
        WaitingForPermission = 1,
        Starting = 2,
        Ready = 3,
        Failed = 4
    }

    public interface ICameraPermissionService
    {
        CameraPermissionState State { get; }
        Task<CameraPermissionState> RequestAsync(CancellationToken cancellationToken = default(CancellationToken));
    }

    public interface ICameraStreamBackend
    {
        bool IsPlaying { get; }
        Task StartAsync(CancellationToken cancellationToken = default(CancellationToken));
        Task StopAsync(CancellationToken cancellationToken = default(CancellationToken));
        Task<ImageFrame?> CaptureAsync(CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>
    /// Platform-neutral camera lifecycle used by Quest/Unity adapters.
    /// It deliberately mirrors the Meta sample lifecycle without importing Meta or Unity types into Core.
    /// </summary>
    public sealed class CameraCaptureCoordinator
    {
        private readonly ICameraPermissionService permission;
        private readonly ICameraStreamBackend stream;

        public CameraCaptureCoordinator(ICameraPermissionService permission, ICameraStreamBackend stream)
        {
            this.permission = permission ?? throw new ArgumentNullException(nameof(permission));
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public CameraCaptureState State { get; private set; } = CameraCaptureState.Stopped;
        public string? FailureReason { get; private set; }

        public async Task<CameraCaptureState> EnsureReadyAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (State == CameraCaptureState.Ready && stream.IsPlaying)
                return State;

            FailureReason = null;
            try
            {
                var permissionState = permission.State;
                if (permissionState != CameraPermissionState.Granted)
                {
                    State = CameraCaptureState.WaitingForPermission;
                    permissionState = await permission.RequestAsync(cancellationToken).ConfigureAwait(false);
                    if (permissionState != CameraPermissionState.Granted)
                    {
                        State = CameraCaptureState.Failed;
                        FailureReason = "Camera permission was not granted.";
                        return State;
                    }
                }

                State = CameraCaptureState.Starting;
                if (!stream.IsPlaying)
                    await stream.StartAsync(cancellationToken).ConfigureAwait(false);

                if (!stream.IsPlaying)
                {
                    State = CameraCaptureState.Failed;
                    FailureReason = "Camera stream did not enter the playing state.";
                    return State;
                }

                State = CameraCaptureState.Ready;
                return State;
            }
            catch (OperationCanceledException)
            {
                State = CameraCaptureState.Stopped;
                FailureReason = null;
                throw;
            }
            catch (Exception exception)
            {
                State = CameraCaptureState.Failed;
                FailureReason = exception.Message;
                return State;
            }
        }

        public async Task<ImageFrame?> CaptureAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (await EnsureReadyAsync(cancellationToken).ConfigureAwait(false) != CameraCaptureState.Ready)
                return null;

            try
            {
                var frame = await stream.CaptureAsync(cancellationToken).ConfigureAwait(false);
                if (frame != null) return frame;

                State = CameraCaptureState.Failed;
                FailureReason = "Camera stream returned no frame.";
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                State = CameraCaptureState.Failed;
                FailureReason = exception.Message;
                return null;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                if (stream.IsPlaying)
                    await stream.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                State = CameraCaptureState.Stopped;
                FailureReason = null;
            }
        }
    }
}
