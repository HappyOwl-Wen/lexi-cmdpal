using System.Globalization;
using Lexi.Core.Models;
using Lexi.Core.Text;
using Lexi.Dictionary.Storage;
using Microsoft.Data.Sqlite;

namespace Lexi.Dictionary.Import;

/// <summary>
/// Builds the reverse (中 → 英/德) index from the Chinese text the sources give for each headword: ECDICT
/// translations and en.wiktionary translation tables of English words, zh.wiktionary glosses and de.wiktionary
/// translation tables of German words, and the translation tables of Chinese headwords in zh.wiktionary.
/// A term's weight says how central it is: the first term of the first definition line is 100, later terms and
/// lines less, web-sourced ECDICT lines ("[网络]") about half, name transliterations ("[地名]") less; every further
/// source that links the same term and word adds <see cref="AgreementBonus"/>. Nothing is chained through another
/// language: a German word is only found through Chinese text a source gives for that German word.
/// </summary>
public static class ZhIndexBuilder
{
    /// <summary>Added to a link's weight for each further independent source that agrees with it.</summary>
    public const int AgreementBonus = 6;

    private const int MaxLinesPerEntry = 12;
    private const int MaxTermsPerLine = 16;
    private const double WebLineFactor = 0.55;
    private const double NameLineFactor = 0.3;
    // "危险的" → 危险: dictionaries write almost every adjective with 的, so the stem is nearly as good a match.
    private const double StemFactor = 0.95;
    private const double TranslationFactor = 0.95;
    private const double ReverseFactor = 0.9;

