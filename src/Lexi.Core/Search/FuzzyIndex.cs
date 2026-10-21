using System.Runtime.InteropServices;
using System.Text;

namespace Lexi.Core.Search;

public readonly record struct FuzzyMatch(string Key, int Distance);

/// <summary>
/// Compact fuzzy-lookup structure: a minimal acyclic DFA (DAWG) over folded keys, searched with a bounded
/// optimal-string-alignment (Damerau) distance. Suffix sharing keeps it small (a few MB for ~1M keys) and it is
/// stored as one blob in the dictionary database, so loading is a single read with no per-key allocations.
/// </summary>
public sealed class FuzzyIndex
{
    public const int MaxKeyLength = 48;
    private const uint Magic = 0x5A46584C; // "LXFZ"
    private const int FormatVersion = 1;

    private readonly char[] _alphabet; // code -> char, code 0 unused
    private readonly byte[] _codeOf;   // char (< 0x10000) -> code, 0 = not in alphabet
    private readonly int[] _edgeStart; // state -> first edge; length = states + 1
    private readonly byte[] _edgeLabel;
    private readonly int[] _edgeTarget;
    private readonly ulong[] _final;

    private FuzzyIndex(char[] alphabet, int[] edgeStart, byte[] edgeLabel, int[] edgeTarget, ulong[] final, int keyCount)
    {
        _alphabet = alphabet;
        _edgeStart = edgeStart;
        _edgeLabel = edgeLabel;
        _edgeTarget = edgeTarget;
        _final = final;
        KeyCount = keyCount;
        _codeOf = new byte[0x10000];
        for (var i = 1; i < alphabet.Length; i++)
        {
            _codeOf[alphabet[i]] = (byte)i;
        }
    }

    public int KeyCount { get; }

    public int StateCount => _edgeStart.Length - 1;

    public int EdgeCount => _edgeLabel.Length;

    public long SizeInBytes => (_edgeStart.Length * 4L) + _edgeLabel.Length + (_edgeTarget.Length * 4L) + (_final.Length * 8L) + _codeOf.Length;

