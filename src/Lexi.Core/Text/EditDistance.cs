using System.Buffers;

namespace Lexi.Core.Text;

/// <summary>
/// Optimal-string-alignment (restricted Damerau-Levenshtein) distance with an upper bound:
/// insertions, deletions, substitutions and adjacent transpositions ("teh" → "the") each cost 1.
/// </summary>
public static class EditDistance
{
    /// <summary>Returns the OSA distance, or <paramref name="max"/> + 1 as soon as it is known to exceed <paramref name="max"/>.</summary>
    public static int Osa(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return max + 1;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            var d = Math.Max(a.Length, b.Length);
            return d <= max ? d : max + 1;
        }

        var m = b.Length;
        int[]? rented = null;
        Span<int> buffer = m + 1 <= 96 ? stackalloc int[3 * (m + 1)] : (rented = ArrayPool<int>.Shared.Rent(3 * (m + 1))).AsSpan(0, 3 * (m + 1));
        try
        {
            var prev2 = buffer[..(m + 1)];
            var prev = buffer.Slice(m + 1, m + 1);
            var cur = buffer.Slice(2 * (m + 1), m + 1);
            for (var j = 0; j <= m; j++)
            {
                prev[j] = j;
            }

            var prevMin = 0;
            for (var i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                var rowMin = i;
                var ai = a[i - 1];
                for (var j = 1; j <= m; j++)
                {
                    var cost = ai == b[j - 1] ? 0 : 1;
                    var v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                    if (i > 1 && j > 1 && ai == b[j - 2] && a[i - 2] == b[j - 1])
                    {
                        v = Math.Min(v, prev2[j - 2] + 1);
                    }

                    cur[j] = v;
                    if (v < rowMin)
                    {
                        rowMin = v;
                    }
                }

                // A transposition can still reach back one row, so only stop when both rows are hopeless.
                if (rowMin > max && prevMin >= max)
                {
                    return max + 1;
                }

                prevMin = rowMin;
                var t = prev2;
                prev2 = prev;
                prev = cur;
                cur = t;
            }

            return prev[m] <= max ? prev[m] : max + 1;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }
}
