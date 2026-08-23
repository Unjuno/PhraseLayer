using System;
using System.Security.Cryptography;
using System.Text;
using PhraseLayer.Core.Translation;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Scene-wiring gate for local translation assets. It validates the locally staged model/support files and,
    /// when requested by the runtime bootstrap, verifies the generated managed-tokenizer/fixture TextAssets
    /// against the SHA-256 values recorded in the local staging manifest. No remote fallback exists.
    /// </summary>
    public sealed class UnityLocalTranslationAssetGateBehaviour : MonoBehaviour
    {
        [SerializeField] private TextAsset stagingManifest = null;
        [SerializeField] private bool validateOnAwake = true;
        [SerializeField] private string lastReport = string.Empty;

        public string LastReport => lastReport;
        public bool HasManifest => stagingManifest != null;

        public LocalTranslationRuntimeSet ValidateAssets()
        {
            var manifest = RequireManifest();
            var runtimeSet = LocalTranslationStagingContract.ValidateAndResolve(manifest);
            lastReport = LocalTranslationStagingContract.ValidateAndBuildReport(manifest);
            Debug.Log("PhraseLayer local translation staging PASS: " + lastReport, this);
            return runtimeSet;
        }

        public LocalTranslationBootstrapArtifacts ValidateBootstrapAssets(
            TextAsset managedTokenizerManifest,
            TextAsset tokenizerFixtureManifest)
        {
            if (managedTokenizerManifest == null) throw new ArgumentNullException(nameof(managedTokenizerManifest));
            if (tokenizerFixtureManifest == null) throw new ArgumentNullException(nameof(tokenizerFixtureManifest));

            var manifest = RequireManifest();
            LocalTranslationStagingContract.ValidateAndResolve(manifest);
            var generated = LocalTranslationStagingContract.ValidateAndResolveBootstrapArtifacts(manifest);
            VerifyTextAsset(managedTokenizerManifest, generated.ManagedTokenizerManifest, "managed tokenizer manifest");
            VerifyTextAsset(tokenizerFixtureManifest, generated.TokenizerFixtureManifest, "tokenizer fixture manifest");
            lastReport = LocalTranslationStagingContract.ValidateAndBuildReport(manifest) + " bootstrap_artifacts=verified";
            return generated;
        }

        private StagedTranslationManifest RequireManifest()
        {
            if (stagingManifest == null)
                throw new InvalidOperationException(
                    "Local translation staging manifest is not assigned. Run prepare_unity_translation_assets.py after a parity-verified export.");
            return UnityLocalTranslationManifest.ParseManifest(stagingManifest);
        }

        private static void VerifyTextAsset(TextAsset asset, StagedTranslationAsset expected, string label)
        {
            if (asset.bytes == null || asset.bytes.LongLength != expected.SizeBytes)
                throw new InvalidOperationException(label + " byte length differs from the staging manifest.");
            var digest = ComputeSha256(asset.bytes);
            if (!string.Equals(digest, expected.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException(label + " SHA-256 differs from the staging manifest.");
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

        private void Awake()
        {
            if (!validateOnAwake || stagingManifest == null) return;
            ValidateAssets();
        }
    }
}