    public static bool IsIndexable(string key)
    {
        if (key.Length == 0 || key.Length > MaxKeyLength)
        {
            return false;
        }

        foreach (var c in key)
        {
            if (char.IsControl(c) || char.IsSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds the automaton. Keys must already be folded (see TextNormalizer.ToFold).</summary>
    public static FuzzyIndex Build(IEnumerable<string> keys)
    {
        var list = new List<string>();
        var alphabet = new SortedSet<char>();
        foreach (var k in keys)
        {
            if (!IsIndexable(k))
            {
                continue;
            }

            list.Add(k);
            foreach (var c in k)
            {
                alphabet.Add(c);
            }
        }

        if (alphabet.Count > 254)
        {
            // Keep the most common characters only; keys with rare characters are skipped.
            var counts = new Dictionary<char, int>();
            foreach (var k in list)
            {
                foreach (var c in k)
                {
                    counts[c] = counts.GetValueOrDefault(c) + 1;
                }
            }

            var keep = counts.OrderByDescending(kv => kv.Value).Take(254).Select(kv => kv.Key).ToHashSet();
            list.RemoveAll(k => k.Any(c => !keep.Contains(c)));
            alphabet = new SortedSet<char>(keep);
        }

        list.Sort(StringComparer.Ordinal);
        var alpha = new char[alphabet.Count + 1];
        var codeOf = new Dictionary<char, byte>();
        var idx = 1;
        foreach (var c in alphabet)
        {
            alpha[idx] = c;
            codeOf[c] = (byte)idx;
            idx++;
        }

        var builder = new DawgBuilder();
        string? previous = null;
        var count = 0;
        var buffer = new byte[MaxKeyLength];
        foreach (var k in list)
        {
            if (k == previous)
            {
                continue;
            }

            for (var i = 0; i < k.Length; i++)
            {
                buffer[i] = codeOf[k[i]];
            }

            builder.Add(buffer.AsSpan(0, k.Length));
            previous = k;
            count++;
        }

        var (edgeStart, edgeLabel, edgeTarget, final) = builder.Finish();
        return new FuzzyIndex(alpha, edgeStart, edgeLabel, edgeTarget, final, count);
    }

    public bool Contains(string key)
    {
        var state = 0;
        foreach (var c in key)
        {
            var code = _codeOf[c];
            if (code == 0)
            {
                return false;
            }

            state = Step(state, code);
            if (state < 0)
            {
                return false;
            }
        }

        return IsFinal(state);
    }

    /// <summary>
    /// Enumerates keys within <paramref name="maxDistance"/> (OSA) of <paramref name="query"/> (a folded key).
    /// Stops after <paramref name="maxResults"/> matches.
    /// </summary>
    public List<FuzzyMatch> Search(string query, int maxDistance, int maxResults = 512, CancellationToken cancellationToken = default)
    {
        var results = new List<FuzzyMatch>();
        var m = query.Length;
        if (m == 0 || m > MaxKeyLength + maxDistance)
        {
            return results;
        }

        var q = new byte[m];
        for (var i = 0; i < m; i++)
        {
            // Characters outside the alphabet map to 0, which never equals an edge label (cost 1, like any substitution).
            q[i] = query[i] < 0x10000 ? _codeOf[query[i]] : (byte)0;
        }

        var rows = new int[MaxKeyLength + 2][];
        for (var d = 0; d < rows.Length; d++)
        {
            rows[d] = new int[m + 1];
        }

        for (var j = 0; j <= m; j++)
        {
            rows[0][j] = j;
        }

        var search = new SearchState(this, q, rows, maxDistance, maxResults, results, cancellationToken);
        search.Run(0, 0);
        return results;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(KeyCount);
            w.Write(_alphabet.Length);
            foreach (var c in _alphabet)
            {
                w.Write((ushort)c);
            }

            w.Write(_edgeStart.Length);
            w.Write(_edgeLabel.Length);
            w.Write(_final.Length);
            w.Write(MemoryMarshal.AsBytes(_edgeStart.AsSpan()));
            w.Write(_edgeLabel);
            w.Write(MemoryMarshal.AsBytes(_edgeTarget.AsSpan()));
            w.Write(MemoryMarshal.AsBytes(_final.AsSpan()));
        }

        return ms.ToArray();
    }

    public static FuzzyIndex Deserialize(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        if (ReadInt32(data, ref pos) != unchecked((int)Magic))
        {
            throw new InvalidDataException("Not a Lexi fuzzy index.");
        }

        var version = ReadInt32(data, ref pos);
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported fuzzy index version {version}.");
        }

        var keyCount = ReadInt32(data, ref pos);
        var alphaLen = ReadInt32(data, ref pos);
        var alphabet = new char[alphaLen];
        for (var i = 0; i < alphaLen; i++)
        {
            alphabet[i] = (char)MemoryMarshal.Read<ushort>(data[pos..]);
            pos += 2;
        }

        var startLen = ReadInt32(data, ref pos);
        var edgeLen = ReadInt32(data, ref pos);
        var finalLen = ReadInt32(data, ref pos);
        var edgeStart = MemoryMarshal.Cast<byte, int>(data.Slice(pos, startLen * 4)).ToArray();
        pos += startLen * 4;
        var edgeLabel = data.Slice(pos, edgeLen).ToArray();
        pos += edgeLen;
        var edgeTarget = MemoryMarshal.Cast<byte, int>(data.Slice(pos, edgeLen * 4)).ToArray();
        pos += edgeLen * 4;
        var final = MemoryMarshal.Cast<byte, ulong>(data.Slice(pos, finalLen * 8)).ToArray();
        return new FuzzyIndex(alphabet, edgeStart, edgeLabel, edgeTarget, final, keyCount);
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int pos)
    {
        var v = MemoryMarshal.Read<int>(data[pos..]);
        pos += 4;
        return v;
    }

    private bool IsFinal(int state) => (_final[state >> 6] & (1UL << (state & 63))) != 0;

    private int Step(int state, byte code)
    {
        // Edges are sorted by label: binary search.
        int lo = _edgeStart[state], hi = _edgeStart[state + 1] - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var l = _edgeLabel[mid];
            if (l == code)
            {
                return _edgeTarget[mid];
            }

            if (l < code)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    private sealed class SearchState(FuzzyIndex index, byte[] q, int[][] rows, int maxDistance, int maxResults, List<FuzzyMatch> results, CancellationToken ct)
    {
        private readonly byte[] _path = new byte[MaxKeyLength + 1];
        private int _visits;

        /// <param name="state">current automaton state</param>
        /// <param name="depth">length of the current prefix; rows[depth] holds its DP row</param>
        public void Run(int state, int depth)
        {
            if (depth >= MaxKeyLength || results.Count >= maxResults)
            {
                return;
            }

            if ((++_visits & 0x3FFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var m = q.Length;
            var row = rows[depth];
            var parentRow = depth > 0 ? rows[depth - 1] : null;
            var lastLabel = depth > 0 ? _path[depth - 1] : (byte)0;
            var rowMin = row[0];
            for (var j = 1; j <= m; j++)
            {
                rowMin = Math.Min(rowMin, row[j]);
            }

            var newRow = rows[depth + 1];
            for (int e = index._edgeStart[state], end = index._edgeStart[state + 1]; e < end; e++)
            {
                var c = index._edgeLabel[e];
                newRow[0] = depth + 1;
                var newMin = newRow[0];
                for (var j = 1; j <= m; j++)
                {
                    var cost = q[j - 1] == c ? 0 : 1;
                    var v = Math.Min(Math.Min(row[j] + 1, newRow[j - 1] + 1), row[j - 1] + cost);
                    if (parentRow is not null && j >= 2 && q[j - 1] == lastLabel && q[j - 2] == c)
                    {
                        v = Math.Min(v, parentRow[j - 2] + 1);
                    }

                    newRow[j] = v;
                    if (v < newMin)
                    {
                        newMin = v;
                    }
                }

                _path[depth] = c;
                var target = index._edgeTarget[e];
                if (newRow[m] <= maxDistance && index.IsFinal(target))
                {
                    results.Add(new FuzzyMatch(Decode(depth + 1), newRow[m]));
                    if (results.Count >= maxResults)
                    {
                        return;
                    }
                }

                // Continue while the new row can still reach maxDistance, or a transposition from the
                // current row (looked back from two levels deeper) still could.
                if (newMin <= maxDistance || rowMin + 1 <= maxDistance)
                {
                    // The recursion only reads rows[depth + 1] and writes deeper rows.
                    Run(target, depth + 1);
                }
            }
        }

        private string Decode(int length)
        {
            return string.Create(length, (index._alphabet, _path), static (span, s) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = s._alphabet[s._path[i]];
                }
            });
        }
    }

    /// <summary>Incremental construction of a minimal acyclic DFA from sorted input (Daciuk et al., 2000).</summary>
    private sealed class DawgBuilder
    {
        private readonly Node _root = new();
        private readonly Dictionary<Node, Node> _register = new(NodeComparer.Instance);
        private readonly List<(Node Parent, byte Label, Node Child)> _unchecked = [];
        private byte[] _previous = [];
        private int _nextId;

        public void Add(ReadOnlySpan<byte> word)
        {
            var common = 0;
            var max = Math.Min(word.Length, _previous.Length);
            while (common < max && word[common] == _previous[common])
            {
                common++;
            }

            Minimize(common);
            var node = _unchecked.Count == 0 ? _root : _unchecked[^1].Child;
            for (var i = common; i < word.Length; i++)
            {
                var next = new Node();
                node.AddEdge(word[i], next);
                _unchecked.Add((node, word[i], next));
                node = next;
            }

            node.Final = true;
            _previous = word.ToArray();
        }

        public (int[] EdgeStart, byte[] EdgeLabel, int[] EdgeTarget, ulong[] Final) Finish()
        {
            Minimize(0);
            _root.Id = _nextId++;

            // Number states breadth-first from the root so the root is state 0.
            var stateOf = new int[_nextId];
            Array.Fill(stateOf, -1);
            var order = new List<Node>(_nextId);
            var queue = new Queue<Node>();
            queue.Enqueue(_root);
            stateOf[_root.Id] = 0;
            order.Add(_root);
            var edgeCount = 0;
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                edgeCount += n.Count;
                for (var i = 0; i < n.Count; i++)
                {
                    var t = n.Targets[i];
                    if (stateOf[t.Id] < 0)
                    {
                        stateOf[t.Id] = order.Count;
                        order.Add(t);
                        queue.Enqueue(t);
                    }
                }
            }

            var edgeStart = new int[order.Count + 1];
            var edgeLabel = new byte[edgeCount];
            var edgeTarget = new int[edgeCount];
            var final = new ulong[(order.Count + 63) / 64];
            var e = 0;
            for (var s = 0; s < order.Count; s++)
            {
                var n = order[s];
                edgeStart[s] = e;
                if (n.Final)
                {
                    final[s >> 6] |= 1UL << (s & 63);
                }

                for (var i = 0; i < n.Count; i++)
                {
                    edgeLabel[e] = n.Labels[i];
                    edgeTarget[e] = stateOf[n.Targets[i].Id];
                    e++;
                }
            }

            edgeStart[order.Count] = e;
            return (edgeStart, edgeLabel, edgeTarget, final);
        }

        private void Minimize(int downTo)
        {
            for (var i = _unchecked.Count - 1; i >= downTo; i--)
            {
                var (parent, _, child) = _unchecked[i];
                if (_register.TryGetValue(child, out var existing))
                {
                    parent.Targets[parent.Count - 1] = existing;
                }
                else
                {
                    child.Id = _nextId++;
                    _register.Add(child, child);
                }

                _unchecked.RemoveAt(i);
            }
        }
    }

    private sealed class Node
    {
        public bool Final;
        public byte[] Labels = [];
        public Node[] Targets = [];
        public int Count;
        public int Id = -1;

        public void AddEdge(byte label, Node target)
        {
            if (Count == Labels.Length)
            {
                var size = Count == 0 ? 2 : Count * 2;
                Array.Resize(ref Labels, size);
                Array.Resize(ref Targets, size);
            }

            Labels[Count] = label;
            Targets[Count] = target;
            Count++;
        }
    }

    private sealed class NodeComparer : IEqualityComparer<Node>
    {
        public static readonly NodeComparer Instance = new();

        public bool Equals(Node? a, Node? b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a is null || b is null || a.Final != b.Final || a.Count != b.Count)
            {
                return false;
            }

            for (var i = 0; i < a.Count; i++)
            {
                if (a.Labels[i] != b.Labels[i] || !ReferenceEquals(a.Targets[i], b.Targets[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(Node n)
        {
            var h = new HashCode();
            h.Add(n.Final);
            for (var i = 0; i < n.Count; i++)
            {
                h.Add(n.Labels[i]);
                h.Add(n.Targets[i].Id);
            }

            return h.ToHashCode();
        }
    }
}
