using System;
using System.Collections.Generic;
using System.Linq;
using PhraseLayer.Core.Audio;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
using UnityEngine;
#endif

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Small immutable result used by editor/Quest parity gates. It deliberately exposes generated token ids,
    /// because transcript-only comparison can hide tokenizer or early-decoder divergence.
    /// </summary>
    public sealed class UnityMoonshineParityResult
    {
        public UnityMoonshineParityResult(
            string transcript,
            IReadOnlyList<int> tokenIds,
            bool terminatedByEos,
            int decoderSteps)
        {
            Transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
            if (tokenIds == null) throw new ArgumentNullException(nameof(tokenIds));
            TokenIds = tokenIds.ToArray();
            TerminatedByEos = terminatedByEos;
            DecoderSteps = decoderSteps;
        }

        public string Transcript { get; }
        public IReadOnlyList<int> TokenIds { get; }
        public bool TerminatedByEos { get; }
        public int DecoderSteps { get; }
    }

    public static class UnityMoonshineParityProbe
    {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        public static UnityMoonshineParityResult Run(
            ModelAsset preprocessModel,
            ModelAsset encoderModel,
            ModelAsset uncachedDecoderModel,
            ModelAsset cachedDecoderModel,
            TextAsset tokenDecoderAsset,
            byte[] wavBytes,
            BackendType backendType = BackendType.CPU,
            int maximumGenerationLength = MoonshineTinyAsrContract.MaximumGenerationLength)
        {
            if (preprocessModel == null) throw new ArgumentNullException(nameof(preprocessModel));
            if (encoderModel == null) throw new ArgumentNullException(nameof(encoderModel));
            if (uncachedDecoderModel == null) throw new ArgumentNullException(nameof(uncachedDecoderModel));
            if (cachedDecoderModel == null) throw new ArgumentNullException(nameof(cachedDecoderModel));
            if (tokenDecoderAsset == null || tokenDecoderAsset.bytes == null || tokenDecoderAsset.bytes.Length == 0)
                throw new ArgumentException("Moonshine parity probe requires a non-empty token decoder asset.", nameof(tokenDecoderAsset));
            if (wavBytes == null || wavBytes.Length == 0)
                throw new ArgumentException("Moonshine parity probe requires non-empty WAV bytes.", nameof(wavBytes));

            var decodedAudio = WaveAudioDecoder.Decode(wavBytes);
            var preparedAudio = AudioChunkPreprocessor.PrepareMono(
                decodedAudio,
                MoonshineTinyAsrContract.RequiredSampleRate);
            var tokenDecoder = new MoonshineBinaryTokenDecoder(tokenDecoderAsset.bytes);

            using (var backend = new UnityMoonshineV1GenerationBackend(
                preprocessModel,
                encoderModel,
                uncachedDecoderModel,
                cachedDecoderModel,
                backendType))
            {
                var runtime = new MoonshineGreedyAsrRuntime(
                    backend,
                    tokenDecoder,
                    maximumGenerationLength);
                var trace = runtime
                    .TranscribePreparedWithTraceAsync(preparedAudio)
                    .GetAwaiter()
                    .GetResult();
                return new UnityMoonshineParityResult(
                    trace.Observation.Text,
                    trace.TokenIds,
                    trace.TerminatedByEos,
                    trace.DecoderSteps);
            }
        }
#else
        public static bool IsSupported => false;
#endif
    }
}
