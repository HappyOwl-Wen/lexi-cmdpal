namespace Lexi.Tests;

/// <summary>Unbounded full-matrix oracle, independent of the production banding and pruning.</summary>
internal static class OsaReference
{
    public static int Distance(string a, string b)
    {
        var cells = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            cells[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            cells[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cells[i, j] = Math.Min(Math.Min(cells[i - 1, j] + 1, cells[i, j - 1] + 1), cells[i - 1, j - 1] + cost);
                if (i >= 2 && j >= 2 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    cells[i, j] = Math.Min(cells[i, j], cells[i - 2, j - 2] + 1);
                }
            }
        }

        return cells[a.Length, b.Length];
    }

    public static IEnumerable<string> BinaryWords(int maxLength)
    {
        yield return string.Empty;
        for (var length = 1; length <= maxLength; length++)
        {
            for (var bits = 0; bits < 1 << length; bits++)
            {
                var word = new char[length];
                for (var j = 0; j < length; j++)
                {
                    word[j] = (bits & (1 << j)) == 0 ? 'a' : 'b';
                }

                yield return new string(word);
            }
        }
    }
}
