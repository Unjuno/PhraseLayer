using System;
using System.Collections.Generic;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
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
            Debug.Log("PhraseLayer Gate 3 PASS: language pipeline and OCR viewport geometry verified.");
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

        private static void AssertNear(double actual, double expected, string label)
        {
            if (Math.Abs(actual - expected) > 0.0001)
                throw new InvalidOperationException(label + " expected " + expected + " but was " + actual);
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
