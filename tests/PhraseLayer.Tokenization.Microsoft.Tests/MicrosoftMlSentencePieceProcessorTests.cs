using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PhraseLayer.Core.Translation;
using PhraseLayer.Tokenization.Microsoft;
using Xunit;

namespace PhraseLayer.Tokenization.Microsoft.Tests
{
    public sealed class MicrosoftMlSentencePieceProcessorTests
    {
        [Fact]
        public void SyntheticUnigramModelUsesEmbeddedWhitespaceNormalizationAndRoundTripsPieces()
        {
            var processor = new MicrosoftMlSentencePieceProcessor(BuildSimpleUnigramModel());

            var pieces = processor.EncodePieces("hello world");

            Assert.Equal(new[] { "▁hello", "▁world" }, pieces);
            Assert.Equal("hello world", processor.DecodePieces(pieces));
            Assert.True(processor.AddDummyPrefix);
            Assert.True(processor.EscapeWhiteSpaces);
            Assert.False(processor.TreatWhitespaceAsSuffix);
            Assert.False(processor.ByteFallback);
        }

        [Fact]
        public void SentencePieceSegmentationIsNotPreSplitAtSymbolOrCjkBoundaries()
        {
            var processor = new MicrosoftMlSentencePieceProcessor(BuildBoundarySensitiveUnigramModel());

            var pieces = processor.EncodePieces("0% 東京 $9");

            Assert.Equal(new[] { "▁0%", "▁東京", "▁$9" }, pieces);
        }

        [Fact]
        public void InternalSentencePieceIdsCanBeResolvedWithoutAssumingMarianIds()
        {
            var processor = new MicrosoftMlSentencePieceProcessor(BuildSimpleUnigramModel());

            Assert.True(processor.TryGetPiece(3, out var piece));
            Assert.Equal("▁hello", piece);
            Assert.False(processor.TryGetPiece(999, out _));
        }

        [Fact]
        public void UnknownSurfacePiecesMapThroughExternalMarianVocabularyToUnknownId()
        {
            var processor = new MicrosoftMlSentencePieceProcessor(BuildSimpleUnigramModel());
            var externalVocabulary = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "</s>", 0 },
                { "<unk>", 1 },
                { "▁hello", 2 },
                { "▁world", 3 },
                { "<pad>", 46275 },
            };
            var marian = new MarianSentencePieceTokenizer(
                processor,
                processor,
                externalVocabulary);

            var surfacePieces = processor.EncodePieces("mystery");
            var encoded = marian.EncodeSource("mystery", maximumTokens: 32);

            Assert.NotEmpty(surfacePieces);
            Assert.DoesNotContain("<unk>", surfacePieces);
            Assert.Equal(0, encoded.TokenIds[encoded.TokenIds.Count - 1]);
            Assert.All(encoded.TokenIds.Take(encoded.TokenIds.Count - 1), tokenId => Assert.Equal(1, tokenId));
        }

        [Fact]
        public void MissingDecodePieceFailsInsteadOfGuessing()
        {
            var processor = new MicrosoftMlSentencePieceProcessor(BuildSimpleUnigramModel());

            var error = Assert.Throws<InvalidOperationException>(() =>
                processor.DecodePieces(new[] { "not-in-model" }));

            Assert.Contains("not present", error.Message);
        }

        [Fact]
        public void EmptyInputDoesNotAskSentencePieceToInventTokens()
        {
            var processor = new MicrosoftMlSentencePieceProcessor(BuildSimpleUnigramModel());

            Assert.Empty(processor.EncodePieces(string.Empty));
            Assert.Equal(string.Empty, processor.DecodePieces(Array.Empty<string>()));
        }

        private static byte[] BuildSimpleUnigramModel()
        {
            return BuildUnigramModel(new[]
            {
                ("▁hello", -0.1f),
                ("▁world", -0.1f),
            });
        }

        private static byte[] BuildBoundarySensitiveUnigramModel()
        {
            return BuildUnigramModel(new[]
            {
                ("▁0%", -0.1f),
                ("▁東京", -0.1f),
                ("▁$9", -0.1f),
            });
        }

        private static byte[] BuildUnigramModel(IReadOnlyList<(string Piece, float Score)> normalPieces)
        {
            var trainer = new ProtoWriter();
            trainer.WriteInt32(3, 1);  // TrainerSpec.model_type = UNIGRAM
            trainer.WriteInt32(40, 0); // unk_id
            trainer.WriteInt32(41, 1); // bos_id
            trainer.WriteInt32(42, 2); // eos_id
            trainer.WriteInt32(43, -1); // pad_id disabled
            trainer.WriteString(45, "<unk>");
            trainer.WriteString(46, "<s>");
            trainer.WriteString(47, "</s>");

            var normalizer = new ProtoWriter();
            normalizer.WriteString(1, "identity");
            normalizer.WriteBool(3, true); // add_dummy_prefix
            normalizer.WriteBool(4, true); // remove_extra_whitespaces
            normalizer.WriteBool(5, true); // escape_whitespaces

            var model = new ProtoWriter();
            model.WriteMessage(1, BuildPiece("<unk>", 0f, 2));
            model.WriteMessage(1, BuildPiece("<s>", 0f, 3));
            model.WriteMessage(1, BuildPiece("</s>", 0f, 3));
            foreach (var item in normalPieces)
                model.WriteMessage(1, BuildPiece(item.Piece, item.Score, 1));
            model.WriteMessage(2, trainer.ToArray());
            model.WriteMessage(3, normalizer.ToArray());
            return model.ToArray();
        }

        private static byte[] BuildPiece(string text, float score, int type)
        {
            var piece = new ProtoWriter();
            piece.WriteString(1, text);
            piece.WriteFloat(2, score);
            piece.WriteInt32(3, type);
            return piece.ToArray();
        }

        private sealed class ProtoWriter
        {
            private readonly MemoryStream stream = new MemoryStream();

            public void WriteInt32(int fieldNumber, int value)
            {
                WriteTag(fieldNumber, 0);
                WriteVarint(unchecked((ulong)(long)value));
            }

            public void WriteBool(int fieldNumber, bool value)
            {
                WriteTag(fieldNumber, 0);
                WriteVarint(value ? 1UL : 0UL);
            }

            public void WriteString(int fieldNumber, string value)
            {
                WriteBytes(fieldNumber, Encoding.UTF8.GetBytes(value));
            }

            public void WriteMessage(int fieldNumber, byte[] value)
            {
                WriteBytes(fieldNumber, value);
            }

            public void WriteFloat(int fieldNumber, float value)
            {
                WriteTag(fieldNumber, 5);
                var bytes = BitConverter.GetBytes(value);
                if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
                stream.Write(bytes, 0, bytes.Length);
            }

            public byte[] ToArray() => stream.ToArray();

            private void WriteBytes(int fieldNumber, byte[] value)
            {
                WriteTag(fieldNumber, 2);
                WriteVarint((ulong)value.Length);
                stream.Write(value, 0, value.Length);
            }

            private void WriteTag(int fieldNumber, int wireType)
            {
                WriteVarint((ulong)((fieldNumber << 3) | wireType));
            }

            private void WriteVarint(ulong value)
            {
                while (value >= 0x80)
                {
                    stream.WriteByte((byte)(value | 0x80));
                    value >>= 7;
                }
                stream.WriteByte((byte)value);
            }
        }
    }
}