    /// <summary>Adds (or rebuilds) the index in an existing database, e.g. one built before the index existed.</summary>
    public static (long Terms, long Links) AddTo(string databasePath, Action<string> log)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        using var db = new SqliteConnection(cs);
        db.Open();
        Exec(db, "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA locking_mode = EXCLUSIVE; PRAGMA cache_size = -262144; PRAGMA temp_store = FILE;");
        var result = Build(db, log);
        Exec(db, "ANALYZE;");
        log("Compacting (VACUUM)…");
        Exec(db, "PRAGMA journal_mode = DELETE; VACUUM;");
        db.Close();
        SqliteConnection.ClearAllPools();
        return result;
    }

    internal static (long Terms, long Links) Build(SqliteConnection db, Action<string> log)
    {
        Exec(db, "DROP TABLE IF EXISTS zh_index; DROP TABLE IF EXISTS zh_stage; CREATE TABLE zh_stage (term TEXT NOT NULL, word_id INTEGER NOT NULL, source_id INTEGER NOT NULL, weight INTEGER NOT NULL);");
        long staged = 0;
        long entries = 0;
        using (var tx = db.BeginTransaction())
        {
            using var insert = db.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO zh_stage (term, word_id, source_id, weight) VALUES ($t, $w, $s, $g)";
            var term = insert.Parameters.Add("$t", SqliteType.Text);
            var wordId = insert.Parameters.Add("$w", SqliteType.Integer);
            var sourceId = insert.Parameters.Add("$s", SqliteType.Integer);
            var weight = insert.Parameters.Add("$g", SqliteType.Integer);

            using var read = db.CreateCommand();
            read.Transaction = tx;
            read.CommandText = "SELECT e.word_id, e.data, w.word, e.source_id FROM entries e JOIN words w ON w.id = e.word_id";
            var best = new Dictionary<string, int>(StringComparer.Ordinal);
            using (var r = read.ExecuteReader())
            {
                while (r.Read())
                {
                    // ECDICT also lists example sentences and long phrases as headwords; they are not translations.
                    if (r.GetString(2).Count(c => c == ' ') >= 3)
                    {
                        continue;
                    }

                    best.Clear();
                    Collect(EntryCodec.Decode((byte[])r.GetValue(1)), best);
                    if (best.Count == 0)
                    {
                        continue;
                    }

                    entries++;
                    wordId.Value = r.GetInt64(0);
                    sourceId.Value = r.GetInt64(3);
                    foreach (var (text, w) in best)
                    {
                        term.Value = text;
                        weight.Value = w;
                        insert.ExecuteNonQuery();
                        staged++;
                    }
                }
            }

            tx.Commit();
        }

        log($"  {staged:N0} term links from {entries:N0} entries with Chinese text");
        Exec(db, Schema.CreateZhIndex);
        Exec(db, $"""
            INSERT INTO zh_index (term, word_id, weight)
            SELECT term, word_id, MAX(weight) + {AgreementBonus} * (COUNT(DISTINCT source_id) - 1)
            FROM zh_stage GROUP BY term, word_id ORDER BY term, word_id;
            DROP TABLE zh_stage;
            """);
        var links = Scalar(db, "SELECT COUNT(*) FROM zh_index");
        var terms = Scalar(db, "SELECT COUNT(DISTINCT term) FROM zh_index");
        using (var meta = db.CreateCommand())
        {
            meta.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ('zh_terms', $t), ('zh_links', $l)";
            meta.Parameters.AddWithValue("$t", terms.ToString(CultureInfo.InvariantCulture));
            meta.Parameters.AddWithValue("$l", links.ToString(CultureInfo.InvariantCulture));
            meta.ExecuteNonQuery();
        }

        return (terms, links);
    }

    /// <summary>Weight of a term: definition line (0 = first) and position within the line (0 = first).</summary>
    internal static int Weight(int line, int position, double factor)
    {
        var lineFactor = line switch { 0 => 1.0, 1 => 0.93, 2 => 0.87, 3 => 0.82, _ => 0.78 };
        var positionFactor = position switch { 0 => 1.0, 1 => 0.94, 2 => 0.89, 3 => 0.85, 4 => 0.81, 5 => 0.78, _ => 0.75 };
        return Math.Clamp((int)Math.Round(100 * lineFactor * positionFactor * factor), 1, 100);
    }

    /// <summary>Best weight per term of one entry.</summary>
    internal static void Collect(EntryData data, Dictionary<string, int> best)
    {
        if (data.Senses is not { Count: > 0 } senses)
        {
            return;
        }

        if (data.Kind == EntryData.ReverseKind)
        {
            // Each sense is a Chinese headword whose own entry lists this word as a translation.
            foreach (var sense in senses)
            {
                Add(ChineseTerms.Extract(sense.Gloss).Where(t => !t.IsStem).Select(t => t with { Position = 0 }).ToList(), 0, ReverseFactor, best);
            }

            return;
        }

        var glossLine = 0;
        var translationLine = 0;
        foreach (var sense in senses)
        {
            if (sense.GlossLang == "zh" && !string.IsNullOrWhiteSpace(sense.Gloss) && glossLine < MaxLinesPerEntry)
            {
                var factor = ChineseTerms.IsWebLine(sense.Gloss) ? WebLineFactor : ChineseTerms.IsNameLine(sense.Gloss) ? NameLineFactor : 1;
                Add(ChineseTerms.Extract(sense.Gloss), glossLine++, factor, best);
            }

            if (sense.Zh is { Count: > 0 } equivalents && translationLine < MaxLinesPerEntry)
            {
                // Sense-aligned equivalents: every item is one translation of this sense.
                var terms = new List<ChineseTerms.Term>();
                for (var i = 0; i < equivalents.Count; i++)
                {
                    terms.AddRange(ChineseTerms.Extract(equivalents[i]).Select(t => t with { Position = i }));
                }

                Add(terms, translationLine++, TranslationFactor, best);
            }
        }
    }

    private static void Add(List<ChineseTerms.Term> terms, int line, double factor, Dictionary<string, int> best)
    {
        foreach (var t in terms)
        {
            if (t.Position >= MaxTermsPerLine)
            {
                break;
            }

            var w = Weight(line, t.Position, factor * (t.IsStem ? StemFactor : 1));
            if (!best.TryGetValue(t.Text, out var old) || w > old)
            {
                best[t.Text] = w;
            }
        }
    }

    private static long Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
