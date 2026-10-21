using System.Text.Json;
using System.Text.Json.Serialization;
using Lexi.Core.Models;

namespace Lexi.Extension.Services;

internal sealed record HistoryItem(string Word, Lang Lang, string? Brief, DateTimeOffset At);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(List<HistoryItem>))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;

/// <summary>Recently opened entries (local JSON file, newest first, max 30).</summary>
internal sealed class HistoryStore(string directory)
{
    private const int Max = 30;
    private readonly Lock _lock = new();
    private readonly string _path = Path.Combine(directory, "history.json");
    private List<HistoryItem>? _items;

    public IReadOnlyList<HistoryItem> Items
    {
        get
        {
            lock (_lock)
            {
                return [.. Load()];
            }
        }
    }

    public void Add(string word, Lang lang, string? brief)
    {
        lock (_lock)
        {
            var items = Load();
            items.RemoveAll(i => i.Word == word && i.Lang == lang);
            items.Insert(0, new HistoryItem(word, lang, brief, DateTimeOffset.Now));
            if (items.Count > Max)
            {
                items.RemoveRange(Max, items.Count - Max);
            }

            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(_path, JsonSerializer.Serialize(items, HistoryJsonContext.Default.ListHistoryItem));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // History is a convenience; ignore write failures.
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items = [];
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private List<HistoryItem> Load()
    {
        if (_items is not null)
        {
            return _items;
        }

        try
        {
            _items = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), HistoryJsonContext.Default.ListHistoryItem) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _items = [];
        }

        return _items;
    }
}
