using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Core.Text;
using Lexi.Dictionary.Import;

namespace Lexi.Tests;

public class ChineseTermsTests
{
    [Fact]
    public void Definition_line_is_split_into_positioned_terms()
    {
        var terms = ChineseTerms.Extract("n. 恐吓, 恶兆, 威胁；[经] 威胁");
        Assert.Equal(["恐吓", "恶兆", "威胁", "威胁"], terms.Select(t => t.Text));
        Assert.Equal([0, 1, 2, 3], terms.Select(t => t.Position));
    }

    [Fact]
    public void Bracketed_notes_and_tokens_with_latin_letters_are_dropped()
    {
        var terms = ChineseTerms.Extract("n. 裂缝( chink的名词复数 ); （论点、性格等中）弱点; DOS内部命令");
        Assert.Equal(["裂缝", "弱点"], terms.Select(t => t.Text));
    }

    [Fact]
    public void Patterns_with_placeholders_are_not_terms()
    {
        Assert.Empty(ChineseTerms.Extract("n. 吃...的人"));
        Assert.Empty(ChineseTerms.Extract("v. 沿着……走"));
        Assert.Equal(["房子"], ChineseTerms.Extract("n. 房子；vt. 给…房子住").Select(t => t.Text));
    }

    [Fact]
    public void Adjectives_also_give_their_stem()
    {
        var terms = ChineseTerms.Extract("a. 危险的");
        Assert.Contains(terms, t => t.Text == "危险的" && !t.IsStem);
        Assert.Contains(terms, t => t.Text == "危险" && t.IsStem && t.Position == 0);
    }

    [Theory]
    [InlineData("[网络] 苹果", true, false)]
    [InlineData("[地名] 房子、地区", false, true)]
    [InlineData("  [人名] 埃格德", false, true)]
    [InlineData("n. 房子", false, false)]
    public void Web_and_name_lines_are_recognized(string line, bool web, bool name)
    {
        Assert.Equal(web, ChineseTerms.IsWebLine(line));
        Assert.Equal(name, ChineseTerms.IsNameLine(line));
    }

    [Fact]
    public void Query_key_keeps_only_chinese_characters()
    {
        Assert.Equal("房子苹果", ChineseTerms.QueryKey(" 房子, 苹果! "));
        Assert.Equal("苹果", ChineseTerms.QueryKey("苹果 apple"));
    }
}

public class ZhIndexWeightTests
{
    private static Dictionary<string, int> Collect(EntryData data)
    {
        var best = new Dictionary<string, int>(StringComparer.Ordinal);
        ZhIndexBuilder.Collect(data, best);
        return best;
    }

    private static EntryData Lines(params string[] zh) =>
        new() { Senses = [.. zh.Select(g => new Sense { Gloss = g, GlossLang = "zh" })] };

    [Fact]
    public void First_term_of_the_first_line_weighs_most()
    {
        var w = Collect(Lines("n. 恐吓, 恶兆, 威胁", "vt. 威吓"));
        Assert.Equal(100, w["恐吓"]);
        Assert.True(w["恐吓"] > w["恶兆"] && w["恶兆"] > w["威胁"]);
        Assert.True(w["威吓"] < 100);
    }

    [Fact]
    public void Web_and_name_lines_weigh_less()
    {
        Assert.True(Collect(Lines("[网络] 苹果"))["苹果"] <= 55);
        Assert.True(Collect(Lines("[地名] 房子、地区"))["房子"] <= 30);
    }

    [Fact]
    public void Translation_tables_and_reverse_links_count_as_translations()
    {
        var tables = new EntryData
        {
            Senses =
            [
                new Sense { Gloss = "A slit in a castle wall", GlossLang = "en", Zh = ["枪眼"] },
                new Sense { Gloss = "A method of escape", GlossLang = "en", Zh = ["漏洞"] },
            ],
        };
        var w = Collect(tables);
        Assert.Equal(95, w["枪眼"]);
        Assert.InRange(w["漏洞"], 80, 94);

        var reverse = new EntryData { Kind = EntryData.ReverseKind, Senses = [new Sense { Gloss = "防火墙", GlossLang = "zh", Labels = ["计算机资安系统"] }] };
        Assert.Equal(90, Collect(reverse)["防火墙"]);
    }
}

