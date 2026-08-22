using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PhraseLayer.Core.Translation;
using UnityEditor;
using UnityEngine;

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Editor-only integrity gate for locally staged OPUS-MT assets.
    ///
    /// The staging script already verifies bytes during copy. This gate intentionally verifies them again from
    /// inside the Unity project before import/runtime wiring, then creates .bytes mirrors for SentencePiece assets
    /// so Unity imports the exact tokenizer bytes as TextAsset. Model binaries remain git-ignored/local-only.
    /// </summary>
    public static class PhraseLayerLocalTranslationAssets
    {
        public const string RootAssetPath = "Assets/LocalTranslationAssets/OpusMtEnJap";
        public const string ManifestAssetPath = RootAssetPath + "/PhraseLayerTranslationAssets.manifest.json";

        [MenuItem("PhraseLayer/Verify and Prepare Local Translation Assets")]
        public static void VerifyAndPrepareFromMenu()
        {
            var report = VerifyAndPrepare();
            Debug.Log("PhraseLayer local translation assets PASS: " + report);
        }

        public static string VerifyAndPrepare()
        {
            var manifestAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestAssetPath);
            if (manifestAsset == null)
            {
                throw new InvalidOperationException(
                    "Local translation manifest is missing. Run tools/prepare_unity_translation_assets.py first: " +
                    ManifestAssetPath);
            }

            var manifest = UnityLocalTranslationManifest.ParseManifest(manifestAsset);
            var runtime = LocalTranslationStagingContract.ValidateAndResolve(manifest);

            for (var index = 0; index < manifest.Files.Count; index++)
                VerifyStagedFile(manifest.Files[index]);

            var sourceSidecar = EnsureTextAssetSidecar(runtime.SourceSentencePiece);
            var targetSidecar = EnsureTextAssetSidecar(runtime.TargetSentencePiece);
            AssetDatabase.Refresh();

            VerifyImportedSidecar(sourceSidecar, runtime.SourceSentencePiece);
            VerifyImportedSidecar(targetSidecar, runtime.TargetSentencePiece);

            return LocalTranslationStagingContract.ValidateAndBuildReport(manifest) +
                " source_spm_textasset=" + sourceSidecar +
                " target_spm_textasset=" + targetSidecar;
        }

        private static void VerifyStagedFile(StagedTranslationAsset asset)
        {
            var assetPath = RootAssetPath + "/" + asset.Path;
            var fullPath = ToFullPath(assetPath);
            if (!File.Exists(fullPath))
                throw new InvalidOperationException("Staged translation asset is missing: " + assetPath);

            var size = new FileInfo(fullPath).Length;
            if (size != asset.SizeBytes)
            {
                throw new InvalidOperationException(
                    "Staged translation asset size mismatch for " + asset.Path +
                    ": expected " + asset.SizeBytes + " actual " + size);
            }

            var digest = ComputeSha256(File.ReadAllBytes(fullPath));
            if (!string.Equals(digest, asset.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Staged translation asset SHA-256 mismatch for " + asset.Path +
                    ": expected " + asset.Sha256 + " actual " + digest);
            }
        }

        private static string EnsureTextAssetSidecar(StagedTranslationAsset source)
        {
            VerifyStagedFile(source);
            var sourceAssetPath = RootAssetPath + "/" + source.Path;
            var destinationAssetPath = sourceAssetPath + ".bytes";
            var sourceFullPath = ToFullPath(sourceAssetPath);
            var destinationFullPath = ToFullPath(destinationAssetPath);

            File.Copy(sourceFullPath, destinationFullPath, true);
            var copied = File.ReadAllBytes(destinationFullPath);
            if (copied.LongLength != source.SizeBytes ||
                !string.Equals(ComputeSha256(copied), source.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "SentencePiece TextAsset sidecar did not preserve exact bytes: " + destinationAssetPath);
            }
            return destinationAssetPath;
        }

        private static void VerifyImportedSidecar(string assetPath, StagedTranslationAsset expected)
        {
            var imported = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
            if (imported == null)
                throw new InvalidOperationException("Unity did not import SentencePiece sidecar as TextAsset: " + assetPath);
            if (imported.bytes == null || imported.bytes.LongLength != expected.SizeBytes)
                throw new InvalidOperationException("Imported SentencePiece TextAsset size mismatch: " + assetPath);

            var digest = ComputeSha256(imported.bytes);
            if (!string.Equals(digest, expected.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Imported SentencePiece TextAsset SHA-256 mismatch: " + assetPath);
        }

        private static string ToFullPath(string assetPath)
        {
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected an Assets-relative path: " + assetPath);

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                throw new InvalidOperationException("Cannot resolve Unity project root from Application.dataPath.");

            var relative = assetPath.Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(projectRoot, relative));
        }

        private static string ComputeSha256(byte[] bytes)
        {
            byte[] digest;
            using (var sha256 = SHA256.Create())
                digest = sha256.ComputeHash(bytes);

            var builder = new StringBuilder(digest.Length * 2);
            for (var index = 0; index < digest.Length; index++)
                builder.Append(digest[index].ToString("x2"));
            return builder.ToString();
        }
    }
}
