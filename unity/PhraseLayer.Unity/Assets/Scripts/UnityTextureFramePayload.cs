using System;
using PhraseLayer.Core.Inputs;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Keeps a Unity Texture in an ImageFrame so GPU-capable OCR adapters can consume it without a forced CPU readback.
    /// When created by MetaPassthroughCameraBridge it also retains the camera Timestamp/GetCameraPose pair for that
    /// exact observation. The texture remains the Meta-managed live texture; callers must consume it immediately,
    /// matching Meta's official Inference Engine sample, rather than copying it through blocking Graphics.Blit.
    /// </summary>
    public sealed class UnityTextureFramePayload : IImageFramePayload
    {
        public UnityTextureFramePayload(Texture texture)
        {
            Texture = texture != null ? texture : throw new ArgumentNullException(nameof(texture));
            HasCameraCaptureMetadata = false;
            CameraTimestamp = default(DateTime);
            CameraPose = default(Pose);
        }

        public UnityTextureFramePayload(Texture texture, DateTime cameraTimestamp, Pose cameraPose)
        {
            Texture = texture != null ? texture : throw new ArgumentNullException(nameof(texture));
            if (cameraTimestamp.Ticks <= 0)
                throw new ArgumentOutOfRangeException(nameof(cameraTimestamp), "Camera timestamp must be initialized.");

            CameraTimestamp = cameraTimestamp;
            CameraPose = cameraPose;
            HasCameraCaptureMetadata = true;
        }

        public Texture Texture { get; }
        public bool HasCameraCaptureMetadata { get; }
        public DateTime CameraTimestamp { get; }
        public Pose CameraPose { get; }
    }
}
