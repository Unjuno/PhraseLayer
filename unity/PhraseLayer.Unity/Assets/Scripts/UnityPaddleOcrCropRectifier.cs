using System;
using PhraseLayer.Core.Inputs;
using UnityEngine;

namespace PhraseLayer.Unity
{
    public sealed class PaddleOcrRectifiedCrop : IDisposable
    {
        private bool disposed;

        internal PaddleOcrRectifiedCrop(RenderTexture texture, PaddleOcrCropRectificationPlan plan)
        {
            Texture = texture != null ? texture : throw new ArgumentNullException(nameof(texture));
            Plan = plan;
        }

        public RenderTexture Texture { get; private set; }
        public PaddleOcrCropRectificationPlan Plan { get; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (Texture != null)
            {
                Texture.Release();
                UnityEngine.Object.Destroy(Texture);
                Texture = null;
            }
        }
    }

    /// <summary>
    /// GPU perspective rectifier connecting detector quads to the PP-OCR recognizer.
    /// Geometry and 90-degree orientation follow PaddleOCR get_rotate_crop_image.
    /// Sampling is currently bilinear; OpenCV INTER_CUBIC parity remains a fixture-gated optimization.
    /// </summary>
    public sealed class UnityPaddleOcrCropRectifier : IDisposable
    {
        private const string ShaderResourceName = "PaddleOcrPerspectiveCrop";
        private readonly Material material;
        private bool disposed;

        public UnityPaddleOcrCropRectifier()
        {
            var shader = Resources.Load<Shader>(ShaderResourceName);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Missing Resources/PaddleOcrPerspectiveCrop.shader. The crop shader must be bundled for Quest builds.");
            }

            material = new Material(shader)
            {
                name = "PhraseLayer PP-OCR Perspective Crop Material",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        public PaddleOcrRectifiedCrop Rectify(Texture source, ImageQuad sourceQuad)
        {
            ThrowIfDisposed();
            if (source == null) throw new ArgumentNullException(nameof(source));

            var plan = PaddleOcrCropRectification.CreatePlan(sourceQuad);
            var transform = ProjectiveTransformFactory.UnitSquareToQuad(sourceQuad);

            var target = new RenderTexture(
                plan.OutputWidth,
                plan.OutputHeight,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default)
            {
                name = "PhraseLayer PP-OCR Rectified Crop",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            target.Create();

            try
            {
                material.SetVector("_SourceSize", new Vector4(source.width, source.height, 0f, 0f));
                material.SetVector("_H0", new Vector4(
                    (float)transform.M00,
                    (float)transform.M01,
                    (float)transform.M02,
                    0f));
                material.SetVector("_H1", new Vector4(
                    (float)transform.M10,
                    (float)transform.M11,
                    (float)transform.M12,
                    0f));
                material.SetVector("_H2", new Vector4(
                    (float)transform.M20,
                    (float)transform.M21,
                    (float)transform.M22,
                    0f));
                material.SetFloat("_RotateCCW90", plan.RotateCounterClockwise90 ? 1f : 0f);

                Graphics.Blit(source, target, material, 0);
                return new PaddleOcrRectifiedCrop(target, plan);
            }
            catch
            {
                target.Release();
                UnityEngine.Object.Destroy(target);
                throw;
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(UnityPaddleOcrCropRectifier));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            UnityEngine.Object.Destroy(material);
        }
    }
}
