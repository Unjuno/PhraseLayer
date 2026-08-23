using System;
using System.Collections.Generic;

namespace PhraseLayer.Core.Translation
{
    public sealed class StagedTranslationAsset
    {
        public StagedTranslationAsset(string path, long sizeBytes, string sha256, string kind)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            if (sizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
            if (!IsSha256(sha256)) throw new ArgumentException("Asset SHA-256 must be 64 hexadecimal characters.", nameof(sha256));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));

            SizeBytes = sizeBytes;
            Sha256 = sha256.ToLowerInvariant();
        }

        public string Path { get; }
        public long SizeBytes { get; }
        public string Sha256 { get; }
        public string Kind { get; }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                var hexadecimal =
                    (character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F');
                if (!hexadecimal) return false;
            }
            return true;
        }
    }

    public sealed class StagedTranslationManifest
    {
        public const int CurrentSchemaVersion = 1;

        public StagedTranslationManifest(
            int schemaVersion,
            string modelId,
            string revision,
            bool referenceParityExact,
            string runtimeStatus,
            IReadOnlyList<StagedTranslationAsset> files)
        {
            SchemaVersion = schemaVersion;
            ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
            Revision = revision ?? throw new ArgumentNullException(nameof(revision));
            ReferenceParityExact = referenceParityExact;
            RuntimeStatus = runtimeStatus ?? throw new ArgumentNullException(nameof(runtimeStatus));
            Files = files ?? throw new ArgumentNullException(nameof(files));
        }

        public int SchemaVersion { get; }
        public string ModelId { get; }
        public string Revision { get; }
        public bool ReferenceParityExact { get; }
        public string RuntimeStatus { get; }
        public IReadOnlyList<StagedTranslationAsset> Files { get; }
    }

    public sealed class LocalTranslationRuntimeSet
    {
        public LocalTranslationRuntimeSet(
            StagedTranslationAsset encoder,
            StagedTranslationAsset decoder,
            StagedTranslationAsset sourceSentencePiece,
            StagedTranslationAsset targetSentencePiece,
            StagedTranslationAsset vocabulary,
            StagedTranslationAsset generationConfig)
        {
            Encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
            Decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
            SourceSentencePiece = sourceSentencePiece ?? throw new ArgumentNullException(nameof(sourceSentencePiece));
            TargetSentencePiece = targetSentencePiece ?? throw new ArgumentNullException(nameof(targetSentencePiece));
            Vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
            GenerationConfig = generationConfig ?? throw new ArgumentNullException(nameof(generationConfig));
        }

        public StagedTranslationAsset Encoder { get; }
        public StagedTranslationAsset Decoder { get; }
        public StagedTranslationAsset SourceSentencePiece { get; }
        public StagedTranslationAsset TargetSentencePiece { get; }
        public StagedTranslationAsset Vocabulary { get; }
        public StagedTranslationAsset GenerationConfig { get; }
    }

    /// <summary>
    /// Generated, parity-gated runtime support that is derived locally from the pinned tokenizer/probe metadata.
    /// These files contain no remote endpoint and are hash-locked in the local staging manifest just like model
    /// artifacts. The bootstrap refuses to construct the official local translation engine without both files.
    /// </summary>
    public sealed class LocalTranslationBootstrapArtifacts
    {
        public LocalTranslationBootstrapArtifacts(
            StagedTranslationAsset managedTokenizerManifest,
            StagedTranslationAsset tokenizerFixtureManifest)
        {
            ManagedTokenizerManifest = managedTokenizerManifest ?? throw new ArgumentNullException(nameof(managedTokenizerManifest));
            TokenizerFixtureManifest = tokenizerFixtureManifest ?? throw new ArgumentNullException(nameof(tokenizerFixtureManifest));
        }

        public StagedTranslationAsset ManagedTokenizerManifest { get; }
        public StagedTranslationAsset TokenizerFixtureManifest { get; }
    }

    public static class LocalTranslationStagingContract
    {
        public const string ExpectedModelId = "Helsinki-NLP/opus-mt-en-jap";
        public const string ExpectedRevision = "a863894cdd2b80f3bc1c5966734aee9ffec207d1";
        public const string ExpectedRuntimeStatus = "unverified-real-unity-import-required";

        public const string EncoderPath = "encoder_model.onnx";
        public const string DecoderPath = "decoder_model.onnx";
        public const string SourceSentencePiecePath = "source.spm";
        public const string TargetSentencePiecePath = "target.spm";
        public const string VocabularyPath = "vocab.json";
        public const string GenerationConfigPath = "generation_config.json";
        public const string ManagedTokenizerManifestPath = "phraselayer-sentencepiece-unigram-v1.txt";
        public const string TokenizerFixtureManifestPath = "phraselayer-tokenizer-fixtures-v1.txt";

        public static LocalTranslationRuntimeSet ValidateAndResolve(StagedTranslationManifest manifest)
        {
            var byPath = ValidateManifestAndIndex(manifest);
            return new LocalTranslationRuntimeSet(
                Require(byPath, EncoderPath, "onnx"),
                Require(byPath, DecoderPath, "onnx"),
                Require(byPath, SourceSentencePiecePath, "support"),
                Require(byPath, TargetSentencePiecePath, "support"),
                Require(byPath, VocabularyPath, "support"),
                Require(byPath, GenerationConfigPath, "support"));
        }

        public static LocalTranslationBootstrapArtifacts ValidateAndResolveBootstrapArtifacts(StagedTranslationManifest manifest)
        {
            var byPath = ValidateManifestAndIndex(manifest);
            return new LocalTranslationBootstrapArtifacts(
                Require(byPath, ManagedTokenizerManifestPath, "generated"),
                Require(byPath, TokenizerFixtureManifestPath, "generated"));
        }

        public static string ValidateAndBuildReport(StagedTranslationManifest manifest)
        {
            var runtime = ValidateAndResolve(manifest);
            return
                "translation staging model=" + manifest.ModelId +
                " revision=" + manifest.Revision +
                " parity=exact" +
                " runtime_status=" + manifest.RuntimeStatus +
                " encoder=" + runtime.Encoder.Path +
                " decoder=" + runtime.Decoder.Path +
                " files=" + manifest.Files.Count;
        }

        private static Dictionary<string, StagedTranslationAsset> ValidateManifestAndIndex(StagedTranslationManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (manifest.SchemaVersion != StagedTranslationManifest.CurrentSchemaVersion)
                throw new InvalidOperationException("Unsupported staged translation manifest schema version: " + manifest.SchemaVersion);
            if (!string.Equals(manifest.ModelId, ExpectedModelId, StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected staged translation model id: " + manifest.ModelId);
            if (!string.Equals(manifest.Revision, ExpectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected staged translation revision: " + manifest.Revision);
            if (!manifest.ReferenceParityExact)
                throw new InvalidOperationException("Staged translation assets require token-exact/text-exact reference parity.");
            if (!string.Equals(manifest.RuntimeStatus, ExpectedRuntimeStatus, StringComparison.Ordinal))
                throw new InvalidOperationException("Staged translation runtime status must remain unverified until real Unity import succeeds.");

            var byPath = new Dictionary<string, StagedTranslationAsset>(StringComparer.Ordinal);
            for (var index = 0; index < manifest.Files.Count; index++)
            {
                var asset = manifest.Files[index];
                if (asset == null)
                    throw new InvalidOperationException("Staged translation manifest contains a null file entry.");
                ValidateCanonicalRelativePath(asset.Path);
                ValidateKind(asset);
                if (byPath.ContainsKey(asset.Path))
                    throw new InvalidOperationException("Duplicate staged translation asset path: " + asset.Path);
                byPath.Add(asset.Path, asset);
            }
            return byPath;
        }

        private static StagedTranslationAsset Require(
            IReadOnlyDictionary<string, StagedTranslationAsset> byPath,
            string path,
            string expectedKind)
        {
            StagedTranslationAsset asset;
            if (!byPath.TryGetValue(path, out asset))
                throw new InvalidOperationException("Missing staged translation runtime asset: " + path);
            if (!string.Equals(asset.Kind, expectedKind, StringComparison.Ordinal))
                throw new InvalidOperationException("Staged translation asset kind mismatch for " + path + ": " + asset.Kind);
            return asset;
        }

        private static void ValidateKind(StagedTranslationAsset asset)
        {
            if (!string.Equals(asset.Kind, "onnx", StringComparison.Ordinal) &&
                !string.Equals(asset.Kind, "support", StringComparison.Ordinal) &&
                !string.Equals(asset.Kind, "generated", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unknown staged translation asset kind: " + asset.Kind);
            }

            var isOnnxPath = asset.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);
            if (isOnnxPath != string.Equals(asset.Kind, "onnx", StringComparison.Ordinal))
                throw new InvalidOperationException("Staged translation asset kind does not match file extension: " + asset.Path);
        }

        private static void ValidateCanonicalRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Staged translation asset path is empty.");
            if (path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("\\", StringComparison.Ordinal))
                throw new InvalidOperationException("Staged translation asset path must be relative: " + path);
            if (path.IndexOf('\\') >= 0)
                throw new InvalidOperationException("Staged translation asset path must use forward slashes: " + path);
            if (path.IndexOf(':') >= 0)
                throw new InvalidOperationException("Staged translation asset path must not contain a drive or URI prefix: " + path);

            var parts = path.Split('/');
            for (var index = 0; index < parts.Length; index++)
            {
                if (parts[index].Length == 0 || parts[index] == "." || parts[index] == "..")
                    throw new InvalidOperationException("Staged translation asset path is not canonical: " + path);
            }
        }
    }
}
