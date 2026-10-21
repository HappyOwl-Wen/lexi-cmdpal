using System.Diagnostics;
using System.Globalization;
using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Core.Text;
using Lexi.Dictionary.Sources;
using Lexi.Dictionary.Storage;
using Microsoft.Data.Sqlite;

namespace Lexi.Dictionary.Import;

public sealed class ImportOptions
{
    public string RawDirectory { get; init; } = DataPaths.RawDirectory;

    public string OutputPath { get; init; } = DataPaths.DefaultDatabasePath;

    /// <summary>Max entries per source (quick test builds). Null = full import.</summary>
    public int? LimitPerSource { get; init; }

    /// <summary>ECDICT content is not confirmed redistributable; disable for a redistributable build.</summary>
    public bool IncludeEcdict { get; init; } = true;

    public Action<string> Log { get; init; } = Console.WriteLine;
}

public sealed record ImportReport(string OutputPath, long Words, long Entries, long Forms, int FuzzyKeys, long FuzzyBytes, long FileBytes, TimeSpan Elapsed);

/// <summary>Repeatable build of lexi.db from the raw downloads (see <see cref="RawFiles"/>).</summary>
public static class LexiImporter
{
    /// <summary>Minimum subtitle count for an inflected form to be included in the fuzzy index.</summary>
    private const int FuzzyFormMinCount = 30;

