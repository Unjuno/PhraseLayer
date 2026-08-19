using System;

namespace PhraseLayer.Core.Inputs
{
    public sealed class PaddleOcrDictionaryManifest
    {
        public PaddleOcrDictionaryManifest(
            int schemaVersion,
            string modelId,
            string upstream,
            string revision,
            string sourceArtifact,
            string postprocessName,
            int rawTokenCount,
            bool rawContainsLiteralSpace,
            bool useSpaceChar,
            int effectiveTokenCount,
            string generatedArtifact,
            string generatedSha256)
        {
            SchemaVersion = schemaVersion;
            ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
            Upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
            Revision = revision ?? throw new ArgumentNullException(nameof(revision));
            SourceArtifact = sourceArtifact ?? throw new ArgumentNullException(nameof(sourceArtifact));
            PostprocessName = postprocessName ?? throw new ArgumentNullException(nameof(postprocessName));
            if (rawTokenCount < 0) throw new ArgumentOutOfRangeException(nameof(rawTokenCount));
            if (effectiveTokenCount < 0) throw new ArgumentOutOfRangeException(nameof(effectiveTokenCount));
            RawTokenCount = rawTokenCount;
            RawContainsLiteralSpace = rawContainsLiteralSpace;
            UseSpaceChar = useSpaceChar;
            EffectiveTokenCount = effectiveTokenCount;
            GeneratedArtifact = generatedArtifact ?? throw new ArgumentNullException(nameof(generatedArtifact));
            GeneratedSha256 = generatedSha256 ?? throw new ArgumentNullException(nameof(generatedSha256));
        }

        public int SchemaVersion { get; }
        public string ModelId { get; }
        public string Upstream { get; }
        public string Revision { get; }
        public string SourceArtifact { get; }
        public string PostprocessName { get; }
        public int RawTokenCount { get; }
        public bool RawContainsLiteralSpace { get; }
        public bool UseSpaceChar { get; }
        public int EffectiveTokenCount { get; }
        public string GeneratedArtifact { get; }
        public string GeneratedSha256 { get; }
    }

    /// <summary>
    /// Validates the generated recognition dictionary manifest against the exact PP-OCRv6 tiny recognizer
    /// revision currently reviewed by PhraseLayer. JSON parsing and file hashing stay platform-specific;
    /// identity, token-count, space-token and digest equality rules remain testable in Core.
    /// </summary>
    public static class PaddleOcrDictionaryManifestContract
    {
        public const int ExpectedSchemaVersion = 1;
        public const string ExpectedModelId = "pp-ocrv6-tiny-rec";
        public const string ExpectedUpstream = "PaddlePaddle/PP-OCRv6_tiny_rec_onnx";
        public const string ExpectedRevision = "2612ab37152ae0a677521bae4e1e3d4fb4cf7c30";
        public const string ExpectedSourceArtifact = "inference.json";
        public const string ExpectedPostprocessName = "CTCLabelDecode";
        public const string ExpectedGeneratedArtifact = "ppocr_keys.txt";

        public static string ValidateAndBuildReport(
            PaddleOcrDictionaryManifest manifest,
            int actualRawTokenCount,
            bool configuredUseSpaceCharacter,
            string actualDictionarySha256)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (actualRawTokenCount < 0) throw new ArgumentOutOfRangeException(nameof(actualRawTokenCount));
            if (actualDictionarySha256 == null) throw new ArgumentNullException(nameof(actualDictionarySha256));

            RequireEqual(manifest.SchemaVersion, ExpectedSchemaVersion, "schema_version");
            RequireEqual(manifest.ModelId, ExpectedModelId, "model_id");
            RequireEqual(manifest.Upstream, ExpectedUpstream, "upstream");
            RequireEqual(manifest.Revision, ExpectedRevision, "revision");
            RequireEqual(manifest.SourceArtifact, ExpectedSourceArtifact, "source_artifact");
            RequireEqual(manifest.PostprocessName, ExpectedPostprocessName, "postprocess_name");
            RequireEqual(manifest.GeneratedArtifact, ExpectedGeneratedArtifact, "generated_artifact");

            if (manifest.RawTokenCount <= 0)
                throw new InvalidOperationException("Dictionary manifest raw_token_count must be greater than zero.");
            if (manifest.RawTokenCount != actualRawTokenCount)
            {
                throw new InvalidOperationException(
                    "Dictionary manifest raw_token_count does not match the assigned dictionary. " +
                    "Manifest=" + manifest.RawTokenCount + ", actual=" + actualRawTokenCount + ".");
            }

            if (manifest.UseSpaceChar != configuredUseSpaceCharacter)
            {
                throw new InvalidOperationException(
                    "Dictionary manifest use_space_char does not match the Unity bootstrap setting. " +
                    "Manifest=" + manifest.UseSpaceChar + ", configured=" + configuredUseSpaceCharacter + ".");
            }

            if (manifest.UseSpaceChar && manifest.RawContainsLiteralSpace)
            {
                throw new InvalidOperationException(
                    "Dictionary manifest is ambiguous: raw dictionary already contains a literal single-space token while use_space_char=true.");
            }

            var expectedEffectiveCount = checked(manifest.RawTokenCount + (manifest.UseSpaceChar ? 1 : 0));
            if (manifest.EffectiveTokenCount != expectedEffectiveCount)
            {
                throw new InvalidOperationException(
                    "Dictionary manifest effective_token_count is inconsistent with raw_token_count/use_space_char. " +
                    "Manifest=" + manifest.EffectiveTokenCount + ", expected=" + expectedEffectiveCount + ".");
            }

            ValidateSha256(manifest.GeneratedSha256, "generated_sha256");
            ValidateSha256(actualDictionarySha256, nameof(actualDictionarySha256));
            if (!string.Equals(manifest.GeneratedSha256, actualDictionarySha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Dictionary SHA-256 does not match the generated manifest. Manifest=" +
                    manifest.GeneratedSha256 + ", actual=" + actualDictionarySha256 + ".");
            }

            return "dictionary manifest model=" + manifest.ModelId +
                   " revision=" + manifest.Revision +
                   " raw=" + manifest.RawTokenCount +
                   " effective=" + manifest.EffectiveTokenCount +
                   " use_space_char=" + manifest.UseSpaceChar.ToString().ToLowerInvariant() +
                   " sha256=" + manifest.GeneratedSha256;
        }

        private static void RequireEqual(int actual, int expected, string field)
        {
            if (actual != expected)
                throw new InvalidOperationException(field + " mismatch. Expected=" + expected + ", actual=" + actual + ".");
        }

        private static void RequireEqual(string actual, string expected, string field)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidOperationException(field + " mismatch. Expected=" + expected + ", actual=" + actual + ".");
        }

        private static void ValidateSha256(string value, string field)
        {
            if (value.Length != 64)
                throw new InvalidOperationException(field + " must be 64 lowercase hexadecimal characters.");

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                var isDigit = character >= '0' && character <= '9';
                var isLowerHex = character >= 'a' && character <= 'f';
                if (!isDigit && !isLowerHex)
                    throw new InvalidOperationException(field + " must be 64 lowercase hexadecimal characters.");
            }
        }
    }
}
