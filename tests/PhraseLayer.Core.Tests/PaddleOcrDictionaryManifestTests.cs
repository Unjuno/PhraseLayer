using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleOcrDictionaryManifestTests
    {
        private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Fact]
        public void ValidManifestBuildsPinnedReport()
        {
            var manifest = CreateManifest();

            var report = PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                manifest,
                actualRawTokenCount: 100,
                configuredUseSpaceCharacter: true,
                actualDictionarySha256: Digest);

            Assert.Contains(PaddleOcrDictionaryManifestContract.ExpectedModelId, report, StringComparison.Ordinal);
            Assert.Contains(PaddleOcrDictionaryManifestContract.ExpectedRevision, report, StringComparison.Ordinal);
            Assert.Contains("raw=100", report, StringComparison.Ordinal);
            Assert.Contains("effective=101", report, StringComparison.Ordinal);
            Assert.Contains("use_space_char=true", report, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsRevisionDrift()
        {
            var manifest = CreateManifest(revision: new string('0', 40));

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest, 100, true, Digest));

            Assert.Contains("revision mismatch", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsRawTokenCountDrift()
        {
            var manifest = CreateManifest();

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest, 99, true, Digest));

            Assert.Contains("raw_token_count", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsUseSpaceCharacterDrift()
        {
            var manifest = CreateManifest();

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest, 100, false, Digest));

            Assert.Contains("use_space_char", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAmbiguousRawSpaceWhenPaddleWouldAppendSpace()
        {
            var manifest = CreateManifest(rawContainsLiteralSpace: true);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest, 100, true, Digest));

            Assert.Contains("literal single-space", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsEffectiveTokenCountDrift()
        {
            var manifest = CreateManifest(effectiveTokenCount: 100);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest, 100, true, Digest));

            Assert.Contains("effective_token_count", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsDictionaryDigestMismatch()
        {
            var manifest = CreateManifest();
            var actual = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest, 100, true, actual));

            Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsUppercaseDigestEvenWhenCharactersAreHex()
        {
            var uppercase = Digest.ToUpperInvariant();
            var manifest = CreateManifest(generatedSha256: uppercase);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest, 100, true, uppercase));

            Assert.Contains("lowercase hexadecimal", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AllowsRawLiteralSpaceWhenUseSpaceCharIsFalse()
        {
            var manifest = CreateManifest(
                rawContainsLiteralSpace: true,
                useSpaceChar: false,
                effectiveTokenCount: 100);

            var report = PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                manifest,
                actualRawTokenCount: 100,
                configuredUseSpaceCharacter: false,
                actualDictionarySha256: Digest);

            Assert.Contains("effective=100", report, StringComparison.Ordinal);
            Assert.Contains("use_space_char=false", report, StringComparison.Ordinal);
        }

        private static PaddleOcrDictionaryManifest CreateManifest(
            string revision = PaddleOcrDictionaryManifestContract.ExpectedRevision,
            bool rawContainsLiteralSpace = false,
            bool useSpaceChar = true,
            int effectiveTokenCount = 101,
            string generatedSha256 = Digest)
        {
            return new PaddleOcrDictionaryManifest(
                PaddleOcrDictionaryManifestContract.ExpectedSchemaVersion,
                PaddleOcrDictionaryManifestContract.ExpectedModelId,
                PaddleOcrDictionaryManifestContract.ExpectedUpstream,
                revision,
                PaddleOcrDictionaryManifestContract.ExpectedSourceArtifact,
                PaddleOcrDictionaryManifestContract.ExpectedPostprocessName,
                rawTokenCount: 100,
                rawContainsLiteralSpace,
                useSpaceChar,
                effectiveTokenCount,
                PaddleOcrDictionaryManifestContract.ExpectedGeneratedArtifact,
                generatedSha256);
        }
    }
}
