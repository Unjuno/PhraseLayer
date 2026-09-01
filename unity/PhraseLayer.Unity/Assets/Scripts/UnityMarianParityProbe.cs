using System;
using System.Collections.Generic;
using System.Linq;
using PhraseLayer.Core.Translation;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
    public sealed class UnityMarianParityResult
    {
        public UnityMarianParityResult(
            string translation,
            IReadOnlyList<int> sourceTokenIds,
            IReadOnlyList<int> targetTokenIds,
            TranslationGenerationStopReason stopReason)
        {
            Translation = translation ?? throw new ArgumentNullException(nameof(translation));
            SourceTokenIds = (sourceTokenIds ?? throw new ArgumentNullException(nameof(sourceTokenIds))).ToArray();
            TargetTokenIds = (targetTokenIds ?? throw new ArgumentNullException(nameof(targetTokenIds))).ToArray();
            StopReason = stopReason;
        }

        public string Translation { get; }
        public IReadOnlyList<int> SourceTokenIds { get; }
        public IReadOnlyList<int> TargetTokenIds { get; }
        public TranslationGenerationStopReason StopReason { get; }
    }

    /// <summary>
    /// Deterministic real-Unity correctness probe for the pinned Marian stack. It intentionally bypasses the demo
    /// assistance layer: the gate tests tokenizer -> encoder/cache decoder -> greedy policy -> target tokenizer on
    /// one exact source sentence so a Unity Inference or managed-tokenizer drift can be compared token-for-token
    /// against the independent PyTorch/ONNX Runtime reference generated from the same local source snapshot.
    /// </summary>
    public static class UnityMarianParityProbe
    {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        public static UnityMarianParityResult Run(
            ModelAsset encoderModel,
            ModelAsset decoderModel,
            ModelAsset decoderWithPastModel,
            string tokenizerResourceRoot,
            string sourceText,
            BackendType backendType = BackendType.CPU,
            int maximumSourceTokens = 128,
            int maximumTargetTokens = 128)
        {
            if (encoderModel == null) throw new ArgumentNullException(nameof(encoderModel));
            if (decoderModel == null) throw new ArgumentNullException(nameof(decoderModel));
            if (decoderWithPastModel == null) throw new ArgumentNullException(nameof(decoderWithPastModel));
            if (string.IsNullOrWhiteSpace(tokenizerResourceRoot))
                throw new ArgumentException("Marian tokenizer resource root must not be empty.", nameof(tokenizerResourceRoot));
            if (string.IsNullOrWhiteSpace(sourceText))
                throw new ArgumentException("Marian parity source text must not be empty.", nameof(sourceText));

            if (!UnityManagedMarianTokenizerLoader.TryCreateFromResources(
                    tokenizerResourceRoot,
                    out var tokenizer,
                    out var tokenizerError))
            {
                throw new InvalidOperationException("Marian parity tokenizer initialization failed: " + tokenizerError);
            }

            var options = OpusMtEnJaGenerationPolicy.CreateGreedyParityOptions(
                maximumSourceTokens,
                maximumTargetTokens);
            var source = tokenizer.EncodeSource(sourceText, options.MaximumSourceTokens);
            if (source.WasTruncated)
                throw new InvalidOperationException("Marian parity source unexpectedly exceeded the reviewed source-token budget.");

            using (var backend = new UnityMarianDeviceResidentGenerationBackend(
                encoderModel,
                decoderModel,
                decoderWithPastModel,
                backendType))
            {
                var model = OpusMtEnJaGenerationPolicy.CreateGreedyModel(backend);
                var generated = model.GenerateAsync(source.TokenIds, options).GetAwaiter().GetResult();
                if (generated == null)
                    throw new InvalidOperationException("Marian parity generation returned null.");
                var translation = tokenizer.DecodeTarget(generated.TokenIds);
                if (string.IsNullOrWhiteSpace(translation))
                    throw new InvalidOperationException("Marian parity tokenizer decoded an empty translation.");
                return new UnityMarianParityResult(
                    translation.Trim(),
                    source.TokenIds,
                    generated.TokenIds,
                    generated.StopReason);
            }
        }
#else
        public static bool IsSupported => false;
#endif
    }
}