    public static ImportReport Run(ImportOptions options)
    {
        var sw = Stopwatch.StartNew();
        var log = options.Log;
        var raw = new RawFiles(options.RawDirectory);
        var missing = raw.Missing(options.IncludeEcdict).ToList();
        if (missing.Count > 0)
        {
            throw new FileNotFoundException("Missing raw data files (run `lexi download` first):" + Environment.NewLine + string.Join(Environment.NewLine, missing));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
        var tmp = options.OutputPath + ".building";
        File.Delete(tmp);

        var cs = new SqliteConnectionStringBuilder { DataSource = tmp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using (var db = new SqliteConnection(cs))
        {
            db.Open();
            Exec(db, "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA locking_mode = EXCLUSIVE; PRAGMA cache_size = -262144; PRAGMA temp_store = FILE;");
            Exec(db, Schema.CreateTables);

            var sourceIds = InsertSources(db, raw, options.IncludeEcdict);
            log("Loading frequency lists…");
            var freqEn = FrequencyTable.Load(raw.FreqEn);
            var freqDe = FrequencyTable.Load(raw.FreqDe);
            log($"  en: {freqEn.Count:N0} words, de: {freqDe.Count:N0} words");

            var ctx = new ImportContext(db, log, freqEn, freqDe);
            try
            {
                if (options.IncludeEcdict)
                {
                    log("Importing ECDICT…");
                    EcdictImporter.Import(ctx, raw.Ecdict, sourceIds["ecdict"], options.LimitPerSource);
                }

                log("Importing Wiktionary (English edition, English words)…");
                KaikkiImporter.Import(ctx, new KaikkiImporter.Options(
                    raw.WiktionaryEnglish, KaikkiEdition.English, Lang.En, sourceIds["wikt-en"], "kaikki:enwiktionary", options.LimitPerSource,
                    AcceptHeadword: w => ctx.TryGetWord(Lang.En, w, out var acc) && acc.Entries > 0 || freqEn.CountOf(w.ToLowerInvariant()) >= 3));

                log("Importing Wiktionary (German edition)…");
                KaikkiImporter.Import(ctx, new KaikkiImporter.Options(
                    raw.WiktionaryDe, KaikkiEdition.German, Lang.De, sourceIds["wikt-de"], "kaikki:dewiktionary", options.LimitPerSource));

                log("Importing Wiktionary (English edition, German words)…");
                KaikkiImporter.Import(ctx, new KaikkiImporter.Options(
                    raw.WiktionaryGerman, KaikkiEdition.English, Lang.De, sourceIds["wikt-en"], "kaikki:enwiktionary", options.LimitPerSource));

                log("Importing Wiktionary (Chinese edition, German words)…");
                KaikkiImporter.Import(ctx, new KaikkiImporter.Options(
                    raw.WiktionaryZh, KaikkiEdition.Chinese, Lang.De, sourceIds["wikt-zh"], "kaikki:zhwiktionary", options.LimitPerSource));

                ctx.Complete();
                log($"Entries: {ctx.EntryCount:N0}; staged forms: {ctx.StagedForms:N0}");

                log("Resolving inflected forms…");
                var (formCount, fuzzyForms) = ResolveForms(ctx, sourceIds.GetValueOrDefault("ecdict", -1));
                log($"  {formCount:N0} form → lemma links");

                log("Writing headwords…");
                var wordCount = InsertWords(ctx);
                log($"  {wordCount:N0} headwords");
                ctx.Dispose();

                log("Building fuzzy index…");
                var (fuzzyKeys, fuzzyBytes) = BuildFuzzyIndex(db, fuzzyForms);

                log("Creating indexes and statistics…");
                Exec(db, "DROP TABLE stage_forms;");
                Exec(db, Schema.CreateIndexes);
                WriteMeta(db, new Dictionary<string, string>
                {
                    ["schema_version"] = Schema.Version.ToString(CultureInfo.InvariantCulture),
                    ["built_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                    ["word_count"] = wordCount.ToString(CultureInfo.InvariantCulture),
                    ["entry_count"] = ctx.EntryCount.ToString(CultureInfo.InvariantCulture),
                    ["form_count"] = formCount.ToString(CultureInfo.InvariantCulture),
                    ["profile"] = options.IncludeEcdict ? "personal" : "redistributable",
                    ["limit_per_source"] = options.LimitPerSource?.ToString(CultureInfo.InvariantCulture) ?? "none",
                });
                Exec(db, "ANALYZE;");
                log("Compacting (VACUUM)…");
                Exec(db, "PRAGMA journal_mode = DELETE; VACUUM;");
                db.Close();

                SqliteConnection.ClearAllPools();
                File.Move(tmp, options.OutputPath, overwrite: true);
                var size = new FileInfo(options.OutputPath).Length;
                log($"Done: {options.OutputPath} ({size / 1048576.0:F1} MB) in {sw.Elapsed.TotalMinutes:F1} min");
                return new ImportReport(options.OutputPath, wordCount, ctx.EntryCount, formCount, fuzzyKeys, fuzzyBytes, size, sw.Elapsed);
            }
            catch
            {
                ctx.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Turns staged (form, lemma-name) rows into form → lemma-id links. Rows are read ordered by form, so all
    /// lemmas of one form are seen together: tags are merged per link, and the form's corpus count is shared
    /// among its lemmas in proportion to their own counts (so "Häuser" lends its frequency to Haus, not to a rare
    /// homograph). Lemmas must match a headword exactly (case matters: German nouns vs. other words).
    /// ECDICT inflections are machine-generated, so a link backed only by ECDICT must share a prefix with its
    /// lemma (rejects e.g. "ging" → "ge"); curated Wiktionary links (went → go) are always kept.
    /// </summary>
    private static (long Count, HashSet<string> FuzzyFolds) ResolveForms(ImportContext ctx, int ecdictSourceId)
    {
        var db = ctx.Connection;
        var tagIds = new Dictionary<string, long>(StringComparer.Ordinal);
        using var insertTag = db.CreateCommand();
        insertTag.CommandText = "INSERT INTO form_tags (id, tags) VALUES ($id, $t)";
        insertTag.Parameters.Add(new SqliteParameter("$id", null));
        insertTag.Parameters.Add(new SqliteParameter("$t", null));

        using var insertForm = db.CreateCommand();
        insertForm.CommandText = "INSERT OR IGNORE INTO forms (fold, form, lemma_id, tags_id, source_id) VALUES ($f, $fo, $l, $t, $s)";
        foreach (var p in new[] { "$f", "$fo", "$l", "$t", "$s" })
        {
            insertForm.Parameters.Add(new SqliteParameter(p, null));
        }

        var fuzzyFolds = new HashSet<string>(StringComparer.Ordinal);
        long count = 0;
        var tx = db.BeginTransaction();
        insertTag.Transaction = tx;
        insertForm.Transaction = tx;

        using (var select = db.CreateCommand())
        {
            select.CommandText = "SELECT lang, fold, form, lemma, tags, source_id FROM stage_forms ORDER BY fold, lang, form, lemma";
            using var r = select.ExecuteReader();
            (Lang Lang, string Fold, string Form)? group = null;
            var links = new Dictionary<string, PendingLink>(StringComparer.Ordinal);

            void EmitGroup()
            {
                if (group is not { } g || links.Count == 0)
                {
                    links.Clear();
                    return;
                }

                var accepted = new List<(WordAcc Lemma, PendingLink Link)>();
                foreach (var (lemmaName, link) in links)
                {
                    if (!ctx.TryGetWord(g.Lang, lemmaName, out var lemma) || lemma.Entries == 0 ||
                        TextNormalizer.ToFold(lemma.Word) == g.Fold)
                    {
                        continue;
                    }

                    if (link.Sources.Count == 1 && link.Sources.Contains(ecdictSourceId) &&
                        CommonPrefix(g.Form.ToLowerInvariant(), lemma.Word.ToLowerInvariant()) < Math.Min(3, lemma.Word.Length))
                    {
                        continue;
                    }

                    accepted.Add((lemma, link));
                }

                links.Clear();
                if (accepted.Count == 0)
                {
                    return;
                }

                var freq = ctx.Freq(g.Lang);
                var formCount = freq.CountOf(g.Form.ToLowerInvariant());
                var formIsHeadword = ctx.TryGetWord(g.Lang, g.Form, out var asWord) && asWord.Entries > 0;
                var weights = accepted.Select(a => freq.CountOf(a.Lemma.Word.ToLowerInvariant()) + 1.0).ToList();
                var total = weights.Sum();
                for (var i = 0; i < accepted.Count; i++)
                {
                    var (lemma, link) = accepted[i];
                    var tagText = string.Join(' ', link.Tags);
                    if (!tagIds.TryGetValue(tagText, out var tagId))
                    {
                        tagId = tagIds.Count + 1;
                        tagIds[tagText] = tagId;
                        insertTag.Parameters[0].Value = tagId;
                        insertTag.Parameters[1].Value = tagText;
                        insertTag.ExecuteNonQuery();
                    }

                    insertForm.Parameters[0].Value = g.Fold;
                    insertForm.Parameters[1].Value = g.Form;
                    insertForm.Parameters[2].Value = lemma.Id;
                    insertForm.Parameters[3].Value = tagId;
                    insertForm.Parameters[4].Value = link.Sources.Min();
                    if (insertForm.ExecuteNonQuery() == 0)
                    {
                        continue;
                    }

                    count++;
                    if (!formIsHeadword)
                    {
                        lemma.FormCount += (long)(formCount * weights[i] / total);
                    }

                    if (count % 50_000 == 0)
                    {
                        tx.Commit();
                        tx.Dispose();
                        tx = db.BeginTransaction();
                        insertTag.Transaction = tx;
                        insertForm.Transaction = tx;
                    }
                }

                if (formCount >= FuzzyFormMinCount)
                {
                    fuzzyFolds.Add(g.Fold);
                }
            }

            while (r.Read())
            {
                var lang = (Lang)r.GetInt32(0);
                var fold = r.GetString(1);
                var form = r.GetString(2);
                if (group is null || group.Value.Lang != lang || group.Value.Fold != fold || group.Value.Form != form)
                {
                    EmitGroup();
                    group = (lang, fold, form);
                }

                var lemmaName = r.GetString(3);
                if (!links.TryGetValue(lemmaName, out var link))
                {
                    links[lemmaName] = link = new PendingLink();
                }

                foreach (var t in r.GetString(4).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    link.Tags.Add(t);
                }

                link.Sources.Add(r.GetInt32(5));
            }

            EmitGroup();
        }

        tx.Commit();
        tx.Dispose();
        return (count, fuzzyFolds);
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    private sealed class PendingLink
    {
        public SortedSet<string> Tags { get; } = new(StringComparer.Ordinal);

        public HashSet<int> Sources { get; } = [];
    }

    private static long InsertWords(ImportContext ctx)
    {
        var db = ctx.Connection;
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO words (id, lang, word, fold, zipf, flags, badge, brief) VALUES ($id, $l, $w, $f, $z, $fl, $b, $br)";
        foreach (var p in new[] { "$id", "$l", "$w", "$f", "$z", "$fl", "$b", "$br" })
        {
            cmd.Parameters.Add(new SqliteParameter(p, null));
        }

        long n = 0;
        foreach (var w in ctx.Words)
        {
            if (w.Entries == 0)
            {
                continue;
            }

            var freq = ctx.Freq(w.Lang);
            var own = freq.CountOf(w.Word.ToLowerInvariant());
            var zipf = Math.Max(freq.Zipf(own + w.FormCount), w.RankZipf);
            var flags = w.Flags;
            if (w.Lang == Lang.De && zipf >= 4.2f)
            {
                flags |= WordFlags.Core;
            }

            cmd.Parameters[0].Value = w.Id;
            cmd.Parameters[1].Value = (int)w.Lang;
            cmd.Parameters[2].Value = w.Word;
            cmd.Parameters[3].Value = TextNormalizer.ToFold(w.Word);
            cmd.Parameters[4].Value = Math.Round(zipf, 3);
            cmd.Parameters[5].Value = (int)flags;
            cmd.Parameters[6].Value = (object?)w.Badge ?? DBNull.Value;
            cmd.Parameters[7].Value = (object?)(w.BriefZh ?? w.BriefEn ?? w.BriefDe) ?? DBNull.Value;
            cmd.ExecuteNonQuery();
            n++;
        }

        tx.Commit();
        return n;
    }

    private static (int Keys, long Bytes) BuildFuzzyIndex(SqliteConnection db, HashSet<string> formFolds)
    {
        var keys = new List<string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT fold FROM words";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var f = r.GetString(0);
                if (f.Length >= 2 && f.Count(c => c == ' ') <= 2)
                {
                    keys.Add(f);
                }
            }
        }

        keys.AddRange(formFolds);
        var index = FuzzyIndex.Build(keys);
        var blob = index.Serialize();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO blobs (name, data) VALUES ('fuzzy', $d)";
            cmd.Parameters.AddWithValue("$d", blob);
            cmd.ExecuteNonQuery();
        }

        return (index.KeyCount, blob.LongLength);
    }

    private static Dictionary<string, int> InsertSources(SqliteConnection db, RawFiles raw, bool includeEcdict)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO sources (id, code, name, license, url, version, attribution, redistributable) VALUES ($id, $c, $n, $l, $u, $v, $a, $r)";
        foreach (var p in new[] { "$id", "$c", "$n", "$l", "$u", "$v", "$a", "$r" })
        {
            cmd.Parameters.Add(new SqliteParameter(p, null));
        }

        var id = 1;
        foreach (var s in KnownSources.All)
        {
            if (s.Code == "ecdict" && !includeEcdict)
            {
                continue;
            }

            cmd.Parameters[0].Value = id;
            cmd.Parameters[1].Value = s.Code;
            cmd.Parameters[2].Value = s.Name;
            cmd.Parameters[3].Value = s.License;
            cmd.Parameters[4].Value = s.Url;
            cmd.Parameters[5].Value = (object?)raw.VersionOf(s.Code) ?? DBNull.Value;
            cmd.Parameters[6].Value = s.Attribution;
            cmd.Parameters[7].Value = s.Redistributable ? 1 : 0;
            cmd.ExecuteNonQuery();
            ids[s.Code] = id++;
        }

        return ids;
    }

    private static void WriteMeta(SqliteConnection db, Dictionary<string, string> meta)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)";
        cmd.Parameters.Add(new SqliteParameter("$k", null));
        cmd.Parameters.Add(new SqliteParameter("$v", null));
        foreach (var (k, v) in meta)
        {
            cmd.Parameters[0].Value = k;
            cmd.Parameters[1].Value = v;
            cmd.ExecuteNonQuery();
        }
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        cmd.ExecuteNonQuery();
    }
}
