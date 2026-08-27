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
    /// PhraseLayer assistance. A tiny local dictionary remains the safe bootstrap fallback, while the actual
    /// translation engine is injectable through ITranslationEngine so the reviewed local NMT runtime can replace
    /// it without changing the OCR/semantic/spatial pipeline.
    ///
    /// The visible language plan is frozen per Read encounter. OCR geometry may refresh, but a transient OCR
    /// mutation cannot cause the displayed source/translation mix to oscillate frame by frame. Replacing the
    /// translation engine explicitly resets the encounter so one encounter can never mix outputs from two engines.
    /// Small viewport-space OCR jitter is smoothed per semantic target; large movement is accepted immediately.
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
        [SerializeField] private float overlayBlendFactor = 0.35f;
        [SerializeField] private float overlayResetCenterDistance = 0.10f;
        [SerializeField] private TranslationEntry[] localDebugTranslations =
        {
            new TranslationEntry("keep off", "立ち入らない"),
            new TranslationEntry("emergency exit", "非常口"),
            new TranslationEntry("went home", "家に帰った"),
            new TranslationEntry("fell asleep", "眠ってしまった")
        };

        private ReadEncounterPipeline pipeline;
        private ITranslationEngine configuredTranslationEngine;
        private CancellationTokenSource lifetime;
        private Task worker;
        private OcrObservation pendingObservation;
        private ImageFrame pendingFrame;
        private long latestSequence;
        private ReadModeSpatialResult lastResult;
        private ViewportEnvelopeStabilizer overlayStabilizer;
        private readonly Dictionary<string, ViewportEnvelope> stabilizedEnvelopes =
            new Dictionary<string, ViewportEnvelope>(StringComparer.Ordinal);
        private string stabilizedEncounterId = string.Empty;
        private string currentEncounterId = string.Empty;
        private string status = "Waiting for real OCR observation.";

        public ReadModeSpatialResult LastResult => lastResult;
        public string CurrentEncounterId => currentEncounterId;
        public string Status => status;
        public bool HasConfiguredTranslationEngine => configuredTranslationEngine != null;

        /// <summary>
        /// Replaces the translation implementation used for future Read encounters.
        /// Ownership remains with the caller; this behaviour does not dispose the injected engine.
        /// If an encounter pipeline already exists it is reset before rebuilding, so an existing frozen plan is
        /// never partially recomputed with a different translation engine.
        /// </summary>
        public void ConfigureTranslationEngine(ITranslationEngine engine)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            configuredTranslationEngine = engine;

            if (pipeline != null)
            {
                pipeline.Reset();
                pipeline = BuildPipeline();
                lastResult = null;
                currentEncounterId = string.Empty;
                ResetOverlayStability();
                status = "Translation engine changed; waiting for a new Read encounter: " + engine.GetType().Name;
            }
        }

        private void OnEnable()
        {
            EnsureReferences();
            learnerProfile.Initialize();
            pipeline = BuildPipeline();
            overlayStabilizer = BuildOverlayStabilizer();
            ResetOverlayStability();
            lifetime?.Dispose();
            lifetime = new CancellationTokenSource();
            ocrPresenter.ObservationPresented += HandleObservationPresented;
        }

        private void OnDisable()
        {
            if (ocrPresenter != null)
                ocrPresenter.ObservationPresented -= HandleObservationPresented;
            lifetime?.Cancel();
            pipeline?.Reset();
            ResetOverlayStability();
        }

        private void OnDestroy()
        {
            if (ocrPresenter != null)
                ocrPresenter.ObservationPresented -= HandleObservationPresented;
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;
            pipeline?.Reset();
            pipeline = null;
            ResetOverlayStability();
            overlayStabilizer = null;
        }

        private ReadEncounterPipeline BuildPipeline()
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

            ITranslationEngine translationEngine = configuredTranslationEngine;
            if (translationEngine == null)
                translationEngine = new DictionaryTranslationEngine(translations);

            var language = new LanguagePipeline(
                new RuleBasedSemanticSegmenter(multiwordExpressions),
                learnerProfile.Model,
                new AssistancePlanner(),
                translationEngine);
            return new ReadEncounterPipeline(language);
        }

        private ViewportEnvelopeStabilizer BuildOverlayStabilizer()
        {
            var blendFactor = Math.Max(0.01, Math.Min(1.0, overlayBlendFactor));
            var resetDistance = Math.Max(0.0, overlayResetCenterDistance);
            return new ViewportEnvelopeStabilizer(new ViewportEnvelopeStabilizerOptions
            {
                BlendFactor = blendFactor,
                ResetCenterDistance = resetDistance,
            });
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

                    var encounter = await pipeline.ProcessAsync(
                        frame,
                        observation,
                        AssistancePolicy.ForMode(assistanceMode),
                        cancellationToken);

                    if (sequence == latestSequence && !encounter.Decision.IsStale)
                    {
                        currentEncounterId = encounter.Decision.EncounterId;
                        var result = encounter.SpatialResult;

                        // A one-frame contradictory OCR sample must not erase a stable overlay. Hold the most
                        // recent placeable geometry until the encounter tracker either recovers or confirms a switch.
                        var keepPreviousOverlay =
                            !encounter.Decision.IsNewEncounter &&
                            lastResult != null &&
                            lastResult.SpatialAssistance.ExactCount > 0 &&
                            (encounter.Decision.IsPendingSwitch || result.SpatialAssistance.ExactCount == 0);

                        if (!keepPreviousOverlay)
                        {
                            lastResult = result;
                            UpdateStabilizedEnvelopes(encounter.Decision.EncounterId, result);
                        }

                        status = string.Format(
                            "Read encounter {0} | {1} | targets={2}, exact={3}, partial={4}, unresolved={5}, held={6}, stabilized={7}.",
                            encounter.Decision.EncounterId,
                            encounter.Decision.Transition,
                            result.SpatialAssistance.Targets.Count,
                            result.SpatialAssistance.ExactCount,
                            result.SpatialAssistance.PartialCount,
                            result.SpatialAssistance.UnresolvedCount,
                            keepPreviousOverlay,
                            stabilizedEnvelopes.Count);
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

        private void UpdateStabilizedEnvelopes(string encounterId, ReadModeSpatialResult result)
        {
            if (overlayStabilizer == null)
                overlayStabilizer = BuildOverlayStabilizer();

            if (!string.Equals(stabilizedEncounterId, encounterId, StringComparison.Ordinal))
            {
                overlayStabilizer.Reset();
                stabilizedEncounterId = encounterId;
            }

            stabilizedEnvelopes.Clear();
            var targets = result.SpatialAssistance.Targets;
            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                if (!target.Envelope.HasValue || target.Segment.Unit == null)
                    continue;

                var key = target.Segment.Unit.Id;
                stabilizedEnvelopes[key] = overlayStabilizer.Stabilize(key, target.Envelope.Value);
            }
        }

        private void ResetOverlayStability()
        {
            overlayStabilizer?.Reset();
            stabilizedEnvelopes.Clear();
            stabilizedEncounterId = string.Empty;
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

                var envelope = target.Envelope.Value;
                if (target.Segment.Unit != null &&
                    stabilizedEnvelopes.TryGetValue(target.Segment.Unit.Id, out var stabilized))
                {
                    envelope = stabilized;
                }

                GUI.Box(ToScreenRect(envelope), target.Segment.DisplayText);
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
