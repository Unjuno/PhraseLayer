using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Evidence-producing wrapper around the real Unity Marian parity gate. The wrapped probe throws on any graph,
    /// tokenizer, generated-token, decoded-text or semantic-span mismatch; evidence is written only after it returns.
    /// This prevents CI from converting a mere process exit code into unsupported product-parity claims.
    /// </summary>
    public static class PhraseLayerMarianParityEvidence
    {
        private const string EvidencePathEnvironment = "PHRASELAYER_MARIAN_PARITY_EVIDENCE_PATH";
        private const string DefaultRelativeEvidencePath = "Temp/PhraseLayer.marian-unity-parity-evidence.json";

        [MenuItem("PhraseLayer/Marian/Run Translation Parity + Evidence")]
        public static void Run()
        {
            PhraseLayerLocalMarianAssets.RunTranslationParityProbe();
            var evidencePath = ResolveEvidencePath();
            var parent = Path.GetDirectoryName(evidencePath);
            if (string.IsNullOrWhiteSpace(parent))
                throw new InvalidOperationException("Marian parity evidence path has no parent directory: " + evidencePath);
            Directory.CreateDirectory(parent);

            var json = string.Format(
                CultureInfo.InvariantCulture,
                "{{\n  \"schema_version\": 1,\n  \"purpose\": \"phrase-layer-real-unity-marian-parity\",\n  \"unity_version\": \"{0}\",\n  \"model_revision\": \"{1}\",\n  \"real_unity_execution\": true,\n  \"model_graph_contract_passed\": true,\n  \"managed_tokenizer_source_token_parity_passed\": true,\n  \"cpu_clone_backend_generated_token_parity_passed\": true,\n  \"device_resident_backend_generated_token_parity_passed\": true,\n  \"decoded_text_parity_passed\": true,\n  \"language_pipeline_semantic_replacement_passed\": true,\n  \"gloss_marker_injection_detected\": false,\n  \"minimum_reference_samples\": 3,\n  \"quest_device_execution_performed\": false\n}}\n",
                EscapeJson(Application.unityVersion),
                EscapeJson(PhraseLayerLocalMarianAssets.ExpectedRevision));
            File.WriteAllText(evidencePath, json);
            if (!File.Exists(evidencePath) || new FileInfo(evidencePath).Length <= 0)
                throw new IOException("Marian parity evidence file was not written: " + evidencePath);

            Debug.Log("PhraseLayer real Unity Marian parity evidence PASS: " + evidencePath);
        }

        public static void RunBatch()
        {
            try
            {
                Run();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static string ResolveEvidencePath()
        {
            var configured = Environment.GetEnvironmentVariable(EvidencePathEnvironment);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                configured = configured.Trim();
                return Path.GetFullPath(Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(ProjectRoot(), configured));
            }
            return Path.GetFullPath(Path.Combine(ProjectRoot(), DefaultRelativeEvidencePath));
        }

        private static string ProjectRoot()
        {
            var assetsPath = Path.GetFullPath(Application.dataPath);
            var root = Path.GetDirectoryName(assetsPath);
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("Unable to derive Unity project root from Application.dataPath.");
            return Path.GetFullPath(root);
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }
    }
}
