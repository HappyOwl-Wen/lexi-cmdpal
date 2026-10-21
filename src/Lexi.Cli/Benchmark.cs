using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Lexi.Core.Search;
using Lexi.Dictionary.Quality;
using Lexi.Dictionary.Storage;
using Lexi.Dictionary.View;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace Lexi.Cli;

/// <summary>
/// Latency benchmark over real queries: the acceptance list, random headwords, typo variants and prefixes
/// sampled from the database itself. Reports cold start and warm percentiles plus machine details.
/// </summary>
internal static class Benchmark
{
    public static int Run(string dbPath, int rounds, int sample, string? outFile)
    {
        var report = new StringBuilder();
        void Line(string s = "")
        {
            Console.WriteLine(s);
            report.AppendLine(s);
        }

        var swOpen = Stopwatch.StartNew();
        using var db = LexiDatabase.Open(dbPath);
        swOpen.Stop();

        var engine = new SearchEngine(db);
        var swFirst = Stopwatch.StartNew();
        engine.Search(new SearchRequest { Text = "house", Fuzzy = false });
        swFirst.Stop();
        var swFuzzy = Stopwatch.StartNew();
        var fuzzy = db.GetFuzzyIndex();
        swFuzzy.Stop();
        var swFirstFuzzy = Stopwatch.StartNew();
        engine.Search(new SearchRequest { Text = "vulnerabilty" });
        swFirstFuzzy.Stop();

        var rnd = new Random(42);
        var words = SampleWords(dbPath, sample);
        var categories = new Dictionary<string, List<string>>
        {
            ["acceptance"] = AcceptanceCases.All.Select(c => c.Query).ToList(),
            ["exact"] = words,
            ["typo"] = words.Where(w => w.Length >= 5).Select(w => Typo(w, rnd)).ToList(),
            ["prefix"] = words.Where(w => w.Length >= 6).Select(w => w[..Math.Max(3, w.Length / 2)]).ToList(),
        };

        var info = db.Info;
        Line("# Lexi search benchmark");
        Line();
        Line($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
        Line($"- Machine: {CpuName()}, {Environment.ProcessorCount} logical cores, {TotalRamGb():F0} GB RAM");
        Line($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}); runtime {RuntimeInformation.FrameworkDescription}");
        Line($"- Database: {new FileInfo(dbPath).Length / 1048576.0:F0} MB, {info.WordCount:N0} headwords, {info.EntryCount:N0} entries, {info.FormCount:N0} forms (built {info.BuiltAt})");
        Line($"- Fuzzy index: {fuzzy?.KeyCount:N0} keys, {fuzzy?.StateCount:N0} states, ~{(fuzzy?.SizeInBytes ?? 0) / 1048576.0:F1} MB");
        Line($"- Method: `lexi bench --rounds {rounds} --sample {sample}`; each query runs {rounds}× after warm-up, single thread, Release build, wall-clock via Stopwatch.");
        Line();
        Line("## Cold start");
        Line();
        Line("| Step | ms |");
        Line("|---|---:|");
        Line($"| Open database (read-only) | {swOpen.Elapsed.TotalMilliseconds:F1} |");
        Line($"| First query (no fuzzy) | {swFirst.Elapsed.TotalMilliseconds:F1} |");
        Line($"| Load fuzzy index | {swFuzzy.Elapsed.TotalMilliseconds:F1} |");
        Line($"| First fuzzy query | {swFirstFuzzy.Elapsed.TotalMilliseconds:F1} |");
        Line();
        Line("## Warm query latency (ms)");
        Line();
        Line("| Category | Queries | p50 | p95 | p99 | max |");
        Line("|---|---:|---:|---:|---:|---:|");

        var all = new List<double>();
        foreach (var (name, queries) in categories)
        {
            foreach (var q in queries)
            {
                engine.Search(new SearchRequest { Text = q });
            }

            var times = new List<double>();
            for (var r = 0; r < rounds; r++)
            {
                foreach (var q in queries)
                {
                    var sw = Stopwatch.StartNew();
                    engine.Search(new SearchRequest { Text = q });
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
            }

            all.AddRange(times);
            Line($"| {name} | {queries.Count} | {P(times, 50):F2} | {P(times, 95):F2} | {P(times, 99):F2} | {times.Max():F2} |");
        }

        Line($"| **all** | | {P(all, 50):F2} | {P(all, 95):F2} | {P(all, 99):F2} | {all.Max():F2} |");

        // Details pane: aggregate entries + render Markdown for top hits.
        var detailTimes = new List<double>();
        foreach (var q in categories["exact"].Take(100))
        {
            var hit = engine.Search(new SearchRequest { Text = q, Limit = 1 }).Hits.FirstOrDefault();
            if (hit is null)
            {
                continue;
            }

            var sw = Stopwatch.StartNew();
            var view = WordViewBuilder.Build(db, hit.Word);
            MarkdownRenderer.Full(view);
            detailTimes.Add(sw.Elapsed.TotalMilliseconds);
        }

        Line();
        Line($"Details (aggregate + Markdown, {detailTimes.Count} words): p50 {P(detailTimes, 50):F2} ms, p95 {P(detailTimes, 95):F2} ms, max {detailTimes.DefaultIfEmpty(0).Max():F2} ms");
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        Line($"Process memory after benchmark: working set {proc.WorkingSet64 / 1048576.0:F0} MB, private {proc.PrivateMemorySize64 / 1048576.0:F0} MB, GC heap {GC.GetTotalMemory(false) / 1048576.0:F0} MB");

        if (outFile is not null)
        {
            File.WriteAllText(outFile, report.ToString());
            Console.WriteLine($"written {outFile}");
        }

        return 0;
    }

    private static List<string> SampleWords(string dbPath, int n)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        // Deterministic sample of reasonably common single words in both languages.
        cmd.CommandText = "SELECT word FROM words WHERE zipf >= 2.5 AND instr(word, ' ') = 0 AND length(word) >= 3 ORDER BY (id * 2654435761) % 1000003 LIMIT $n";
        cmd.Parameters.AddWithValue("$n", n);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(r.GetString(0));
        }

        return list;
    }

    private static string Typo(string w, Random rnd)
    {
        var i = rnd.Next(1, w.Length - 1);
        return rnd.Next(3) switch
        {
            0 => w.Remove(i, 1),                                         // deletion
            1 => string.Concat(w.AsSpan(0, i), w[i + 1].ToString(), w[i].ToString(), w.AsSpan(i + 2)), // transposition
            _ => w.Insert(i, w[i].ToString()),                          // duplication
        };
    }

    private static double P(List<double> values, int percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        var idx = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    private static string CpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string s)
            {
                return s.Trim();
            }
        }

        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static double TotalRamGb() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;
}
