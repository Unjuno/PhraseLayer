using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleOcrDictionaryManifestTests
    {
        [Fact]
        public void ValidManifestBuildsPinnedReport()
        {
            var manifest = CreateManifest();

            var report = PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                manifest,
                actualRawTokenCount: PaddleOcrDictionaryManifestContract.ExpectedRawTokenCount,
                configuredUseSpaceCharacter: true,
                actualDictionarySha256: PaddleOcrDictionaryManifestContract.ExpectedGeneratedSha256);

            Assert.Contains(PaddleOcrDictionaryManifestContract.ExpectedModelId, report, StringComparison.Ordinal);
            Assert.Contains(PaddleOcrDictionaryManifestContract.ExpectedRevision, report, StringComparison.Ordinal);
            Assert.Contains("source=inference.yml", report, StringComparison.Ordinal);
            Assert.Contains("raw=6904", report, StringComparison.Ordinal);
            Assert.Contains("effective=6905", report, StringComparison.Ordinal);
            Assert.Contains("use_space_char=true", report, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsRevisionDrift()
        {
            var manifest = CreateManifest(revision: new string('0', 40));

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest,
                    PaddleOcrDictionaryManifestContract.ExpectedRawTokenCount,
                    true,
                    PaddleOcrDictionaryManifestContract.ExpectedGeneratedSha256));

            Assert.Contains("revision mismatch", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsPinnedRawTokenCountDriftEvenWhenAssignedDictionaryMatchesManifest()
        {
            var manifest = CreateManifest(rawTokenCount: 6903, effectiveTokenCount: 6904);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest,
                    actualRawTokenCount: 6903,
                    configuredUseSpaceCharacter: true,
                    actualDictionarySha256: PaddleOcrDictionaryManifestContract.ExpectedGeneratedSha256));

            Assert.Contains("raw_token_count mismatch", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAssignedDictionaryTokenCountDrift()
        {
            var manifest = CreateManifest();

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest,
                    actualRawTokenCount: 6903,
                    configuredUseSpaceCharacter: true,
                    actualDictionarySha256: PaddleOcrDictionaryManifestContract.ExpectedGeneratedSha256));

            Assert.Contains("assigned dictionary", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsUseSpaceCharacterDrift()
        {
            var manifest = CreateManifest();

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest,
                    PaddleOcrDictionaryManifestContract.ExpectedRawTokenCount,
                    false,
                    PaddleOcrDictionaryManifestContract.ExpectedGeneratedSha256));

            Assert.Contains("useSpaceCharacter", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsManifestUseSpaceCharDrift()
        {
            var manifest = CreateManifest(useSpaceChar: false);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest,
                    PaddleOcrDictionaryManifestContract.ExpectedRawTokenCount,
                    true,
                    PaddleOcrDictionaryManifestContract.ExpectedGeneratedSha256));

            Assert.Contains("use_space_char mismatch", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAmbiguousRawSpace()
        {
            var manifest = CreateManifest(rawContainsLiteralSpace: true);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest,
                    PaddleOcrDictionaryManifestContract.ExpectedRawTokenCount,
                    true,
                    PaddleOcrDictionaryManifestContract.ExpectedGeneratedSha256));

            Assert.Contains("literal single-space", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsPinnedEffectiveTokenCountDrift()
        {
            var manifest = CreateManifest(effectiveTokenCount: 6904);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest,
                    PaddleOcrDictionaryManifestContract.ExpectedRawTokenCount,
                    true,
                    PaddleOcrDictionaryManifestContract.ExpectedGeneratedSha256));

            Assert.Contains("effective_token_count mismatch", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsPinnedGeneratedDigestDrift()
        {
            var manifest = CreateManifest(generatedSha256: new string('f', 64));

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest,
                    PaddleOcrDictionaryManifestContract.ExpectedRawTokenCount,
                    true,
                    new string('f', 64)));

            Assert.Contains("generated_sha256 mismatch", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAssignedDictionaryDigestMismatch()
        {
            var manifest = CreateManifest();
            var actual = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest,
                    PaddleOcrDictionaryManifestContract.ExpectedRawTokenCount,
                    true,
                    actual));

            Assert.Contains("Dictionary SHA-256", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsUppercaseDigestEvenWhenCharactersAreHex()
        {
            var uppercase = PaddleOcrDictionaryManifestContract.ExpectedGeneratedSha256.ToUpperInvariant();
            var manifest = CreateManifest(generatedSha256: uppercase);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest,
                    PaddleOcrDictionaryManifestContract.ExpectedRawTokenCount,
                    true,
                    uppercase));

            Assert.Contains("lowercase hexadecimal", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsOldJsonSourceArtifact()
        {
            var manifest = CreateManifest(sourceArtifact: "inference.json");

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                    manifest,
                    PaddleOcrDictionaryManifestContract.ExpectedRawTokenCount,
                    true,
                    PaddleOcrDictionaryManifestContract.ExpectedGeneratedSha256));

            Assert.Contains("source_artifact mismatch", exception.Message, StringComparison.Ordinal);
        }

        private static PaddleOcrDictionaryManifest CreateManifest(
            string revision = PaddleOcrDictionaryManifestContract.ExpectedRevision,
            string sourceArtifact = PaddleOcrDictionaryManifestContract.ExpectedSourceArtifact,
            int rawTokenCount = PaddleOcrDictionaryManifestContract.ExpectedRawTokenCount,
            bool rawContainsLiteralSpace = false,
            bool useSpaceChar = PaddleOcrDictionaryManifestContract.ExpectedUseSpaceChar,
            int effectiveTokenCount = PaddleOcrDictionaryManifestContract.ExpectedEffectiveTokenCount,
            string generatedSha256 = PaddleOcrDictionaryManifestContract.ExpectedGeneratedSha256)
        {
            return new PaddleOcrDictionaryManifest(
                PaddleOcrDictionaryManifestContract.ExpectedSchemaVersion,
                PaddleOcrDictionaryManifestContract.ExpectedModelId,
                PaddleOcrDictionaryManifestContract.ExpectedUpstream,
                revision,
                sourceArtifact,
                PaddleOcrDictionaryManifestContract.ExpectedPostprocessName,
                rawTokenCount,
                rawContainsLiteralSpace,
                useSpaceChar,
                effectiveTokenCount,
                PaddleOcrDictionaryManifestContract.ExpectedGeneratedArtifact,
                generatedSha256);
        }
    }
}
