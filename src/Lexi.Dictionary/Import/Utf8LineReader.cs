using System.IO.Compression;

namespace Lexi.Dictionary.Import;

/// <summary>Streams UTF-8 lines (as raw bytes) from a .jsonl or .jsonl.gz file without allocating strings.</summary>
internal sealed class Utf8LineReader : IDisposable
{
    private readonly Stream _stream;
    private byte[] _buffer = new byte[4 << 20];
    private int _start;
    private int _end;
    private bool _eof;

    public Utf8LineReader(string path)
    {
        Stream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        _stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new BufferedStream(new GZipStream(file, CompressionMode.Decompress), 1 << 20)
            : file;
        Length = file.Length;
        _file = file;
    }

    private readonly Stream _file;

    /// <summary>Compressed file length, for progress reporting.</summary>
    public long Length { get; }

    public long Position => _file.Position;

    /// <summary>Returns the next line (without '\n'). The memory is only valid until the next call.</summary>
    public bool TryReadLine(out ReadOnlyMemory<byte> line)
    {
        while (true)
        {
            var span = _buffer.AsSpan(_start, _end - _start);
            var idx = span.IndexOf((byte)'\n');
            if (idx >= 0)
            {
                var len = idx > 0 && span[idx - 1] == (byte)'\r' ? idx - 1 : idx;
                line = _buffer.AsMemory(_start, len);
                _start += idx + 1;
                return true;
            }

            if (_eof)
            {
                if (_end > _start)
                {
                    line = _buffer.AsMemory(_start, _end - _start);
                    _start = _end;
                    return true;
                }

                line = default;
                return false;
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            var n = _stream.Read(_buffer, _end, _buffer.Length - _end);
            if (n == 0)
            {
                _eof = true;
            }
            else
            {
                _end += n;
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
