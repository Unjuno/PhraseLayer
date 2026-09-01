using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Stages locally supplied, license-reviewed Read Mode visual assets without committing the font binary.
    /// The source-mask material is generated from PhraseLayer's committed opaque/double-sided shader so the
    /// self-hosted Quest gate is reproducible from a clean checkout plus an explicitly supplied Japanese font.
    /// </summary>
    public static class PhraseLayerLocalReadModeVisualAssets
    {
        public const string Root = "Assets/LocalReadModeAssets";
        public const string MaskMaterialPath = Root + "/PhraseLayerSourceMask.mat";
        public const string EvidencePath = Root + "/read-mode-visual-assets.json";
        public const string SourceMaskShaderName = "PhraseLayer/SourceMask";
        public const string FontSourceEnvironment = "PHRASELAYER_JAPANESE_FONT_SOURCE";

        [MenuItem("PhraseLayer/Read Mode/Stage Local Visual Assets And Recreate Demo")]
        public static void StageAndCreateDemoScene()
        {
            StageAndCreateDemoScene(false);
        }

        public static void StageAndCreateDemoScene(bool autoRunQuestReadModeSmoke)
        {
            var source = Environment.GetEnvironmentVariable(FontSourceEnvironment);
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new InvalidOperationException(
                    FontSourceEnvironment + " must point to a locally reviewed Japanese-capable .ttf or .otf file.");
            }

            source = Path.GetFullPath(source);
            if (!File.Exists(source))
                throw new FileNotFoundException("Reviewed Japanese font source does not exist.", source);

            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (extension != ".ttf" && extension != ".otf")
                throw new InvalidOperationException("Reviewed Japanese font source must use .ttf or .otf extension: " + source);

            Directory.CreateDirectory(Path.Combine(Application.dataPath, "LocalReadModeAssets"));
            var fontAssetPath = Root + "/ReviewedJapaneseFont" + extension;
            var fontAbsolutePath = Path.Combine(
                Application.dataPath,
                "LocalReadModeAssets",
                "ReviewedJapaneseFont" + extension);
            if (!string.Equals(source, Path.GetFullPath(fontAbsolutePath), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, fontAbsolutePath, true);

            var sourceHash = Sha256(source);
            var stagedHash = Sha256(fontAbsolutePath);
            if (!string.Equals(sourceHash, stagedHash, StringComparison.Ordinal))
            {
                throw new IOException(
                    "Reviewed Japanese font staging changed the source bytes; refusing to create the Read Mode fixture.");
            }

            AssetDatabase.ImportAsset(fontAssetPath, ImportAssetOptions.ForceUpdate);
            var font = AssetDatabase.LoadAssetAtPath<Font>(fontAssetPath);
            if (font == null)
                throw new InvalidOperationException("Unity did not import the reviewed font as a Font asset: " + fontAssetPath);

            var shader = Shader.Find(SourceMaskShaderName);
            if (shader == null)
                throw new InvalidOperationException("Could not resolve committed source-mask shader: " + SourceMaskShaderName);

            var material = AssetDatabase.LoadAssetAtPath<Material>(MaskMaterialPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, MaskMaterialPath);
            }

            // Re-assert the committed shader every staging run. A git-ignored .mat can survive between runs, so
            // merely updating its color would otherwise allow a stale local shader assignment to escape the gate.
            material.shader = shader;
            material.color = Color.white;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            if (material.shader == null || !string.Equals(material.shader.name, SourceMaskShaderName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Read Mode source-mask material did not retain the committed shader after staging: " + SourceMaskShaderName);
            }

            WriteEvidence(source, sourceHash, fontAssetPath, material, autoRunQuestReadModeSmoke);
            PhraseLayerEditorSetup.CreateDemoScene(font, material, autoRunQuestReadModeSmoke);
            AssetDatabase.SaveAssets();
            Debug.Log(
                "PhraseLayer local Read Mode visual assets PASS: font=" + fontAssetPath +
                "; mask=" + MaskMaterialPath +
                "; smoke_autorun=" + (autoRunQuestReadModeSmoke ? "true" : "false") +
                "; evidence=" + EvidencePath +
                ". Font bytes remain under the git-ignored LocalReadModeAssets directory.");
        }

        public static void StageAndCreateDemoSceneBatch()
        {
            try
            {
                StageAndCreateDemoScene(false);
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void WriteEvidence(
            string sourcePath,
            string sourceHash,
            string fontAssetPath,
            Material material,
            bool autoRunQuestReadModeSmoke)
        {
            var info = new FileInfo(sourcePath);
            var json = string.Format(
                CultureInfo.InvariantCulture,
                "{{\n  \"schema_version\": 1,\n  \"purpose\": \"phrase-layer-read-mode-visual-assets\",\n  \"font_asset_path\": \"{0}\",\n  \"font_source_file_name\": \"{1}\",\n  \"font_size_bytes\": {2},\n  \"font_sha256\": \"{3}\",\n  \"font_staged_bytes_verified\": true,\n  \"mask_material_path\": \"{4}\",\n  \"mask_shader\": \"{5}\",\n  \"mask_shader_reasserted\": true,\n  \"mask_color_rgba\": [1.0, 1.0, 1.0, 1.0],\n  \"quest_read_mode_smoke_autorun\": {6}\n}}\n",
                EscapeJson(fontAssetPath),
                EscapeJson(info.Name),
                info.Length,
                sourceHash,
                EscapeJson(MaskMaterialPath),
                EscapeJson(material.shader == null ? SourceMaskShaderName : material.shader.name),
                autoRunQuestReadModeSmoke ? "true" : "false");
            File.WriteAllText(Path.Combine(Application.dataPath, "LocalReadModeAssets", "read-mode-visual-assets.json"), json);
            AssetDatabase.ImportAsset(EvidencePath, ImportAssetOptions.ForceUpdate);
        }

        private static string Sha256(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var bytes = algorithm.ComputeHash(stream);
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
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
