using System;
using System.Collections.Generic;
using PhraseLayer.Core.Audio;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class MoonshineOnnxGraphContractTests
    {
        [Fact]
        public void ReviewedFourGraphBundlePasses()
        {
            var bundle = ReviewedBundle();

            var report = MoonshineTinyV1OnnxContract.ValidateBundle(
                bundle.preprocess,
                bundle.encoder,
                bundle.uncached,
                bundle.cached);

            Assert.Equal(24, MoonshineTinyV1OnnxContract.CacheStateCount);
            Assert.Equal(25, report.UncachedDecoder.Outputs.Count);
            Assert.Equal(27, report.CachedDecoder.Inputs.Count);
            Assert.Contains("cache_states=24", report.ToString());
        }

        [Fact]
        public void UnknownStaticMetadataIsAllowedWhenHostDoesNotExposeIt()
        {
            var bundle = ReviewedBundle(unknown: true);
            MoonshineTinyV1OnnxContract.ValidateBundle(
                bundle.preprocess,
                bundle.encoder,
                bundle.uncached,
                bundle.cached);
        }

        [Fact]
        public void MissingCacheStateFailsBecauseAbiIsPositional()
        {
            var bundle = ReviewedBundle();
            var cachedInputs = new List<MoonshineOnnxTensorSignature>(bundle.cached.Inputs);
            cachedInputs.RemoveAt(cachedInputs.Count - 1);
            var cached = new MoonshineOnnxGraphSignature("cached_decode", cachedInputs, bundle.cached.Outputs);

            var error = Assert.Throws<InvalidOperationException>(() =>
                MoonshineTinyV1OnnxContract.ValidateBundle(bundle.preprocess, bundle.encoder, bundle.uncached, cached));

            Assert.Contains("expected 27 inputs/25 outputs", error.Message);
        }

        [Fact]
        public void ExtraDecoderOutputFailsBecauseStateOrderingWouldDrift()
        {
            var bundle = ReviewedBundle();
            var outputs = new List<MoonshineOnnxTensorSignature>(bundle.uncached.Outputs)
            {
                Float("unexpected", 4)
            };
            var uncached = new MoonshineOnnxGraphSignature("uncached_decode", bundle.uncached.Inputs, outputs);

            Assert.Throws<InvalidOperationException>(() =>
                MoonshineTinyV1OnnxContract.ValidateBundle(bundle.preprocess, bundle.encoder, uncached, bundle.cached));
        }

        [Fact]
        public void WrongTokenAndLengthTypesFail()
        {
            var bundle = ReviewedBundle();
            var uncachedInputs = new List<MoonshineOnnxTensorSignature>(bundle.uncached.Inputs)
            {
                [0] = Float("token", 2)
            };
            var uncached = new MoonshineOnnxGraphSignature("uncached_decode", uncachedInputs, bundle.uncached.Outputs);
            Assert.Contains(
                "token input dtype drift",
                Assert.Throws<InvalidOperationException>(() =>
                    MoonshineTinyV1OnnxContract.ValidateBundle(bundle.preprocess, bundle.encoder, uncached, bundle.cached)).Message);

            var encoderInputs = new List<MoonshineOnnxTensorSignature>(bundle.encoder.Inputs)
            {
                [1] = Float("features_len", 1)
            };
            var encoder = new MoonshineOnnxGraphSignature("encode", encoderInputs, bundle.encoder.Outputs);
            Assert.Contains(
                "feature-length input dtype drift",
                Assert.Throws<InvalidOperationException>(() =>
                    MoonshineTinyV1OnnxContract.ValidateBundle(bundle.preprocess, encoder, bundle.uncached, bundle.cached)).Message);
        }

        [Fact]
        public void WrongCacheRankFailsWhenKnown()
        {
            var bundle = ReviewedBundle();
            var cachedInputs = new List<MoonshineOnnxTensorSignature>(bundle.cached.Inputs)
            {
                [3] = Float("state_0", 3)
            };
            var cached = new MoonshineOnnxGraphSignature("cached_decode", cachedInputs, bundle.cached.Outputs);

            Assert.Contains(
                "state input 0 rank drift",
                Assert.Throws<InvalidOperationException>(() =>
                    MoonshineTinyV1OnnxContract.ValidateBundle(bundle.preprocess, bundle.encoder, bundle.uncached, cached)).Message);
        }

        private static (
            MoonshineOnnxGraphSignature preprocess,
            MoonshineOnnxGraphSignature encoder,
            MoonshineOnnxGraphSignature uncached,
            MoonshineOnnxGraphSignature cached) ReviewedBundle(bool unknown = false)
        {
            Func<string, int, MoonshineOnnxTensorSignature> f = unknown
                ? (name, rank) => new MoonshineOnnxTensorSignature(name)
                : Float;
            Func<string, int, MoonshineOnnxTensorSignature> i = unknown
                ? (name, rank) => new MoonshineOnnxTensorSignature(name)
                : Integer;

            var preprocess = new MoonshineOnnxGraphSignature(
                "preprocess",
                new[] { f("audio", 2) },
                new[] { f("features", 3) });
            var encoder = new MoonshineOnnxGraphSignature(
                "encode",
                new[] { f("features", 3), i("features_len", 1) },
                new[] { f("encoder_out", 3) });

            var uncachedOutputs = new List<MoonshineOnnxTensorSignature> { f("logits", 3) };
            for (var index = 0; index < MoonshineTinyV1OnnxContract.CacheStateCount; index++)
                uncachedOutputs.Add(f("state_" + index, 4));
            var uncached = new MoonshineOnnxGraphSignature(
                "uncached_decode",
                new[] { i("token", 2), f("encoder_out", 3), i("token_len", 1) },
                uncachedOutputs);

            var cachedInputs = new List<MoonshineOnnxTensorSignature>
            {
                i("token", 2),
                f("encoder_out", 3),
                i("token_len", 1)
            };
            for (var index = 0; index < MoonshineTinyV1OnnxContract.CacheStateCount; index++)
                cachedInputs.Add(f("state_" + index, 4));
            var cachedOutputs = new List<MoonshineOnnxTensorSignature> { f("logits", 3) };
            for (var index = 0; index < MoonshineTinyV1OnnxContract.CacheStateCount; index++)
                cachedOutputs.Add(f("next_state_" + index, 4));
            var cached = new MoonshineOnnxGraphSignature("cached_decode", cachedInputs, cachedOutputs);

            return (preprocess, encoder, uncached, cached);
        }

        private static MoonshineOnnxTensorSignature Float(string name, int rank)
        {
            return new MoonshineOnnxTensorSignature(name, MoonshineOnnxTensorElementType.Float, rank);
        }

        private static MoonshineOnnxTensorSignature Integer(string name, int rank)
        {
            return new MoonshineOnnxTensorSignature(name, MoonshineOnnxTensorElementType.Integer, rank);
        }
    }
}
