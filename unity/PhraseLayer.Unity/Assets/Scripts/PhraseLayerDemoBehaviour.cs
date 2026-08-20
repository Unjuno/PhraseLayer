using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;
using UnityEngine;

namespace PhraseLayer.Unity
{
    public sealed class PhraseLayerDemoBehaviour : MonoBehaviour
    {
        private const string DefaultSource = "I was tired, so I went home, and I fell asleep immediately.";

        [SerializeField, TextArea(2, 5)] private string sourceText = DefaultSource;
        [SerializeField] private AssistanceMode assistanceMode = AssistanceMode.Balanced;

        private InMemoryLearnerModel learner;
        private LanguagePipeline pipeline;
        private MixedLanguagePlan currentPlan;
        private LearningEncounterSession currentEncounter;
        private string status = "Preparing PhraseLayer...";
        private string learningStatus = "No learning evidence recorded yet.";
        private Vector2 scroll;

        public string SourceText => sourceText;
        public string DisplayText => currentPlan != null ? currentPlan.DisplayText : sourceText;
        public MixedLanguagePlan CurrentPlan => currentPlan;

        private async void Start()
        {
            BuildPipeline();
            await ReplanAsync();
        }

        public async Task ReplanAsync()
        {
            if (pipeline == null) BuildPipeline();
            status = "Planning...";
            try
            {
                currentPlan = await pipeline.PlanAsync(sourceText, AssistancePolicy.ForMode(assistanceMode), sourceText);
                currentEncounter = new LearningEncounterSession(
                    currentPlan,
                    new LearnerAdaptationEngine(learner));
                status = string.Format(
                    "Mode: {0}  Target: {1:P0}  Selected: {2:P0}",
                    assistanceMode,
                    currentPlan.Assistance.TargetRatio,
                    currentPlan.Assistance.SelectedRatio);
            }
            catch (Exception exception)
            {
                currentEncounter = null;
                status = exception.Message;
                Debug.LogException(exception, this);
            }
        }

        public async Task SetProfileAsync(DemoLearnerProfile profile)
        {
            ConfigureLearner(profile);
            learningStatus = string.Format("Demo profile reset to {0}.", profile);
            await ReplanAsync();
        }

        public async Task RecordFirstAssistedAsync(LearningEvidenceKind evidence)
        {
            if (currentEncounter == null || currentPlan == null)
            {
                learningStatus = "No active encounter.";
                return;
            }

            if (currentPlan.Assistance.Decisions.Count == 0)
            {
                learningStatus = "This encounter has no assisted unit to score.";
                return;
            }

            var unit = currentPlan.Assistance.Decisions[0].Unit;
            currentEncounter.Record(unit, evidence);
            var summary = currentEncounter.Finish();
            var update = FindUpdate(summary, unit.Id);
            learningStatus = update == null
                ? string.Format("Recorded {0} for {1}.", evidence, unit.Text)
                : string.Format(
                    "{0}: {1}  {2:P0} → {3:P0}",
                    evidence,
                    unit.Text,
                    update.PreviousUnderstanding,
                    update.UpdatedUnderstanding);
            await ReplanAsync();
        }

        public async Task CompleteEncounterAsync(bool successfulUnassistedCompletion)
        {
            if (currentEncounter == null)
            {
                learningStatus = "No active encounter.";
                return;
            }

            var summary = currentEncounter.Finish(successfulUnassistedCompletion);
            learningStatus = string.Format(
                "Encounter finished. {0} learner update(s); unassisted success: {1}.",
                summary.Updates.Count,
                successfulUnassistedCompletion ? "yes" : "no");
            await ReplanAsync();
        }

        private static LearnerUpdate FindUpdate(LearningEncounterSummary summary, string unitId)
        {
            for (var i = 0; i < summary.Updates.Count; i++)
            {
                if (string.Equals(summary.Updates[i].Unit.Id, unitId, StringComparison.Ordinal))
                    return summary.Updates[i];
            }

            return null;
        }

        private void BuildPipeline()
        {
            learner = new InMemoryLearnerModel(0.90);
            ConfigureLearner(DemoLearnerProfile.Intermediate);

            var segmenter = new RuleBasedSemanticSegmenter(new[]
            {
                "was tired",
                "went home",
                "fell asleep"
            });

            var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "so i went home", "だから家に帰って" },
                { "was tired", "疲れていた" },
                { "went home", "家に帰った" },
                { "fell asleep", "眠ってしまった" },
                { "immediately", "すぐ" }
            };

