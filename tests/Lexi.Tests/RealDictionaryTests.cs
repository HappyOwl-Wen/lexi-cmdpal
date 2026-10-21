using System.Diagnostics;
using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Dictionary.Quality;
using Lexi.Dictionary.Storage;
using Lexi.Dictionary.View;

namespace Lexi.Tests;

/// <summary>Opens the real dictionary once (skipped when it has not been built on this machine).</summary>
public sealed class RealDictionaryFixture : IDisposable
{
    public RealDictionaryFixture()
    {
        var path = DataPaths.FindDatabase();
        if (path is not null)
        {
            Db = LexiDatabase.Open(path);
            Engine = new SearchEngine(Db);
            Engine.Search(new SearchRequest { Text = "warm up" });
        }
    }

    public LexiDatabase? Db { get; }

    public SearchEngine? Engine { get; }

    public void Dispose() => Db?.Dispose();
}

/// <summary>
/// The acceptance list from the brief, evaluated against the full dictionary (integration tests).
/// Run `lexi import` first; without a database these tests are skipped, not passed.
/// </summary>
public sealed class RealDictionaryTests(RealDictionaryFixture f) : IClassFixture<RealDictionaryFixture>
{
    public static TheoryData<string> Queries() => new(AcceptanceCases.All.Select(c => c.Query));

    [Theory]
    [MemberData(nameof(Queries))]
    public void Acceptance_case(string query)
    {
        Assert.SkipWhen(f.Engine is null, "Dictionary database not built (run: lexi import)");
        var c = AcceptanceCases.All.Single(x => x.Query == query);
        var hits = f.Engine!.Search(new SearchRequest { Text = c.Query, Limit = 10 }, TestContext.Current.CancellationToken).Hits.ToList();
        var rank = hits.FindIndex(h => h.Word.Word == c.Expected && h.Word.Lang == c.Lang) + 1;
        Assert.True(rank > 0 && rank <= c.WithinTop, $"{c.Query}: expected {c.Expected} within top {c.WithinTop}, got rank {rank}: {string.Join(", ", hits.Take(5))}");
    }

    [Fact]
    public void German_noun_entry_has_article_plural_and_ipa()
    {
        Assert.SkipWhen(f.Db is null, "Dictionary database not built");
        var word = f.Db!.GetWordsByFold("schwachstelle", Lang.De).Single(w => w.Word == "Schwachstelle");
        var view = WordViewBuilder.Build(f.Db, word);
        Assert.Equal("die Schwachstelle", view.Title);
        Assert.Contains(view.Grammar, g => g.Label.StartsWith("复数", StringComparison.Ordinal) && g.Value.Contains("Schwachstellen", StringComparison.Ordinal));
        Assert.NotEmpty(view.Ipa);
        Assert.NotEmpty(view.Chinese);
    }

    [Fact]
    public void German_verb_entry_has_principal_parts()
    {
        Assert.SkipWhen(f.Db is null, "Dictionary database not built");
        var word = f.Db!.GetWordsByFold("gehen", Lang.De).Single(w => w.Word == "gehen");
        var view = WordViewBuilder.Build(f.Db, word);
        Assert.Contains(view.Grammar, g => g.Label.StartsWith("过去时", StringComparison.Ordinal) && g.Value.Contains("ging", StringComparison.Ordinal));
        Assert.Contains(view.Grammar, g => g.Label.StartsWith("过去分词", StringComparison.Ordinal) && g.Value.Contains("gegangen", StringComparison.Ordinal));
        Assert.Contains(view.Grammar, g => g.Label.StartsWith("助动词", StringComparison.Ordinal) && g.Value.Contains("sein", StringComparison.Ordinal));
    }

    [Fact]
    public void English_entry_has_uk_ipa_and_chinese()
    {
        Assert.SkipWhen(f.Db is null, "Dictionary database not built");
        var word = f.Db!.GetWordsByFold("authentication", Lang.En).Single(w => w.Word == "authentication");
        var view = WordViewBuilder.Build(f.Db, word);
        Assert.Contains(view.Ipa, p => p.Region == "UK");
        Assert.Contains(view.Chinese, b => b.Source.Code == "ecdict");
    }

    [Fact]
    public void Typical_query_latency_is_well_under_100ms()
    {
        Assert.SkipWhen(f.Engine is null, "Dictionary database not built");
        var times = new List<double>();
        for (var round = 0; round < 3; round++)
        {
            foreach (var c in AcceptanceCases.All)
            {
                var sw = Stopwatch.StartNew();
                f.Engine!.Search(new SearchRequest { Text = c.Query }, TestContext.Current.CancellationToken);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        times.Sort();
        var p95 = times[(int)Math.Ceiling(0.95 * times.Count) - 1];
        Assert.True(p95 < 100, $"p95 {p95:F1} ms");
    }
}
