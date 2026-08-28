using System;
using System.Reflection;
using PhraseLayer.Core.Translation;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Late-bound bridge for the optional managed SentencePiece runtime.
    /// PhraseLayer.Unity deliberately does not reference Microsoft.ML.Tokenizers or the adapter assembly
    /// at compile time. Reviewed managed DLLs can be staged locally under Assets/LocalTokenizerRuntime,
    /// and this bridge discovers them only when Unity has actually loaded the assembly.
    /// </summary>
    public static class UnityManagedMarianTokenizerLoader
    {
        private const string FactoryTypeName =
            "PhraseLayer.Tokenization.Microsoft.MicrosoftMlMarianTokenizerFactory, PhraseLayer.Tokenization.Microsoft";

        public static bool IsRuntimeAvailable => ResolveFactoryType() != null;

        public static bool TryCreate(
            byte[] sourceSentencePieceModel,
            byte[] targetSentencePieceModel,
            string vocabularyJson,
            out ITranslationTokenizer? tokenizer,
            out string? error)
        {
            tokenizer = null;
            error = null;

            if (sourceSentencePieceModel == null)
            {
                error = "Source SentencePiece model bytes were not supplied.";
                return false;
            }
            if (targetSentencePieceModel == null)
            {
                error = "Target SentencePiece model bytes were not supplied.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(vocabularyJson))
            {
                error = "Marian vocab.json text was not supplied.";
                return false;
            }

            var factoryType = ResolveFactoryType();
            if (factoryType == null)
            {
                error =
                    "Managed tokenizer runtime is not loaded. Stage PhraseLayer.Tokenization.Microsoft and its " +
                    "reviewed managed dependency closure into Assets/LocalTokenizerRuntime, then reimport Unity.";
                return false;
            }

            var create = factoryType.GetMethod(
                "Create",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(byte[]), typeof(byte[]), typeof(string) },
                modifiers: null);
            if (create == null)
            {
                error = "Managed Marian tokenizer factory does not expose the reviewed Create(byte[], byte[], string) contract.";
                return false;
            }

            try
            {
                var result = create.Invoke(
                    null,
                    new object[] { sourceSentencePieceModel, targetSentencePieceModel, vocabularyJson });
                tokenizer = result as ITranslationTokenizer;
                if (tokenizer == null)
                {
                    error = "Managed Marian tokenizer factory returned an object outside the PhraseLayer Core tokenizer contract.";
                    return false;
                }
                return true;
            }
            catch (TargetInvocationException exception)
            {
                var cause = exception.InnerException ?? exception;
                error = cause.GetType().Name + ": " + cause.Message;
                return false;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        public static bool TryCreateFromResources(
            string resourceRoot,
            out ITranslationTokenizer? tokenizer,
            out string? error)
        {
            tokenizer = null;
            error = null;
            if (string.IsNullOrWhiteSpace(resourceRoot))
            {
                error = "Translation tokenizer resource root must not be empty.";
                return false;
            }

            var source = Resources.Load<TextAsset>(resourceRoot + "/source.spm");
            var target = Resources.Load<TextAsset>(resourceRoot + "/target.spm");
            var vocabulary = Resources.Load<TextAsset>(resourceRoot + "/vocab");
            if (source == null || target == null || vocabulary == null)
            {
                error =
                    "Managed Marian tokenizer Resources are incomplete. Expected source.spm.bytes, " +
                    "target.spm.bytes, and vocab.json under Resources/" + resourceRoot + ".";
                return false;
            }

            return TryCreate(source.bytes, target.bytes, vocabulary.text, out tokenizer, out error);
        }

        private static Type? ResolveFactoryType()
        {
            return Type.GetType(FactoryTypeName, throwOnError: false);
        }
    }
}
