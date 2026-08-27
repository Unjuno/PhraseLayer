using System;
using System.Collections.Generic;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Spatial;
using PhraseLayer.Core.Translation;
using UnityEditor;
using UnityEngine;

namespace PhraseLayer.Unity.Editor
{
    public static class PhraseLayerEditorVerification
    {
        private const string Source = "I was tired, so I went home, and I fell asleep immediately.";
        private const string Expected = "I was tired, だから家に帰って, and I fell asleep immediately.";

        [MenuItem("PhraseLayer/Verify Core Pipeline")]
        public static void VerifyCorePipeline()
        {
            VerifyLanguagePipeline();
            VerifyViewportGeometry();
            VerifyOcrPresentationContract();
            VerifyOcrRuntimeContract();
            VerifyUnitySpatialProjectionGate();
            VerifyInferenceApiGate();
            Debug.Log("PhraseLayer Gate 3/4/5 shell PASS: language pipeline, OCR viewport geometry, OCR presentation/runtime contracts, Unity surface projection bridge, Unity Inference 2.2 API gate, PP-OCR detector/DB/crop/recognizer gates, end-to-end engine, scene bootstrap, and local OCR asset Editor bridge verified.");
        }

        private static void VerifyLanguagePipeline()
        {
            var learner = new InMemoryLearnerModel(0.95);
            learner.SetUnderstanding("I was tired", 0.95);
            learner.SetUnderstanding("so I went home", 0.20);
            learner.SetUnderstanding("and I fell asleep immediately", 0.95);

            var segmenter = new RuleBasedSemanticSegmenter(new[] { "was tired", "went home", "fell asleep" });
            var translator = new DictionaryTranslationEngine(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "so i went home", "だから家に帰って" }
            });
            var pipeline = new LanguagePipeline(segmenter, learner, new AssistancePlanner(), translator);
            var plan = pipeline.PlanAsync(Source, AssistancePolicy.ForMode(AssistanceMode.Balanced), Source)
                .GetAwaiter().GetResult();

