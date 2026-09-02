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

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Extends the real-Unity Marian parity gate through the exact Core processor used by live Read Mode after OCR.
    /// No camera or Quest execution occurs here: a deterministic already-recognized OCR observation is supplied so
    /// the host gate can prove that model-backed translation survives semantic planning and OCR-region alignment.
    /// </summary>
    public static class PhraseLayerMarianReadModeParityProbe
    {
        public static void Validate()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();
            var encoder = LoadRequired<ModelAsset>(PhraseLayerLocalMarianAssets.EncoderPath);
            var decoder = LoadRequired<ModelAsset>(PhraseLayerLocalMarianAssets.DecoderPath);
            var decoderWithPast = LoadRequired<ModelAsset>(PhraseLayerLocalMarianAssets.DecoderWithPastPath);
            var referenceAsset = LoadRequired<TextAsset>(PhraseLayerLocalMarianAssets.ReferencePath);
            var reference = JsonUtility.FromJson<ReferenceFixture>(referenceAsset.text);
            var sample = FindRequiredSample(reference, "keep off");

            if (!UnityManagedMarianTokenizerLoader.TryCreateFromResources(
                    PhraseLayerLocalMarianAssets.TokenizerResourceRoot,
                    out var tokenizer,
                    out var tokenizerError))
            {
                throw new InvalidOperationException(
                    "Managed Marian tokenizer could not be created for Read Mode parity: " + tokenizerError);
            }

            var backend = new UnityMarianDeviceResidentGenerationBackend(
                encoder,
                decoder,
                decoderWithPast,
                BackendType.GPUCompute);
            using (backend)
            {
                var model = OpusMtEnJaGenerationPolicy.CreateGreedyModel(backend);
                var options = OpusMtEnJaGenerationPolicy.CreateGreedyParityOptions(
                    reference.generation.maximum_source_tokens,
                    reference.generation.maximum_target_tokens);
                var runtime = new OfflineSeq2SeqTranslationRuntime(tokenizer, model, options);
                var engine = new OfflineTranslationEngine(runtime);
                var learner = new InMemoryLearnerModel(0.95);
                learner.SetUnderstanding("keep off", 0.0);
                var pipeline = new LanguagePipeline(
                    new RuleBasedSemanticSegmenter(new[] { "keep off" }),
                    learner,
                    new AssistancePlanner(),
                    engine);
                var processor = new ReadModeObservationProcessor(pipeline);

                var frame = new ImageFrame(new byte[4], 1000, 500, 9001);
                var observation = new OcrObservation(
                    "keep off",
                    0.99,
                    new List<OcrRegion>
                    {
                        new OcrRegion("keep", 0.99, ImageQuad.FromRect(100, 100, 180, 80)),
                        new OcrRegion("off", 0.99, ImageQuad.FromRect(300, 100, 120, 80)),
                    });
                var result = processor.ProcessAlignedAsync(
                    frame,
                    observation,
                    AssistancePolicy.ForMode(AssistanceMode.Balanced)).GetAwaiter().GetResult();

                if (!ReferenceEquals(result.Spatial.Frame, frame) || !ReferenceEquals(result.Spatial.Observation, observation))
                    throw new InvalidOperationException("Marian Read Mode parity did not retain the exact supplied frame/observation pair.");
                if (!string.Equals(result.Spatial.LanguagePlan.DisplayText, sample.translated_text, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Marian Read Mode parity did not preserve the exact model-backed translation through ReadModeObservationProcessor.");
                if (result.Spatial.LanguagePlan.Assistance.Decisions.Count != 1)
                    throw new InvalidOperationException("Marian Read Mode parity expected exactly one assisted semantic unit.");

                if (result.SpatialAssistance.Targets.Count != 1)
                    throw new InvalidOperationException("Marian Read Mode parity expected exactly one spatial assistance target.");
                var target = result.SpatialAssistance.Targets[0];
                if (!string.Equals(target.Segment.SourceText, "keep off", StringComparison.Ordinal))
                    throw new InvalidOperationException("Marian Read Mode parity changed the selected semantic source span.");
                if (!string.Equals(target.Segment.DisplayText, sample.translated_text, StringComparison.Ordinal))
                    throw new InvalidOperationException("Marian Read Mode parity spatial target did not retain the exact Marian translation.");
                if (target.Coverage != SpatialAssistanceCoverage.Exact || target.Regions.Count != 2 || target.Envelope == null)
                {
                    throw new InvalidOperationException(
                        "Marian Read Mode parity expected exact two-region OCR geometry coverage for the assisted semantic span.");
                }
            }

            Debug.Log(
                "PhraseLayer Marian Read Mode observation parity PASS: source=keep off; semantic_units=1; " +
                "ocr_regions=2; coverage=Exact; translation=model-reference-exact; camera_or_quest_execution=false.");
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve com.unity.ai.inference 2.2.1 before running Marian Read Mode parity.");
#endif
        }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Required Marian Read Mode parity asset is missing or has the wrong Unity type: " + path);
            return asset;
        }

        private static ReferenceSample FindRequiredSample(ReferenceFixture reference, string sourceText)
        {
            if (reference == null || reference.purpose != "phrase-layer-marian-greedy-reference")
                throw new InvalidOperationException("Marian Read Mode parity reference fixture purpose is missing or invalid.");
            if (reference.revision != PhraseLayerLocalMarianAssets.ExpectedRevision)
                throw new InvalidOperationException("Marian Read Mode parity reference revision drifted: " + reference.revision);
            if (reference.generation == null ||
                reference.generation.maximum_source_tokens <= 0 ||
                reference.generation.maximum_target_tokens <= 0)
            {
                throw new InvalidOperationException("Marian Read Mode parity reference generation limits are invalid.");
            }
            if (reference.samples == null)
                throw new InvalidOperationException("Marian Read Mode parity reference has no samples.");

            for (var index = 0; index < reference.samples.Length; index++)
            {
                var sample = reference.samples[index];
                if (sample != null && string.Equals(sample.source_text, sourceText, StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(sample.translated_text))
                        throw new InvalidOperationException("Marian Read Mode parity sample has empty translated text: " + sourceText);
                    return sample;
                }
            }
            throw new InvalidOperationException("Marian Read Mode parity reference is missing required sample: " + sourceText);
        }

        [Serializable]
        private sealed class ReferenceFixture
        {
            public string purpose = string.Empty;
            public string revision = string.Empty;
            public ReferenceGeneration generation = null;
            public ReferenceSample[] samples = Array.Empty<ReferenceSample>();
        }

        [Serializable]
        private sealed class ReferenceGeneration
        {
            public int maximum_source_tokens;
            public int maximum_target_tokens;
        }

        [Serializable]
        private sealed class ReferenceSample
        {
            public string source_text = string.Empty;
            public string translated_text = string.Empty;
        }
#endif
    }
}
