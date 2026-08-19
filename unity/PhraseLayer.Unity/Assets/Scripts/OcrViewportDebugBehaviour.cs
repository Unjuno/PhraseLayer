using System;
using System.Collections.Generic;
using PhraseLayer.Core.Inputs;
using UnityEngine;

namespace PhraseLayer.Unity
{
    public sealed class OcrViewportDebugBehaviour : MonoBehaviour
    {
        private readonly List<OcrViewportRegion> regions = new List<OcrViewportRegion>();

        public IReadOnlyList<OcrViewportRegion> Regions => regions;

        private void Start()
        {
            LoadSyntheticFixture();
        }

        public void LoadSyntheticFixture()
        {
            var observation = new OcrObservation(
                "Please keep off the grass. Emergency exit.",
                0.96,
                new[]
                {
                    new OcrRegion(
                        "keep off",
                        0.98,
                        new ImageQuad(
                            new ImagePoint(160, 170),
                            new ImagePoint(430, 150),
                            new ImagePoint(440, 260),
                            new ImagePoint(170, 280))),
                    new OcrRegion(
                        "Emergency exit",
                        0.94,
                        ImageQuad.FromRect(610, 330, 250, 100))
                });
            var frame = new ImageFrame(new byte[4], 1000, 600, 0);

            regions.Clear();
            regions.AddRange(OcrViewportMapper.Map(observation, frame));
        }

        private void OnGUI()
        {
            if (regions.Count == 0) return;

            var canvasWidth = Math.Max(320, Math.Min(900, Screen.width - 48));
            var canvasHeight = Math.Max(180, Math.Min(500, Screen.height / 2));
            var canvas = new Rect(24, Screen.height - canvasHeight - 24, canvasWidth, canvasHeight);
            GUI.Box(canvas, "Synthetic OCR viewport — top-left pixel input → bottom-left normalized viewport");

            foreach (var region in regions)
            {
                var box = ViewportGuiMapper.ToScreenRect(region.ViewportBounds, canvas);
                GUI.Box(box, string.Format("{0}  {1:P0}", region.Source.Text, region.Source.Confidence));

                var anchor = ViewportGuiMapper.ToScreenPoint(region.Anchor, canvas);
                GUI.Box(new Rect(anchor.x - 3, anchor.y - 3, 6, 6), string.Empty);
            }
        }
    }

    public static class ViewportGuiMapper
    {
        public static Rect ToScreenRect(ViewportQuad quad, Rect canvas)
        {
            var minU = Math.Min(Math.Min(quad.P0.U, quad.P1.U), Math.Min(quad.P2.U, quad.P3.U));
            var maxU = Math.Max(Math.Max(quad.P0.U, quad.P1.U), Math.Max(quad.P2.U, quad.P3.U));
            var minV = Math.Min(Math.Min(quad.P0.V, quad.P1.V), Math.Min(quad.P2.V, quad.P3.V));
            var maxV = Math.Max(Math.Max(quad.P0.V, quad.P1.V), Math.Max(quad.P2.V, quad.P3.V));

            return new Rect(
                canvas.x + (float)(minU * canvas.width),
                canvas.y + (float)((1.0 - maxV) * canvas.height),
                (float)((maxU - minU) * canvas.width),
                (float)((maxV - minV) * canvas.height));
        }

        public static Vector2 ToScreenPoint(ViewportPoint point, Rect canvas)
        {
            return new Vector2(
                canvas.x + (float)(point.U * canvas.width),
                canvas.y + (float)((1.0 - point.V) * canvas.height));
        }
    }
}
