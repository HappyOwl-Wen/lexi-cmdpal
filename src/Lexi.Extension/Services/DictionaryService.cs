using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Dictionary.Storage;
using Lexi.Dictionary.View;

namespace Lexi.Extension.Services;

internal enum DictionaryState
{
    NotLoaded,
    Ready,
    Missing,
    Error,
}

/// <summary>
/// Owns the dictionary database for the extension process: opened lazily on first use (Command Palette starts
/// extensions at its own startup, so nothing heavy happens there), released again after a period without
/// lookups. No polling: a one-shot timer is re-armed on every use.
/// </summary>
internal sealed partial class DictionaryService : IDisposable
{
    private static readonly TimeSpan IdleRelease = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private readonly Timer _idleTimer;
    private readonly Dictionary<long, WordView> _viewCache = [];
    private readonly LinkedList<long> _viewOrder = new();
    private LexiDatabase? _db;
    private SearchEngine? _engine;
    private bool _disposed;

    public DictionaryService()
    {
        _idleTimer = new Timer(_ => Release(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public DictionaryState State { get; private set; } = DictionaryState.NotLoaded;

    public string? ErrorMessage { get; private set; }

    public string? DatabasePath { get; private set; }

    /// <summary>A Lexi data folder found in another app's private LocalCache when the database is missing.</summary>
    public string? RedirectedRoot { get; private set; }

    public DatabaseInfo? Info { get; private set; }

    public bool IsLoaded
    {
        get
        {
            lock (_lock)
            {
                return _db is not null;
            }
        }
    }

    /// <summary>Opens the database if needed. Returns false (with <see cref="State"/> set) when unavailable.</summary>
    public bool EnsureLoaded()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Touch();
            if (_db is not null)
            {
                return true;
            }

            var path = DataPaths.FindDatabase();
            DatabasePath = path ?? DataPaths.DefaultDatabasePath;
            if (path is null)
            {
                if (State != DictionaryState.Missing)
                {
                    // Checked once per missing period (it scans the package folders).
                    RedirectedRoot = DataPaths.FindRedirectedRoot();
                }

                State = DictionaryState.Missing;
                ErrorMessage = RedirectedRoot is null
                    ? $"未找到词典数据库：{DataPaths.DefaultDatabasePath}"
                    : $"词典数据库在另一个应用的私有目录里：{RedirectedRoot}。在打包应用（如 Claude 桌面版）的终端里构建时，Windows 会把 AppData 写入重定向到那里。";
                return false;
            }

            RedirectedRoot = null;

            try
            {
                _db = LexiDatabase.Open(path);
                _engine = new SearchEngine(_db);
                Info = _db.Info;
                State = DictionaryState.Ready;
                ErrorMessage = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
            {
                State = DictionaryState.Error;
                ErrorMessage = ex.Message;
                return false;
            }
        }
    }

    /// <summary>Loads the fuzzy index in the background so the first typo query does not pay for it.</summary>
    public void Prewarm()
    {
        _ = Task.Run(() =>
        {
            if (EnsureLoaded())
            {
                GetDatabase()?.GetFuzzyIndex();
            }
        });
    }

    public SearchResult Search(string text, Lang? language, int limit, CancellationToken ct)
    {
        if (!EnsureLoaded())
        {
            return SearchResult.Empty;
        }

        SearchEngine engine;
        lock (_lock)
        {
            engine = _engine!;
        }

        return engine.Search(new SearchRequest { Text = text, Language = language, Limit = limit }, ct);
    }

    /// <summary>Exact headword / inflected-form lookup only (cheap; used by the main-page fallback).</summary>
    public SearchResult Lookup(string text)
    {
        if (!EnsureLoaded())
        {
            return SearchResult.Empty;
        }

        SearchEngine engine;
        lock (_lock)
        {
            engine = _engine!;
        }

        return engine.Search(new SearchRequest { Text = text, Limit = 3, Fuzzy = false });
    }

    public WordView? GetView(WordRecord word)
    {
        lock (_lock)
        {
            if (_viewCache.TryGetValue(word.Id, out var cached))
            {
                _viewOrder.Remove(word.Id);
                _viewOrder.AddFirst(word.Id);
                return cached;
            }
        }

        if (!EnsureLoaded())
        {
            return null;
        }

        var db = GetDatabase();
        if (db is null)
        {
            return null;
        }

        var view = WordViewBuilder.Build(db, word);
        lock (_lock)
        {
            if (_viewCache.TryAdd(word.Id, view))
            {
                _viewOrder.AddFirst(word.Id);
            }

            while (_viewOrder.Count > 64)
            {
                _viewCache.Remove(_viewOrder.Last!.Value);
                _viewOrder.RemoveLast();
            }
        }

        return view;
    }

    /// <summary>Closes the database and drops caches (also called by the idle timer).</summary>
    public void Release()
    {
        lock (_lock)
        {
            _db?.Dispose();
            _db = null;
            _engine = null;
            _viewCache.Clear();
            _viewOrder.Clear();
            if (State == DictionaryState.Ready)
            {
                State = DictionaryState.NotLoaded;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        _idleTimer.Dispose();
        Release();
    }

    private LexiDatabase? GetDatabase()
    {
        lock (_lock)
        {
            return _db;
        }
    }

    private void Touch() => _idleTimer.Change(IdleRelease, Timeout.InfiniteTimeSpan);
}
