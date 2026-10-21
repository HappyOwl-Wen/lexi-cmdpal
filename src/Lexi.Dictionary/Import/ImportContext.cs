using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Core.Text;
using Lexi.Dictionary.Storage;
using Microsoft.Data.Sqlite;

namespace Lexi.Dictionary.Import;

/// <summary>Per-headword accumulator while sources are imported (list-view summary and popularity).</summary>
internal sealed class WordAcc
{
    public long Id;
    public Lang Lang;
    public string Word = string.Empty;
    public WordFlags Flags;
    public int Entries;
    public string? BriefZh;
    public int BriefZhRank = int.MaxValue;
    public string? BriefEn;
    public int BriefEnRank = int.MaxValue;
    public string? BriefDe;
    public int BriefDeRank = int.MaxValue;
    public string? Badge;
    public int BadgeRank = int.MaxValue;

    /// <summary>Popularity estimated from corpus ranks (ECDICT COCA/BNC) when no subtitle count exists.</summary>
    public float RankZipf;

    /// <summary>Summed subtitle counts of inflected forms that are not headwords themselves.</summary>
    public long FormCount;

    public void OfferBrief(string lang, string? text, int rank)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        switch (lang)
        {
            case "zh" when rank < BriefZhRank:
                BriefZh = text;
                BriefZhRank = rank;
                break;
            case "en" when rank < BriefEnRank:
                BriefEn = text;
                BriefEnRank = rank;
                break;
            case "de" when rank < BriefDeRank:
                BriefDe = text;
                BriefDeRank = rank;
                break;
        }
    }

    public void OfferBadge(string? badge, int rank)
    {
        if (!string.IsNullOrWhiteSpace(badge) && rank < BadgeRank)
        {
            Badge = badge;
            BadgeRank = rank;
        }
    }
}

/// <summary>
/// Shared state of one import run: headword registry, entry writer and the staging table for forms
/// (forms are resolved to lemma ids only at the end, because a lemma may come from a later source).
/// </summary>
internal sealed class ImportContext : IDisposable
{
    private const int CommitEvery = 25_000;

    private readonly SqliteConnection _db;
    private readonly SqliteCommand _insertEntry;
    private readonly SqliteCommand _insertStage;
    private readonly Dictionary<(Lang, string), WordAcc> _words = new();
    private readonly Dictionary<(long, int), int> _seq = new();
    private SqliteTransaction? _tx;
    private long _nextWordId = 1;
    private int _pending;

    public ImportContext(SqliteConnection db, Action<string> log, FrequencyTable freqEn, FrequencyTable freqDe)
    {
        _db = db;
        Log = log;
        FreqEn = freqEn;
        FreqDe = freqDe;

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE stage_forms (lang INTEGER NOT NULL, fold TEXT NOT NULL, form TEXT NOT NULL, lemma TEXT NOT NULL, tags TEXT NOT NULL, source_id INTEGER NOT NULL)";
            cmd.ExecuteNonQuery();
        }

        _tx = db.BeginTransaction();
        _insertEntry = db.CreateCommand();
        _insertEntry.CommandText = "INSERT INTO entries (word_id, source_id, seq, pos, data) VALUES ($w, $s, $q, $p, $d)";
        foreach (var p in new[] { "$w", "$s", "$q", "$p", "$d" })
        {
            _insertEntry.Parameters.Add(new SqliteParameter(p, null));
        }

        _insertStage = db.CreateCommand();
        _insertStage.CommandText = "INSERT INTO stage_forms (lang, fold, form, lemma, tags, source_id) VALUES ($l, $f, $fo, $le, $t, $s)";
        foreach (var p in new[] { "$l", "$f", "$fo", "$le", "$t", "$s" })
        {
            _insertStage.Parameters.Add(new SqliteParameter(p, null));
        }

        _insertEntry.Transaction = _tx;
        _insertStage.Transaction = _tx;
    }

    public Action<string> Log { get; }

    public FrequencyTable FreqEn { get; }

    public FrequencyTable FreqDe { get; }

    public int EntryCount { get; private set; }

    public int StagedForms { get; private set; }

    public IEnumerable<WordAcc> Words => _words.Values;

    public SqliteConnection Connection => _db;

    public FrequencyTable Freq(Lang lang) => lang == Lang.De ? FreqDe : FreqEn;

    public bool TryGetWord(Lang lang, string headword, out WordAcc word) => _words.TryGetValue((lang, headword), out word!);

    public WordAcc Word(Lang lang, string headword)
    {
        if (!_words.TryGetValue((lang, headword), out var w))
        {
            w = new WordAcc { Id = _nextWordId++, Lang = lang, Word = headword };
            if (headword.Contains(' ', StringComparison.Ordinal))
            {
                w.Flags |= WordFlags.MultiWord;
            }

            _words.Add((lang, headword), w);
        }

        return w;
    }

    public void AddEntry(WordAcc word, int sourceId, EntryData entry)
    {
        var key = (word.Id, sourceId);
        var seq = _seq.GetValueOrDefault(key);
        _seq[key] = seq + 1;

        _insertEntry.Parameters[0].Value = word.Id;
        _insertEntry.Parameters[1].Value = sourceId;
        _insertEntry.Parameters[2].Value = seq;
        _insertEntry.Parameters[3].Value = (object?)entry.Pos ?? DBNull.Value;
        _insertEntry.Parameters[4].Value = EntryCodec.Encode(entry);
        _insertEntry.ExecuteNonQuery();

        word.Entries++;
        EntryCount++;
        if (entry.Ipa is { Count: > 0 })
        {
            word.Flags |= WordFlags.HasIpa;
        }

        if (entry.Audio is { Count: > 0 })
        {
            word.Flags |= WordFlags.HasAudio;
        }

        Tick();
    }

    /// <summary>Stages "form → lemma". Tags use the Wiktionary vocabulary (space separated).</summary>
    public void StageForm(Lang lang, string form, string lemma, string tags, int sourceId)
    {
        form = form.Trim();
        lemma = lemma.Trim();
        if (form.Length == 0 || lemma.Length == 0 || form.Length > 60 || string.Equals(form, lemma, StringComparison.Ordinal))
        {
            return;
        }

        var fold = TextNormalizer.ToFold(form);
        if (fold.Length == 0 || fold == TextNormalizer.ToFold(lemma))
        {
            return;
        }

        _insertStage.Parameters[0].Value = (int)lang;
        _insertStage.Parameters[1].Value = fold;
        _insertStage.Parameters[2].Value = form;
        _insertStage.Parameters[3].Value = lemma;
        _insertStage.Parameters[4].Value = tags;
        _insertStage.Parameters[5].Value = sourceId;
        _insertStage.ExecuteNonQuery();
        StagedForms++;
        Tick();
    }

    public void Flush()
    {
        if (_tx is null)
        {
            throw new InvalidOperationException("Import already completed.");
        }

        _tx.Commit();
        _tx.Dispose();
        _tx = _db.BeginTransaction();
        _insertEntry.Transaction = _tx;
        _insertStage.Transaction = _tx;
        _pending = 0;
    }

    /// <summary>Commits the source-import transaction; the finalization steps then run their own transactions.</summary>
    public void Complete()
    {
        if (_tx is not null)
        {
            _tx.Commit();
            _tx.Dispose();
            _tx = null;
        }
    }

    public void Dispose()
    {
        Complete();
        _insertEntry.Dispose();
        _insertStage.Dispose();
    }

    private void Tick()
    {
        if (++_pending >= CommitEvery)
        {
            Flush();
        }
    }
}
