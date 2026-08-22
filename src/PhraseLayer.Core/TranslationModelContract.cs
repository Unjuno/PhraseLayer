using System;
using System.Collections.Generic;

namespace PhraseLayer.Core.Translation
{
    public enum TranslationArtifactRole
    {
        EncoderOnnx,
        DecoderOnnx,
        SourceSentencePiece,
        TargetSentencePiece,
        VocabularyJson,
        GenerationConfigJson,
    }

    public sealed class TranslationArtifactDescriptor
    {
        public TranslationArtifactDescriptor(
            TranslationArtifactRole role,
            string fileName,
            long sizeBytes,
            string sha256)
        {
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("Artifact file name is required.", nameof(fileName));
            if (sizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
            if (!IsSha256(sha256)) throw new ArgumentException("Artifact SHA-256 must be 64 hexadecimal characters.", nameof(sha256));

            Role = role;
            FileName = fileName;
            SizeBytes = sizeBytes;
            Sha256 = sha256.ToLowerInvariant();
        }

        public TranslationArtifactRole Role { get; }
        public string FileName { get; }
        public long SizeBytes { get; }
        public string Sha256 { get; }

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

    /// <summary>
    /// Platform-neutral contract for a local English-to-Japanese seq2seq translation bundle.
    /// It deliberately models exported runtime artifacts rather than a Hugging Face repository layout:
    /// PhraseLayer may use an upstream Marian model, but the official Quest build consumes a reviewed,
    /// hash-pinned ONNX export plus the exact tokenizer/generation assets used to create its inputs/outputs.
    /// </summary>
    public sealed class LocalTranslationModelManifest
    {
        public const int CurrentSchemaVersion = 1;

        public LocalTranslationModelManifest(
            int schemaVersion,
            string modelId,
            string upstreamRevision,
            string architecture,
            string sourceLanguage,
            string targetLanguage,
            string runtimeTarget,
            int decoderStartTokenId,
            int eosTokenId,
            int padTokenId,
            int maxLength,
            int numBeams,
            IReadOnlyList<TranslationArtifactDescriptor> artifacts)
        {
            SchemaVersion = schemaVersion;
            ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
            UpstreamRevision = upstreamRevision ?? throw new ArgumentNullException(nameof(upstreamRevision));
            Architecture = architecture ?? throw new ArgumentNullException(nameof(architecture));
            SourceLanguage = sourceLanguage ?? throw new ArgumentNullException(nameof(sourceLanguage));
            TargetLanguage = targetLanguage ?? throw new ArgumentNullException(nameof(targetLanguage));
            RuntimeTarget = runtimeTarget ?? throw new ArgumentNullException(nameof(runtimeTarget));
            DecoderStartTokenId = decoderStartTokenId;
            EosTokenId = eosTokenId;
            PadTokenId = padTokenId;
            MaxLength = maxLength;
            NumBeams = numBeams;
            Artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        }

        public int SchemaVersion { get; }
        public string ModelId { get; }
        public string UpstreamRevision { get; }
        public string Architecture { get; }
        public string SourceLanguage { get; }
        public string TargetLanguage { get; }
        public string RuntimeTarget { get; }
        public int DecoderStartTokenId { get; }
        public int EosTokenId { get; }
        public int PadTokenId { get; }
        public int MaxLength { get; }
        public int NumBeams { get; }
        public IReadOnlyList<TranslationArtifactDescriptor> Artifacts { get; }
    }

    public static class LocalTranslationModelContract
    {
        public const string ExpectedArchitecture = "marian";
        public const string ExpectedSourceLanguage = "en";
        public const string ExpectedTargetLanguage = "ja";
        public const string ExpectedRuntimeTarget = "com.unity.ai.inference@2.2.1";

        private static readonly TranslationArtifactRole[] RequiredRoles =
        {
            TranslationArtifactRole.EncoderOnnx,
            TranslationArtifactRole.DecoderOnnx,
            TranslationArtifactRole.SourceSentencePiece,
            TranslationArtifactRole.TargetSentencePiece,
            TranslationArtifactRole.VocabularyJson,
            TranslationArtifactRole.GenerationConfigJson,
        };

        public static string ValidateAndBuildReport(LocalTranslationModelManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (manifest.SchemaVersion != LocalTranslationModelManifest.CurrentSchemaVersion)
                throw new InvalidOperationException("Unsupported translation model manifest schema version: " + manifest.SchemaVersion);
            if (string.IsNullOrWhiteSpace(manifest.ModelId))
                throw new InvalidOperationException("Translation model id is required.");
            if (!IsFullGitSha(manifest.UpstreamRevision))
                throw new InvalidOperationException("Translation model upstream revision must be a full 40-character Git SHA.");
            if (!string.Equals(manifest.Architecture, ExpectedArchitecture, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Translation architecture must remain Marian for the reviewed baseline.");
            if (!string.Equals(manifest.SourceLanguage, ExpectedSourceLanguage, StringComparison.Ordinal))
                throw new InvalidOperationException("Translation source language must be 'en'.");
            if (!string.Equals(manifest.TargetLanguage, ExpectedTargetLanguage, StringComparison.Ordinal))
                throw new InvalidOperationException("Translation target language must be 'ja'.");
            if (!string.Equals(manifest.RuntimeTarget, ExpectedRuntimeTarget, StringComparison.Ordinal))
                throw new InvalidOperationException("Translation runtime target drift: " + manifest.RuntimeTarget);
            if (manifest.DecoderStartTokenId < 0 || manifest.EosTokenId < 0 || manifest.PadTokenId < 0)
                throw new InvalidOperationException("Translation generation token ids must be non-negative.");
            if (manifest.MaxLength <= 0 || manifest.MaxLength > 512)
                throw new InvalidOperationException("Translation max length must be within the reviewed 1..512 range.");
            if (manifest.NumBeams <= 0 || manifest.NumBeams > 8)
                throw new InvalidOperationException("Translation beam count must be within the reviewed 1..8 range.");

            var byRole = new Dictionary<TranslationArtifactRole, TranslationArtifactDescriptor>();
            foreach (var artifact in manifest.Artifacts)
            {
                if (artifact == null)
                    throw new InvalidOperationException("Translation model manifest contains a null artifact.");
                if (byRole.ContainsKey(artifact.Role))
                    throw new InvalidOperationException("Duplicate translation artifact role: " + artifact.Role);
                byRole.Add(artifact.Role, artifact);
            }

            foreach (var role in RequiredRoles)
            {
                if (!byRole.ContainsKey(role))
                    throw new InvalidOperationException("Missing required translation artifact: " + role);
            }

            RequireExtension(byRole[TranslationArtifactRole.EncoderOnnx], ".onnx");
            RequireExtension(byRole[TranslationArtifactRole.DecoderOnnx], ".onnx");
            RequireExtension(byRole[TranslationArtifactRole.SourceSentencePiece], ".spm");
            RequireExtension(byRole[TranslationArtifactRole.TargetSentencePiece], ".spm");
            RequireExtension(byRole[TranslationArtifactRole.VocabularyJson], ".json");
            RequireExtension(byRole[TranslationArtifactRole.GenerationConfigJson], ".json");

            return
                "translation model=" + manifest.ModelId +
                " architecture=" + manifest.Architecture +
                " source=" + manifest.SourceLanguage +
                " target=" + manifest.TargetLanguage +
                " runtime=" + manifest.RuntimeTarget +
                " artifacts=" + byRole.Count +
                " max_length=" + manifest.MaxLength +
                " beams=" + manifest.NumBeams;
        }

        private static void RequireExtension(TranslationArtifactDescriptor artifact, string extension)
        {
            if (!artifact.FileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(artifact.Role + " must use a " + extension + " artifact.");
        }

        private static bool IsFullGitSha(string value)
        {
            if (value == null || value.Length != 40) return false;
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
}
