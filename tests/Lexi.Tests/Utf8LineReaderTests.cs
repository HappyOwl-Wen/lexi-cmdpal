using System.IO.Compression;
using System.Text;
using Lexi.Dictionary.Import;

namespace Lexi.Tests;

public class Utf8LineReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reads_empty_unicode_and_unterminated_lines(bool gzip)
    {
        CheckLines(gzip, "\n中文\r\n\r\n尾行", ["", "中文", "", "尾行"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reads_lines_across_compaction_growth_and_crlf_boundaries(bool gzip)
    {
        // The first CR sits at the initial buffer's end; the LF arrives in a later read.
        var first = new string('a', (4 << 20) - 1);
        var second = new string('中', (4 << 20) / 3 + 100);
        CheckLines(gzip, first + "\r\n短行\n" + second + "\r\nend", [first, "短行", second, "end"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_file_has_no_lines(bool gzip) => CheckLines(gzip, "", []);

    private static void CheckLines(bool gzip, string contents, string[] expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lexi-lines-{Guid.NewGuid():N}.jsonl" + (gzip ? ".gz" : ""));
        try
        {
            using (var file = File.Create(path))
            using (Stream stream = gzip ? new GZipStream(file, CompressionMode.Compress) : file)
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(contents);
            }

            using var reader = new Utf8LineReader(path);
            foreach (var value in expected)
            {
                Assert.True(reader.TryReadLine(out var line));
                Assert.Equal(value, Encoding.UTF8.GetString(line.Span));
            }

            Assert.False(reader.TryReadLine(out _));
            Assert.False(reader.TryReadLine(out _));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
