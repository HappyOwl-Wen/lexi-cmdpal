using System.Buffers;
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
    /// <remarks>Each expanded edge computes at most 2 * maxDistance + 1 cells; only active prefix bands are stored.</remarks>
    public List<FuzzyMatch> Search(string query, int maxDistance, int maxResults = 512, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDistance);
        var results = new List<FuzzyMatch>();
        var m = query.Length;
        if (m == 0 || maxResults <= 0 || (long)m > MaxKeyLength + (long)maxDistance)
        {
            return results;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bound = Math.Min(maxDistance, Math.Max(m, MaxKeyLength));
        var maxDepth = (int)Math.Min(MaxKeyLength, (long)m + bound);
        // Only |depth - column| <= bound can yield a match. Keep a compact band for each
        // active prefix; sibling branches reuse the same rows without clearing them.
        var width = (int)Math.Min((long)m + 1, (2L * bound) + 1);
        var cells = checked((maxDepth + 1) * width);
        int[]? rented = null;
        Span<int> rows = cells <= 1024 ? stackalloc int[cells] : (rented = ArrayPool<int>.Shared.Rent(cells)).AsSpan(0, cells);
        Span<char> path = stackalloc char[MaxKeyLength];
        try
        {
            for (var j = 0; j <= Math.Min(m, bound); j++)
            {
                rows[j] = j;
            }

            var search = new SearchState(this, query, rows, path, width, maxDepth, bound, maxResults, results, cancellationToken);
            search.Run(0, 0, 0);
            return results;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
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

    private ref struct SearchState
    {
        private readonly FuzzyIndex _index;
        private readonly ReadOnlySpan<char> _query;
        private readonly Span<int> _rows;
        private readonly Span<char> _path;
        private readonly int _width;
        private readonly int _maxDepth;
        private readonly int _maxDistance;
        private readonly int _maxResults;
        private readonly List<FuzzyMatch> _results;
        private readonly CancellationToken _ct;
        private int _visits;

        public SearchState(FuzzyIndex index, ReadOnlySpan<char> query, Span<int> rows, Span<char> path, int width, int maxDepth,
            int maxDistance, int maxResults, List<FuzzyMatch> results, CancellationToken ct)
        {
            _index = index;
            _query = query;
            _rows = rows;
            _path = path;
            _width = width;
            _maxDepth = maxDepth;
            _maxDistance = maxDistance;
            _maxResults = maxResults;
            _results = results;
            _ct = ct;
        }

        /// <param name="state">current automaton state</param>
        /// <param name="depth">length of the current prefix; rows[depth] holds its DP row</param>
        /// <param name="rowMin">minimum of that row's active band</param>
        public void Run(int state, int depth, int rowMin)
        {
            if (depth >= _maxDepth || _results.Count >= _maxResults)
            {
                return;
            }

            if ((++_visits & 0x3FFF) == 0)
            {
                _ct.ThrowIfCancellationRequested();
            }

            var m = _query.Length;
            var nextDepth = depth + 1;
            var rowStart = Math.Max(0, depth - _maxDistance);
            var rowEnd = (int)Math.Min(m, (long)depth + _maxDistance);
            var parentStart = Math.Max(0, depth - 1 - _maxDistance);
            var first = Math.Max(0, nextDepth - _maxDistance);
            var last = (int)Math.Min(m, (long)nextDepth + _maxDistance);
            var row = _rows.Slice(depth * _width, _width);
            var parentRow = depth > 0 ? _rows.Slice((depth - 1) * _width, _width) : Span<int>.Empty;
            var newRow = _rows.Slice(nextDepth * _width, _width);
            var lastLabel = depth > 0 ? _path[depth - 1] : '\0';
            var unreachable = _maxDistance + 1;

            for (int e = _index._edgeStart[state], end = _index._edgeStart[state + 1]; e < end; e++)
            {
                var c = _index._alphabet[_index._edgeLabel[e]];
                var newMin = unreachable;
                for (var j = first; j <= last; j++)
                {
                    int v;
                    if (j == 0)
                    {
                        v = nextDepth;
                    }
                    else
                    {
                        var deletion = j <= rowEnd ? row[j - rowStart] + 1 : unreachable;
                        var insertion = j > first ? newRow[j - first - 1] + 1 : unreachable;
                        var substitution = j > rowStart ? row[j - rowStart - 1] + (_query[j - 1] == c ? 0 : 1) : unreachable;
                        v = Math.Min(Math.Min(deletion, insertion), substitution);
                        if (depth > 0 && j >= 2 && _query[j - 1] == lastLabel && _query[j - 2] == c)
                        {
                            v = Math.Min(v, parentRow[j - 2 - parentStart] + 1);
                        }
                    }

                    newRow[j - first] = v;
                    if (v < newMin)
                    {
                        newMin = v;
                    }
                }

                _path[depth] = c;
                var target = _index._edgeTarget[e];
                if (last == m && newRow[m - first] <= _maxDistance && _index.IsFinal(target))
                {
                    _results.Add(new FuzzyMatch(new string(_path[..nextDepth]), newRow[m - first]));
                    if (_results.Count >= _maxResults)
                    {
                        return;
                    }
                }

                // Continue while the new row can still reach maxDistance, or a transposition from the
                // current row (looked back from two levels deeper) still could.
                if (newMin <= _maxDistance || rowMin < _maxDistance)
                {
                    // The recursion only reads rows[depth + 1] and writes deeper rows.
                    Run(target, nextDepth, newMin);
                    if (_results.Count >= _maxResults)
                    {
                        return;
                    }
                }
            }
        }
    }

    /// <summary>Incremental construction of a minimal acyclic DFA from sorted input (Daciuk et al., 2000).</summary>
    private sealed class DawgBuilder
    {
        private readonly Node _root = new();
        private readonly Dictionary<Node, Node> _register = new(NodeComparer.Instance);
        private readonly List<(Node Parent, byte Label, Node Child)> _unchecked = [];
        private readonly byte[] _previous = new byte[MaxKeyLength];
        private int _previousLength;
        private int _nextId;

        public void Add(ReadOnlySpan<byte> word)
        {
            var common = 0;
            var max = Math.Min(word.Length, _previousLength);
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
            word.CopyTo(_previous);
            _previousLength = word.Length;
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