            if (!string.Equals(plan.DisplayText, Expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected PhraseLayer output. Expected: " + Expected + " Actual: " + plan.DisplayText);
            if (plan.DisplayText.Contains("[") || plan.DisplayText.Contains("]"))
                throw new InvalidOperationException("Core output must not contain gloss brackets.");
        }

        private static void VerifyViewportGeometry()
        {
            var viewport = ImageCoordinateMapper.ToViewport(ImageQuad.FromRect(100, 50, 200, 100), 1000, 500);
            AssertNear(viewport.Centroid.U, 0.20, "viewport centroid U");
            AssertNear(viewport.Centroid.V, 0.80, "viewport centroid V");

            var canvas = new Rect(10, 20, 1000, 500);
            var screen = ViewportGuiMapper.ToScreenRect(viewport, canvas);
            AssertNear(screen.x, 110.0, "screen rect x");
            AssertNear(screen.y, 70.0, "screen rect y");
            AssertNear(screen.width, 200.0, "screen rect width");
            AssertNear(screen.height, 100.0, "screen rect height");
        }

        private static void VerifyOcrPresentationContract()
        {
            if (!typeof(IOcrObservationSink).IsAssignableFrom(typeof(OcrViewportDebugBehaviour)))
                throw new InvalidOperationException("OcrViewportDebugBehaviour must remain an IOcrObservationSink.");

            var observation = new OcrObservation(
                "Emergency exit",
                0.94,
                new[] { new OcrRegion("Emergency exit", 0.94, ImageQuad.FromRect(10, 20, 100, 40)) });
            var frame = new ImageFrame(new byte[4], 200, 100, 777);
            var sink = new RecordingOcrSink();
            var coordinator = new OcrPresentationCoordinator(sink);
            var result = new OcrScheduleResult(OcrScheduleStatus.Processed, 777, observation);

            if (!coordinator.PresentIfProcessed(result, frame))
                throw new InvalidOperationException("Processed OCR result was not presented.");
            if (!ReferenceEquals(sink.Observation, observation) || !ReferenceEquals(sink.Frame, frame))
                throw new InvalidOperationException("OCR presentation did not preserve the observation/frame pairing.");
        }

        private static void VerifyOcrRuntimeContract()
        {
            var detector = PaddleOcrRuntimeContract.ValidateDetector(
                new[] { 1, 1, 4, 5 },
                new float[20]);
            var recognizer = PaddleOcrRuntimeContract.ValidateRecognizer(
                new[] { 1, 12, 97 },
                new float[12 * 97],
                dictionaryTokenCount: 96);
            var report = PaddleOcrRuntimeContract.BuildReport(detector, recognizer, 96);

            if (!report.Contains("detector shape=[1,1,4,5]"))
                throw new InvalidOperationException("PP-OCR detector runtime contract report is missing the observed shape.");
            if (!report.Contains("classes=97") || !report.Contains("dictionary=96"))
                throw new InvalidOperationException("PP-OCR recognizer runtime contract report is missing class/dictionary parity.");
        }

        private static void VerifyUnitySpatialProjectionGate()
        {
            if (!typeof(ISurfaceRaycaster).IsAssignableFrom(typeof(UnityPhysicsSurfaceRaycaster)))
                throw new InvalidOperationException("UnityPhysicsSurfaceRaycaster must implement the Core ISurfaceRaycaster boundary.");
            if (typeof(UnitySpatialProjectionBehaviour) == null)
                throw new InvalidOperationException("UnitySpatialProjectionBehaviour must compile for aligned Read Mode projection.");
        }

        private static void VerifyInferenceApiGate()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            var probeType = typeof(UnityInferenceModelProbe);
            if (probeType == null)
                throw new InvalidOperationException("Unity Inference 2.2 model probe type was not compiled.");

            var detectorRuntimeType = typeof(UnityPaddleOcrDetectorRuntime);
            if (detectorRuntimeType == null)
                throw new InvalidOperationException("Unity PP-OCR detector runtime type was not compiled.");

            var recognizerRuntimeType = typeof(UnityPaddleOcrRecognizerRuntime);
            if (recognizerRuntimeType == null)
                throw new InvalidOperationException("Unity PP-OCR recognizer runtime type was not compiled.");

            var cropRectifierType = typeof(UnityPaddleOcrCropRectifier);
            if (cropRectifierType == null)
                throw new InvalidOperationException("Unity PP-OCR crop rectifier type was not compiled.");

            var engineType = typeof(UnityPaddleOcrEngine);
            if (engineType == null || !typeof(IOcrEngine).IsAssignableFrom(engineType))
                throw new InvalidOperationException("UnityPaddleOcrEngine must compile and implement IOcrEngine.");

            var bootstrapType = typeof(UnityPaddleOcrBootstrapBehaviour);
            if (bootstrapType == null)
                throw new InvalidOperationException("UnityPaddleOcrBootstrapBehaviour must compile for Inspector scene wiring.");

            var localAssetEditorType = typeof(PhraseLayerLocalOcrAssets);
            if (localAssetEditorType == null)
                throw new InvalidOperationException("PhraseLayerLocalOcrAssets must compile for local OCR asset import/probe and bootstrap assignment.");
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve com.unity.ai.inference in the reviewed [2.2.1,2.3.0) range before Gate 4 verification.");
#endif
        }

        private static void AssertNear(double actual, double expected, string label)
        {
            if (Math.Abs(actual - expected) > 0.0001)
                throw new InvalidOperationException(label + " expected " + expected + " but was " + actual);
        }

        private sealed class RecordingOcrSink : IOcrObservationSink
        {
            public OcrObservation Observation { get; private set; }
            public ImageFrame Frame { get; private set; }

            public void Present(OcrObservation observation, ImageFrame frame)
            {
                Observation = observation;
                Frame = frame;
            }
        }

        public static void VerifyCorePipelineBatch()
        {
            try
            {
                VerifyCorePipeline();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }
    }
}
