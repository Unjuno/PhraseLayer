using System;
using System.Collections.Generic;

namespace PhraseLayer.Core.Inputs
{
    /// <summary>
    /// Parses a PaddleOCR recognition character dictionary from UTF-8 text semantics.
    ///
    /// PaddleOCR reads the file line-by-line and removes only CR/LF terminators; it does not trim
    /// ordinary spaces or reject empty tokens. When use_space_char is enabled, a literal single-space
    /// token is appended after the file tokens. This parser mirrors those semantics for Unity TextAsset.text.
    /// </summary>
    public static class PaddleOcrCharacterDictionary
    {
        public static IReadOnlyList<string> Parse(string text, bool useSpaceCharacter)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            var tokens = new List<string>();
            var lineStart = 0;
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] != '\n')
                    continue;

                tokens.Add(StripTrailingCarriageReturns(text.Substring(lineStart, index - lineStart)));
                lineStart = index + 1;
            }

            // Binary readlines() does not synthesize an additional empty line after a terminal LF.
            // It does preserve a final non-LF-terminated line.
            if (lineStart < text.Length)
                tokens.Add(StripTrailingCarriageReturns(text.Substring(lineStart)));

            if (useSpaceCharacter)
                tokens.Add(" ");

            return tokens.ToArray();
        }

        private static string StripTrailingCarriageReturns(string value)
        {
            var length = value.Length;
            while (length > 0 && value[length - 1] == '\r')
                length--;
            return length == value.Length ? value : value.Substring(0, length);
        }
    }
}
