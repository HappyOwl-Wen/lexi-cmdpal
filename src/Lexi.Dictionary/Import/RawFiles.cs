using System.Globalization;

namespace Lexi.Dictionary.Import;

/// <summary>Layout of the raw download directory and where each file comes from.</summary>
public sealed class RawFiles(string root)
{
    public sealed record Download(string RelativePath, string Url, string SourceCode, bool Optional = false);

    /// <summary>
    /// Official download locations. Kaikki per-language files are the "postprocessed" exports of the English
    /// Wiktionary edition; de/zh are the raw wiktextract dumps of the German/Chinese editions.
    /// </summary>
    public static IReadOnlyList<Download> Downloads { get; } =
    [
        new("ecdict/ecdict.csv", "https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv", "ecdict", Optional: true),
        new("freq/en_full.txt", "https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/en/en_full.txt", "freq"),
        new("freq/de_full.txt", "https://raw.githubusercontent.com/hermitdave/FrequencyWords/master/content/2018/de/de_full.txt", "freq"),
        new("kaikki/kaikki.org-dictionary-English.jsonl.gz", "https://kaikki.org/dictionary/English/kaikki.org-dictionary-English.jsonl.gz", "wikt-en"),
        new("kaikki/kaikki.org-dictionary-German.jsonl.gz", "https://kaikki.org/dictionary/German/kaikki.org-dictionary-German.jsonl.gz", "wikt-en"),
        new("kaikki/de-extract.jsonl.gz", "https://kaikki.org/dictionary/downloads/de/de-extract.jsonl.gz", "wikt-de"),
        new("kaikki/zh-extract.jsonl.gz", "https://kaikki.org/dictionary/downloads/zh/zh-extract.jsonl.gz", "wikt-zh"),
    ];

    public string Root => root;

    public string Ecdict => Path.Combine(root, "ecdict", "ecdict.csv");

    public string FreqEn => Path.Combine(root, "freq", "en_full.txt");

    public string FreqDe => Path.Combine(root, "freq", "de_full.txt");

    public string WiktionaryEnglish => Path.Combine(root, "kaikki", "kaikki.org-dictionary-English.jsonl.gz");

    public string WiktionaryGerman => Path.Combine(root, "kaikki", "kaikki.org-dictionary-German.jsonl.gz");

    public string WiktionaryDe => Path.Combine(root, "kaikki", "de-extract.jsonl.gz");

    public string WiktionaryZh => Path.Combine(root, "kaikki", "zh-extract.jsonl.gz");

    public IEnumerable<string> Missing(bool includeEcdict)
    {
        foreach (var d in Downloads)
        {
            if (d.SourceCode == "ecdict" && !includeEcdict)
            {
                continue;
            }

            var p = Path.Combine(root, d.RelativePath);
            if (!File.Exists(p))
            {
                yield return p;
            }
        }
    }

    /// <summary>Version label recorded in the database: the local download date of the source file(s).</summary>
    public string? VersionOf(string sourceCode)
    {
        var dates = Downloads
            .Where(d => d.SourceCode == sourceCode)
            .Select(d => Path.Combine(root, d.RelativePath))
            .Where(File.Exists)
            .Select(p => File.GetLastWriteTimeUtc(p).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Distinct()
            .ToList();
        return dates.Count == 0 ? null : "downloaded " + string.Join(", ", dates);
    }
}
