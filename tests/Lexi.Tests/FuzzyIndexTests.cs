using Lexi.Core.Search;

namespace Lexi.Tests;

public class FuzzyIndexTests
{
    private static readonly string[] Words =
    [
        "vulnerability", "vulnerable", "authentication", "authenticate", "authentic", "schwachstelle",
        "schwachstellen", "haus", "hause", "haeuser", "hauser", "maus", "laus", "the", "then", "them",
        "gehen", "ging", "gegangen", "ubertragung", "bedrohung", "angriff", "a", "ab",
    ];

    [Fact]
    public void Contains_exactly_the_built_keys()
    {
        var index = FuzzyIndex.Build(Words);
        foreach (var w in Words)
        {
            Assert.True(index.Contains(w), w);
        }

        Assert.False(index.Contains("vulnerabilty"));
        Assert.False(index.Contains("hau"));
        Assert.Equal(Words.Distinct().Count(), index.KeyCount);
    }

    [Fact]
    public void Dawg_is_minimal_enough_to_share_suffixes()
    {
        // 1000 words with the same suffix need far fewer states than characters.
        var words = Enumerable.Range(0, 1000).Select(i => $"w{i:D4}ation").ToList();
        var index = FuzzyIndex.Build(words);
        Assert.True(index.StateCount < words.Sum(w => w.Length) / 3, $"states {index.StateCount}");
    }

    [Theory]
    [InlineData("vulnerabilty", "vulnerability", 1)]
    [InlineData("authentcation", "authentication", 1)]
    [InlineData("schwachstele", "schwachstelle", 1)]
    [InlineData("teh", "the", 1)]
    [InlineData("bedorhung", "bedrohung", 1)]
    public void Finds_typos(string query, string expected, int distance)
    {
        var index = FuzzyIndex.Build(Words);
        var match = Assert.Single(index.Search(query, 1, cancellationToken: TestContext.Current.CancellationToken), m => m.Key == expected);
        Assert.Equal(distance, match.Distance);
    }

    [Fact]
    public void Search_matches_brute_force_on_random_dictionary()
    {
        var rnd = new Random(7);
        const string alphabet = "abcdeghilnorstu";
        var words = Enumerable.Range(0, 3000)
            .Select(_ => new string(Enumerable.Range(0, rnd.Next(2, 10)).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray()))
            .Distinct()
            .ToList();
        var index = FuzzyIndex.Build(words);

        for (var i = 0; i < 200; i++)
        {
            var q = new string(Enumerable.Range(0, rnd.Next(2, 10)).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray());
            for (var d = 1; d <= 2; d++)
            {
                var expected = words.Select(w => new FuzzyMatch(w, OsaReference.Distance(q, w))).Where(m => m.Distance <= d).OrderBy(m => m.Key, StringComparer.Ordinal).ToList();
                var actual = index.Search(q, d, maxResults: 100_000, cancellationToken: TestContext.Current.CancellationToken).OrderBy(m => m.Key, StringComparer.Ordinal).ToList();
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void Serialization_round_trips()
    {
        var index = FuzzyIndex.Build(Words);
        var copy = FuzzyIndex.Deserialize(index.Serialize());
        Assert.Equal(index.KeyCount, copy.KeyCount);
        Assert.Equal(index.StateCount, copy.StateCount);
        Assert.Equal(
            index.Search("vulnerabilty", 2, cancellationToken: TestContext.Current.CancellationToken).Select(m => (m.Key, m.Distance)).OrderBy(x => x.Key),
            copy.Search("vulnerabilty", 2, cancellationToken: TestContext.Current.CancellationToken).Select(m => (m.Key, m.Distance)).OrderBy(x => x.Key));
    }

    [Fact]
    public void Unknown_characters_count_as_substitutions()
    {
        var index = FuzzyIndex.Build(["haus"]);
        var match = Assert.Single(index.Search("hau%", 1, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("haus", match.Key);
    }

    [Fact]
    public void Banded_search_matches_full_matrix_exhaustively()
    {
        var words = OsaReference.BinaryWords(5).Where(w => w.Length > 0).ToArray();
        var index = FuzzyIndex.Build(words);
        foreach (var query in words.Append("xaba").Append("abx"))
        {
            var distances = words.Select(w => new FuzzyMatch(w, OsaReference.Distance(query, w))).OrderBy(m => m.Key, StringComparer.Ordinal).ToArray();
            foreach (var bound in new[] { 0, 1, 2, 5, int.MaxValue })
            {
                var expected = distances.Where(m => m.Distance <= bound).ToArray();
                var actual = index.Search(query, bound, 1000, TestContext.Current.CancellationToken);
                Assert.Equal(expected, actual);
                Assert.Equal(expected.Take(3), index.Search(query, bound, 3, TestContext.Current.CancellationToken));
            }
        }
    }

    [Fact]
    public void Banded_search_handles_longest_keys_and_queries()
    {
        var word = new string('a', FuzzyIndex.MaxKeyLength - 2) + "bc";
        var index = FuzzyIndex.Build([word, "ab", "ba"]);
        foreach (var query in new[] { word, word[..^2] + "cb", "xx" + word, word + "xx", word[2..], "ba" })
        {
            foreach (var bound in new[] { 0, 1, 2, FuzzyIndex.MaxKeyLength, int.MaxValue })
            {
                var expected = new[] { word, "ab", "ba" }.Select(w => new FuzzyMatch(w, OsaReference.Distance(query, w)))
                    .Where(m => m.Distance <= bound).OrderBy(m => m.Key, StringComparer.Ordinal).ToArray();
                Assert.Equal(expected, index.Search(query, bound, 1000, TestContext.Current.CancellationToken));
            }
        }
    }

    [Fact]
    public void Search_honors_cancellation_before_traversal()
    {
        var index = FuzzyIndex.Build(Words);
        Assert.Throws<OperationCanceledException>(() => index.Search("haus", 1, cancellationToken: new CancellationToken(true)));
    }
}
