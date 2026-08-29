using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Translation;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    /// <summary>
    /// Correctness-first Marian encoder/decoder backend for Unity Inference Engine 2.2.x.
    ///
    /// This baseline intentionally clones encoder state, logits and KV caches to CPU-owned tensors between steps.
    /// That makes Worker output lifetime explicit and avoids relying on undocumented cross-schedule aliasing. It is
    /// not the final Quest performance path; once parity is proven, cache tensors can be kept on-device behind the
    /// same Core ISeq2SeqGenerationBackend contract.
    /// </summary>
    public sealed class UnityMarianSeq2SeqGenerationBackend : ISeq2SeqGenerationBackend, IDisposable
    {
        private readonly Model encoderModel;
        private readonly Model decoderModel;
        private readonly Model decoderWithPastModel;
        private readonly BackendType backendType;
        private readonly MarianOnnxBundleContractReport graphContract;
        private bool disposed;

        public UnityMarianSeq2SeqGenerationBackend(
            ModelAsset encoderModelAsset,
            ModelAsset decoderModelAsset,
            ModelAsset decoderWithPastModelAsset,
            BackendType backendType = BackendType.GPUCompute)
        {
            if (encoderModelAsset == null) throw new ArgumentNullException(nameof(encoderModelAsset));
            if (decoderModelAsset == null) throw new ArgumentNullException(nameof(decoderModelAsset));
            if (decoderWithPastModelAsset == null) throw new ArgumentNullException(nameof(decoderWithPastModelAsset));

            graphContract = UnityMarianOnnxContractProbe.ValidateBundle(
                encoderModelAsset,
                decoderModelAsset,
                decoderWithPastModelAsset);
            encoderModel = ModelLoader.Load(encoderModelAsset);
            decoderModel = ModelLoader.Load(decoderModelAsset);
            decoderWithPastModel = ModelLoader.Load(decoderWithPastModelAsset);
            this.backendType = backendType;
        }

        public bool IsSupported => true;
        public BackendType BackendType => backendType;
        public MarianOnnxBundleContractReport GraphContract => graphContract;

        public Task<ISeq2SeqGenerationSession> StartAsync(
            IReadOnlyList<int> sourceTokenIds,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            if (sourceTokenIds == null) throw new ArgumentNullException(nameof(sourceTokenIds));
            if (sourceTokenIds.Count == 0)
                throw new ArgumentException("Marian source token sequence must not be empty.", nameof(sourceTokenIds));
            if (sourceTokenIds.Count > OpusMtEnJaMarianContract.ExpectedMaximumPositionEmbeddings)
                throw new ArgumentOutOfRangeException(nameof(sourceTokenIds));
            cancellationToken.ThrowIfCancellationRequested();

            var encoderWorker = new Worker(encoderModel, backendType);
            var decoderWorker = new Worker(decoderModel, backendType);
            var decoderWithPastWorker = new Worker(decoderWithPastModel, backendType);
            Tensor<float> encoderHiddenStates = null;
            Tensor<int> encoderAttentionMask = null;

            try
            {
                var source = sourceTokenIds.ToArray();
                var mask = Enumerable.Repeat(1, source.Length).ToArray();
                using (var sourceTensor = new Tensor<int>(new TensorShape(1, source.Length), source))
                using (var maskTensor = new Tensor<int>(new TensorShape(1, source.Length), mask))
                {
                    encoderWorker.SetInput(OpusMtEnJaMarianOnnxContract.EncoderInputIds, sourceTensor);
                    encoderWorker.SetInput(OpusMtEnJaMarianOnnxContract.EncoderAttentionMask, maskTensor);
                    encoderWorker.Schedule();

                    var output = encoderWorker.PeekOutput(OpusMtEnJaMarianOnnxContract.EncoderLastHiddenState) as Tensor<float>;
                    if (output == null)
                    {
                        throw new InvalidOperationException(
                            "Marian encoder 'last_hidden_state' is not a float tensor after Unity execution.");
                    }

                    encoderHiddenStates = output.ReadbackAndClone();
                    ValidateEncoderHiddenState(encoderHiddenStates, source.Length);
                    encoderAttentionMask = new Tensor<int>(new TensorShape(1, source.Length), mask);
                }

                cancellationToken.ThrowIfCancellationRequested();
                ISeq2SeqGenerationSession session = new Session(
                    decoderWorker,
                    decoderWithPastWorker,
                    encoderHiddenStates,
                    encoderAttentionMask,
                    graphContract.DecoderWithPastReturnsCrossAttentionCache);

                encoderHiddenStates = null;
                encoderAttentionMask = null;
                decoderWorker = null;
                decoderWithPastWorker = null;
                return Task.FromResult(session);
            }
            catch
            {
                encoderHiddenStates?.Dispose();
                encoderAttentionMask?.Dispose();
                decoderWorker?.Dispose();
                decoderWithPastWorker?.Dispose();
                throw;
            }
            finally
            {
                encoderWorker.Dispose();
            }
        }

        private static void ValidateEncoderHiddenState(Tensor<float> tensor, int sourceLength)
        {
            var shape = tensor.shape;
            if (shape.rank != 3 || shape[0] != 1 || shape[1] != sourceLength ||
                shape[2] != OpusMtEnJaMarianContract.ExpectedModelDimension)
            {
                throw new InvalidOperationException(
                    string.Format(
                        "Marian encoder output shape drift: expected [1,{0},{1}] but found {2}.",
                        sourceLength,
                        OpusMtEnJaMarianContract.ExpectedModelDimension,
                        shape));
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(UnityMarianSeq2SeqGenerationBackend));
        }

        public void Dispose()
        {
            disposed = true;
        }

        private sealed class Session : ISeq2SeqGenerationSession
        {
            private readonly Worker decoderWorker;
            private readonly Worker decoderWithPastWorker;
            private readonly Tensor<float> encoderHiddenStates;
            private readonly Tensor<int> encoderAttentionMask;
            private readonly bool decoderWithPastReturnsCrossAttentionCache;
            private readonly Dictionary<string, Tensor<float>> pastCache =
                new Dictionary<string, Tensor<float>>(StringComparer.Ordinal);
            private bool hasDecoded;
            private bool disposed;

            public Session(
                Worker decoderWorker,
                Worker decoderWithPastWorker,
                Tensor<float> encoderHiddenStates,
                Tensor<int> encoderAttentionMask,
                bool decoderWithPastReturnsCrossAttentionCache)
            {
                this.decoderWorker = decoderWorker ?? throw new ArgumentNullException(nameof(decoderWorker));
                this.decoderWithPastWorker = decoderWithPastWorker ?? throw new ArgumentNullException(nameof(decoderWithPastWorker));
                this.encoderHiddenStates = encoderHiddenStates ?? throw new ArgumentNullException(nameof(encoderHiddenStates));
                this.encoderAttentionMask = encoderAttentionMask ?? throw new ArgumentNullException(nameof(encoderAttentionMask));
                this.decoderWithPastReturnsCrossAttentionCache = decoderWithPastReturnsCrossAttentionCache;
            }

            public Task<Seq2SeqDecoderStepResult> DecodeNextAsync(
                int previousTokenId,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                ThrowIfDisposed();
                if (previousTokenId < 0 || previousTokenId >= OpusMtEnJaMarianContract.ExpectedVocabularySize)
                    throw new ArgumentOutOfRangeException(nameof(previousTokenId));
                cancellationToken.ThrowIfCancellationRequested();

                using (var tokenTensor = new Tensor<int>(new TensorShape(1, 1), new[] { previousTokenId }))
                {
                    Seq2SeqDecoderStepResult result;
                    if (!hasDecoded)
                    {
                        result = DecodeFirst(tokenTensor);
                        hasDecoded = true;
                    }
                    else
                    {
                        result = DecodeWithPast(tokenTensor);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(result);
                }
            }

            private Seq2SeqDecoderStepResult DecodeFirst(Tensor<int> tokenTensor)
            {
                decoderWorker.SetInput(OpusMtEnJaMarianOnnxContract.DecoderInputIds, tokenTensor);
                decoderWorker.SetInput(OpusMtEnJaMarianOnnxContract.DecoderEncoderHiddenStates, encoderHiddenStates);
                decoderWorker.SetInput(OpusMtEnJaMarianOnnxContract.DecoderEncoderAttentionMask, encoderAttentionMask);
                decoderWorker.Schedule();

                var logits = ReadLastTokenLogits(decoderWorker);
                ReplaceAllCachesFromInitialDecoder();
                return new Seq2SeqDecoderStepResult(logits);
            }

            private Seq2SeqDecoderStepResult DecodeWithPast(Tensor<int> tokenTensor)
            {
                decoderWithPastWorker.SetInput(OpusMtEnJaMarianOnnxContract.DecoderInputIds, tokenTensor);
                decoderWithPastWorker.SetInput(
                    OpusMtEnJaMarianOnnxContract.DecoderEncoderHiddenStates,
                    encoderHiddenStates);
                decoderWithPastWorker.SetInput(
                    OpusMtEnJaMarianOnnxContract.DecoderEncoderAttentionMask,
                    encoderAttentionMask);

                for (var layer = 0; layer < OpusMtEnJaMarianContract.ExpectedDecoderLayers; layer++)
                {
                    SetPastCacheInput(layer, "decoder", "key");
                    SetPastCacheInput(layer, "decoder", "value");
                    SetPastCacheInput(layer, "encoder", "key");
                    SetPastCacheInput(layer, "encoder", "value");
                }

                decoderWithPastWorker.Schedule();
                var logits = ReadLastTokenLogits(decoderWithPastWorker);
                ReplaceSelfAttentionCachesFromWithPastDecoder();
                if (decoderWithPastReturnsCrossAttentionCache)
                    ReplaceCrossAttentionCachesFromWithPastDecoder();
                return new Seq2SeqDecoderStepResult(logits);
            }

            private void SetPastCacheInput(int layer, string attentionKind, string keyOrValue)
            {
                var name = OpusMtEnJaMarianOnnxContract.PastCacheName(layer, attentionKind, keyOrValue);
                if (!pastCache.TryGetValue(name, out var tensor))
                    throw new InvalidOperationException("Marian generation cache is missing '" + name + "'.");
                decoderWithPastWorker.SetInput(name, tensor);
            }

            private void ReplaceAllCachesFromInitialDecoder()
            {
                for (var layer = 0; layer < OpusMtEnJaMarianContract.ExpectedDecoderLayers; layer++)
                {
                    ReplaceCache(decoderWorker, layer, "decoder", "key");
                    ReplaceCache(decoderWorker, layer, "decoder", "value");
                    ReplaceCache(decoderWorker, layer, "encoder", "key");
                    ReplaceCache(decoderWorker, layer, "encoder", "value");
                }
            }

            private void ReplaceSelfAttentionCachesFromWithPastDecoder()
            {
                for (var layer = 0; layer < OpusMtEnJaMarianContract.ExpectedDecoderLayers; layer++)
                {
                    ReplaceCache(decoderWithPastWorker, layer, "decoder", "key");
                    ReplaceCache(decoderWithPastWorker, layer, "decoder", "value");
                }
            }

            private void ReplaceCrossAttentionCachesFromWithPastDecoder()
            {
                for (var layer = 0; layer < OpusMtEnJaMarianContract.ExpectedDecoderLayers; layer++)
                {
                    ReplaceCache(decoderWithPastWorker, layer, "encoder", "key");
                    ReplaceCache(decoderWithPastWorker, layer, "encoder", "value");
                }
            }

            private void ReplaceCache(Worker worker, int layer, string attentionKind, string keyOrValue)
            {
                var outputName = OpusMtEnJaMarianOnnxContract.PresentCacheName(layer, attentionKind, keyOrValue);
                var output = worker.PeekOutput(outputName) as Tensor<float>;
                if (output == null)
                    throw new InvalidOperationException("Marian cache output '" + outputName + "' is not a float tensor.");

                var cloned = output.ReadbackAndClone();
                if (cloned.shape.rank != 4)
                {
                    cloned.Dispose();
                    throw new InvalidOperationException("Marian cache output '" + outputName + "' must have rank 4.");
                }

                var inputName = OpusMtEnJaMarianOnnxContract.PastCacheName(layer, attentionKind, keyOrValue);
                if (pastCache.TryGetValue(inputName, out var previous))
                    previous.Dispose();
                pastCache[inputName] = cloned;
            }

            private static float[] ReadLastTokenLogits(Worker worker)
            {
                var output = worker.PeekOutput(OpusMtEnJaMarianOnnxContract.DecoderLogits) as Tensor<float>;
                if (output == null)
                    throw new InvalidOperationException("Marian decoder 'logits' output is not a float tensor.");

                using (var cpu = output.ReadbackAndClone())
                {
                    var shape = cpu.shape;
                    if (shape.rank != 3 || shape[0] != 1 ||
                        shape[2] != OpusMtEnJaMarianContract.ExpectedVocabularySize)
                    {
                        throw new InvalidOperationException(
                            string.Format(
                                "Marian logits shape drift: expected [1,T,{0}] but found {1}.",
                                OpusMtEnJaMarianContract.ExpectedVocabularySize,
                                shape));
                    }
                    if (shape[1] <= 0)
                        throw new InvalidOperationException("Marian logits sequence length must be positive.");

                    var values = cpu.DownloadToArray();
                    var vocabularySize = OpusMtEnJaMarianContract.ExpectedVocabularySize;
                    if (values.Length != checked(shape[1] * vocabularySize))
                        throw new InvalidOperationException("Marian logits tensor length does not match its reviewed shape.");

                    var last = new float[vocabularySize];
                    Array.Copy(values, values.Length - vocabularySize, last, 0, vocabularySize);
                    return last;
                }
            }

            private void ThrowIfDisposed()
            {
                if (disposed) throw new ObjectDisposedException(nameof(Session));
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;

                foreach (var tensor in pastCache.Values)
                    tensor.Dispose();
                pastCache.Clear();
                encoderHiddenStates.Dispose();
                encoderAttentionMask.Dispose();
                decoderWorker.Dispose();
                decoderWithPastWorker.Dispose();
            }
        }
    }
#else
    /// <summary>
    /// Host-CI fallback. Real Marian execution is compiled only when the reviewed Unity Inference Engine gate is active.
    /// </summary>
    public sealed class UnityMarianSeq2SeqGenerationBackend : ISeq2SeqGenerationBackend, IDisposable
    {
        public bool IsSupported => false;

        public Task<ISeq2SeqGenerationSession> StartAsync(
            IReadOnlyList<int> sourceTokenIds,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException(
                "Unity Marian generation requires com.unity.ai.inference in the reviewed 2.2.x range.");
        }

        public void Dispose()
        {
        }
    }
#endif
}