public class ChineseSearchTests
{
    private static MemoryStore Store() => new MemoryStore()
        .Word("house", Lang.En, 5.75f, "n. 房子, 住宅")
        .Word("bure", Lang.En, 2.06f, "n. 草屋, 房子")
        .Word("Haus", Lang.De, 5.98f, "房子、住房")
        .Word("threat", Lang.En, 4.97f, "n. 威胁")
        .Word("threats", Lang.En, 4.0f, "n. 威胁")
        .Word("datum", Lang.En, 2.5f, "n. 数据")
        .Word("data", Lang.En, 5.23f, "n. 数据")
        .Word("B", Lang.En, 5.5f, "[计] 字节, 数据库")
        .Word("database", Lang.En, 4.72f, "n. 数据库")
        .Word("cybersecurity", Lang.En, 1.69f, "n. 网络安全")
        .Word("leak", Lang.En, 4.36f, "n. 漏洞")
        .Form("threats", "threat", Lang.En, "plural")
        .Form("data", "datum", Lang.En, "plural")
        .Chinese("房子", "house", Lang.En)
        .Chinese("房子", "bure", Lang.En, 94)
        .Chinese("房子", "Haus", Lang.De)
        .Chinese("威胁", "threat", Lang.En)
        .Chinese("威胁", "threats", Lang.En)
        .Chinese("数据", "datum", Lang.En)
        .Chinese("数据", "data", Lang.En)
        .Chinese("数据库", "B", Lang.En)
        .Chinese("数据库", "database", Lang.En)
        .Chinese("网络安全", "cybersecurity", Lang.En)
        .Chinese("漏洞", "leak", Lang.En);

    private static IReadOnlyList<SearchHit> Search(string text, Lang? lang = null, int limit = 20, bool fuzzy = true) =>
        new SearchEngine(Store()).Search(new SearchRequest { Text = text, Language = lang, Limit = limit, Fuzzy = fuzzy }).Hits;

    [Fact]
    public void English_equivalents_come_first_then_german()
    {
        var hits = Search("房子");
        Assert.Equal(["house", "bure", "Haus"], hits.Select(h => h.Word.Word));
        Assert.All(hits, h => Assert.Equal(MatchKind.Chinese, h.Kind));
        Assert.Equal("房子", hits[0].Matched);
    }

    [Fact]
    public void Each_language_gets_half_of_the_limit()
        => Assert.Equal(["house", "Haus"], Search("房子", limit: 2).Select(h => h.Word.Word));

    [Fact]
    public void Language_filter_and_punctuation()
    {
        Assert.Equal(["Haus"], Search("房子", Lang.De).Select(h => h.Word.Word));
        Assert.Equal("house", Search("「房子」！")[0].Word.Word);
    }

    [Fact]
    public void Inflected_duplicates_are_hidden_unless_they_rank_higher()
    {
        Assert.Equal(["threat"], Search("威胁").Select(h => h.Word.Word));

        // "data" is the plural of datum but far more common: both stay, data first.
        Assert.Equal(["data", "datum"], Search("数据").Select(h => h.Word.Word));
    }

    [Fact]
    public void Single_letters_rank_after_words()
        => Assert.Equal(["database", "B"], Search("数据库").Select(h => h.Word.Word));

    [Theory]
    [InlineData("DB", true)]
    [InlineData("XFER", true)]
    [InlineData("Dr.", true)]
    [InlineData("app.", true)]
    [InlineData("B", false)]
    [InlineData("GmbH", false)]
    [InlineData("database", false)]
    public void Abbreviations_are_recognized(string word, bool expected) => Assert.Equal(expected, SearchEngine.IsAbbreviation(word));

    [Fact]
    public void Without_a_whole_match_the_longest_known_parts_are_used()
    {
        var hits = Search("网络安全漏洞");
        Assert.Equal(["cybersecurity", "leak"], hits.Select(h => h.Word.Word));
        Assert.All(hits, h => Assert.Equal(MatchKind.ChinesePart, h.Kind));
        Assert.Equal("网络安全", hits[0].Matched);
    }

    [Fact]
    public void Cheap_mode_does_not_look_up_parts()
        => Assert.Empty(Search("网络安全漏洞", fuzzy: false));
}
