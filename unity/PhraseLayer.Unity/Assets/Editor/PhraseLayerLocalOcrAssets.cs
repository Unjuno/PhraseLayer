using System;
using PhraseLayer.Core.Inputs;
using UnityEditor;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Editor-only bridge for the git-ignored OCR assets prepared by tools/prepare_unity_ocr_assets.py.
    /// It verifies Unity import types, probes both imported models, validates the generated dictionary manifest,
    /// can execute a one-shot synthetic detector/recognizer inference gate, and can assign the reviewed local
    /// assets to exactly one scene bootstrap without serializing model binaries.
    /// </summary>
    public static class PhraseLayerLocalOcrAssets
    {
        public const string Root = "Assets/LocalOcrAssets/PaddleOCR";
        public const string DetectorPath = Root + "/detector.onnx";
        public const string RecognizerPath = Root + "/recognizer.onnx";
        public const string DictionaryPath = Root + "/ppocr_keys.txt";
        public const string DictionaryManifestPath = Root + "/ppocr_keys.manifest.json";
        private const int SyntheticProbeSize = 256;

        [MenuItem("PhraseLayer/PP-OCR/Verify Local Assets")]
        public static void VerifyLocalAssets()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();

            var detector = LoadRequired<ModelAsset>(DetectorPath);
            var recognizer = LoadRequired<ModelAsset>(RecognizerPath);
            var dictionary = LoadRequired<TextAsset>(DictionaryPath);
            var manifest = LoadRequired<TextAsset>(DictionaryManifestPath);

            var dictionaryReport = UnityPaddleOcrDictionaryManifest.Validate(
                dictionary,
                manifest,
                configuredUseSpaceCharacter: true);
            var detectorReport = UnityInferenceModelProbe.BuildReport(detector);
            var recognizerReport = UnityInferenceModelProbe.BuildReport(recognizer);

            Debug.Log(
                "PhraseLayer local PP-OCR assets PASS\n" +
                dictionaryReport + "\n--- detector ---\n" + detectorReport +
                "\n--- recognizer ---\n" + recognizerReport);
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve the reviewed com.unity.ai.inference 2.2.x package before verifying local OCR assets.");
#endif
        }

        /// <summary>
        /// Batchmode entry point for the import/metadata gate.
        /// Example: Unity -batchmode -projectPath unity/PhraseLayer.Unity -executeMethod PhraseLayer.Unity.Editor.PhraseLayerLocalOcrAssets.VerifyLocalAssetsBatch -quit
        /// </summary>
        public static void VerifyLocalAssetsBatch()
        {
            try
            {
                VerifyLocalAssets();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        [MenuItem("PhraseLayer/PP-OCR/Run Local Inference Probe")]
        public static void RunLocalInferenceProbe()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();

            var detectorAsset = LoadRequired<ModelAsset>(DetectorPath);
            var recognizerAsset = LoadRequired<ModelAsset>(RecognizerPath);
            var dictionaryAsset = LoadRequired<TextAsset>(DictionaryPath);
            var manifestAsset = LoadRequired<TextAsset>(DictionaryManifestPath);

            var dictionaryReport = UnityPaddleOcrDictionaryManifest.Validate(
                dictionaryAsset,
                manifestAsset,
                configuredUseSpaceCharacter: true);
            var dictionary = PaddleOcrCharacterDictionary.Parse(
                dictionaryAsset.text,
                useSpaceCharacter: true);
            if (dictionary.Count != PaddleOcrDictionaryManifestContract.ExpectedEffectiveTokenCount)
            {
                throw new InvalidOperationException(
                    "Synthetic inference probe dictionary count mismatch. Expected " +
                    PaddleOcrDictionaryManifestContract.ExpectedEffectiveTokenCount +
                    " but parsed " + dictionary.Count + ".");
            }

            var texture = CreateSyntheticProbeTexture();
            try
            {
                PaddleDetectorRuntimeContract detectorContract;
                string detectorRange;
                using (var detectorRuntime = new UnityPaddleOcrDetectorRuntime(
                    detectorAsset,
                    BackendType.GPUCompute))
                {
                    var detectorOutput = detectorRuntime.Execute(
                        texture,
                        texture.width,
                        texture.height);
                    detectorContract = PaddleOcrRuntimeContract.ValidateDetector(
                        detectorOutput.OutputShape,
                        detectorOutput.OutputValues);
                    detectorRange = ValidateUnitInterval(
                        detectorOutput.OutputValues,
                        "detector probability map");
                }

                PaddleRecognizerRuntimeContract recognizerContract;
                PaddleCtcDecodeResult decoded;
                string recognizerRange;
                using (var recognizerRuntime = new UnityPaddleOcrRecognizerRuntime(
                    recognizerAsset,
                    BackendType.GPUCompute))
                {
                    var recognizerOutput = recognizerRuntime.Execute(
                        texture,
                        PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth);
                    recognizerContract = PaddleOcrRuntimeContract.ValidateRecognizer(
                        recognizerOutput.OutputShape,
                        recognizerOutput.OutputValues,
                        dictionary.Count);
                    recognizerRange = ValidateUnitInterval(
                        recognizerOutput.OutputValues,
                        "recognizer probability matrix");
                    decoded = recognizerOutput.Decode(dictionary);
                }

                ValidateUnitInterval(
                    new[] { (float)decoded.Confidence },
                    "decoded CTC confidence");

                Debug.Log(
                    "PhraseLayer local PP-OCR inference probe PASS\n" +
                    dictionaryReport + "\n" +
                    detectorContract + " " + detectorRange + "\n" +
                    recognizerContract + " " + recognizerRange + "\n" +
                    "decoded_emitted_tokens=" + decoded.EmittedTokenCount +
                    " decoded_confidence=" + decoded.Confidence);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve the reviewed com.unity.ai.inference 2.2.x package before running the local OCR inference probe.");
#endif
        }

        /// <summary>
        /// Batchmode Gate 4 entry point. Requires a real graphics device because the reviewed OCR path uses
        /// GPUCompute plus Graphics.Blit/ReadPixels; do not run this gate with -nographics.
        /// </summary>
        public static void RunLocalInferenceProbeBatch()
        {
            try
            {
                RunLocalInferenceProbe();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        [MenuItem("PhraseLayer/PP-OCR/Assign Local Assets To Scene Bootstrap")]
        public static void AssignLocalAssetsToSceneBootstrap()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();

            var detector = LoadRequired<ModelAsset>(DetectorPath);
            var recognizer = LoadRequired<ModelAsset>(RecognizerPath);
            var dictionary = LoadRequired<TextAsset>(DictionaryPath);
            var manifest = LoadRequired<TextAsset>(DictionaryManifestPath);

            // Refuse to assign stale or edited local dictionary assets.
            UnityPaddleOcrDictionaryManifest.Validate(
                dictionary,
                manifest,
                configuredUseSpaceCharacter: true);

            var bootstrap = FindSingleSceneBootstrap();
            Undo.RecordObject(bootstrap, "Assign PhraseLayer Local PP-OCR Assets");

            var serialized = new SerializedObject(bootstrap);
            RequireProperty(serialized, "detectorModel").objectReferenceValue = detector;
            RequireProperty(serialized, "recognizerModel").objectReferenceValue = recognizer;
            RequireProperty(serialized, "characterDictionary").objectReferenceValue = dictionary;
            RequireProperty(serialized, "characterDictionaryManifest").objectReferenceValue = manifest;
            RequireProperty(serialized, "useSpaceCharacter").boolValue = true;
            var changed = serialized.ApplyModifiedProperties();

            if (changed)
            {
                EditorUtility.SetDirty(bootstrap);
                AssetDatabase.SaveAssets();
                Debug.Log("Assigned verified local PP-OCR assets to scene bootstrap: " + bootstrap.name, bootstrap);
            }
            else
            {
                Debug.Log("Verified local PP-OCR assets were already assigned to scene bootstrap: " + bootstrap.name, bootstrap);
            }
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve the reviewed com.unity.ai.inference 2.2.x package before assigning local OCR assets.");
#endif
        }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        private static Texture2D CreateSyntheticProbeTexture()
        {
            var texture = new Texture2D(
                SyntheticProbeSize,
                SyntheticProbeSize,
                TextureFormat.RGBA32,
                false,
                false);
            texture.name = "PhraseLayer-PP-OCR-Synthetic-Probe";
            texture.filterMode = FilterMode.Bilinear;

            var pixels = new Color32[SyntheticProbeSize * SyntheticProbeSize];
            for (var y = 0; y < SyntheticProbeSize; y++)
            {
                for (var x = 0; x < SyntheticProbeSize; x++)
                {
                    var checker = (((x / 16) + (y / 16)) & 1) == 0;
                    var value = checker ? (byte)224 : (byte)32;
                    pixels[(y * SyntheticProbeSize) + x] = new Color32(value, value, value, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static string ValidateUnitInterval(float[] values, string label)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (values.Length == 0)
                throw new InvalidOperationException(label + " must contain at least one value.");

            var minimum = double.PositiveInfinity;
            var maximum = double.NegativeInfinity;
            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    throw new InvalidOperationException(
                        label + " contains a non-finite value at index " + index + ".");
                }
                if (value < 0f || value > 1f)
                {
                    throw new InvalidOperationException(
                        label + " must be probabilistic in [0,1]. Observed " + value +
                        " at index " + index + ". The imported ONNX may expose logits instead of the reviewed probability contract.");
                }
                if (value < minimum) minimum = value;
                if (value > maximum) maximum = value;
            }

            return "range=[" + minimum + "," + maximum + "] values=" + values.Length;
        }

        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                var imported = AssetDatabase.LoadMainAssetAtPath(path);
                var actual = imported == null ? "missing" : imported.GetType().FullName;
                throw new InvalidOperationException(
                    "Expected " + typeof(T).FullName + " at " + path + " but Unity imported " + actual +
                    ". Run tools/prepare_unity_ocr_assets.py and resolve any Unity import errors before continuing.");
            }
            return asset;
        }

        private static UnityPaddleOcrBootstrapBehaviour FindSingleSceneBootstrap()
        {
            var all = Resources.FindObjectsOfTypeAll<UnityPaddleOcrBootstrapBehaviour>();
            UnityPaddleOcrBootstrapBehaviour found = null;
            var count = 0;
            for (var index = 0; index < all.Length; index++)
            {
                var candidate = all[index];
                if (candidate == null || EditorUtility.IsPersistent(candidate))
                    continue;
                if (!candidate.gameObject.scene.IsValid())
                    continue;

                found = candidate;
                count++;
            }

            if (count != 1 || found == null)
            {
                throw new InvalidOperationException(
                    "Expected exactly one UnityPaddleOcrBootstrapBehaviour in loaded scenes; found " + count + ".");
            }
            return found;
        }

        private static SerializedProperty RequireProperty(SerializedObject serialized, string name)
        {
            var property = serialized.FindProperty(name);
            if (property == null)
                throw new InvalidOperationException("UnityPaddleOcrBootstrapBehaviour serialized field missing: " + name);
            return property;
        }
#endif
    }
}
