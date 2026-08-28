using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Experimental Marian backend that keeps encoder state and decoder KV cache on the Inference Engine backend.
    ///
    /// In Inference Engine 2.2.1 a PeekOutput reference remains valid until the producing Worker is scheduled again
    /// or disposed. PhraseLayer therefore keeps the first decoder Worker alive and never reschedules it, allowing
    /// its immutable cross-attention cache to be reused directly. Self-attention cache from decoder_with_past is
    /// copied with Worker.CopyOutput before that Worker is rescheduled, producing an independently owned backend
    /// tensor without a CPU readback. Only logits are read back to CPU for Core greedy argmax.
    ///
    /// This is still experimental until real Unity 6000.0.66f2 import and numerical parity are measured.
    /// </summary>
    public sealed class UnityMarianDeviceResidentGenerationBackend : ISeq2SeqGenerationBackend, IDisposable
    {
        private readonly Model encoderModel;
        private readonly Model decoderModel;
        private readonly Model decoderWithPastModel;
        private readonly BackendType backendType;
        private readonly MarianOnnxBundleContractReport graphContract;
        private bool disposed;

        public UnityMarianDeviceResidentGenerationBackend(
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
                    encoderHiddenStates = CopyFloatOutput(
                        encoderWorker,
                        OpusMtEnJaMarianOnnxContract.EncoderLastHiddenState,
                        expectedRank: 3);
                    ValidateEncoderHiddenState(encoderHiddenStates, source.Length);
                    encoderAttentionMask = new Tensor<int>(new TensorShape(1, source.Length), mask);
                }

                cancellationToken.ThrowIfCancellationRequested();
                ISeq2SeqGenerationSession session = new Session(
                    decoderWorker,
                    decoderWithPastWorker,
                    encoderHiddenStates,
                    encoderAttentionMask);

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

        private static Tensor<float> CopyFloatOutput(Worker worker, string outputName, int expectedRank)
        {
            Tensor copied = null;
            worker.CopyOutput(outputName, ref copied);
            var typed = copied as Tensor<float>;
            if (typed == null)
            {
                copied?.Dispose();
                throw new InvalidOperationException(
                    "Marian output '" + outputName + "' could not be copied as a float tensor.");
            }
            if (typed.shape.rank != expectedRank)
            {
                typed.Dispose();
                throw new InvalidOperationException(
                    "Marian output '" + outputName + "' expected rank " + expectedRank +
                    " but found " + typed.shape.rank + ".");
            }
            return typed;
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
            if (disposed) throw new ObjectDisposedException(nameof(UnityMarianDeviceResidentGenerationBackend));
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
            private readonly Dictionary<string, Tensor<float>> ownedSelfCache =
                new Dictionary<string, Tensor<float>>(StringComparer.Ordinal);
            private bool hasDecoded;
            private bool hasOwnedSelfCache;
            private bool disposed;

            public Session(
                Worker decoderWorker,
                Worker decoderWithPastWorker,
                Tensor<float> encoderHiddenStates,
                Tensor<int> encoderAttentionMask)
            {
                this.decoderWorker = decoderWorker ?? throw new ArgumentNullException(nameof(decoderWorker));
                this.decoderWithPastWorker = decoderWithPastWorker ?? throw new ArgumentNullException(nameof(decoderWithPastWorker));
                this.encoderHiddenStates = encoderHiddenStates ?? throw new ArgumentNullException(nameof(encoderHiddenStates));
                this.encoderAttentionMask = encoderAttentionMask ?? throw new ArgumentNullException(nameof(encoderAttentionMask));
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

                // Do not copy cache here. decoderWorker is deliberately never scheduled again, so its output
                // references remain valid for the lifetime of this session under the 2.2.1 Worker contract.
                ValidateInitialDecoderCacheOutputs();
                return new Seq2SeqDecoderStepResult(ReadLastTokenLogits(decoderWorker));
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
                    SetSelfCacheInput(layer, "key");
                    SetSelfCacheInput(layer, "value");
                    SetStaticCrossCacheInput(layer, "key");
                    SetStaticCrossCacheInput(layer, "value");
                }

                decoderWithPastWorker.Schedule();
                var logits = ReadLastTokenLogits(decoderWithPastWorker);
                CopyNextSelfCacheOnBackend();
                hasOwnedSelfCache = true;
                return new Seq2SeqDecoderStepResult(logits);
            }

            private void SetSelfCacheInput(int layer, string keyOrValue)
            {
                var inputName = OpusMtEnJaMarianOnnxContract.PastCacheName(layer, "decoder", keyOrValue);
                Tensor<float> tensor;
                if (hasOwnedSelfCache)
                {
                    if (!ownedSelfCache.TryGetValue(inputName, out tensor))
                        throw new InvalidOperationException("Marian owned self-cache is missing '" + inputName + "'.");
                }
                else
                {
                    var outputName = OpusMtEnJaMarianOnnxContract.PresentCacheName(layer, "decoder", keyOrValue);
                    tensor = RequireFloatOutputReference(decoderWorker, outputName, expectedRank: 4);
                }
                decoderWithPastWorker.SetInput(inputName, tensor);
            }

            private void SetStaticCrossCacheInput(int layer, string keyOrValue)
            {
                var inputName = OpusMtEnJaMarianOnnxContract.PastCacheName(layer, "encoder", keyOrValue);
                var outputName = OpusMtEnJaMarianOnnxContract.PresentCacheName(layer, "encoder", keyOrValue);
                var tensor = RequireFloatOutputReference(decoderWorker, outputName, expectedRank: 4);
                decoderWithPastWorker.SetInput(inputName, tensor);
            }

            private void ValidateInitialDecoderCacheOutputs()
            {
                for (var layer = 0; layer < OpusMtEnJaMarianContract.ExpectedDecoderLayers; layer++)
                {
                    RequireFloatOutputReference(
                        decoderWorker,
                        OpusMtEnJaMarianOnnxContract.PresentCacheName(layer, "decoder", "key"),
                        expectedRank: 4);
                    RequireFloatOutputReference(
                        decoderWorker,
                        OpusMtEnJaMarianOnnxContract.PresentCacheName(layer, "decoder", "value"),
                        expectedRank: 4);
                    RequireFloatOutputReference(
                        decoderWorker,
                        OpusMtEnJaMarianOnnxContract.PresentCacheName(layer, "encoder", "key"),
                        expectedRank: 4);
                    RequireFloatOutputReference(
                        decoderWorker,
                        OpusMtEnJaMarianOnnxContract.PresentCacheName(layer, "encoder", "value"),
                        expectedRank: 4);
                }
            }

            private void CopyNextSelfCacheOnBackend()
            {
                var next = new Dictionary<string, Tensor<float>>(StringComparer.Ordinal);
                try
                {
                    for (var layer = 0; layer < OpusMtEnJaMarianContract.ExpectedDecoderLayers; layer++)
                    {
                        CopySelfCache(next, layer, "key");
                        CopySelfCache(next, layer, "value");
                    }
                }
                catch
                {
                    foreach (var tensor in next.Values)
                        tensor.Dispose();
                    throw;
                }

                foreach (var tensor in ownedSelfCache.Values)
                    tensor.Dispose();
                ownedSelfCache.Clear();
                foreach (var pair in next)
                    ownedSelfCache.Add(pair.Key, pair.Value);
            }

            private void CopySelfCache(
                IDictionary<string, Tensor<float>> destination,
                int layer,
                string keyOrValue)
            {
                var outputName = OpusMtEnJaMarianOnnxContract.PresentCacheName(layer, "decoder", keyOrValue);
                var inputName = OpusMtEnJaMarianOnnxContract.PastCacheName(layer, "decoder", keyOrValue);
                Tensor copied = null;
                decoderWithPastWorker.CopyOutput(outputName, ref copied);
                var typed = copied as Tensor<float>;
                if (typed == null)
                {
                    copied?.Dispose();
                    throw new InvalidOperationException(
                        "Marian self-cache output '" + outputName + "' could not be copied as a float tensor.");
                }
                if (typed.shape.rank != 4)
                {
                    typed.Dispose();
                    throw new InvalidOperationException(
                        "Marian self-cache output '" + outputName + "' must have rank 4.");
                }
                destination.Add(inputName, typed);
            }

            private static Tensor<float> RequireFloatOutputReference(
                Worker worker,
                string outputName,
                int expectedRank)
            {
                var output = worker.PeekOutput(outputName) as Tensor<float>;
                if (output == null)
                    throw new InvalidOperationException("Marian output '" + outputName + "' is not a float tensor.");
                if (output.shape.rank != expectedRank)
                    throw new InvalidOperationException(
                        "Marian output '" + outputName + "' expected rank " + expectedRank +
                        " but found " + output.shape.rank + ".");
                return output;
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

                foreach (var tensor in ownedSelfCache.Values)
                    tensor.Dispose();
                ownedSelfCache.Clear();
                encoderHiddenStates.Dispose();
                encoderAttentionMask.Dispose();
                decoderWithPastWorker.Dispose();
                decoderWorker.Dispose();
            }
        }
    }
#else
    public sealed class UnityMarianDeviceResidentGenerationBackend : ISeq2SeqGenerationBackend, IDisposable
    {
        public bool IsSupported => false;

        public Task<ISeq2SeqGenerationSession> StartAsync(
            IReadOnlyList<int> sourceTokenIds,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException(
                "Device-resident Marian generation requires com.unity.ai.inference in the reviewed 2.2.x range.");
        }

        public void Dispose()
        {
        }
    }
#endif
}
