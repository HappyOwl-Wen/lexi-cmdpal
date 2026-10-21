using System.Globalization;
using System.Text;
using Lexi.Core.Models;
using Lexi.Core.Search;
using Microsoft.Data.Sqlite;

namespace Lexi.Dictionary.Storage;

public sealed record StoredEntry(SourceInfo Source, int Seq, string? Pos, EntryData Data);

public sealed record DatabaseInfo(int SchemaVersion, string BuiltAt, long WordCount, long EntryCount, long FormCount, string Profile, long ChineseTerms);

/// <summary>
/// Read-only access to lexi.db. One connection, prepared statements, a lock around every call:
/// searches are serialized by the UI anyway and SQLite connections are not thread-safe.
/// </summary>
public sealed class LexiDatabase : ILexiconStore, IDisposable
{
    private const string WordColumns = "w.id, w.lang, w.word, w.fold, w.zipf, w.flags, w.badge, w.brief";

    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, SourceInfo> _sources;
    private readonly SqliteCommand _byFold;
    private readonly SqliteCommand _byFolds;
    private readonly SqliteCommand _formsByFold;
    private readonly SqliteCommand _formsByFolds;
    private readonly SqliteCommand _prefix;
    private readonly SqliteCommand _entries;
    private readonly SqliteCommand _byId;
    private readonly SqliteCommand _formsOfLemma;
    private readonly SqliteCommand? _chinese;
    private FuzzyIndex? _fuzzy;
    private bool _fuzzyLoaded;
    private bool _disposed;

    private LexiDatabase(string path, SqliteConnection connection)
    {
        Path = path;
        _connection = connection;
        Exec("PRAGMA query_only = 1; PRAGMA mmap_size = 268435456; PRAGMA cache_size = -16000; PRAGMA temp_store = MEMORY;");

        Info = ReadInfo();
        if (Info.SchemaVersion != Schema.Version)
        {
            throw new InvalidDataException($"Dictionary schema version {Info.SchemaVersion} is not supported (expected {Schema.Version}). Rebuild the database with the importer.");
        }

        _sources = ReadSources();

        _byFold = Prepare($"SELECT {WordColumns} FROM words w WHERE w.fold = $fold AND ($lang = 0 OR w.lang = $lang)", "$fold", "$lang");
        _byFolds = Prepare($"SELECT {WordColumns} FROM words w WHERE w.fold IN (SELECT value FROM json_each($folds)) AND ($lang = 0 OR w.lang = $lang)", "$folds", "$lang");
        _formsByFold = Prepare(
            $"SELECT f.form, t.tags, {WordColumns} FROM forms f JOIN form_tags t ON t.id = f.tags_id JOIN words w ON w.id = f.lemma_id WHERE f.fold = $fold AND ($lang = 0 OR w.lang = $lang)",
            "$fold",
            "$lang");
        _formsByFolds = Prepare(
            $"SELECT f.form, t.tags, {WordColumns} FROM forms f JOIN form_tags t ON t.id = f.tags_id JOIN words w ON w.id = f.lemma_id WHERE f.fold IN (SELECT value FROM json_each($folds)) AND ($lang = 0 OR w.lang = $lang)",
            "$folds",
            "$lang");
        _prefix = Prepare(
            $"SELECT {WordColumns} FROM (SELECT id FROM words WHERE fold >= $lo AND fold < $hi AND ($lang = 0 OR lang = $lang) ORDER BY zipf DESC LIMIT $limit) t JOIN words w ON w.id = t.id ORDER BY w.zipf DESC",
            "$lo",
            "$hi",
            "$lang",
            "$limit");
        _entries = Prepare("SELECT source_id, seq, pos, data FROM entries WHERE word_id = $id ORDER BY source_id, seq", "$id");
        _byId = Prepare($"SELECT {WordColumns} FROM words w WHERE w.id = $id", "$id");
        _formsOfLemma = Prepare("SELECT f.form, t.tags FROM forms f JOIN form_tags t ON t.id = f.tags_id WHERE f.lemma_id = $id LIMIT 400", "$id");

        // The reverse index is optional (databases built before it existed lack it).
        HasChineseIndex = Exists("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'zh_index'");
        if (HasChineseIndex)
        {
            var zipf = SearchEngine.ChineseZipfWeight.ToString(CultureInfo.InvariantCulture);
            _chinese = Prepare(
                $"SELECT {WordColumns}, z.weight FROM zh_index z JOIN words w ON w.id = z.word_id WHERE z.term = $term AND ($lang = 0 OR w.lang = $lang) ORDER BY z.weight + w.zipf * {zipf} DESC LIMIT $limit",
                "$term",
                "$lang",
                "$limit");
        }
    }

