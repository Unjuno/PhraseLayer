using System;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class OpusMtSentencePieceContractTests
    {
        [Fact]
        public void MeasuredManifestIsAccepted()
        {
            OpusMtEnJapSentencePieceContract.ValidateMeasuredManifest(CreateMeasuredManifest());
        }

        [Fact]
        public void WrongModelTypeFailsClosed()
        {
            var manifest = new SentencePieceRuntimeManifest(
                "BPE",
                OpusMtEnJapSentencePieceContract.NormalizerName,
                OpusMtEnJapSentencePieceContract.NormalizerCharsMapSha256,
                OpusMtEnJapSentencePieceContract.SourcePieceCount,
                OpusMtEnJapSentencePieceContract.TargetPieceCount,
                OpusMtEnJapSentencePieceContract.ByteFallback,
                OpusMtEnJapSentencePieceContract.AddDummyPrefix,
                OpusMtEnJapSentencePieceContract.RemoveExtraWhitespaces,
                OpusMtEnJapSentencePieceContract.EscapeWhitespaces);

            Assert.Throws<InvalidOperationException>(() =>
                OpusMtEnJapSentencePieceContract.ValidateMeasuredManifest(manifest));
        }

        [Fact]
        public void NormalizerCharsMapIdentityDriftFailsClosed()
        {
            var manifest = new SentencePieceRuntimeManifest(
                OpusMtEnJapSentencePieceContract.ModelType,
                OpusMtEnJapSentencePieceContract.NormalizerName,
                new string('0', 64),
                OpusMtEnJapSentencePieceContract.SourcePieceCount,
                OpusMtEnJapSentencePieceContract.TargetPieceCount,
                OpusMtEnJapSentencePieceContract.ByteFallback,
                OpusMtEnJapSentencePieceContract.AddDummyPrefix,
                OpusMtEnJapSentencePieceContract.RemoveExtraWhitespaces,
                OpusMtEnJapSentencePieceContract.EscapeWhitespaces);

            Assert.Throws<InvalidOperationException>(() =>
                OpusMtEnJapSentencePieceContract.ValidateMeasuredManifest(manifest));
        }

        [Fact]
        public void PieceCountDriftFailsClosed()
        {
            var manifest = new SentencePieceRuntimeManifest(
                OpusMtEnJapSentencePieceContract.ModelType,
                OpusMtEnJapSentencePieceContract.NormalizerName,
                OpusMtEnJapSentencePieceContract.NormalizerCharsMapSha256,
                OpusMtEnJapSentencePieceContract.SourcePieceCount - 1,
                OpusMtEnJapSentencePieceContract.TargetPieceCount,
                OpusMtEnJapSentencePieceContract.ByteFallback,
                OpusMtEnJapSentencePieceContract.AddDummyPrefix,
                OpusMtEnJapSentencePieceContract.RemoveExtraWhitespaces,
                OpusMtEnJapSentencePieceContract.EscapeWhitespaces);

            Assert.Throws<InvalidOperationException>(() =>
                OpusMtEnJapSentencePieceContract.ValidateMeasuredManifest(manifest));
        }

        [Fact]
        public void ReportContainsMeasuredIdentity()
        {
            var report = OpusMtEnJapSentencePieceContract.BuildReport();

            Assert.Contains("UNIGRAM", report, StringComparison.Ordinal);
            Assert.Contains("15882", report, StringComparison.Ordinal);
            Assert.Contains("32000", report, StringComparison.Ordinal);
            Assert.Contains("nmt_nfkc", report, StringComparison.Ordinal);
            Assert.Contains(OpusMtEnJapSentencePieceContract.NormalizerCharsMapSha256, report, StringComparison.Ordinal);
        }

        private static SentencePieceRuntimeManifest CreateMeasuredManifest()
        {
            return new SentencePieceRuntimeManifest(
                OpusMtEnJapSentencePieceContract.ModelType,
                OpusMtEnJapSentencePieceContract.NormalizerName,
                OpusMtEnJapSentencePieceContract.NormalizerCharsMapSha256,
                OpusMtEnJapSentencePieceContract.SourcePieceCount,
                OpusMtEnJapSentencePieceContract.TargetPieceCount,
                OpusMtEnJapSentencePieceContract.ByteFallback,
                OpusMtEnJapSentencePieceContract.AddDummyPrefix,
                OpusMtEnJapSentencePieceContract.RemoveExtraWhitespaces,
                OpusMtEnJapSentencePieceContract.EscapeWhitespaces);
        }
    }
}
