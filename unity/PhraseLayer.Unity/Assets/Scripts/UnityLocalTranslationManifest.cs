using System;
using System.Collections.Generic;
using PhraseLayer.Core.Translation;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Unity bridge for the git-ignored local translation staging manifest produced by
    /// tools/prepare_unity_translation_assets.py. This validates identity/parity metadata only;
    /// successful parsing is not a claim that Unity Inference Engine can import or execute the ONNX graphs.
    /// </summary>
    public static class UnityLocalTranslationManifest
    {
        public static StagedTranslationManifest ParseManifest(TextAsset manifestAsset)
        {
            if (manifestAsset == null) throw new ArgumentNullException(nameof(manifestAsset));

            var dto = JsonUtility.FromJson<ManifestJson>(manifestAsset.text);
            if (dto == null)
                throw new InvalidOperationException("Local translation staging manifest JSON could not be parsed.");
            if (dto.files == null)
                throw new InvalidOperationException("Local translation staging manifest files array is missing.");

            var files = new List<StagedTranslationAsset>(dto.files.Length);
            for (var index = 0; index < dto.files.Length; index++)
            {
                var item = dto.files[index];
                if (item == null)
                    throw new InvalidOperationException("Local translation staging manifest contains a null file entry.");
                files.Add(new StagedTranslationAsset(
                    item.asset_path ?? string.Empty,
                    item.size_bytes,
                    item.sha256 ?? string.Empty,
                    item.kind ?? string.Empty));
            }

            return new StagedTranslationManifest(
                dto.schema_version,
                dto.model_id ?? string.Empty,
                dto.revision ?? string.Empty,
                dto.reference_parity_exact,
                dto.runtime_status ?? string.Empty,
                files);
        }

        public static LocalTranslationRuntimeSet ParseAndValidate(TextAsset manifestAsset)
        {
            return LocalTranslationStagingContract.ValidateAndResolve(ParseManifest(manifestAsset));
        }

        public static string ValidateAndBuildReport(TextAsset manifestAsset)
        {
            return LocalTranslationStagingContract.ValidateAndBuildReport(ParseManifest(manifestAsset));
        }

        [Serializable]
        private sealed class ManifestJson
        {
#pragma warning disable 0649
            public int schema_version;
            public string model_id;
            public string revision;
            public bool reference_parity_exact;
            public string runtime_status;
            public FileJson[] files;
#pragma warning restore 0649
        }

        [Serializable]
        private sealed class FileJson
        {
#pragma warning disable 0649
            public string asset_path;
            public long size_bytes;
            public string sha256;
            public string kind;
#pragma warning restore 0649
        }
    }
}
