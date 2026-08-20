using System;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Scene-facing owner for the persistent learner model used by production pipelines.
    /// The Editor demo intentionally remains ephemeral so demo profile buttons cannot overwrite real learner history.
    /// </summary>
    public sealed class UnityLearnerProfileBehaviour : MonoBehaviour
    {
        [SerializeField] private float fallbackDefaultUnderstanding = 0.55f;

        private PersistentLearnerModel model;
        private UnityLearnerProfileStore store;

        public bool IsInitialized => model != null;
        public IMutableLearnerModel Model => model ?? throw new InvalidOperationException(
            "Learner profile has not been initialized yet.");
        public string StoragePath
        {
            get
            {
#if UNITY_5_3_OR_NEWER
                return store != null ? store.FilePath : string.Empty;
#else
                return string.Empty;
#endif
            }
        }

        private void Awake()
        {
            Initialize();
        }

        public void Initialize()
        {
            if (model != null) return;
            if (float.IsNaN(fallbackDefaultUnderstanding) ||
                float.IsInfinity(fallbackDefaultUnderstanding) ||
                fallbackDefaultUnderstanding < 0f ||
                fallbackDefaultUnderstanding > 1f)
            {
                throw new InvalidOperationException(
                    "Fallback learner understanding must be finite and within [0,1].");
            }

            store = new UnityLearnerProfileStore();
            model = new PersistentLearnerModel(store, fallbackDefaultUnderstanding);
        }

        public void SetUnderstanding(string text, double understanding)
        {
            Model.SetUnderstanding(text, understanding);
        }

        public LearnerProfileSnapshot CreateSnapshot()
        {
            return Model.CreateSnapshot();
        }

        /// <summary>
        /// Starts a deferred learning encounter backed by the persistent learner profile.
        /// Evidence collected in the returned session is saved only when the session is finished.
        /// </summary>
        public LearningEncounterSession BeginEncounter(
            MixedLanguagePlan plan,
            LearnerAdaptationPolicy policy = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            Initialize();
            var adaptation = new LearnerAdaptationEngine(Model, policy);
            return new LearningEncounterSession(plan, adaptation);
        }
    }
}