            pipeline = new LanguagePipeline(
                segmenter,
                learner,
                new AssistancePlanner(),
                new DictionaryTranslationEngine(translations));
        }

        private void ConfigureLearner(DemoLearnerProfile profile)
        {
            if (learner == null) return;

            switch (profile)
            {
                case DemoLearnerProfile.Beginner:
                    learner.SetUnderstanding("I was tired", 0.30);
                    learner.SetUnderstanding("so I went home", 0.20);
                    learner.SetUnderstanding("and I fell asleep immediately", 0.35);
                    learner.SetUnderstanding("fell asleep", 0.20);
                    learner.SetUnderstanding("immediately", 0.35);
                    break;
                case DemoLearnerProfile.Advanced:
                    learner.SetUnderstanding("I was tired", 0.96);
                    learner.SetUnderstanding("so I went home", 0.92);
                    learner.SetUnderstanding("and I fell asleep immediately", 0.94);
                    learner.SetUnderstanding("fell asleep", 0.94);
                    learner.SetUnderstanding("immediately", 0.95);
                    break;
                default:
                    learner.SetUnderstanding("I was tired", 0.94);
                    learner.SetUnderstanding("so I went home", 0.20);
                    learner.SetUnderstanding("and I fell asleep immediately", 0.91);
                    learner.SetUnderstanding("fell asleep", 0.82);
                    learner.SetUnderstanding("immediately", 0.78);
                    break;
            }
        }

        private void OnGUI()
        {
            var oldSkin = GUI.skin;
            var box = new GUIStyle(oldSkin.box) { padding = new RectOffset(18, 18, 16, 16) };
            var title = new GUIStyle(oldSkin.label) { fontSize = 24, fontStyle = FontStyle.Bold, wordWrap = true };
            var body = new GUIStyle(oldSkin.label) { fontSize = 18, wordWrap = true };
            var assisted = new GUIStyle(body) { fontSize = 26, fontStyle = FontStyle.Bold };

            GUILayout.BeginArea(new Rect(24, 24, Mathf.Min(900, Screen.width - 48), Mathf.Max(260, Screen.height - 48)), box);
            scroll = GUILayout.BeginScrollView(scroll);

            GUILayout.Label("PhraseLayer — Editor Demo", title);
            GUILayout.Space(8);
            GUILayout.Label("Source", body);
            GUILayout.Label(sourceText, body);
            GUILayout.Space(16);
            GUILayout.Label("Adaptive mixed-language view", body);
            GUILayout.Label(DisplayText, assisted);
            GUILayout.Space(12);
            GUILayout.Label(status, body);
            GUILayout.Label(learningStatus, body);
            GUILayout.Space(16);

            GUILayout.Label("Encounter evidence (applies to the next view)", body);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Remembered assisted phrase", GUILayout.Height(36)))
                _ = RecordFirstAssistedAsync(LearningEvidenceKind.RecallSucceeded);
            if (GUILayout.Button("Need more help", GUILayout.Height(36)))
                _ = RecordFirstAssistedAsync(LearningEvidenceKind.AssistanceRequested);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Read without extra help", GUILayout.Height(36)))
                _ = CompleteEncounterAsync(true);
            if (GUILayout.Button("Continue", GUILayout.Height(36)))
                _ = CompleteEncounterAsync(false);
            GUILayout.EndHorizontal();

            GUILayout.Space(16);
            GUILayout.Label("Reset demo learner", body);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Beginner", GUILayout.Height(36))) _ = SetProfileAsync(DemoLearnerProfile.Beginner);
            if (GUILayout.Button("Intermediate", GUILayout.Height(36))) _ = SetProfileAsync(DemoLearnerProfile.Intermediate);
            if (GUILayout.Button("Advanced", GUILayout.Height(36))) _ = SetProfileAsync(DemoLearnerProfile.Advanced);
            GUILayout.EndHorizontal();

            GUILayout.Space(12);
            GUILayout.Label("Assistance mode", body);
            GUILayout.BeginHorizontal();
            foreach (AssistanceMode mode in Enum.GetValues(typeof(AssistanceMode)))
            {
                if (GUILayout.Button(mode.ToString(), GUILayout.Height(32)))
                {
                    assistanceMode = mode;
                    _ = ReplanAsync();
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }

    public enum DemoLearnerProfile
    {
        Beginner = 0,
        Intermediate = 1,
        Advanced = 2
    }
}
