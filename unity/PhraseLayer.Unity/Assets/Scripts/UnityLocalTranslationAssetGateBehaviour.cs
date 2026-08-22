using System;
using PhraseLayer.Core.Translation;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Scene-wiring gate for local translation assets. It deliberately stops at manifest validation until
    /// the exported encoder/decoder pass a real Unity Inference Engine import probe. No remote fallback exists.
    /// </summary>
    public sealed class UnityLocalTranslationAssetGateBehaviour : MonoBehaviour
    {
        [SerializeField] private TextAsset stagingManifest;
        [SerializeField] private bool validateOnAwake = true;
        [SerializeField] private string lastReport = string.Empty;

        public string LastReport => lastReport;
        public bool HasManifest => stagingManifest != null;

        public LocalTranslationRuntimeSet ValidateAssets()
        {
            if (stagingManifest == null)
                throw new InvalidOperationException(
                    "Local translation staging manifest is not assigned. Run prepare_unity_translation_assets.py after a parity-verified export.");

            var runtimeSet = UnityLocalTranslationManifest.ParseAndValidate(stagingManifest);
            lastReport = UnityLocalTranslationManifest.ValidateAndBuildReport(stagingManifest);
            Debug.Log("PhraseLayer local translation staging PASS: " + lastReport, this);
            return runtimeSet;
        }

        private void Awake()
        {
            if (!validateOnAwake || stagingManifest == null) return;
            ValidateAssets();
        }
    }
}
