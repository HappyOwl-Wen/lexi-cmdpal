using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lexi.Core.Models;

namespace Lexi.Dictionary.Storage;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, WriteIndented = false)]
[JsonSerializable(typeof(EntryData))]
internal sealed partial class EntryJsonContext : JsonSerializerContext;

/// <summary>
/// Entry blob format: [1 byte codec][4 bytes uncompressed length, little endian][payload].
/// Codec 0 = raw UTF-8 JSON, 1 = Brotli. JSON uses the source-generated context (trimming/AOT safe).
/// </summary>
public static class EntryCodec
{
    private const byte Raw = 0;
    private const byte Brotli = 1;

    public static byte[] Encode(EntryData entry)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(entry, EntryJsonContext.Default.EntryData);
        if (json.Length > 96)
        {
            var compressed = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(json.Length));
            try
            {
                if (BrotliEncoder.TryCompress(json, compressed, out var written, quality: 6, window: 22) &&
                    written < json.Length - 16)
                {
                    var result = new byte[5 + written];
                    result[0] = Brotli;
                    BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1), json.Length);
                    compressed.AsSpan(0, written).CopyTo(result.AsSpan(5));
                    return result;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressed);
            }
        }

        var raw = new byte[5 + json.Length];
        raw[0] = Raw;
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(1), json.Length);
        json.CopyTo(raw.AsSpan(5));
        return raw;
    }

    public static EntryData Decode(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 5)
        {
            throw new InvalidDataException("Entry blob too short.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(blob[1..]);
        var payload = blob[5..];
        if (blob[0] == Raw)
        {
            return JsonSerializer.Deserialize(payload, EntryJsonContext.Default.EntryData) ?? new EntryData();
        }

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            if (!BrotliDecoder.TryDecompress(payload, buffer.AsSpan(0, length), out var written) || written != length)
            {
                throw new InvalidDataException("Corrupt entry blob.");
            }

            return JsonSerializer.Deserialize(buffer.AsSpan(0, length), EntryJsonContext.Default.EntryData) ?? new EntryData();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
