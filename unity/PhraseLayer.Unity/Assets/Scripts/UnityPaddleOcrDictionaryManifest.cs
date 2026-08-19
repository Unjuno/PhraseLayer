using System;
using System.Security.Cryptography;
using System.Text;
using PhraseLayer.Core.Inputs;
using UnityEngine;

namespace PhraseLayer.Unity
{
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    /// <summary>
    /// Parses the generated PP-OCR dictionary manifest with Unity JsonUtility, hashes the exact TextAsset bytes,
    /// then delegates all identity/token/space/digest rules to the platform-neutral Core contract.
    /// </summary>
    public static class UnityPaddleOcrDictionaryManifest
    {
        public static string Validate(
            TextAsset dictionaryAsset,
            TextAsset manifestAsset,
            bool configuredUseSpaceCharacter)
        {
            if (dictionaryAsset == null) throw new ArgumentNullException(nameof(dictionaryAsset));
            if (manifestAsset == null) throw new ArgumentNullException(nameof(manifestAsset));

            var rawDictionary = PaddleOcrCharacterDictionary.Parse(
                dictionaryAsset.text,
                useSpaceCharacter: false);
            var json = JsonUtility.FromJson<ManifestJson>(manifestAsset.text);
            if (json == null)
                throw new InvalidOperationException("PP-OCR dictionary manifest JSON could not be parsed.");

            var manifest = new PaddleOcrDictionaryManifest(
                json.schema_version,
                json.model_id ?? string.Empty,
                json.upstream ?? string.Empty,
                json.revision ?? string.Empty,
                json.source_artifact ?? string.Empty,
                json.postprocess_name ?? string.Empty,
                json.raw_token_count,
                json.raw_contains_literal_space,
                json.use_space_char,
                json.effective_token_count,
                json.generated_artifact ?? string.Empty,
                json.generated_sha256 ?? string.Empty);

            var actualSha256 = ComputeSha256(dictionaryAsset.bytes);
            return PaddleOcrDictionaryManifestContract.ValidateAndBuildReport(
                manifest,
                rawDictionary.Count,
                configuredUseSpaceCharacter,
                actualSha256);
        }

        private static string ComputeSha256(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            byte[] digest;
            using (var sha256 = SHA256.Create())
                digest = sha256.ComputeHash(bytes);

            var builder = new StringBuilder(digest.Length * 2);
            for (var index = 0; index < digest.Length; index++)
                builder.Append(digest[index].ToString("x2"));
            return builder.ToString();
        }

        [Serializable]
        private sealed class ManifestJson
        {
#pragma warning disable 0649
            public int schema_version;
            public string model_id;
            public string upstream;
            public string revision;
            public string source_artifact;
            public string postprocess_name;
            public int raw_token_count;
            public bool raw_contains_literal_space;
            public bool use_space_char;
            public int effective_token_count;
            public string generated_artifact;
            public string generated_sha256;
#pragma warning restore 0649
        }
    }
#else
    public static class UnityPaddleOcrDictionaryManifest
    {
        public static string UnsupportedReport =>
            "PP-OCR dictionary manifest validation unavailable: reviewed com.unity.ai.inference 2.2.x API gate is not active.";
    }
#endif
}
