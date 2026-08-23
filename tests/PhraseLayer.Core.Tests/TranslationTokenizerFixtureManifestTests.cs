using System;
using System.Text;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class TranslationTokenizerFixtureManifestTests
    {
        [Fact]
        public void ValidPinnedManifestBuildsEncodeAndDecodeFixtures()
        {
            var parsed = TranslationTokenizerFixtureManifest.Parse(BuildManifest(
                LocalTranslationStagingContract.ExpectedRevision,
                "E\t" + B64("hello") + "\t42,0\n" +
                "D\t100,101\t" + B64("こんにちは") + "\n",
                1,
                1));

            Assert.Single(parsed.EncodeFixtures);
            Assert.Equal("hello", parsed.EncodeFixtures[0].SourceText);
            Assert.Equal(new[] { 42, 0 }, parsed.EncodeFixtures[0].ExpectedTokenIds);
            Assert.Single(parsed.DecodeFixtures);
            Assert.Equal(new[] { 100, 101 }, parsed.DecodeFixtures[0].TokenIds);
            Assert.Equal("こんにちは", parsed.DecodeFixtures[0].ExpectedText);
        }

        [Fact]
        public void RevisionDriftFailsClosed()
        {
            var error = Assert.Throws<FormatException>(() => TranslationTokenizerFixtureManifest.Parse(BuildManifest(
                "0000000000000000000000000000000000000000",
                "E\t" + B64("hello") + "\t42,0\nD\t100\t" + B64("訳") + "\n",
                1,
                1)));

            Assert.Contains("revision drift", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void HeaderCountsMustMatchParsedFixtures()
        {
            Assert.Throws<FormatException>(() => TranslationTokenizerFixtureManifest.Parse(BuildManifest(
                LocalTranslationStagingContract.ExpectedRevision,
                "E\t" + B64("hello") + "\t42,0\nD\t100\t" + B64("訳") + "\n",
                2,
                1)));
        }

        [Fact]
        public void EmptyTokenSequenceIsRejected()
        {
            Assert.Throws<FormatException>(() => TranslationTokenizerFixtureManifest.Parse(BuildManifest(
                LocalTranslationStagingContract.ExpectedRevision,
                "E\t" + B64("hello") + "\t\nD\t100\t" + B64("訳") + "\n",
                1,
                1)));
        }

        private static string BuildManifest(string revision, string body, int encodeCount, int decodeCount)
        {
            return TranslationTokenizerFixtureManifest.Magic + "\n" +
                "model_id_b64\t" + B64(LocalTranslationStagingContract.ExpectedModelId) + "\n" +
                "revision\t" + revision + "\n" +
                "encode_fixture_count\t" + encodeCount + "\n" +
                "decode_fixture_count\t" + decodeCount + "\n" +
                "END_HEADER\n" +
                body +
                "END\n";
        }

        private static string B64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }
    }
}
