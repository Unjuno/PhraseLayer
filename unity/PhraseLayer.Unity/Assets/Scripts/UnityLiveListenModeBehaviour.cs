using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Pipeline;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Scene adapter that wires microphone utterances into the Core Listen Mode coordinator:
    /// microphone AudioChunk -> offline ASR -> semantic assistance/translation -> mixed-language plan.
    ///
    /// The adapter lazily waits for the demo LanguagePipeline and Moonshine bootstrap, avoiding Unity Start-order
    /// coupling. Core's latest-timestamp-wins coordinator prevents stale ASR work from replacing newer speech.
    /// Timing fields separate ASR from adaptive language planning while also retaining the end-to-end submission
    /// time. They are measurement hooks for real-device validation and must not be treated as Quest performance
    /// evidence until captured on the target headset.
    /// </summary>
    public sealed class UnityLiveListenModeBehaviour : MonoBehaviour
    {
        [SerializeField] private PhraseLayerDemoBehaviour languageSource = null;
        [SerializeField] private UnityMoonshineAsrBootstrapBehaviour asrBootstrap = null;
        [SerializeField] private UnityMicrophoneUtteranceSourceBehaviour microphoneSource = null;
        [SerializeField] private AssistanceMode assistanceMode = AssistanceMode.Balanced;
        [SerializeField] private string latestTranscript = string.Empty;
        [SerializeField] private string latestDisplayText = string.Empty;
        [SerializeField] private string lastStatus = "Listen Mode is waiting for runtime dependencies.";
        [SerializeField] private double latestAudioDurationSeconds;
        [SerializeField] private double latestAsrMilliseconds;
        [SerializeField] private double latestLanguagePlanMilliseconds;
        [SerializeField] private double latestCoreProcessingMilliseconds;
        [SerializeField] private double latestPipelineMilliseconds;
        [SerializeField] private double latestProcessingToAudioRatio;
        [SerializeField] private long processedUtteranceCount;

        private LanguagePipeline languagePipeline;
        private LiveListenModeCoordinator coordinator;
        private CancellationTokenSource lifetimeCancellation;
        private MixedLanguagePlan latestPlan;
        private bool sourceSubscribed;

        public string LatestTranscript => latestTranscript;
        public string LatestDisplayText => latestDisplayText;
        public string LastStatus => lastStatus;
        public MixedLanguagePlan LatestPlan => latestPlan;
        public bool IsReady => coordinator != null;
        public double LatestAudioDurationSeconds => latestAudioDurationSeconds;
        public double LatestAsrMilliseconds => latestAsrMilliseconds;
        public double LatestLanguagePlanMilliseconds => latestLanguagePlanMilliseconds;
        public double LatestCoreProcessingMilliseconds => latestCoreProcessingMilliseconds;
        public double LatestPipelineMilliseconds => latestPipelineMilliseconds;
        public double LatestProcessingToAudioRatio => latestProcessingToAudioRatio;
        public long ProcessedUtteranceCount => processedUtteranceCount;

        private void OnEnable()
        {
            if (lifetimeCancellation == null)
                lifetimeCancellation = new CancellationTokenSource();
            SubscribeSource();
            TryBuildCoordinator();
        }

        private void Update()
        {
            if (languagePipeline == null && languageSource != null && languageSource.Pipeline != null)
                languagePipeline = languageSource.Pipeline;
            if (coordinator == null)
                TryBuildCoordinator();
        }

        public void SetSceneReferences(
            PhraseLayerDemoBehaviour language,
            UnityMoonshineAsrBootstrapBehaviour bootstrap,
            UnityMicrophoneUtteranceSourceBehaviour microphone)
        {
            if (language == null) throw new ArgumentNullException(nameof(language));
            if (bootstrap == null) throw new ArgumentNullException(nameof(bootstrap));
            if (microphone == null) throw new ArgumentNullException(nameof(microphone));

            UnsubscribeSource();
            languageSource = language;
            languagePipeline = language.Pipeline;
            asrBootstrap = bootstrap;
            microphoneSource = microphone;
            SubscribeSource();
            RebuildCoordinator();
        }

        public void SetLanguagePipeline(LanguagePipeline pipeline)
        {
            languagePipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            RebuildCoordinator();
        }

        public void SetAsrBootstrap(UnityMoonshineAsrBootstrapBehaviour bootstrap)
        {
            asrBootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
            RebuildCoordinator();
        }

        public void SetMicrophoneSource(UnityMicrophoneUtteranceSourceBehaviour source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            UnsubscribeSource();
            microphoneSource = source;
            SubscribeSource();
        }

        public void SetAssistanceMode(AssistanceMode mode)
        {
            assistanceMode = mode;
        }

        public Task<LiveListenModeProcessingResult> SubmitAsync(
            PhraseLayer.Core.Inputs.AudioChunk audio,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (audio == null) throw new ArgumentNullException(nameof(audio));
            if (!TryBuildCoordinator())
                throw new InvalidOperationException("Listen Mode runtime dependencies are not ready: " + lastStatus);
            return coordinator.SubmitAsync(audio, AssistancePolicy.ForMode(assistanceMode), cancellationToken);
        }

        private void SubscribeSource()
        {
            if (sourceSubscribed || microphoneSource == null || !enabled)
                return;
            microphoneSource.UtteranceReady += OnUtteranceReady;
            sourceSubscribed = true;
        }

        private void UnsubscribeSource()
        {
            if (!sourceSubscribed || microphoneSource == null)
                return;
            microphoneSource.UtteranceReady -= OnUtteranceReady;
            sourceSubscribed = false;
        }

        private void OnUtteranceReady(PhraseLayer.Core.Inputs.AudioChunk audio)
        {
            _ = ProcessUtteranceAsync(audio);
        }

        private async Task ProcessUtteranceAsync(PhraseLayer.Core.Inputs.AudioChunk audio)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (!TryBuildCoordinator())
                {
                    lastStatus = "Listen Mode skipped an utterance because the ASR/language runtime is not ready.";
                    return;
                }

                var cancellationToken = lifetimeCancellation != null
                    ? lifetimeCancellation.Token
                    : CancellationToken.None;
                var result = await coordinator.SubmitAsync(
                    audio,
                    AssistancePolicy.ForMode(assistanceMode),
                    cancellationToken);

                if (!result.WasProcessed)
                {
                    lastStatus = "Listen Mode utterance " + result.Status + ".";
                    return;
                }

                stopwatch.Stop();
                var audioDurationSeconds = audio.Samples.Length / (double)audio.SampleRate;
                latestAudioDurationSeconds = audioDurationSeconds;
                latestPipelineMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                latestProcessingToAudioRatio = audioDurationSeconds > 0.0
                    ? stopwatch.Elapsed.TotalSeconds / audioDurationSeconds
                    : 0.0;
                processedUtteranceCount = checked(processedUtteranceCount + 1);

                var output = result.Output;
                latestAsrMilliseconds = output.Timings.AsrMilliseconds;
                latestLanguagePlanMilliseconds = output.Timings.LanguagePlanMilliseconds;
                latestCoreProcessingMilliseconds = output.Timings.TotalMilliseconds;
                latestTranscript = output.Observation.Text;
                latestPlan = output.LanguagePlan;
                latestDisplayText = latestPlan != null ? latestPlan.DisplayText : latestTranscript;
                lastStatus = string.Format(
                    CultureInfo.InvariantCulture,
                    "Listen Mode processed {0:F2}s in {1:F1}ms (ASR={2:F1}ms, plan={3:F1}ms, pipeline/audio={4:F3}): transcript={5}; adaptive-plan={6}.",
                    latestAudioDurationSeconds,
                    latestPipelineMilliseconds,
                    latestAsrMilliseconds,
                    latestLanguagePlanMilliseconds,
                    latestProcessingToAudioRatio,
                    string.IsNullOrWhiteSpace(latestTranscript) ? "empty" : "final",
                    latestPlan != null ? "yes" : "no");
                Debug.Log(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "PHRASELAYER_LISTEN_METRIC utterance={0} audio_s={1:F6} asr_ms={2:F3} plan_ms={3:F3} core_ms={4:F3} pipeline_ms={5:F3} processing_to_audio={6:F6} transcript_chars={7} adaptive_plan={8}",
                        processedUtteranceCount,
                        latestAudioDurationSeconds,
                        latestAsrMilliseconds,
                        latestLanguagePlanMilliseconds,
                        latestCoreProcessingMilliseconds,
                        latestPipelineMilliseconds,
                        latestProcessingToAudioRatio,
                        latestTranscript != null ? latestTranscript.Length : 0,
                        latestPlan != null ? 1 : 0),
                    this);
            }
            catch (OperationCanceledException)
            {
                lastStatus = "Listen Mode utterance cancelled.";
            }
            catch (Exception exception)
            {
                lastStatus = exception.GetType().Name + ": " + exception.Message;
                Debug.LogException(exception, this);
            }
            finally
            {
                if (stopwatch.IsRunning)
                    stopwatch.Stop();
            }
        }

        private bool TryBuildCoordinator()
        {
            if (coordinator != null)
                return true;
            if (languagePipeline == null)
            {
                if (languageSource != null && languageSource.Pipeline != null)
                    languagePipeline = languageSource.Pipeline;
                if (languagePipeline == null)
                {
                    lastStatus = "Listen Mode is waiting for a LanguagePipeline.";
                    return false;
                }
            }
            if (asrBootstrap == null)
            {
                lastStatus = "Listen Mode is waiting for a Moonshine ASR bootstrap.";
                return false;
            }
            if (!asrBootstrap.TryGetAsrEngine(out var asrEngine))
            {
                lastStatus = "Listen Mode is waiting for Moonshine ASR: " + asrBootstrap.LastStatus;
                return false;
            }

            coordinator = new LiveListenModeCoordinator(
                new ListenModeObservationProcessor(asrEngine, languagePipeline));
            lastStatus = "Listen Mode ready: microphone -> Moonshine ASR -> adaptive language plan.";
            Debug.Log(lastStatus, this);
            return true;
        }

        private void RebuildCoordinator()
        {
            coordinator?.Dispose();
            coordinator = null;
            TryBuildCoordinator();
        }

        private void OnDisable()
        {
            UnsubscribeSource();
            lifetimeCancellation?.Cancel();
            lifetimeCancellation?.Dispose();
            lifetimeCancellation = null;
            coordinator?.Dispose();
            coordinator = null;
        }

        private void OnDestroy()
        {
            OnDisable();
        }
    }
}
