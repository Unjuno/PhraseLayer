using System;
using System.Collections.Generic;
using System.Text.Json;
using PhraseLayer.Core.Translation;

namespace PhraseLayer.Tokenization.Microsoft
{
    public static class MicrosoftMlMarianTokenizerFactory
    {
        public static ITranslationTokenizer Create(
            byte[] sourceSentencePieceModel,
            byte[] targetSentencePieceModel,
            string vocabularyJson)
        {
            if (sourceSentencePieceModel == null) throw new ArgumentNullException(nameof(sourceSentencePieceModel));
            if (targetSentencePieceModel == null) throw new ArgumentNullException(nameof(targetSentencePieceModel));
            if (vocabularyJson == null) throw new ArgumentNullException(nameof(vocabularyJson));

            var vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabularyJson);
            if (vocabulary == null || vocabulary.Count == 0)
                throw new InvalidOperationException("Marian vocab.json did not contain a non-empty piece-to-id object.");

            var source = new MicrosoftMlSentencePieceProcessor(sourceSentencePieceModel);
            var target = new MicrosoftMlSentencePieceProcessor(targetSentencePieceModel);
            return new MarianSentencePieceTokenizer(source, target, vocabulary);
        }
    }
}
