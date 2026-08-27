using System;
using System.Globalization;

namespace PhraseLayer.Core.Spatial
{
    /// <summary>
    /// Deterministic, platform-independent display-width estimate used only to fit translated text inside an
    /// already-verified physical OCR envelope. This is not font shaping and never influences semantic segmentation;
    /// it merely avoids treating narrow Latin glyphs and full-width Japanese glyphs as identical widths.
    /// </summary>
    public static class SurfaceTextSizing
    {
        public static double EstimateEmWidth(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (text.Length == 0) return 0.5;

            var width = 0.0;
            for (var index = 0; index < text.Length;)
            {
                var start = index;
                int codePoint;
                if (char.IsHighSurrogate(text[index]) &&
                    index + 1 < text.Length &&
                    char.IsLowSurrogate(text[index + 1]))
                {
                    codePoint = char.ConvertToUtf32(text[index], text[index + 1]);
                    index += 2;
                }
                else
                {
                    codePoint = text[index];
                    index++;
                }

                if (char.IsWhiteSpace(text, start))
                {
                    width += 0.5;
                    continue;
                }

                var category = CharUnicodeInfo.GetUnicodeCategory(text, start);
                if (category == UnicodeCategory.NonSpacingMark ||
                    category == UnicodeCategory.EnclosingMark ||
                    category == UnicodeCategory.Format ||
                    category == UnicodeCategory.Control)
                    continue;

                if (IsWideCodePoint(codePoint))
                {
                    width += 1.0;
                    continue;
                }

                switch (category)
                {
                    case UnicodeCategory.UppercaseLetter:
                    case UnicodeCategory.LowercaseLetter:
                    case UnicodeCategory.TitlecaseLetter:
                    case UnicodeCategory.ModifierLetter:
                    case UnicodeCategory.OtherLetter:
                    case UnicodeCategory.DecimalDigitNumber:
                    case UnicodeCategory.LetterNumber:
                    case UnicodeCategory.OtherNumber:
                        width += 0.6;
                        break;
                    case UnicodeCategory.ConnectorPunctuation:
                    case UnicodeCategory.DashPunctuation:
                    case UnicodeCategory.OpenPunctuation:
                    case UnicodeCategory.ClosePunctuation:
                    case UnicodeCategory.InitialQuotePunctuation:
                    case UnicodeCategory.FinalQuotePunctuation:
                    case UnicodeCategory.OtherPunctuation:
                        width += 0.5;
                        break;
                    case UnicodeCategory.MathSymbol:
                    case UnicodeCategory.CurrencySymbol:
                    case UnicodeCategory.ModifierSymbol:
                    case UnicodeCategory.OtherSymbol:
                        width += 0.75;
                        break;
                    default:
                        width += 0.6;
                        break;
                }
            }

            return Math.Max(0.5, width);
        }

        public static double ComputeCharacterSizeMeters(
            string displayText,
            SurfaceTextLayout layout,
            double heightFraction,
            double widthFraction,
            double minimumCharacterSizeMeters = 0.001)
        {
            if (displayText == null) throw new ArgumentNullException(nameof(displayText));
            ValidateFraction(heightFraction, nameof(heightFraction));
            ValidateFraction(widthFraction, nameof(widthFraction));
            if (double.IsNaN(minimumCharacterSizeMeters) ||
                double.IsInfinity(minimumCharacterSizeMeters) ||
                minimumCharacterSizeMeters <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(minimumCharacterSizeMeters));

            var estimatedEmWidth = EstimateEmWidth(displayText);
            var heightBound = layout.HeightMeters * heightFraction;
            var widthBound = (layout.WidthMeters * widthFraction) / estimatedEmWidth;
            return Math.Max(minimumCharacterSizeMeters, Math.Min(heightBound, widthBound));
        }

        private static void ValidateFraction(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static bool IsWideCodePoint(int codePoint)
        {
            return
                (codePoint >= 0x1100 && codePoint <= 0x115F) ||
                (codePoint >= 0x2329 && codePoint <= 0x232A) ||
                (codePoint >= 0x2E80 && codePoint <= 0x303E) ||
                (codePoint >= 0x3040 && codePoint <= 0xA4CF) ||
                (codePoint >= 0xAC00 && codePoint <= 0xD7A3) ||
                (codePoint >= 0xF900 && codePoint <= 0xFAFF) ||
                (codePoint >= 0xFE10 && codePoint <= 0xFE19) ||
                (codePoint >= 0xFE30 && codePoint <= 0xFE6F) ||
                (codePoint >= 0xFF01 && codePoint <= 0xFF60) ||
                (codePoint >= 0xFFE0 && codePoint <= 0xFFE6) ||
                (codePoint >= 0x1F300 && codePoint <= 0x1FAFF) ||
                (codePoint >= 0x20000 && codePoint <= 0x3FFFD);
        }
    }
}
