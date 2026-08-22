using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Translation;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    /// <summary>
    /// Correctness-first Unity Inference backend for the measured OPUS-MT encoder + non-cached decoder pair.
    ///
    /// The encoder output is cached only while source token ids are byte-for-byte identical. Every decoder call
    /// reruns the complete generated prefix and synchronously downloads logits. This is intentionally expensive;
    /// it is a parity/reference path, not a Quest performance claim. KV-cache execution can replace the decoder
    /// internals later without changing Core's IAutoregressiveTranslationBackend boundary.
    /// </summary>
    public sealed class UnityOpusMtAutoregressiveBackend : IAutoregressiveTranslationBackend, IDisposable
    {
        private readonly Worker encoderWorker;
        private readonly Worker decoderWorker;
        private int[] cachedSourceTokenIds = Array.Empty<int>();
        private int[] cachedAttentionMask = Array.Empty<int>();
        private float[] cachedEncoderHiddenStates = Array.Empty<float>();
        private bool disposed;

        public UnityOpusMtAutoregressiveBackend(
            ModelAsset encoderModel,
            ModelAsset decoderModel,
            BackendType backendType = BackendType.GPUCompute)
        {
            if (encoderModel == null) throw new ArgumentNullException(nameof(encoderModel));
            if (decoderModel == null) throw new ArgumentNullException(nameof(decoderModel));

            UnityOpusMtModelProbe.ValidateAndBuildReport(encoderModel, decoderModel);
            encoderWorker = new Worker(ModelLoader.Load(encoderModel), backendType);
            decoderWorker = new Worker(ModelLoader.Load(decoderModel), backendType);
            BackendType = backendType;
        }

        public BackendType BackendType { get; }
        public bool IsSupported => true;

        public Task<IReadOnlyList<TranslationTokenCandidate>> PredictNextAsync(
            IReadOnlyList<int> sourceTokenIds,
            IReadOnlyList<int> generatedTokenIds,
            int maxCandidates,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            if (sourceTokenIds == null) throw new ArgumentNullException(nameof(sourceTokenIds));
            if (generatedTokenIds == null) throw new ArgumentNullException(nameof(generatedTokenIds));
            if (sourceTokenIds.Count == 0) throw new ArgumentException("Source token sequence is empty.", nameof(sourceTokenIds));
            if (generatedTokenIds.Count == 0) throw new ArgumentException("Decoder token sequence is empty.", nameof(generatedTokenIds));
            if (maxCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
            cancellationToken.ThrowIfCancellationRequested();

            EnsureEncoderCache(sourceTokenIds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var decoderIds = CopyTokens(generatedTokenIds);
            using (var attentionTensor = new Tensor<int>(
                       new TensorShape(1, cachedAttentionMask.Length),
                       cachedAttentionMask))
            using (var decoderIdsTensor = new Tensor<int>(
                       new TensorShape(1, decoderIds.Length),
                       decoderIds))
            using (var hiddenTensor = new Tensor<float>(
                       new TensorShape(1, cachedSourceTokenIds.Length, OpusMtEnJapMeasuredOnnxContract.HiddenSize),
                       cachedEncoderHiddenStates))
            {
                decoderWorker.SetInput("encoder_attention_mask", attentionTensor);
                decoderWorker.SetInput("input_ids", decoderIdsTensor);
                decoderWorker.SetInput("encoder_hidden_states", hiddenTensor);
                decoderWorker.Schedule();

                var logitsTensor = decoderWorker.PeekOutput("logits") as Tensor<float>;
                if (logitsTensor == null)
                    throw new InvalidOperationException("OPUS-MT decoder output 'logits' is not a float tensor.");

                var shape = logitsTensor.shape;
                ValidateLogitsShape(shape, decoderIds.Length);
                var values = logitsTensor.DownloadToArray();
                var vocabularySize = OpusMtEnJapMeasuredOnnxContract.VocabularySize;
                var expectedLength = checked(decoderIds.Length * vocabularySize);
                if (values.Length != expectedLength)
                {
                    throw new InvalidOperationException(
                        "OPUS-MT decoder logits length mismatch: expected " + expectedLength +
                        " actual " + values.Length + ".");
                }

                var lastPositionOffset = checked((decoderIds.Length - 1) * vocabularySize);
                var candidates = TranslationLogitSelector.SelectTopLogProbabilities(
                    values,
                    lastPositionOffset,
                    vocabularySize,
                    maxCandidates);
                return Task.FromResult(candidates);
            }
        }

        private void EnsureEncoderCache(
            IReadOnlyList<int> sourceTokenIds,
            CancellationToken cancellationToken)
        {
            if (SourceMatchesCache(sourceTokenIds)) return;

            var source = CopyTokens(sourceTokenIds);
            var attention = BuildAttentionMask(source);
            using (var sourceTensor = new Tensor<int>(new TensorShape(1, source.Length), source))
            using (var attentionTensor = new Tensor<int>(new TensorShape(1, attention.Length), attention))
            {
                encoderWorker.SetInput("input_ids", sourceTensor);
                encoderWorker.SetInput("attention_mask", attentionTensor);
                encoderWorker.Schedule();
                cancellationToken.ThrowIfCancellationRequested();

                var hiddenTensor = encoderWorker.PeekOutput("last_hidden_state") as Tensor<float>;
                if (hiddenTensor == null)
                    throw new InvalidOperationException("OPUS-MT encoder output 'last_hidden_state' is not a float tensor.");

                var shape = hiddenTensor.shape;
                if (shape.rank != 3 ||
                    shape[0] != 1 ||
                    shape[1] != source.Length ||
                    shape[2] != OpusMtEnJapMeasuredOnnxContract.HiddenSize)
                {
                    throw new InvalidOperationException(
                        "OPUS-MT encoder output shape mismatch: expected [1," + source.Length + "," +
                        OpusMtEnJapMeasuredOnnxContract.HiddenSize + "] actual " + shape + ".");
                }

                var values = hiddenTensor.DownloadToArray();
                var expectedLength = checked(source.Length * OpusMtEnJapMeasuredOnnxContract.HiddenSize);
                if (values.Length != expectedLength)
                {
                    throw new InvalidOperationException(
                        "OPUS-MT encoder output length mismatch: expected " + expectedLength +
                        " actual " + values.Length + ".");
                }

                cachedSourceTokenIds = source;
                cachedAttentionMask = attention;
                cachedEncoderHiddenStates = values;
            }
        }

        private static void ValidateLogitsShape(TensorShape shape, int decoderLength)
        {
            if (shape.rank != 3 ||
                shape[0] != 1 ||
                shape[1] != decoderLength ||
                shape[2] != OpusMtEnJapMeasuredOnnxContract.VocabularySize)
            {
                throw new InvalidOperationException(
                    "OPUS-MT decoder logits shape mismatch: expected [1," + decoderLength + "," +
                    OpusMtEnJapMeasuredOnnxContract.VocabularySize + "] actual " + shape + ".");
            }
        }

        private bool SourceMatchesCache(IReadOnlyList<int> sourceTokenIds)
        {
            if (cachedSourceTokenIds.Length != sourceTokenIds.Count) return false;
            for (var index = 0; index < cachedSourceTokenIds.Length; index++)
            {
                if (cachedSourceTokenIds[index] != sourceTokenIds[index]) return false;
            }
            return cachedSourceTokenIds.Length > 0;
        }

        private static int[] CopyTokens(IReadOnlyList<int> tokens)
        {
            var result = new int[tokens.Count];
            for (var index = 0; index < result.Length; index++)
            {
                var token = tokens[index];
                if (token < 0) throw new ArgumentOutOfRangeException(nameof(tokens), "Token ids must be non-negative.");
                result[index] = token;
            }
            return result;
        }

        private static int[] BuildAttentionMask(IReadOnlyList<int> sourceTokenIds)
        {
            var mask = new int[sourceTokenIds.Count];
            for (var index = 0; index < mask.Length; index++)
                mask[index] = sourceTokenIds[index] == OpusMtEnJapGenerationContract.PadTokenId ? 0 : 1;
            return mask;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(UnityOpusMtAutoregressiveBackend));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            decoderWorker.Dispose();
            encoderWorker.Dispose();
            cachedSourceTokenIds = Array.Empty<int>();
            cachedAttentionMask = Array.Empty<int>();
            cachedEncoderHiddenStates = Array.Empty<float>();
        }
    }
#endif
}
