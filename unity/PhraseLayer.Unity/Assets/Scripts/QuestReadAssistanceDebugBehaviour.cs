using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Spatial;
using PhraseLayer.Core.Translation;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Device/debug vertical slice from an already-presented Quest OCR observation to viewport-aligned
    /// PhraseLayer assistance. It intentionally uses a tiny local dictionary until the reviewed local NMT
    /// runtime is integrated; camera/OCR is never repeated here.
    /// </summary>
    public sealed class QuestReadAssistanceDebugBehaviour : MonoBehaviour
    {
        [Serializable]
        private sealed class TranslationEntry
        {
            public TranslationEntry() { }
            public TranslationEntry(string source, string target)
            {
                this.source = source;
                this.target = target;
            }

            public string source = string.Empty;
            public string target = string.Empty;
        }

        [SerializeField] private OcrViewportDebugBehaviour ocrPresenter = default(OcrViewportDebugBehaviour);
        [SerializeField] private UnityLearnerProfileBehaviour learnerProfile = default(UnityLearnerProfileBehaviour);
        [SerializeField] private AssistanceMode assistanceMode = AssistanceMode.Balanced;
        [SerializeField] private bool showPartialCoverage = false;
        [SerializeField] private TranslationEntry[] localDebugTranslations =
        {
            new TranslationEntry("keep off", "立ち入らない"),
            new TranslationEntry("emergency exit", "非常口"),
            new TranslationEntry("went home", "家に帰った"),
            new TranslationEntry("fell asleep", "眠ってしまった")
        };

        private ReadObservationPipeline pipeline;
        private CancellationTokenSource lifetime;
        private Task worker;
        private OcrObservation pendingObservation;
        private ImageFrame pendingFrame;
        private long latestSequence;
        private ReadModeSpatialResult lastResult;
        private string status = "Waiting for real OCR observation.";

        public ReadModeSpatialResult LastResult => lastResult;
        public string Status => status;

        private void OnEnable()
        {
            EnsureReferences();
            learnerProfile.Initialize();
            pipeline = BuildPipeline();
            lifetime?.Dispose();
            lifetime = new CancellationTokenSource();
            ocrPresenter.ObservationPresented += HandleObservationPresented;
        }

        private void OnDisable()
        {
            if (ocrPresenter != null)
                ocrPresenter.ObservationPresented -= HandleObservationPresented;
            lifetime?.Cancel();
        }

        private void OnDestroy()
        {
            if (ocrPresenter != null)
                ocrPresenter.ObservationPresented -= HandleObservationPresented;
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;
        }

        private ReadObservationPipeline BuildPipeline()
        {
            var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var multiwordExpressions = new List<string>();

            if (localDebugTranslations != null)
            {
                for (var index = 0; index < localDebugTranslations.Length; index++)
                {
                    var entry = localDebugTranslations[index];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.source) || string.IsNullOrWhiteSpace(entry.target))
                        continue;

                    var normalized = InMemoryLearnerModel.Normalize(entry.source);
                    translations[normalized] = entry.target.Trim();
                    if (normalized.IndexOf(' ') >= 0)
                        multiwordExpressions.Add(entry.source.Trim());
                }
            }

            var language = new LanguagePipeline(
                new RuleBasedSemanticSegmenter(multiwordExpressions),
                learnerProfile.Model,
                new AssistancePlanner(),
                new DictionaryTranslationEngine(translations));
            return new ReadObservationPipeline(language);
        }

        private void HandleObservationPresented(OcrObservation observation, ImageFrame frame)
        {
            if (observation == null || frame == null || lifetime == null || lifetime.IsCancellationRequested)
                return;

            pendingObservation = observation;
            pendingFrame = frame;
            latestSequence++;

            if (worker == null || worker.IsCompleted)
                worker = DrainLatestObservationAsync(lifetime.Token);
        }

        private async Task DrainLatestObservationAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var observation = pendingObservation;
                    var frame = pendingFrame;
                    var sequence = latestSequence;
                    pendingObservation = null;
                    pendingFrame = null;

                    if (observation == null || frame == null)
                        return;

                    var result = await pipeline.ProcessAsync(
                        frame,
                        observation,
                        AssistancePolicy.ForMode(assistanceMode),
                        cancellationToken);

                    if (sequence == latestSequence)
                    {
                        lastResult = result;
                        status = string.Format(
                            "Read assistance: {0} target(s), exact={1}, partial={2}, unresolved={3}.",
                            result.SpatialAssistance.Targets.Count,
                            result.SpatialAssistance.ExactCount,
                            result.SpatialAssistance.PartialCount,
                            result.SpatialAssistance.UnresolvedCount);
                    }

                    if (pendingObservation == null)
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                // Lifecycle cancellation is expected.
            }
            catch (Exception exception)
            {
                status = "Read assistance failed: " + exception.Message;
                Debug.LogException(exception, this);
            }
        }

        private void OnGUI()
        {
            if (lastResult == null) return;

            var targets = lastResult.SpatialAssistance.Targets;
            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                if (!target.Envelope.HasValue) continue;
                if (target.Coverage == SpatialAssistanceCoverage.Unresolved) continue;
                if (target.Coverage == SpatialAssistanceCoverage.Partial && !showPartialCoverage) continue;
                if (string.Equals(target.Segment.SourceText, target.Segment.DisplayText, StringComparison.Ordinal)) continue;

                GUI.Box(ToScreenRect(target.Envelope.Value), target.Segment.DisplayText);
            }
        }

        private static Rect ToScreenRect(ViewportEnvelope envelope)
        {
            return new Rect(
                (float)(envelope.MinU * Screen.width),
                (float)((1.0 - envelope.MaxV) * Screen.height),
                (float)((envelope.MaxU - envelope.MinU) * Screen.width),
                (float)((envelope.MaxV - envelope.MinV) * Screen.height));
        }

        private void EnsureReferences()
        {
            if (ocrPresenter == null)
                throw new InvalidOperationException("Assign OcrViewportDebugBehaviour to QuestReadAssistanceDebugBehaviour.");
            if (learnerProfile == null)
                throw new InvalidOperationException("Assign UnityLearnerProfileBehaviour to QuestReadAssistanceDebugBehaviour.");
        }
    }
}
