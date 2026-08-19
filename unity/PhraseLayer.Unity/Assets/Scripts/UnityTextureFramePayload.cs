using System;
using PhraseLayer.Core.Inputs;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Keeps a Unity Texture in an ImageFrame so GPU-capable OCR adapters can consume it without a forced CPU readback.
    /// </summary>
    public sealed class UnityTextureFramePayload : IImageFramePayload
    {
        public UnityTextureFramePayload(Texture texture)
        {
            Texture = texture != null ? texture : throw new ArgumentNullException(nameof(texture));
        }

        public Texture Texture { get; }
    }
}
