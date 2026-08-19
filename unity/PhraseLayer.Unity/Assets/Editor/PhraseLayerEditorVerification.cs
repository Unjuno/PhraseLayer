using System;
using System.Collections.Generic;
using PhraseLayer.Core.Assistance;
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

            Debug.Log("PhraseLayer Gate 3 PASS: " + plan.DisplayText);
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
