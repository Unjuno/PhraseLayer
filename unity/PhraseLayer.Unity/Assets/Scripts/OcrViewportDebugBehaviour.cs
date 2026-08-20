using System;
using System.Collections.Generic;
using PhraseLayer.Core.Inputs;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Runtime/debug presenter for OCR observations. The observation is always mapped against the exact
    /// ImageFrame whose pixel coordinate system produced its regions.
    /// </summary>
    public sealed class OcrViewportDebugBehaviour : MonoBehaviour, IOcrObservationSink
    {
        [SerializeField] private bool loadSyntheticFixtureOnStart = true;

        private readonly List<OcrViewportRegion> regions = new List<OcrViewportRegion>();
        private OcrObservation lastObservation;
        private ImageFrame lastFrame;
        private int frameWidth;
        private int frameHeight;
        private long frameTimestampMicroseconds;
        private OcrScheduleStatus lastScheduleStatus = OcrScheduleStatus.Processed;
        private bool hasObservation;

        public event Action<OcrObservation, ImageFrame> ObservationPresented;

        public IReadOnlyList<OcrViewportRegion> Regions => regions;
        public bool HasObservation => hasObservation;
        public OcrObservation LastObservation => hasObservation ? lastObservation : null;
        public ImageFrame LastFrame => hasObservation ? lastFrame : null;
        public string LastText => hasObservation ? lastObservation.Text : string.Empty;
        public double LastConfidence => hasObservation ? lastObservation.Confidence : 0.0;
        public long? LastFrameTimestampMicroseconds => hasObservation ? frameTimestampMicroseconds : (long?)null;
        public OcrScheduleStatus LastScheduleStatus => lastScheduleStatus;
        public bool LoadSyntheticFixtureOnStart
        {
            get => loadSyntheticFixtureOnStart;
            set => loadSyntheticFixtureOnStart = value;
        }

        private void Start()
        {
            if (loadSyntheticFixtureOnStart)
                LoadSyntheticFixture();
        }

        public void Present(OcrObservation observation, ImageFrame frame)
        {
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            regions.Clear();
            regions.AddRange(OcrViewportMapper.Map(observation, frame));
            lastObservation = observation;
            lastFrame = frame;
            frameWidth = frame.Width;
            frameHeight = frame.Height;
            frameTimestampMicroseconds = frame.TimestampMicroseconds;
            lastScheduleStatus = OcrScheduleStatus.Processed;
            hasObservation = true;

            var presented = ObservationPresented;
            if (presented != null)
            {
                try
                {
                    presented(observation, frame);
                }
                catch (Exception exception)
                {
                    // Raw OCR presentation must remain usable even if an optional downstream debug consumer fails.
                    Debug.LogException(exception, this);
                }
            }
        }

        /// <summary>
        /// Updates scheduler state without erasing the last successful OCR overlay.
        /// This makes dropped/busy/rate-limited frames visible while keeping the most recent usable text on screen.
        /// </summary>
        public void SetScheduleStatus(OcrScheduleStatus status, long frameTimestamp)
        {
            lastScheduleStatus = status;
            if (!hasObservation) frameTimestampMicroseconds = frameTimestamp;
        }

        public bool PresentScheduleResult(OcrScheduleResult result, ImageFrame frame)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            lastScheduleStatus = result.Status;
            var coordinator = new OcrPresentationCoordinator(this);
            return coordinator.PresentIfProcessed(result, frame);
        }

        public void Clear()
        {
            regions.Clear();
            lastObservation = null;
            lastFrame = null;
            frameWidth = 0;
            frameHeight = 0;
            frameTimestampMicroseconds = 0;
            lastScheduleStatus = OcrScheduleStatus.Processed;
            hasObservation = false;
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
            Present(observation, frame);
        }

        private void OnGUI()
        {
            var panelWidth = Math.Max(320, Math.Min(900, Screen.width - 48));
            var panelHeight = Math.Max(220, Math.Min(560, Screen.height / 2));
            var panel = new Rect(24, Screen.height - panelHeight - 24, panelWidth, panelHeight);
            var header = new Rect(panel.x, panel.y, panel.width, 62);
            var viewportCanvas = new Rect(panel.x, panel.y + 66, panel.width, panel.height - 66);

            GUI.Box(header, BuildHeaderText());
            GUI.Box(viewportCanvas, string.Empty);

            foreach (var region in regions)
            {
                var box = ViewportGuiMapper.ToScreenRect(region.ViewportBounds, viewportCanvas);
                GUI.Box(box, string.Format("{0}\n{1:P1}", region.Source.Text, region.Source.Confidence));

                DrawPoint(region.ViewportBounds.P0, viewportCanvas, 5);
                DrawPoint(region.ViewportBounds.P1, viewportCanvas, 5);
                DrawPoint(region.ViewportBounds.P2, viewportCanvas, 5);
                DrawPoint(region.ViewportBounds.P3, viewportCanvas, 5);
                DrawPoint(region.Anchor, viewportCanvas, 7);
            }
        }

        private string BuildHeaderText()
        {
            if (!hasObservation)
                return "OCR debug | status=" + lastScheduleStatus + " | no successful observation";

            return string.Format(
                "OCR debug | status={0} | frame={1}x{2} @ {3} us | regions={4} | overall={5:P1}\n{6}",
                lastScheduleStatus,
                frameWidth,
                frameHeight,
                frameTimestampMicroseconds,
                regions.Count,
                lastObservation.Confidence,
                lastObservation.Text);
        }

        private static void DrawPoint(ViewportPoint point, Rect canvas, float size)
        {
            var screen = ViewportGuiMapper.ToScreenPoint(point, canvas);
            GUI.Box(new Rect(screen.x - size / 2f, screen.y - size / 2f, size, size), string.Empty);
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
