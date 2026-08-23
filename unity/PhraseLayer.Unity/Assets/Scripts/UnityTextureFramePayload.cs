using System;
using PhraseLayer.Core.Inputs;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Owns or references a Unity Texture carried by ImageFrame.
    ///
    /// Meta PassthroughCameraAccess.GetTexture() exposes a continuously updated streaming texture. OCR must never
    /// retain that mutable texture as if it were a captured frame. CreateSnapshot performs a GPU-to-GPU Blit into
    /// an owned RenderTexture so detector/recognizer inference observes one frozen image without a CPU readback.
    /// The owned snapshot is released by OcrRuntimePump after inference completes.
    /// </summary>
    public sealed class UnityTextureFramePayload : IImageFramePayload, IDisposable
    {
        private Texture texture;
        private RenderTexture ownedSnapshot;
        private bool disposed;

        public UnityTextureFramePayload(Texture texture)
        {
            this.texture = texture != null ? texture : throw new ArgumentNullException(nameof(texture));
        }

        private UnityTextureFramePayload(RenderTexture snapshot)
        {
            texture = snapshot != null ? snapshot : throw new ArgumentNullException(nameof(snapshot));
            ownedSnapshot = snapshot;
        }

        public Texture Texture
        {
            get
            {
                if (disposed || texture == null)
                    throw new ObjectDisposedException(nameof(UnityTextureFramePayload));
                return texture;
            }
        }

        public bool OwnsSnapshot => ownedSnapshot != null && !disposed;

        public static UnityTextureFramePayload CreateSnapshot(Texture source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.width <= 0 || source.height <= 0)
                throw new ArgumentException("Cannot snapshot a texture with non-positive dimensions.", nameof(source));

            var snapshot = new RenderTexture(
                source.width,
                source.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default)
            {
                name = "PhraseLayer Camera Frame Snapshot",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
            };
            snapshot.Create();

            try
            {
                Graphics.Blit(source, snapshot);
                return new UnityTextureFramePayload(snapshot);
            }
            catch
            {
                snapshot.Release();
                UnityEngine.Object.Destroy(snapshot);
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (ownedSnapshot != null)
            {
                ownedSnapshot.Release();
                UnityEngine.Object.Destroy(ownedSnapshot);
                ownedSnapshot = null;
            }
            texture = null;
        }
    }
}
