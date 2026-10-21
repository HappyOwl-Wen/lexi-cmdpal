using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Core.Text;

namespace Lexi.Dictionary.Quality;

/// <summary>An input and the headword expected within the top <see cref="WithinTop"/> results.</summary>
public sealed record AcceptanceCase(string Query, string Expected, Lang Lang, int WithinTop, string Description);

/// <summary>
/// The acceptance list from the project brief, plus Chinese → English/German cases for the reverse lookup.
/// Evaluated against the real database by `lexi quality`, by Lexi.Tests (integration) and by the extension's
/// self-test; no search code special-cases these inputs.
/// </summary>
public static class AcceptanceCases
{
    public static IReadOnlyList<AcceptanceCase> All { get; } =
    [
        new("authentication", "authentication", Lang.En, 1, "exact English"),
        new("vulnerability", "vulnerability", Lang.En, 1, "exact English"),
        new("vulnerabilty", "vulnerability", Lang.En, 1, "typo: missing letter"),
        new("authentcation", "authentication", Lang.En, 1, "typo: missing letter"),
        // Completions are ranked by corpus frequency; authentication (COCA #25,693) follows the more common
        // authentic / authenticity / authenticate / authentically, so it is expected on the first screen, not first.
        new("authen", "authentication", Lang.En, 8, "prefix completion (first screen)"),
        new("Schwachstelle", "Schwachstelle", Lang.De, 1, "exact German"),
        new("Schwachstele", "Schwachstelle", Lang.De, 1, "typo: missing letter"),
        new("Schwachstellen", "Schwachstelle", Lang.De, 1, "plural → lemma"),
        new("Bedrohung", "Bedrohung", Lang.De, 1, "exact German"),
        new("Übertragung", "Übertragung", Lang.De, 1, "exact with umlaut"),
        new("Uebertragung", "Übertragung", Lang.De, 1, "ue → ü transliteration"),
        new("Häuser", "Haus", Lang.De, 1, "umlaut plural → lemma"),
        new("hauser", "Haus", Lang.De, 3, "plural without umlaut → Häuser → Haus"),
        new("gehen", "gehen", Lang.De, 1, "exact verb"),
        new("ging", "gehen", Lang.De, 1, "Präteritum → infinitive"),

        // Chinese → English / German (reverse lookup; rank within the language's section). Where one equivalent
        // is the standard translation it must come first; where several are equally standard (威胁: threaten,
        // threat, menace, intimidate; 弱点: weakness, foible, frailty), within the first five.
        new("房子", "house", Lang.En, 1, "中→英: the standard translation"),
        new("房子", "Haus", Lang.De, 1, "中→德: the standard translation"),
        new("苹果", "apple", Lang.En, 1, "中→英"),
        new("苹果", "Apfel", Lang.De, 1, "中→德"),
        new("数据库", "database", Lang.En, 1, "中→英: technical term"),
        new("数据库", "Datenbank", Lang.De, 1, "中→德: technical term"),
        new("防火墙", "firewall", Lang.En, 1, "中→英: only from translation tables (ECDICT misspells it)"),
        new("防火墙", "Firewall", Lang.De, 3, "中→德: IT sense next to the building sense (Brandmauer)"),
        new("学习", "learn", Lang.En, 3, "中→英: learn and study are both standard"),
        new("学习", "lernen", Lang.De, 1, "中→德"),
        new("威胁", "threat", Lang.En, 5, "中→英: noun among the verb translations"),
        new("威胁", "Bedrohung", Lang.De, 2, "中→德: Bedrohung / Drohung"),
        new("弱点", "weakness", Lang.En, 5, "中→英: weakness / foible / frailty"),
        new("弱点", "Schwachstelle", Lang.De, 5, "中→德: Schwachstelle / Schwachpunkt / Faible"),
        new("加密", "encrypt", Lang.En, 2, "中→英: verb and noun (encryption)"),
        new("加密", "verschlüsseln", Lang.De, 2, "中→德: verb and noun (Verschlüsselung)"),
        new("電腦", "computer", Lang.En, 1, "traditional characters → simplified"),
        new("電腦", "Computer", Lang.De, 1, "traditional characters → simplified"),
        new("网络安全漏洞", "cybersecurity", Lang.En, 3, "no whole match: the longest known part (网络安全) first"),
    ];

    /// <summary>
    /// 1-based rank of the expected headword, 0 when missing. Chinese queries list English and German results
    /// in separate groups, so their rank counts within the expected language.
    /// </summary>
    public static int RankOf(AcceptanceCase c, IReadOnlyList<SearchHit> hits)
    {
        var pool = TextNormalizer.HasCjk(c.Query) ? hits.Where(h => h.Word.Lang == c.Lang).ToList() : [.. hits];
        return pool.FindIndex(h => h.Word.Word == c.Expected && h.Word.Lang == c.Lang) + 1;
    }
}
