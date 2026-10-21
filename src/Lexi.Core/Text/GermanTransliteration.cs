using System.Text;

namespace Lexi.Core.Text;

/// <summary>
/// Handles the conventional ASCII substitutes ae/oe/ue for ä/ö/ü (ß/ss is already equivalent in fold keys).
/// Instead of indexing every word twice, a query like "uebertragung" is turned into fold variants
/// ("ubertragung"), looked up, and accepted only if the candidate's real transliteration equals the query.
/// That verification is what prevents false matches such as "neue" → "neu".
/// </summary>
public static class GermanTransliteration
{
    private const int MaxPositions = 4;

    /// <summary>All fold strings obtained by replacing one or more ae/oe/ue digraphs with a/o/u.</summary>
    public static IEnumerable<string> ReverseVariants(string fold)
    {
        var positions = new List<int>();
        for (var i = 0; i < fold.Length - 1; i++)
        {
            if (fold[i + 1] == 'e' && fold[i] is 'a' or 'o' or 'u')
            {
                positions.Add(i);
                i++;
            }
        }

        if (positions.Count is 0 or > MaxPositions)
        {
            yield break;
        }

        for (var mask = 1; mask < 1 << positions.Count; mask++)
        {
            var sb = new StringBuilder(fold.Length);
            var last = 0;
            for (var k = 0; k < positions.Count; k++)
            {
                var p = positions[k];
                sb.Append(fold, last, p - last);
                sb.Append(fold[p]);
                if ((mask & (1 << k)) == 0)
                {
                    sb.Append('e');
                }

                last = p + 2;
            }

            sb.Append(fold, last, fold.Length - last);
            yield return sb.ToString();
        }
    }
}
