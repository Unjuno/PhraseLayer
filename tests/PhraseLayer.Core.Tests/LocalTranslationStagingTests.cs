using System;
using System.Collections.Generic;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class LocalTranslationStagingTests
    {
        private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Fact]
        public void ValidParityVerifiedBundleResolvesReferenceRuntimeSet()
        {
            var manifest = CreateManifest(CreateRequiredFiles());

            var runtime = LocalTranslationStagingContract.ValidateAndResolve(manifest);
            var report = LocalTranslationStagingContract.ValidateAndBuildReport(manifest);

            Assert.Equal(LocalTranslationStagingContract.EncoderPath, runtime.Encoder.Path);
            Assert.Equal(LocalTranslationStagingContract.MergedDecoderPath, runtime.MergedDecoder.Path);
            Assert.Contains("parity=exact", report);
            Assert.Contains("decoder=decoder_model_merged.onnx", report);
        }

        [Fact]
        public void NonExactReferenceParityFailsClosed()
        {
            var manifest = CreateManifest(CreateRequiredFiles(), referenceParityExact: false);

            var error = Assert.Throws<InvalidOperationException>(
                () => LocalTranslationStagingContract.ValidateAndResolve(manifest));

            Assert.Contains("reference parity", error.Message);
        }

        [Fact]
        public void MissingMergedDecoderFailsClosed()
        {
            var files = CreateRequiredFiles();
            files.RemoveAll(item => item.Path == LocalTranslationStagingContract.MergedDecoderPath);

            var error = Assert.Throws<InvalidOperationException>(
                () => LocalTranslationStagingContract.ValidateAndResolve(CreateManifest(files)));

            Assert.Contains("decoder_model_merged.onnx", error.Message);
        }

        [Fact]
        public void TraversalPathIsRejectedEvenWhenRequiredFilesExist()
        {
            var files = CreateRequiredFiles();
            files.Add(new StagedTranslationAsset("../escape.json", 10, Digest, "support"));

            var error = Assert.Throws<InvalidOperationException>(
                () => LocalTranslationStagingContract.ValidateAndResolve(CreateManifest(files)));

            Assert.Contains("not canonical", error.Message);
        }

        [Fact]
        public void OnnxKindMustMatchOnnxExtension()
        {
            var files = CreateRequiredFiles();
            files.RemoveAll(item => item.Path == LocalTranslationStagingContract.EncoderPath);
            files.Add(new StagedTranslationAsset(
                LocalTranslationStagingContract.EncoderPath,
                10,
                Digest,
                "support"));

            var error = Assert.Throws<InvalidOperationException>(
                () => LocalTranslationStagingContract.ValidateAndResolve(CreateManifest(files)));

            Assert.Contains("kind does not match", error.Message);
        }

        private static StagedTranslationManifest CreateManifest(
            IReadOnlyList<StagedTranslationAsset> files,
            bool referenceParityExact = true)
        {
            return new StagedTranslationManifest(
                StagedTranslationManifest.CurrentSchemaVersion,
                LocalTranslationStagingContract.ExpectedModelId,
                LocalTranslationStagingContract.ExpectedRevision,
                referenceParityExact,
                LocalTranslationStagingContract.ExpectedRuntimeStatus,
                files);
        }

        private static List<StagedTranslationAsset> CreateRequiredFiles()
        {
            return new List<StagedTranslationAsset>
            {
                new StagedTranslationAsset(LocalTranslationStagingContract.EncoderPath, 10, Digest, "onnx"),
                new StagedTranslationAsset(LocalTranslationStagingContract.MergedDecoderPath, 10, Digest, "onnx"),
                new StagedTranslationAsset(LocalTranslationStagingContract.SourceSentencePiecePath, 10, Digest, "support"),
                new StagedTranslationAsset(LocalTranslationStagingContract.TargetSentencePiecePath, 10, Digest, "support"),
                new StagedTranslationAsset(LocalTranslationStagingContract.VocabularyPath, 10, Digest, "support"),
                new StagedTranslationAsset(LocalTranslationStagingContract.GenerationConfigPath, 10, Digest, "support"),
            };
        }
    }
}
