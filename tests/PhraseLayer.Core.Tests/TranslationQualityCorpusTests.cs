using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class TranslationQualityCorpusTests
    {
        [Fact]
        public void CorpusIsStableUniqueAndCoversCriticalDimensions()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "translation-quality-corpus.json");
            Assert.True(File.Exists(path), "translation quality corpus was not copied to the test output directory");

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
            Assert.Equal("en-ja", root.GetProperty("language_pair").GetString());
            Assert.Equal("human-structured", root.GetProperty("review_mode").GetString());

            var policy = root.GetProperty("policy");
            Assert.Equal(0, policy.GetProperty("critical_failures_allowed").GetInt32());
            Assert.Equal(0.05, policy.GetProperty("max_major_or_worse_rate").GetDouble(), 6);
            Assert.True(policy.GetProperty("complete_review_required").GetBoolean());

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var dimensionCounts = new Dictionary<TranslationQualityDimension, int>();
            var cases = root.GetProperty("cases");
            Assert.True(cases.GetArrayLength() >= 20, "quality corpus is too small to represent the current promotion gate");

            foreach (var item in cases.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                var source = item.GetProperty("source").GetString();
                var rationale = item.GetProperty("rationale").GetString();
                Assert.False(string.IsNullOrWhiteSpace(id));
                Assert.False(string.IsNullOrWhiteSpace(source));
                Assert.False(string.IsNullOrWhiteSpace(rationale));
                Assert.True(ids.Add(id!), "duplicate quality corpus id: " + id);

                var parsedDimensions = new List<TranslationQualityDimension>();
                foreach (var dimensionElement in item.GetProperty("dimensions").EnumerateArray())
                {
                    var name = dimensionElement.GetString();
                    Assert.True(
                        Enum.TryParse(name, ignoreCase: false, out TranslationQualityDimension dimension),
                        "unknown translation quality dimension: " + name);
                    parsedDimensions.Add(dimension);
                    dimensionCounts.TryGetValue(dimension, out var count);
                    dimensionCounts[dimension] = count + 1;
                }

                Assert.Contains(TranslationQualityDimension.Adequacy, parsedDimensions);
                Assert.Contains(TranslationQualityDimension.JapaneseReadability, parsedDimensions);

                var qualityCase = new TranslationQualityCase(
                    id!,
                    source!,
                    parsedDimensions,
                    rationale!);
                Assert.Equal(id, qualityCase.Id);

                var criticalIf = item.GetProperty("critical_if");
                Assert.True(criticalIf.GetArrayLength() >= 1, "each quality case must state at least one critical failure condition");
            }

            Assert.True(GetCount(dimensionCounts, TranslationQualityDimension.NegationPolarity) >= 4);
            Assert.True(GetCount(dimensionCounts, TranslationQualityDimension.NamedEntity) >= 3);
            Assert.True(GetCount(dimensionCounts, TranslationQualityDimension.MultiwordExpression) >= 5);
            Assert.True(GetCount(dimensionCounts, TranslationQualityDimension.Modality) >= 5);
            Assert.True(GetCount(dimensionCounts, TranslationQualityDimension.TemporalAspect) >= 8);
            Assert.True(GetCount(dimensionCounts, TranslationQualityDimension.Quantity) >= 5);
        }

        private static int GetCount(
            IReadOnlyDictionary<TranslationQualityDimension, int> counts,
            TranslationQualityDimension dimension)
        {
            return counts.TryGetValue(dimension, out var count) ? count : 0;
        }
    }
}
