using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PhraseLayer.Core.Audio
{
    /// <summary>
    /// Minimal managed decoder for the reviewed Moonshine BinTokenizer-compatible token asset.
    /// It intentionally implements decoding only: ASR generation never needs text-to-token encoding.
    ///
    /// Contract parity with Moonshine native tokens_to_text:
    /// - token entries are indexed by model token id;
    /// - entries surrounded by '&lt;' and '&gt;' are skipped as specials;
    /// - remaining bytes are concatenated before UTF-8 decoding so byte-fallback pieces may span tokens;
    /// - U+2581 is replaced by ASCII space;
    /// - surrounding Unicode whitespace is trimmed.
    /// </summary>
    public sealed class MoonshineBinaryTokenDecoder : IAsrTokenDecoder
    {
        private const string SpaceMarker = "▁";
        private readonly byte[][] tokensToBytes;
        private readonly UTF8Encoding strictUtf8 = new UTF8Encoding(false, true);

        public MoonshineBinaryTokenDecoder(byte[] assetBytes)
        {
            if (assetBytes == null) throw new ArgumentNullException(nameof(assetBytes));
            if (assetBytes.Length == 0) throw new ArgumentException("Moonshine token decoder asset must not be empty.", nameof(assetBytes));
            tokensToBytes = Parse(assetBytes);
            if (tokensToBytes.Length != MoonshineTinyAsrContract.VocabularySize)
            {
                throw new InvalidDataException(
                    string.Format(
                        "Moonshine token decoder vocabulary drift: expected {0} entries but received {1}.",
                        MoonshineTinyAsrContract.VocabularySize,
                        tokensToBytes.Length));
            }
        }

        public int TokenCount => tokensToBytes.Length;

        public string Decode(IReadOnlyList<int> tokenIds)
        {
            if (tokenIds == null) throw new ArgumentNullException(nameof(tokenIds));
            using (var bytes = new MemoryStream())
            {
                for (var index = 0; index < tokenIds.Count; index++)
                {
                    var tokenId = tokenIds[index];
                    if (tokenId < 0 || tokenId >= tokensToBytes.Length)
                        throw new ArgumentOutOfRangeException(nameof(tokenIds), "Moonshine token id is outside the decoder vocabulary.");
                    var tokenBytes = tokensToBytes[tokenId];
                    if (tokenBytes.Length == 0)
                        throw new InvalidDataException("Moonshine token decoder contains an empty entry at token id " + tokenId + ".");
                    if (IsSpecial(tokenBytes))
                        continue;
                    bytes.Write(tokenBytes, 0, tokenBytes.Length);
                }

                string decoded;
                try
                {
                    decoded = strictUtf8.GetString(bytes.ToArray());
                }
                catch (DecoderFallbackException exc)
                {
                    throw new InvalidDataException("Moonshine token sequence produced invalid UTF-8 after byte fallback fusion.", exc);
                }
                return decoded.Replace(SpaceMarker, " ").Trim();
            }
        }

        private static bool IsSpecial(byte[] value)
        {
            return value.Length > 2 && value[0] == (byte)'<' && value[value.Length - 1] == (byte)'>';
        }

        private static byte[][] Parse(byte[] data)
        {
            var entries = new List<byte[]>();
            var offset = 0;
            while (offset < data.Length)
            {
                var first = data[offset++];
                int length;
                if (first == 0)
                {
                    length = 0;
                }
                else if (first < 128)
                {
                    length = first;
                }
                else
                {
                    if (offset >= data.Length)
                        throw new InvalidDataException("Truncated Moonshine token decoder asset: missing second length byte.");
                    var second = data[offset++];
                    length = second * 128 + first - 128;
                }

                if (length > data.Length - offset)
                    throw new InvalidDataException("Truncated Moonshine token decoder asset: token payload exceeds input length.");
                var entry = new byte[length];
                if (length > 0)
                    Buffer.BlockCopy(data, offset, entry, 0, length);
                offset += length;
                entries.Add(entry);
                if (entries.Count > MoonshineTinyAsrContract.VocabularySize)
                    throw new InvalidDataException("Moonshine token decoder asset contains more entries than the reviewed vocabulary.");
            }
            return entries.ToArray();
        }
    }
}
