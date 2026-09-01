using System;
using System.Collections.Generic;
using PhraseLayer.Core.Translation;
using UnityEditor;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Real-Unity pre-device gate for the locally staged, revision-pinned Marian product translation stack.
    /// It validates imported model graph contracts, exact managed SentencePiece source token IDs, exact greedy
    /// generated token IDs, and final decoded text against an offline Transformers reference fixture.
    /// Both the CPU-clone baseline and device-resident cache backend must match before hardware testing.
    /// </summary>
    public static class PhraseLayerLocalMarianAssets
    {
        public const string ModelRoot = "Assets/LocalTranslationAssets/Marian";
        public const string EncoderPath = ModelRoot + "/encoder_model.onnx";
        public const string DecoderPath = ModelRoot + "/decoder_model.onnx";
        public const string DecoderWithPastPath = ModelRoot + "/decoder_with_past_model.onnx";
        public const string ManifestPath = "Assets/LocalTranslationAssets/PhraseLayerMarianAssets.manifest.json";
        public const string ReferencePath = "Assets/Resources/LocalTranslationAssets/marian-reference.json";
        public const string TokenizerResourceRoot = "LocalTranslationAssets";
        public const string ExpectedRevision = "a863894cdd2b80f3bc1c5966734aee9ffec207d1";

        [MenuItem("PhraseLayer/Marian/Verify Local Product Assets")]
        public static void VerifyLocalAssets()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            var assets = LoadAndValidateAssets();
            var report = UnityMarianOnnxContractProbe.ValidateBundle(
                assets.Encoder,
                assets.Decoder,
                assets.DecoderWithPast);
            Debug.Log(
                "PhraseLayer local Marian assets PASS: revision=" + assets.Manifest.revision +
                "; reference_samples=" + assets.Reference.samples.Length +
                "; graph_contract=" + report);
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve com.unity.ai.inference 2.2.1 before verifying Marian assets.");
#endif
        }

        [MenuItem("PhraseLayer/Marian/Run Translation Parity Probe")]
        public static void RunTranslationParityProbe()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            var assets = LoadAndValidateAssets();
            UnityMarianOnnxContractProbe.ValidateBundle(
                assets.Encoder,
                assets.Decoder,
                assets.DecoderWithPast);

            ITranslationTokenizer tokenizer;
            string tokenizerError;
            if (!UnityManagedMarianTokenizerLoader.TryCreateFromResources(
                    TokenizerResourceRoot,
                    out tokenizer,
                    out tokenizerError))
            {
                throw new InvalidOperationException(
                    "Managed Marian tokenizer could not be created for the parity gate: " + tokenizerError);
            }

            ValidateTokenizerReference(tokenizer, assets.Reference);
            ValidateBackend(
                "cpu-clone-baseline",
                tokenizer,
                assets.Reference,
                () => new UnityMarianSeq2SeqGenerationBackend(
                    assets.Encoder,
                    assets.Decoder,
                    assets.DecoderWithPast,
                    BackendType.GPUCompute));
            ValidateBackend(
                "device-resident-cache",
                tokenizer,
                assets.Reference,
                () => new UnityMarianDeviceResidentGenerationBackend(
                    assets.Encoder,
                    assets.Decoder,
                    assets.DecoderWithPast,
                    BackendType.GPUCompute));

            Debug.Log(
                "PhraseLayer Marian translation parity PASS: revision=" + assets.Manifest.revision +
                "; samples=" + assets.Reference.samples.Length +
                "; source_tokens=exact; generated_tokens=exact; decoded_text=exact; " +
                "backends=cpu-clone-baseline,device-resident-cache");
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve com.unity.ai.inference 2.2.1 before running Marian parity.");
#endif
        }

        public static void VerifyLocalAssetsBatch()
        {
            ExitBatch(VerifyLocalAssets);
        }

        public static void RunTranslationParityProbeBatch()
        {
            ExitBatch(RunTranslationParityProbe);
        }

        private static void ExitBatch(Action action)
        {
            try
            {
                action();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        private static LocalAssets LoadAndValidateAssets()
        {
            AssetDatabase.Refresh();
            var encoder = LoadRequired<ModelAsset>(EncoderPath);
            var decoder = LoadRequired<ModelAsset>(DecoderPath);
            var decoderWithPast = LoadRequired<ModelAsset>(DecoderWithPastPath);
            var manifestAsset = LoadRequired<TextAsset>(ManifestPath);
            var referenceAsset = LoadRequired<TextAsset>(ReferencePath);

            var manifest = JsonUtility.FromJson<MarianAssetManifest>(manifestAsset.text);
            if (manifest == null || manifest.purpose != "phrase-layer-unity-local-marian-assets")
                throw new InvalidOperationException("Local Marian manifest purpose is missing or invalid.");
            if (manifest.model_id != "Helsinki-NLP/opus-mt-en-jap")
                throw new InvalidOperationException("Local Marian manifest model id drifted: " + manifest.model_id);
            if (manifest.revision != ExpectedRevision)
                throw new InvalidOperationException("Local Marian manifest revision drifted: " + manifest.revision);
            if (!manifest.onnx_contract_inspected)
                throw new InvalidOperationException("Local Marian assets were not ONNX-contract inspected before Unity staging.");
            if (manifest.source_weight_copied_to_unity)
                throw new InvalidOperationException("The PyTorch Marian source weight must never be staged into Unity assets.");
            if (manifest.reference_sample_count < 3)
                throw new InvalidOperationException("Local Marian manifest must record at least three reference samples.");

            var reference = JsonUtility.FromJson<MarianReferenceFixture>(referenceAsset.text);
            ValidateReferenceFixture(reference, manifest);
            return new LocalAssets(encoder, decoder, decoderWithPast, manifest, reference);
        }

        private static void ValidateReferenceFixture(
            MarianReferenceFixture reference,
            MarianAssetManifest manifest)
        {
            if (reference == null || reference.purpose != "phrase-layer-marian-greedy-reference")
                throw new InvalidOperationException("Marian reference fixture purpose is missing or invalid.");
            if (reference.model_id != manifest.model_id || reference.revision != manifest.revision)
                throw new InvalidOperationException("Marian reference identity does not match staged model identity.");
            if (reference.generation == null)
                throw new InvalidOperationException("Marian reference fixture is missing generation policy.");
            if (reference.generation.beam_width != OpusMtEnJaGenerationPolicy.PhraseLayerGreedyBeamWidth)
                throw new InvalidOperationException("Marian reference must use PhraseLayer beamWidth=1 parity policy.");
            if (reference.generation.maximum_source_tokens <= 0 || reference.generation.maximum_target_tokens <= 0)
                throw new InvalidOperationException("Marian reference generation limits must be positive.");
            if (reference.generation.decoder_start_token_id != OpusMtEnJaMarianContract.ExpectedDecoderStartTokenId ||
                reference.generation.pad_token_id != OpusMtEnJaMarianContract.ExpectedPadTokenId ||
                reference.generation.eos_token_id != OpusMtEnJaMarianContract.ExpectedEosTokenId)
            {
                throw new InvalidOperationException("Marian reference special-token contract drifted.");
            }
            if (reference.generation.banned_token_ids == null ||
                reference.generation.banned_token_ids.Length != 1 ||
                reference.generation.banned_token_ids[0] != OpusMtEnJaGenerationPolicy.BannedPadTokenId)
            {
                throw new InvalidOperationException("Marian reference PAD bad-word policy drifted.");
            }
            if (!reference.generation.force_eos_at_maximum_tokens || reference.generation.do_sample)
                throw new InvalidOperationException("Marian reference must be deterministic and force EOS at the target limit.");
            if (reference.samples == null || reference.samples.Length < 3)
                throw new InvalidOperationException("Marian reference fixture must contain at least three samples.");

            for (var index = 0; index < reference.samples.Length; index++)
            {
                var sample = reference.samples[index];
                if (sample == null || string.IsNullOrWhiteSpace(sample.source_text))
                    throw new InvalidOperationException("Marian reference contains an empty source sample at index " + index + ".");
                if (sample.source_token_ids == null || sample.source_token_ids.Length == 0)
                    throw new InvalidOperationException("Marian reference source tokens are empty at index " + index + ".");
                if (sample.generated_token_ids == null || sample.generated_token_ids.Length == 0)
                    throw new InvalidOperationException("Marian reference generated tokens are empty at index " + index + ".");
                if (sample.generated_token_ids[sample.generated_token_ids.Length - 1] != OpusMtEnJaMarianContract.ExpectedEosTokenId)
                    throw new InvalidOperationException("Marian reference generated sequence does not end in EOS at index " + index + ".");
                if (string.IsNullOrWhiteSpace(sample.translated_text))
                    throw new InvalidOperationException("Marian reference decoded text is empty at index " + index + ".");
            }
        }

        private static void ValidateTokenizerReference(
            ITranslationTokenizer tokenizer,
            MarianReferenceFixture reference)
        {
            foreach (var sample in reference.samples)
            {
                var encoded = tokenizer.EncodeSource(
                    sample.source_text,
                    reference.generation.maximum_source_tokens);
                if (encoded.WasTruncated != sample.source_was_truncated)
                {
                    throw new InvalidOperationException(
                        "Marian source truncation parity failed for sample: " + sample.source_text);
                }
                RequireExactTokens(
                    "source",
                    sample.source_text,
                    sample.source_token_ids,
                    encoded.TokenIds);

                var referenceDecoded = tokenizer.DecodeTarget(sample.generated_token_ids);
                if (!string.Equals(referenceDecoded, sample.translated_text, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Marian target tokenizer parity failed for sample: " + sample.source_text);
                }
            }
        }

        private static void ValidateBackend(
            string backendName,
            ITranslationTokenizer tokenizer,
            MarianReferenceFixture reference,
            Func<ISeq2SeqGenerationBackend> createBackend)
        {
            var backend = createBackend();
            var lease = backend as IDisposable;
            if (lease == null)
                throw new InvalidOperationException("Marian parity backend must be disposable: " + backendName);

            using (lease)
            {
                var model = OpusMtEnJaGenerationPolicy.CreateGreedyModel(backend);
                var options = OpusMtEnJaGenerationPolicy.CreateGreedyParityOptions(
                    reference.generation.maximum_source_tokens,
                    reference.generation.maximum_target_tokens);

                foreach (var sample in reference.samples)
                {
                    var source = tokenizer.EncodeSource(sample.source_text, options.MaximumSourceTokens);
                    var generated = model.GenerateAsync(source.TokenIds, options).GetAwaiter().GetResult();
                    RequireExactTokens(
                        backendName + " generated",
                        sample.source_text,
                        sample.generated_token_ids,
                        generated.TokenIds);

                    var translated = tokenizer.DecodeTarget(generated.TokenIds);
                    if (!string.Equals(translated, sample.translated_text, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Marian " + backendName + " decoded text parity failed for sample: " + sample.source_text);
                    }
                }
            }
        }

        private static void RequireExactTokens(
            string stage,
            string sourceText,
            IReadOnlyList<int> expected,
            IReadOnlyList<int> actual)
        {
            if (expected == null || actual == null)
                throw new InvalidOperationException("Marian " + stage + " token sequence is null.");
            if (expected.Count != actual.Count)
            {
                throw new InvalidOperationException(
                    "Marian " + stage + " token count parity failed for sample '" + sourceText +
                    "': expected=" + expected.Count + " actual=" + actual.Count + ".");
            }
            for (var index = 0; index < expected.Count; index++)
            {
                if (expected[index] != actual[index])
                {
                    throw new InvalidOperationException(
                        "Marian " + stage + " token parity failed for sample '" + sourceText +
                        "' at index " + index + ": expected=" + expected[index] + " actual=" + actual[index] + ".");
                }
            }
        }

        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                var imported = AssetDatabase.LoadMainAssetAtPath(path);
                var actual = imported == null ? "missing" : imported.GetType().FullName;
                throw new InvalidOperationException(
                    "Expected " + typeof(T).FullName + " at " + path + " but Unity imported " + actual + ".");
            }
            return asset;
        }

        private sealed class LocalAssets
        {
            public LocalAssets(
                ModelAsset encoder,
                ModelAsset decoder,
                ModelAsset decoderWithPast,
                MarianAssetManifest manifest,
                MarianReferenceFixture reference)
            {
                Encoder = encoder;
                Decoder = decoder;
                DecoderWithPast = decoderWithPast;
                Manifest = manifest;
                Reference = reference;
            }

            public ModelAsset Encoder { get; }
            public ModelAsset Decoder { get; }
            public ModelAsset DecoderWithPast { get; }
            public MarianAssetManifest Manifest { get; }
            public MarianReferenceFixture Reference { get; }
        }

        [Serializable]
        private sealed class MarianAssetManifest
        {
            public string purpose;
            public string model_id;
            public string revision;
            public bool source_weight_copied_to_unity;
            public bool onnx_contract_inspected;
            public int reference_sample_count;
        }

        [Serializable]
        private sealed class MarianReferenceFixture
        {
            public string purpose;
            public string model_id;
            public string revision;
            public MarianGenerationPolicy generation;
            public MarianReferenceSample[] samples;
        }

        [Serializable]
        private sealed class MarianGenerationPolicy
        {
            public int beam_width;
            public bool do_sample;
            public int maximum_source_tokens;
            public int maximum_target_tokens;
            public int decoder_start_token_id;
            public int pad_token_id;
            public int eos_token_id;
            public int[] banned_token_ids;
            public bool force_eos_at_maximum_tokens;
        }

        [Serializable]
        private sealed class MarianReferenceSample
        {
            public string source_text;
            public int[] source_token_ids;
            public bool source_was_truncated;
            public int[] generated_token_ids;
            public string translated_text;
        }
#endif
    }
}
