using System.Collections.Generic;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class LocalTranslationBootstrapStagingTests
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Fact]
        public void GeneratedTokenizerArtifactsResolveOnlyWhenBothArePresent()
        {
            var manifest = BuildManifest(includeFixtures: true);

            var generated = LocalTranslationStagingContract.ValidateAndResolveBootstrapArtifacts(manifest);

            Assert.Equal(LocalTranslationStagingContract.ManagedTokenizerManifestPath, generated.ManagedTokenizerManifest.Path);
            Assert.Equal(LocalTranslationStagingContract.TokenizerFixtureManifestPath, generated.TokenizerFixtureManifest.Path);
            Assert.Equal("generated", generated.ManagedTokenizerManifest.Kind);
        }

        [Fact]
        public void MissingTokenizerFixturesFailsClosed()
        {
            var manifest = BuildManifest(includeFixtures: false);

            var error = Assert.Throws<System.InvalidOperationException>(
                () => LocalTranslationStagingContract.ValidateAndResolveBootstrapArtifacts(manifest));

            Assert.Contains(LocalTranslationStagingContract.TokenizerFixtureManifestPath, error.Message);
        }

        private static StagedTranslationManifest BuildManifest(bool includeFixtures)
        {
            var files = new List<StagedTranslationAsset>
            {
                Asset(LocalTranslationStagingContract.EncoderPath, "onnx"),
                Asset(LocalTranslationStagingContract.DecoderPath, "onnx"),
                Asset(LocalTranslationStagingContract.SourceSentencePiecePath, "support"),
                Asset(LocalTranslationStagingContract.TargetSentencePiecePath, "support"),
                Asset(LocalTranslationStagingContract.VocabularyPath, "support"),
                Asset(LocalTranslationStagingContract.GenerationConfigPath, "support"),
                Asset(LocalTranslationStagingContract.ManagedTokenizerManifestPath, "generated"),
            };
            if (includeFixtures)
                files.Add(Asset(LocalTranslationStagingContract.TokenizerFixtureManifestPath, "generated"));

            return new StagedTranslationManifest(
                StagedTranslationManifest.CurrentSchemaVersion,
                LocalTranslationStagingContract.ExpectedModelId,
                LocalTranslationStagingContract.ExpectedRevision,
                referenceParityExact: true,
                LocalTranslationStagingContract.ExpectedRuntimeStatus,
                files);
        }

        private static StagedTranslationAsset Asset(string path, string kind)
        {
            return new StagedTranslationAsset(path, 1, Hash, kind);
        }
    }
}
