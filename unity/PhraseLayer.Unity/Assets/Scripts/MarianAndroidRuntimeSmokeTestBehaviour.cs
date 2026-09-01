using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Translation-only Android runtime gate for the product Marian stack. It drives the same PhraseLayerDemoBehaviour
    /// LanguagePipeline used by the product fixture, requires a semantic-span replacement for the deterministic
    /// "keep off" fixture, and compares the final display text against the staged offline Transformers reference.
    /// No camera, OCR, MRUK, network, or Quest-specific API participates in this gate.
    /// </summary>
    public sealed class MarianAndroidRuntimeSmokeTestBehaviour : MonoBehaviour
    {
        private const string FixtureSource = "keep off";
        private const string ReferenceResourcePath = "LocalTranslationAssets/marian-reference";

        [SerializeField] private PhraseLayerDemoBehaviour demo = null;
        [SerializeField] private UnityMarianTranslationBootstrapBehaviour bootstrap = null;
        [SerializeField] private bool autoRun = true;
        [SerializeField] private string lastReport = "Marian Android runtime smoke not started.";
        [SerializeField] private bool lastPassed;

        private bool isRunning;

        public bool AutoRun => autoRun;
        public bool IsRunning => isRunning;
        public bool LastPassed => lastPassed;
        public string LastReport => lastReport;

        public void SetSceneReferences(
            PhraseLayerDemoBehaviour demoBehaviour,
            UnityMarianTranslationBootstrapBehaviour bootstrapBehaviour)
        {
            demo = demoBehaviour ?? throw new ArgumentNullException(nameof(demoBehaviour));
            bootstrap = bootstrapBehaviour ?? throw new ArgumentNullException(nameof(bootstrapBehaviour));
        }

        public void SetAutoRun(bool enabled)
        {
            autoRun = enabled;
        }

        private async void Start()
        {
            if (!autoRun)
                return;

            try
            {
                await RunAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        public async Task<string> RunAsync()
        {
            if (isRunning)
                throw new InvalidOperationException("Marian Android runtime smoke is already running.");
            EnsureReferences();

            isRunning = true;
            lastPassed = false;
            var started = Time.realtimeSinceStartupAsDouble;
            try
            {
                InitializeBootstrap();
                if (!bootstrap.IsSupported || !bootstrap.IsReady)
                {
                    throw new InvalidOperationException(
                        "Marian Android runtime smoke requires an initialized product translation bootstrap.");
                }
                if (!demo.UsesTranslationEngineOverride)
                {
                    throw new InvalidOperationException(
                        "Marian Android runtime smoke refuses to run against the demo dictionary fallback.");
                }

                var expectedTranslation = LoadExpectedTranslation(FixtureSource);
                demo.SetSourceText(FixtureSource);
                await demo.ReplanAsync();

                var plan = demo.CurrentPlan;
                if (plan == null)
                    throw new InvalidOperationException("Marian Android runtime smoke did not produce a LanguagePipeline plan.");
                if (plan.Assistance.Decisions.Count != 1)
                {
                    throw new InvalidOperationException(
                        "Marian Android runtime smoke expected exactly one assisted semantic unit.");
                }
                if (plan.Segments.Count != 1 || !plan.Segments[0].IsAssisted)
                {
                    throw new InvalidOperationException(
                        "Marian Android runtime smoke expected exactly one assisted replacement segment.");
                }
                if (!string.Equals(plan.Segments[0].SourceText, FixtureSource, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Marian Android runtime smoke changed the deterministic source semantic span.");
                }
                if (!string.Equals(plan.DisplayText, expectedTranslation, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Marian Android runtime smoke final display text did not match the staged offline reference.");
                }

                lastPassed = true;
                lastReport = BuildReport(
                    "PASS",
                    Time.realtimeSinceStartupAsDouble - started,
                    plan.Assistance.Decisions.Count,
                    plan.Segments.Count,
                    plan.DisplayText.Length,
                    referenceMatched: true);
                Debug.Log(lastReport, this);
                return lastReport;
            }
            catch (Exception exception)
            {
                lastReport = BuildReport(
                    "FAIL_EXCEPTION",
                    Time.realtimeSinceStartupAsDouble - started,
                    0,
                    0,
                    0,
                    referenceMatched: false) +
                    "\nfailure_type=" + exception.GetType().Name;
                Debug.Log(lastReport, this);
                throw;
            }
            finally
            {
                isRunning = false;
            }
        }

        private void InitializeBootstrap()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            bootstrap.Initialize();
#else
            throw new PlatformNotSupportedException(
                "Marian Android runtime smoke requires the reviewed com.unity.ai.inference 2.2.x compile gate.");
#endif
        }

        private static string LoadExpectedTranslation(string sourceText)
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            var asset = Resources.Load<TextAsset>(ReferenceResourcePath);
            if (asset == null || string.IsNullOrWhiteSpace(asset.text))
                throw new InvalidOperationException("Staged Marian runtime reference fixture is missing or empty.");

            var reference = JsonUtility.FromJson<MarianReferenceFixture>(asset.text);
            if (reference == null ||
                !string.Equals(reference.purpose, "phrase-layer-marian-greedy-reference", StringComparison.Ordinal) ||
                reference.samples == null)
            {
                throw new InvalidOperationException("Staged Marian runtime reference fixture is invalid.");
            }

            for (var index = 0; index < reference.samples.Length; index++)
            {
                var sample = reference.samples[index];
                if (sample != null &&
                    string.Equals(sample.source_text, sourceText, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(sample.translated_text))
                {
                    return sample.translated_text;
                }
            }

            throw new InvalidOperationException(
                "Staged Marian runtime reference fixture does not contain the deterministic smoke sample.");
#else
            throw new PlatformNotSupportedException(
                "Marian Android runtime reference loading requires the reviewed com.unity.ai.inference 2.2.x compile gate.");
#endif
        }

        private static string BuildReport(
            string status,
            double elapsedSeconds,
            int assistedUnits,
            int segments,
            int displayLength,
            bool referenceMatched)
        {
            var builder = new StringBuilder(512);
            builder.AppendLine("PhraseLayer Marian Android runtime smoke " + status);
            builder.Append("elapsed_ms=").Append((elapsedSeconds * 1000.0).ToString("F1"))
                .Append(" bootstrap_ready=true")
                .Append(" translation_override=true")
                .Append(" assisted_units=").Append(assistedUnits)
                .Append(" segments=").Append(segments)
                .Append(" reference_match=").Append(referenceMatched ? "true" : "false")
                .Append(" display_length=").Append(displayLength)
                .AppendLine();
            builder.AppendLine(
                "translation_runtime=MarianOpusMtEnJa generation_backend=UnityMarianDeviceResidentGenerationBackend " +
                "tokenizer_runtime=Microsoft.ML.Tokenizers semantic_span_pipeline=true product_translation_gate=true");
            builder.AppendLine("fixture_source=keep-off translated_text=<redacted; exact offline reference match required>");
            return builder.ToString().TrimEnd();
        }

        private void EnsureReferences()
        {
            if (demo == null)
                throw new InvalidOperationException("Assign PhraseLayerDemoBehaviour to MarianAndroidRuntimeSmokeTestBehaviour.");
            if (bootstrap == null)
                throw new InvalidOperationException("Assign UnityMarianTranslationBootstrapBehaviour to MarianAndroidRuntimeSmokeTestBehaviour.");
        }

        [Serializable]
        private sealed class MarianReferenceFixture
        {
            public string purpose = string.Empty;
            public MarianReferenceSample[] samples = Array.Empty<MarianReferenceSample>();
        }

        [Serializable]
        private sealed class MarianReferenceSample
        {
            public string source_text = string.Empty;
            public string translated_text = string.Empty;
        }
    }
}
