using System.Globalization;

namespace Lexi.Dictionary.Import;

/// <summary>
/// Word counts from a "word count" text file (FrequencyWords format). Converts to the Zipf scale:
/// log10(occurrences per billion tokens); 7 ≈ "the", 4 ≈ moderately common, 1 ≈ very rare.
/// </summary>
internal sealed class FrequencyTable
{
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private long _total;

    public static FrequencyTable Empty { get; } = new();

    public int Count => _counts.Count;

    public static FrequencyTable Load(string path)
    {
        var table = new FrequencyTable();
        if (!File.Exists(path))
        {
            return table;
        }

        foreach (var line in File.ReadLines(path))
        {
            var sp = line.LastIndexOf(' ');
            if (sp <= 0 || !long.TryParse(line.AsSpan(sp + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                continue;
            }

            var word = line[..sp].Trim().ToLowerInvariant();
            if (word.Length == 0)
            {
                continue;
            }

            table._counts[word] = table._counts.GetValueOrDefault(word) + count;
            table._total += count;
        }

        return table;
    }

    public long CountOf(string lowerWord) => _counts.GetValueOrDefault(lowerWord);

    public float Zipf(long count) => count <= 0 || _total == 0 ? 0f : (float)Math.Log10(count * 1e9 / _total);
}
