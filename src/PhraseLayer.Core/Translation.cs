using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Learning;

namespace PhraseLayer.Core.Translation
{
    public interface ITranslationEngine
    {
        Task<string> TranslateAsync(string sourceText, string context, CancellationToken cancellationToken = default(CancellationToken));
    }

    public sealed class DictionaryTranslationEngine : ITranslationEngine
    {
        private readonly IReadOnlyDictionary<string, string> _translations;
        public DictionaryTranslationEngine(IReadOnlyDictionary<string, string> translations) { _translations = translations ?? throw new ArgumentNullException(nameof(translations)); }
        public Task<string> TranslateAsync(string sourceText, string context, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string translation;
            return Task.FromResult(_translations.TryGetValue(InMemoryLearnerModel.Normalize(sourceText), out translation) ? translation : sourceText);
        }
    }
}
