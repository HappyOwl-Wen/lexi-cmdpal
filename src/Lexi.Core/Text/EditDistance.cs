using System.Buffers;

namespace Lexi.Core.Text;

/// <summary>
/// Optimal-string-alignment (restricted Damerau-Levenshtein) distance with an upper bound:
/// insertions, deletions, substitutions and adjacent transpositions ("teh" → "the") each cost 1.
/// </summary>
public static class EditDistance
{
    /// <summary>Returns the OSA distance, or <paramref name="max"/> + 1 as soon as it is known to exceed <paramref name="max"/>.</summary>
    /// <remarks>Uses three compact diagonal bands, with time O(n * min(m + 1, 2 * max + 1)) and workspace O(min(m + 1, 2 * max + 1)).</remarks>
    public static int Osa(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        var exceeded = max == int.MaxValue ? int.MaxValue : max + 1;
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return exceeded;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            var d = Math.Max(a.Length, b.Length);
            return d <= max ? d : exceeded;
        }

        if (max == 0)
        {
            return a.SequenceEqual(b) ? 0 : 1;
        }

        // The shorter string is the row dimension. Cells outside the diagonal band
        // cannot be part of a solution within max edits, including transpositions.
        if (b.Length > a.Length)
        {
            var swap = a;
            a = b;
            b = swap;
        }

        var m = b.Length;
        var bound = Math.Min(max, a.Length);
        var unreachable = bound + 1;
        var width = (int)Math.Min((long)m + 1, (2L * bound) + 1);
        int[]? rented = null;
        Span<int> buffer = width <= 96 ? stackalloc int[3 * width] : (rented = ArrayPool<int>.Shared.Rent(3 * width)).AsSpan(0, 3 * width);
        try
        {
            var prev2 = buffer[..width];
            var prev = buffer.Slice(width, width);
            var cur = buffer.Slice(2 * width, width);
            for (var j = 0; j <= Math.Min(m, bound); j++)
            {
                prev[j] = j;
            }

            var prevMin = 0;
            for (var i = 1; i <= a.Length; i++)
            {
                var first = Math.Max(0, i - bound);
                var last = (int)Math.Min(m, (long)i + bound);
                var prevStart = Math.Max(0, i - 1 - bound);
                var prevEnd = (int)Math.Min(m, (long)i - 1 + bound);
                var prev2Start = Math.Max(0, i - 2 - bound);
                var rowMin = unreachable;
                var ai = a[i - 1];
                for (var j = first; j <= last; j++)
                {
                    int v;
                    if (j == 0)
                    {
                        v = i;
                    }
                    else
                    {
                        var deletion = j <= prevEnd ? prev[j - prevStart] + 1 : unreachable;
                        var insertion = j > first ? cur[j - first - 1] + 1 : unreachable;
                        var substitution = j > prevStart ? prev[j - prevStart - 1] + (ai == b[j - 1] ? 0 : 1) : unreachable;
                        v = Math.Min(Math.Min(deletion, insertion), substitution);
                        if (i > 1 && j > 1 && ai == b[j - 2] && a[i - 2] == b[j - 1])
                        {
                            v = Math.Min(v, prev2[j - 2 - prev2Start] + 1);
                        }
                    }

                    cur[j - first] = v;
                    if (v < rowMin)
                    {
                        rowMin = v;
                    }
                }

                // A transposition can still reach back one row, so only stop when both rows are hopeless.
                if (rowMin > bound && prevMin >= bound)
                {
                    return exceeded;
                }

                prevMin = rowMin;
                var t = prev2;
                prev2 = prev;
                prev = cur;
                cur = t;
            }

            var distance = prev[m - Math.Max(0, a.Length - bound)];
            return distance <= max ? distance : exceeded;
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
