using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Audio;
using PhraseLayer.Core.Inputs;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    /// <summary>
    /// Unity Inference Engine backend for the reviewed Moonshine v1 four-graph deployment ABI.
    /// Preprocessing and encoding run once per prepared 16 kHz audio chunk. Encoder output and decoder
    /// cache remain backend-resident; only logits are read back for Core greedy argmax.
    ///
    /// The upstream v1 ABI is positional. Input/output names are therefore captured from each imported
    /// Model in order and never hard-coded to exporter-generated names such as args_0 or functional_23.
    /// </summary>
    public sealed class UnityMoonshineV1GenerationBackend : IAudioSeq2SeqGenerationBackend, IDisposable
    {
        private readonly Model preprocessModel;
        private readonly Model encoderModel;
        private readonly Model uncachedDecoderModel;
        private readonly Model cachedDecoderModel;
        private readonly BackendType backendType;
        private readonly MoonshineOnnxBundleContractReport graphContract;
        private readonly string[] preprocessInputs;
        private readonly string[] preprocessOutputs;
        private readonly string[] encoderInputs;
        private readonly string[] encoderOutputs;
        private readonly string[] uncachedInputs;
        private readonly string[] uncachedOutputs;
        private readonly string[] cachedInputs;
        private readonly string[] cachedOutputs;
        private bool disposed;

        public UnityMoonshineV1GenerationBackend(
            ModelAsset preprocessModelAsset,
            ModelAsset encoderModelAsset,
            ModelAsset uncachedDecoderModelAsset,
            ModelAsset cachedDecoderModelAsset,
            BackendType backendType = BackendType.GPUCompute)
        {
            graphContract = UnityMoonshineOnnxContractProbe.ValidateBundle(
                preprocessModelAsset,
                encoderModelAsset,
                uncachedDecoderModelAsset,
                cachedDecoderModelAsset);

            preprocessModel = ModelLoader.Load(preprocessModelAsset);
            encoderModel = ModelLoader.Load(encoderModelAsset);
            uncachedDecoderModel = ModelLoader.Load(uncachedDecoderModelAsset);
            cachedDecoderModel = ModelLoader.Load(cachedDecoderModelAsset);
            this.backendType = backendType;

            preprocessInputs = CopyInputNames(preprocessModel);
            preprocessOutputs = CopyOutputNames(preprocessModel);
            encoderInputs = CopyInputNames(encoderModel);
            encoderOutputs = CopyOutputNames(encoderModel);
            uncachedInputs = CopyInputNames(uncachedDecoderModel);
            uncachedOutputs = CopyOutputNames(uncachedDecoderModel);
            cachedInputs = CopyInputNames(cachedDecoderModel);
            cachedOutputs = CopyOutputNames(cachedDecoderModel);
        }

        public bool IsSupported => true;
        public BackendType BackendType => backendType;
        public MoonshineOnnxBundleContractReport GraphContract => graphContract;

        public Task<IAudioSeq2SeqGenerationSession> StartAsync(
            AudioChunk monoAudio,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            if (monoAudio == null) throw new ArgumentNullException(nameof(monoAudio));
            if (monoAudio.SampleRate != MoonshineTinyAsrContract.RequiredSampleRate)
                throw new ArgumentException("Moonshine v1 backend requires prepared 16 kHz mono audio.", nameof(monoAudio));
            if (monoAudio.Samples.Length == 0)
                throw new ArgumentException("Moonshine v1 backend requires at least one audio sample.", nameof(monoAudio));
            cancellationToken.ThrowIfCancellationRequested();

            var preprocessWorker = new Worker(preprocessModel, backendType);
            var encoderWorker = new Worker(encoderModel, backendType);
            var uncachedWorker = new Worker(uncachedDecoderModel, backendType);
            var cachedWorker = new Worker(cachedDecoderModel, backendType);
            Tensor<float> features = null;
            Tensor<float> encoderOut = null;

            try
            {
                using (var audioTensor = new Tensor<float>(
                    new TensorShape(1, monoAudio.Samples.Length),
                    (float[])monoAudio.Samples.Clone()))
                {
                    preprocessWorker.SetInput(preprocessInputs[0], audioTensor);
                    preprocessWorker.Schedule();
                    features = CopyFloatOutput(preprocessWorker, preprocessOutputs[0], expectedRank: 3, "preprocess features");
                }

                cancellationToken.ThrowIfCancellationRequested();
                ValidateFeatures(features);
                var featureLength = features.shape[1];
                using (var featureLengthTensor = new Tensor<int>(new TensorShape(1), new[] { featureLength }))
                {
                    encoderWorker.SetInput(encoderInputs[0], features);
                    encoderWorker.SetInput(encoderInputs[1], featureLengthTensor);
                    encoderWorker.Schedule();
                    encoderOut = CopyFloatOutput(encoderWorker, encoderOutputs[0], expectedRank: 3, "encoder output");
                }
                ValidateEncoderOutput(encoderOut);
                cancellationToken.ThrowIfCancellationRequested();

                IAudioSeq2SeqGenerationSession session = new Session(
                    uncachedWorker,
                    cachedWorker,
                    encoderOut,
                    uncachedInputs,
                    uncachedOutputs,
                    cachedInputs,
                    cachedOutputs);
                uncachedWorker = null;
                cachedWorker = null;
                encoderOut = null;
                return Task.FromResult(session);
            }
            catch
            {
                encoderOut?.Dispose();
                uncachedWorker?.Dispose();
                cachedWorker?.Dispose();
                throw;
            }
            finally
            {
                features?.Dispose();
                encoderWorker.Dispose();
                preprocessWorker.Dispose();
            }
        }

        public void Dispose()
        {
            disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(UnityMoonshineV1GenerationBackend));
        }

        private static string[] CopyInputNames(Model model)
        {
            var result = new string[model.inputs.Count];
            for (var index = 0; index < result.Length; index++) result[index] = model.inputs[index].name;
            return result;
        }

        private static string[] CopyOutputNames(Model model)
        {
            var result = new string[model.outputs.Count];
            for (var index = 0; index < result.Length; index++) result[index] = model.outputs[index].name;
            return result;
        }

        private static Tensor<float> CopyFloatOutput(Worker worker, string outputName, int expectedRank, string label)
        {
            Tensor copied = null;
            worker.CopyOutput(outputName, ref copied);
            var typed = copied as Tensor<float>;
            if (typed == null)
            {
                copied?.Dispose();
                throw new InvalidOperationException("Moonshine " + label + " is not a float tensor.");
            }
            if (typed.shape.rank != expectedRank)
            {
                typed.Dispose();
                throw new InvalidOperationException(
                    "Moonshine " + label + " expected rank " + expectedRank + " but found " + typed.shape.rank + ".");
            }
            return typed;
        }

        private static void ValidateFeatures(Tensor<float> features)
        {
            var shape = features.shape;
            if (shape.rank != 3 || shape[0] != 1 || shape[1] <= 0 || shape[2] != MoonshineTinyAsrContract.HiddenSize)
            {
                throw new InvalidOperationException(
                    "Moonshine preprocess shape drift: expected [1,T," + MoonshineTinyAsrContract.HiddenSize + "] but found " + shape + ".");
            }
        }

        private static void ValidateEncoderOutput(Tensor<float> encoderOut)
        {
            var shape = encoderOut.shape;
            if (shape.rank != 3 || shape[0] != 1 || shape[1] <= 0 || shape[2] != MoonshineTinyAsrContract.HiddenSize)
            {
                throw new InvalidOperationException(
                    "Moonshine encoder shape drift: expected [1,T," + MoonshineTinyAsrContract.HiddenSize + "] but found " + shape + ".");
            }
        }

        private sealed class Session : IAudioSeq2SeqGenerationSession
        {
            private readonly Worker uncachedWorker;
            private readonly Worker cachedWorker;
            private readonly Tensor<float> encoderOut;
            private readonly string[] uncachedInputs;
            private readonly string[] uncachedOutputs;
            private readonly string[] cachedInputs;
            private readonly string[] cachedOutputs;
            private readonly Tensor<float>[] ownedStates = new Tensor<float>[MoonshineTinyV1OnnxContract.CacheStateCount];
            private bool hasDecoded;
            private bool hasOwnedStates;
            private int tokenLength = 1;
            private bool disposed;

            public Session(
                Worker uncachedWorker,
                Worker cachedWorker,
                Tensor<float> encoderOut,
                string[] uncachedInputs,
                string[] uncachedOutputs,
                string[] cachedInputs,
                string[] cachedOutputs)
            {
                this.uncachedWorker = uncachedWorker ?? throw new ArgumentNullException(nameof(uncachedWorker));
                this.cachedWorker = cachedWorker ?? throw new ArgumentNullException(nameof(cachedWorker));
                this.encoderOut = encoderOut ?? throw new ArgumentNullException(nameof(encoderOut));
                this.uncachedInputs = uncachedInputs ?? throw new ArgumentNullException(nameof(uncachedInputs));
                this.uncachedOutputs = uncachedOutputs ?? throw new ArgumentNullException(nameof(uncachedOutputs));
                this.cachedInputs = cachedInputs ?? throw new ArgumentNullException(nameof(cachedInputs));
                this.cachedOutputs = cachedOutputs ?? throw new ArgumentNullException(nameof(cachedOutputs));
            }

            public Task<AsrDecoderStepResult> DecodeNextAsync(
                int previousTokenId,
                CancellationToken cancellationToken = default(CancellationToken))
            {
                ThrowIfDisposed();
                if (previousTokenId < 0 || previousTokenId >= MoonshineTinyAsrContract.VocabularySize)
                    throw new ArgumentOutOfRangeException(nameof(previousTokenId));
                cancellationToken.ThrowIfCancellationRequested();

                using (var tokenTensor = new Tensor<int>(new TensorShape(1, 1), new[] { previousTokenId }))
                using (var tokenLengthTensor = new Tensor<int>(new TensorShape(1), new[] { tokenLength }))
                {
                    float[] logits;
                    if (!hasDecoded)
                    {
                        logits = DecodeUncached(tokenTensor, tokenLengthTensor);
                        hasDecoded = true;
                    }
                    else
                    {
                        logits = DecodeCached(tokenTensor, tokenLengthTensor);
                    }
                    tokenLength = checked(tokenLength + 1);
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(new AsrDecoderStepResult(logits));
                }
            }

            private float[] DecodeUncached(Tensor<int> tokenTensor, Tensor<int> tokenLengthTensor)
            {
                uncachedWorker.SetInput(uncachedInputs[0], tokenTensor);
                uncachedWorker.SetInput(uncachedInputs[1], encoderOut);
                uncachedWorker.SetInput(uncachedInputs[2], tokenLengthTensor);
                uncachedWorker.Schedule();
                ValidateStateOutputs(uncachedWorker, uncachedOutputs);
                return ReadLogits(uncachedWorker, uncachedOutputs[0]);
            }

            private float[] DecodeCached(Tensor<int> tokenTensor, Tensor<int> tokenLengthTensor)
            {
                cachedWorker.SetInput(cachedInputs[0], tokenTensor);
                cachedWorker.SetInput(cachedInputs[1], encoderOut);
                cachedWorker.SetInput(cachedInputs[2], tokenLengthTensor);

                for (var index = 0; index < MoonshineTinyV1OnnxContract.CacheStateCount; index++)
                {
                    Tensor<float> state;
                    if (hasOwnedStates)
                    {
                        state = ownedStates[index];
                        if (state == null)
                            throw new InvalidOperationException("Moonshine owned cache state is missing at index " + index + ".");
                    }
                    else
                    {
                        state = RequireStateOutput(uncachedWorker, uncachedOutputs[1 + index], index);
                    }
                    cachedWorker.SetInput(cachedInputs[3 + index], state);
                }

                cachedWorker.Schedule();
                var logits = ReadLogits(cachedWorker, cachedOutputs[0]);
                CopyNextStates();
                hasOwnedStates = true;
                return logits;
            }

            private void CopyNextStates()
            {
                var next = new Tensor<float>[MoonshineTinyV1OnnxContract.CacheStateCount];
                try
                {
                    for (var index = 0; index < next.Length; index++)
                    {
                        Tensor copied = null;
                        cachedWorker.CopyOutput(cachedOutputs[1 + index], ref copied);
                        var typed = copied as Tensor<float>;
                        if (typed == null)
                        {
                            copied?.Dispose();
                            throw new InvalidOperationException("Moonshine cache output is not float at index " + index + ".");
                        }
                        ValidateStateShape(typed, index);
                        next[index] = typed;
                    }
                }
                catch
                {
                    for (var index = 0; index < next.Length; index++) next[index]?.Dispose();
                    throw;
                }

                for (var index = 0; index < ownedStates.Length; index++) ownedStates[index]?.Dispose();
                for (var index = 0; index < ownedStates.Length; index++) ownedStates[index] = next[index];
            }

            private static void ValidateStateOutputs(Worker worker, string[] outputNames)
            {
                for (var index = 0; index < MoonshineTinyV1OnnxContract.CacheStateCount; index++)
                    RequireStateOutput(worker, outputNames[1 + index], index);
            }

            private static Tensor<float> RequireStateOutput(Worker worker, string outputName, int index)
            {
                var state = worker.PeekOutput(outputName) as Tensor<float>;
                if (state == null)
                    throw new InvalidOperationException("Moonshine cache output is not float at index " + index + ".");
                ValidateStateShape(state, index);
                return state;
            }

            private static void ValidateStateShape(Tensor<float> state, int index)
            {
                var shape = state.shape;
                if (shape.rank != 4 || shape[0] != 1 || shape[2] != MoonshineTinyAsrContract.DecoderAttentionHeads || shape[3] != 36)
                {
                    throw new InvalidOperationException(
                        "Moonshine cache state " + index + " shape drift: expected [1,T,8,36] but found " + shape + ".");
                }
                if (shape[1] <= 0)
                    throw new InvalidOperationException("Moonshine cache state " + index + " sequence length must be positive.");
            }

            private static float[] ReadLogits(Worker worker, string outputName)
            {
                var output = worker.PeekOutput(outputName) as Tensor<float>;
                if (output == null) throw new InvalidOperationException("Moonshine logits output is not a float tensor.");
                using (var cpu = output.ReadbackAndClone())
                {
                    var shape = cpu.shape;
                    if (shape.rank != 3 || shape[0] != 1 || shape[1] <= 0 || shape[2] != MoonshineTinyAsrContract.VocabularySize)
                    {
                        throw new InvalidOperationException(
                            "Moonshine logits shape drift: expected [1,T," + MoonshineTinyAsrContract.VocabularySize + "] but found " + shape + ".");
                    }
                    var values = cpu.DownloadToArray();
                    var vocabularySize = MoonshineTinyAsrContract.VocabularySize;
                    if (values.Length != checked(shape[1] * vocabularySize))
                        throw new InvalidOperationException("Moonshine logits tensor length does not match its reviewed shape.");
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
                for (var index = 0; index < ownedStates.Length; index++) ownedStates[index]?.Dispose();
                encoderOut.Dispose();
                cachedWorker.Dispose();
                uncachedWorker.Dispose();
            }
        }
    }
#else
    public sealed class UnityMoonshineV1GenerationBackend : IAudioSeq2SeqGenerationBackend, IDisposable
    {
        public bool IsSupported => false;

        public Task<IAudioSeq2SeqGenerationSession> StartAsync(
            AudioChunk monoAudio,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException(
                "Moonshine v1 generation requires com.unity.ai.inference in the reviewed 2.2.x range.");
        }

        public void Dispose()
        {
        }
    }
#endif
}
