using System;
using System.Collections.Generic;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class TranslationModelContractTests
    {
        private const string Revision = "0123456789abcdef0123456789abcdef01234567";
        private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Fact]
        public void ValidMarianBundlePasses()
        {
            var manifest = CreateManifest(CreateRequiredArtifacts());

            var report = LocalTranslationModelContract.ValidateAndBuildReport(manifest);

            Assert.Contains("architecture=marian", report);
            Assert.Contains("source=en", report);
            Assert.Contains("target=ja", report);
            Assert.Contains("artifacts=6", report);
        }

        [Fact]
        public void MissingTokenizerArtifactFailsClosed()
        {
            var artifacts = CreateRequiredArtifacts();
            artifacts.RemoveAll(item => item.Role == TranslationArtifactRole.SourceSentencePiece);
            var manifest = CreateManifest(artifacts);

            var error = Assert.Throws<InvalidOperationException>(
                () => LocalTranslationModelContract.ValidateAndBuildReport(manifest));

            Assert.Contains("SourceSentencePiece", error.Message);
        }

        [Fact]
        public void ShortUpstreamRevisionIsRejected()
        {
            var manifest = CreateManifest(CreateRequiredArtifacts(), upstreamRevision: "a863894");

            var error = Assert.Throws<InvalidOperationException>(
                () => LocalTranslationModelContract.ValidateAndBuildReport(manifest));

            Assert.Contains("full 40-character Git SHA", error.Message);
        }

        [Fact]
        public void WrongLanguageDirectionIsRejected()
        {
            var manifest = CreateManifest(CreateRequiredArtifacts(), targetLanguage: "en");

            var error = Assert.Throws<InvalidOperationException>(
                () => LocalTranslationModelContract.ValidateAndBuildReport(manifest));

            Assert.Contains("target language", error.Message);
        }

        [Fact]
        public void DuplicateArtifactRoleIsRejected()
        {
            var artifacts = CreateRequiredArtifacts();
            artifacts.Add(new TranslationArtifactDescriptor(
                TranslationArtifactRole.EncoderOnnx,
                "encoder-duplicate.onnx",
                20,
                Digest));
            var manifest = CreateManifest(artifacts);

            var error = Assert.Throws<InvalidOperationException>(
                () => LocalTranslationModelContract.ValidateAndBuildReport(manifest));

            Assert.Contains("Duplicate translation artifact role", error.Message);
        }

        [Fact]
        public void ArtifactHashMustBePinned()
        {
            Assert.Throws<ArgumentException>(() => new TranslationArtifactDescriptor(
                TranslationArtifactRole.EncoderOnnx,
                "encoder.onnx",
                10,
                "not-a-sha"));
        }

        private static LocalTranslationModelManifest CreateManifest(
            IReadOnlyList<TranslationArtifactDescriptor> artifacts,
            string upstreamRevision = Revision,
            string targetLanguage = "ja")
        {
            return new LocalTranslationModelManifest(
                LocalTranslationModelManifest.CurrentSchemaVersion,
                "opus-mt-en-jap-unity-export",
                upstreamRevision,
                "marian",
                "en",
                targetLanguage,
                LocalTranslationModelContract.ExpectedRuntimeTarget,
                decoderStartTokenId: 46275,
                eosTokenId: 0,
                padTokenId: 46275,
                maxLength: 512,
                numBeams: 4,
                artifacts: artifacts);
        }

        private static List<TranslationArtifactDescriptor> CreateRequiredArtifacts()
        {
            return new List<TranslationArtifactDescriptor>
            {
                new TranslationArtifactDescriptor(TranslationArtifactRole.EncoderOnnx, "encoder_model.onnx", 10, Digest),
                new TranslationArtifactDescriptor(TranslationArtifactRole.DecoderOnnx, "decoder_model.onnx", 10, Digest),
                new TranslationArtifactDescriptor(TranslationArtifactRole.SourceSentencePiece, "source.spm", 10, Digest),
                new TranslationArtifactDescriptor(TranslationArtifactRole.TargetSentencePiece, "target.spm", 10, Digest),
                new TranslationArtifactDescriptor(TranslationArtifactRole.VocabularyJson, "vocab.json", 10, Digest),
                new TranslationArtifactDescriptor(TranslationArtifactRole.GenerationConfigJson, "generation_config.json", 10, Digest),
            };
        }
    }
}