    /// <summary>True when the database can answer Chinese queries (see <see cref="Import.ZhIndexBuilder"/>).</summary>
    public bool HasChineseIndex { get; }

    public string Path { get; }

    public DatabaseInfo Info { get; }

    public IReadOnlyCollection<SourceInfo> Sources => _sources.Values;

    public static LexiDatabase Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Dictionary database not found.", path);
        }

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(cs);
        connection.Open();
        try
        {
            return new LexiDatabase(path, connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public IReadOnlyList<WordRecord> GetWordsByFold(string fold, Lang? language)
    {
        lock (_lock)
        {
            _byFold.Parameters[0].Value = fold;
            _byFold.Parameters[1].Value = (int)(language ?? Lang.Unknown);
            return ReadWords(_byFold);
        }
    }

    public IReadOnlyList<WordRecord> GetWordsByFolds(IReadOnlyCollection<string> folds, Lang? language)
    {
        if (folds.Count == 0)
        {
            return [];
        }

        lock (_lock)
        {
            _byFolds.Parameters[0].Value = ToJsonArray(folds);
            _byFolds.Parameters[1].Value = (int)(language ?? Lang.Unknown);
            return ReadWords(_byFolds);
        }
    }

    public IReadOnlyList<FormRecord> GetFormsByFold(string fold, Lang? language)
    {
        lock (_lock)
        {
            _formsByFold.Parameters[0].Value = fold;
            _formsByFold.Parameters[1].Value = (int)(language ?? Lang.Unknown);
            return ReadForms(_formsByFold);
        }
    }

    public IReadOnlyList<FormRecord> GetFormsByFolds(IReadOnlyCollection<string> folds, Lang? language)
    {
        if (folds.Count == 0)
        {
            return [];
        }

        lock (_lock)
        {
            _formsByFolds.Parameters[0].Value = ToJsonArray(folds);
            _formsByFolds.Parameters[1].Value = (int)(language ?? Lang.Unknown);
            return ReadForms(_formsByFolds);
        }
    }

    public IReadOnlyList<WordRecord> GetWordsByPrefix(string foldPrefix, Lang? language, int limit)
    {
        if (foldPrefix.Length == 0)
        {
            return [];
        }

        var last = foldPrefix[^1];
        if (last == char.MaxValue)
        {
            return [];
        }

        var hi = string.Concat(foldPrefix.AsSpan(0, foldPrefix.Length - 1), ((char)(last + 1)).ToString());
        lock (_lock)
        {
            _prefix.Parameters[0].Value = foldPrefix;
            _prefix.Parameters[1].Value = hi;
            _prefix.Parameters[2].Value = (int)(language ?? Lang.Unknown);
            _prefix.Parameters[3].Value = limit;
            return ReadWords(_prefix);
        }
    }

    public FuzzyIndex? GetFuzzyIndex()
    {
        if (_fuzzyLoaded)
        {
            return _fuzzy;
        }

        lock (_lock)
        {
            if (!_fuzzyLoaded)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT data FROM blobs WHERE name = 'fuzzy'";
                if (cmd.ExecuteScalar() is byte[] blob)
                {
                    _fuzzy = FuzzyIndex.Deserialize(blob);
                }

                _fuzzyLoaded = true;
            }

            return _fuzzy;
        }
    }

    public IReadOnlyList<ZhMatch> GetWordsByChinese(string term, Lang? language, int limit)
    {
        if (_chinese is null || term.Length == 0)
        {
            return [];
        }

        // The index holds simplified Chinese (sources are converted at import); accept traditional input too.
        var key = Import.ChineseScript.ToSimplified(term);
        lock (_lock)
        {
            _chinese.Parameters[0].Value = key;
            _chinese.Parameters[1].Value = (int)(language ?? Lang.Unknown);
            _chinese.Parameters[2].Value = limit;
            var list = new List<ZhMatch>();
            using var r = _chinese.ExecuteReader();
            while (r.Read())
            {
                list.Add(new ZhMatch(ReadWord(r, 0), key, r.GetInt32(8)));
            }

            return list;
        }
    }

    public WordRecord? GetWord(long id)
    {
        lock (_lock)
        {
            _byId.Parameters[0].Value = id;
            var list = ReadWords(_byId);
            return list.Count > 0 ? list[0] : null;
        }
    }

    public IReadOnlyList<StoredEntry> GetEntries(long wordId)
    {
        lock (_lock)
        {
            _entries.Parameters[0].Value = wordId;
            var result = new List<StoredEntry>();
            using var r = _entries.ExecuteReader();
            while (r.Read())
            {
                var sourceId = r.GetInt32(0);
                var source = _sources.TryGetValue(sourceId, out var s)
                    ? s
                    : new SourceInfo(sourceId, "unknown", "Unknown", "", "", null, "", false);
                var blob = (byte[])r.GetValue(3);
                result.Add(new StoredEntry(source, r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2), EntryCodec.Decode(blob)));
            }

            return result;
        }
    }

    /// <summary>Forms that point at a lemma (used for the inflection overview).</summary>
    public IReadOnlyList<(string Form, string Tags)> GetFormsOfLemma(long lemmaId)
    {
        lock (_lock)
        {
            _formsOfLemma.Parameters[0].Value = lemmaId;
            var result = new List<(string, string)>();
            using var r = _formsOfLemma.ExecuteReader();
            while (r.Read())
            {
                result.Add((r.GetString(0), r.GetString(1)));
            }

            return result;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var cmd in new[] { _byFold, _byFolds, _formsByFold, _formsByFolds, _prefix, _entries, _byId, _formsOfLemma })
            {
                cmd.Dispose();
            }

            _chinese?.Dispose();

            _connection.Dispose();
        }
    }

    private static string ToJsonArray(IReadOnlyCollection<string> values)
    {
        var sb = new StringBuilder(values.Count * 12);
        sb.Append('[');
        var first = true;
        foreach (var v in values)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append('"');
            foreach (var c in v)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static List<WordRecord> ReadWords(SqliteCommand cmd)
    {
        var list = new List<WordRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(ReadWord(r, 0));
        }

        return list;
    }

    private static List<FormRecord> ReadForms(SqliteCommand cmd)
    {
        var list = new List<FormRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new FormRecord(r.GetString(0), r.GetString(1), ReadWord(r, 2)));
        }

        return list;
    }

    private static WordRecord ReadWord(SqliteDataReader r, int o) => new(
        r.GetInt64(o),
        (Lang)r.GetInt32(o + 1),
        r.GetString(o + 2),
        r.GetString(o + 3),
        (float)r.GetDouble(o + 4),
        (WordFlags)r.GetInt32(o + 5),
        r.IsDBNull(o + 6) ? null : r.GetString(o + 6),
        r.IsDBNull(o + 7) ? null : r.GetString(o + 7));

    private SqliteCommand Prepare(string sql, params string[] parameters)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(new SqliteParameter(p, null));
        }

        return cmd;
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private DatabaseInfo ReadInfo()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM meta";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                meta[r.GetString(0)] = r.GetString(1);
            }
        }

        long Num(string key) => meta.TryGetValue(key, out var v) && long.TryParse(v, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return new DatabaseInfo(
            (int)Num("schema_version"),
            meta.GetValueOrDefault("built_at", "?"),
            Num("word_count"),
            Num("entry_count"),
            Num("form_count"),
            meta.GetValueOrDefault("profile", "personal"),
            Num("zh_terms"));
    }

    private bool Exists(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() is not null;
    }

    private Dictionary<int, SourceInfo> ReadSources()
    {
        var result = new Dictionary<int, SourceInfo>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, code, name, license, url, version, attribution, redistributable FROM sources";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var s = new SourceInfo(
                r.GetInt32(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.GetString(6),
                r.GetInt32(7) != 0);
            result[s.Id] = s;
        }

        return result;
    }
}
