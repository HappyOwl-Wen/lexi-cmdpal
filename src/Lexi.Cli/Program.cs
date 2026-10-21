using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lexi.AI;
using Lexi.Cli;
using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Dictionary.Import;
using Lexi.Dictionary.Quality;
using Lexi.Dictionary.Storage;
using Lexi.Dictionary.View;

Console.OutputEncoding = Encoding.UTF8;
// Logs and generated reports (docs/BENCHMARKS.md) use one number format regardless of the Windows region.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var cli = new Args(args);
try
{
    return cli.Command switch
    {
        "download" => await Commands.DownloadAsync(cli),
        "import" => Commands.Import(cli),
        "query" or "q" => Commands.Query(cli),
        "show" => Commands.Show(cli),
        "quality" => Commands.Quality(cli),
        "bench" => Commands.Bench(cli),
        "stats" => Commands.Stats(cli),
        "ai" => await Commands.AiAsync(cli),
        _ => Commands.Help(),
    };
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException or InvalidOperationException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

namespace Lexi.Cli
{
    internal sealed class Args
    {
        private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

        public Args(string[] args)
        {
            var positional = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    var key = args[i][2..];
                    string? value = null;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        value = args[++i];
                    }

                    _options[key] = value;
                }
                else
                {
                    positional.Add(args[i]);
                }
            }

            Command = positional.Count > 0 ? positional[0].ToLowerInvariant() : "help";
            Rest = positional.Skip(1).ToList();
        }

        public string Command { get; }

        public List<string> Rest { get; }

        public bool Has(string key) => _options.ContainsKey(key);

        public string? Get(string key) => _options.GetValueOrDefault(key);

        public int? Int(string key) => _options.TryGetValue(key, out var v) && int.TryParse(v, CultureInfo.InvariantCulture, out var n) ? n : null;

        public Lang? Lang => Get("lang") switch
        {
            "en" => Core.Models.Lang.En,
            "de" => Core.Models.Lang.De,
            _ => null,
        };

        public string Db => Get("db") ?? DataPaths.FindDatabase() ?? DataPaths.DefaultDatabasePath;
    }

    internal static class Commands
    {
        public static int Help()
        {
            Console.WriteLine("""
                lexi — Lexi dictionary tool

                  download [--raw DIR] [--force] [--no-ecdict]   download raw dictionary data
                  import   [--raw DIR] [--out PATH] [--limit N] [--no-ecdict]
                                                                 build lexi.db from raw data
                  query    <text> [--lang en|de] [--limit N]     ranked search results with timing
                  show     <text> [--lang en|de] [--full]        aggregated entry as Markdown
                  quality                                        acceptance list (pass/fail)
                  bench    [--rounds N] [--sample N] [--out FILE] latency benchmark
                  stats                                          database statistics
                  ai       <text> [--kind explain|grammar|compare|translate|context] [--with A,B]
                           [--domain D] [--model M] [--effort low|medium|high] [--timeout S]
                           [--no-cache] [--prompt]               one Claude Code request (claude -p), streamed;
                                                                 --prompt only prints the prompt (no request)

                  common: --db PATH (default: %LOCALAPPDATA%\Lexi\data\lexi.db)
                """);
            return 0;
        }

        public static async Task<int> DownloadAsync(Args a)
        {
            var root = a.Get("raw") ?? DataPaths.RawDirectory;
            if (RefuseRedirectedWrites(root))
            {
                return 3;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Lexi-dictionary-importer/0.1 (+local build)");
            foreach (var d in RawFiles.Downloads)
            {
                if (d.SourceCode == "ecdict" && a.Has("no-ecdict"))
                {
                    continue;
                }

                var path = Path.Combine(root, d.RelativePath);
                if (File.Exists(path) && !a.Has("force"))
                {
                    Console.WriteLine($"skip  {d.RelativePath} (exists, {new FileInfo(path).Length / 1048576.0:F1} MB)");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Console.WriteLine($"get   {d.Url}");
                var sw = Stopwatch.StartNew();
                using var response = await http.GetAsync(d.Url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var tmp = path + ".part";
                await using (var src = await response.Content.ReadAsStreamAsync())
                await using (var dst = File.Create(tmp))
                {
                    await src.CopyToAsync(dst);
                }

                File.Move(tmp, path, overwrite: true);
                Console.WriteLine($"      {new FileInfo(path).Length / 1048576.0:F1} MB in {sw.Elapsed.TotalSeconds:F0}s");
            }

            return 0;
        }

        public static int Import(Args a)
        {
            var output = a.Get("out") ?? DataPaths.DefaultDatabasePath;
            if (RefuseRedirectedWrites(output))
            {
                return 3;
            }

            var report = LexiImporter.Run(new ImportOptions
            {
                RawDirectory = a.Get("raw") ?? DataPaths.RawDirectory,
                OutputPath = output,
                LimitPerSource = a.Int("limit"),
                IncludeEcdict = !a.Has("no-ecdict"),
                Log = msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}"),
            });
            Console.WriteLine($"words {report.Words:N0}, entries {report.Entries:N0}, forms {report.Forms:N0}, fuzzy keys {report.FuzzyKeys:N0} ({report.FuzzyBytes / 1048576.0:F1} MB), file {report.FileBytes / 1048576.0:F1} MB");
            return 0;
        }

        public static int Query(Args a)
        {
            using var db = LexiDatabase.Open(a.Db);
            var engine = new SearchEngine(db);
            var text = string.Join(' ', a.Rest);
            engine.Search(new SearchRequest { Text = "warmup" });
            var result = engine.Search(new SearchRequest { Text = text, Language = a.Lang, Limit = a.Int("limit") ?? 15 });
            Console.WriteLine($"query \"{text}\" → fold \"{result.Query.Fold}\" — {result.Hits.Count} hits in {result.Elapsed.TotalMilliseconds:F2} ms");
            var i = 1;
            foreach (var h in result.Hits)
            {
                Console.WriteLine($"{i++,2}. [{h.Word.Lang.Tag()}] {h.Word.Word,-24} {h.Kind,-12} d={h.Distance} score={h.Score,6:F1} zipf={h.Word.Zipf:F2} {(h.Matched != h.Word.Word ? "via " + h.Matched : string.Empty)}  {h.Word.Badge} {Trim(h.Word.Brief, 50)}");
            }

            return 0;
        }

        public static int Show(Args a)
        {
            using var db = LexiDatabase.Open(a.Db);
            var engine = new SearchEngine(db);
            var result = engine.Search(new SearchRequest { Text = string.Join(' ', a.Rest), Language = a.Lang, Limit = 1 });
            if (result.Hits.Count == 0)
            {
                Console.WriteLine("no result");
                return 1;
            }

            var sw = Stopwatch.StartNew();
            var view = WordViewBuilder.Build(db, result.Hits[0].Word);
            var md = a.Has("full") ? MarkdownRenderer.Full(view) : MarkdownRenderer.Details(view, result.Hits[0]);
            sw.Stop();
            Console.WriteLine(md);
            Console.WriteLine($"<!-- search {result.Elapsed.TotalMilliseconds:F2} ms, view+render {sw.Elapsed.TotalMilliseconds:F2} ms -->");
            return 0;
        }

        public static int Quality(Args a)
        {
            using var db = LexiDatabase.Open(a.Db);
            var engine = new SearchEngine(db);
            var failures = 0;
            foreach (var c in AcceptanceCases.All)
            {
                var r = engine.Search(new SearchRequest { Text = c.Query, Limit = 10 });
                var rank = r.Hits.ToList().FindIndex(h => h.Word.Word == c.Expected && h.Word.Lang == c.Lang) + 1;
                var ok = rank > 0 && rank <= c.WithinTop;
                if (!ok)
                {
                    failures++;
                }

                var top = string.Join(", ", r.Hits.Take(3).Select(h => $"{h.Word.Word}[{h.Word.Lang.Tag()},{h.Kind}]"));
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {c.Query,-16} → {c.Expected,-15} rank {(rank == 0 ? "-" : rank.ToString(CultureInfo.InvariantCulture)),-2} (≤{c.WithinTop})  {r.Elapsed.TotalMilliseconds,6:F2} ms  top: {top}");
            }

            Console.WriteLine(failures == 0 ? $"All {AcceptanceCases.All.Count} cases passed." : $"{failures} of {AcceptanceCases.All.Count} cases FAILED.");
            return failures == 0 ? 0 : 2;
        }

        public static int Stats(Args a)
        {
            using var db = LexiDatabase.Open(a.Db);
            var info = db.Info;
            Console.WriteLine($"database   {db.Path} ({new FileInfo(db.Path).Length / 1048576.0:F1} MB)");
            Console.WriteLine($"built      {info.BuiltAt}, schema v{info.SchemaVersion}, profile {info.Profile}");
            Console.WriteLine($"headwords  {info.WordCount:N0}");
            Console.WriteLine($"entries    {info.EntryCount:N0}");
            Console.WriteLine($"forms      {info.FormCount:N0}");
            var sw = Stopwatch.StartNew();
            var fuzzy = db.GetFuzzyIndex();
            Console.WriteLine($"fuzzy      {fuzzy?.KeyCount:N0} keys, {fuzzy?.StateCount:N0} states, {fuzzy?.EdgeCount:N0} edges, ~{(fuzzy?.SizeInBytes ?? 0) / 1048576.0:F1} MB in memory, loaded in {sw.ElapsedMilliseconds} ms");
            foreach (var s in db.Sources)
            {
                Console.WriteLine($"source     #{s.Id} {s.Code,-8} {s.Name} — {s.License} [{s.Version}]{(s.Redistributable ? string.Empty : "  (not redistributable)")}");
            }

            return 0;
        }

        public static int Bench(Args a) => Benchmark.Run(a.Db, a.Int("rounds") ?? 5, a.Int("sample") ?? 300, a.Get("out"));

        /// <summary>The same request the extension sends (dictionary facts as context), through the same AiService.</summary>
        public static async Task<int> AiAsync(Args a)
        {
            var text = string.Join(' ', a.Rest);
            if (text.Length == 0)
            {
                Console.Error.WriteLine("usage: lexi ai <text> [--kind explain|grammar|compare|translate|context] …");
                return 1;
            }

            var kind = a.Get("kind") switch
            {
                null or "explain" => AiTaskKind.ExplainWord,
                "grammar" => AiTaskKind.ExplainGrammar,
                "compare" => AiTaskKind.CompareWords,
                "translate" => AiTaskKind.TranslateSentence,
                "context" => AiTaskKind.ExplainInContext,
                var k => throw new InvalidOperationException($"unknown --kind {k}"),
            };

            string? language = null;
            string? context = null;
            var compareWith = a.Get("with")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (kind != AiTaskKind.TranslateSentence && (a.Has("db") || DataPaths.FindDatabase() is not null))
            {
                using var db = LexiDatabase.Open(a.Db);
                var hit = new SearchEngine(db).Search(new SearchRequest { Text = text, Language = a.Lang, Limit = 1, Fuzzy = false }).Hits.FirstOrDefault();
                if (hit is not null)
                {
                    var view = WordViewBuilder.Build(db, hit.Word);
                    text = hit.Word.Word;
                    language = AiContext.LanguageName(hit.Word.Lang);
                    context = AiContext.Describe(view);
                    if (kind == AiTaskKind.CompareWords && compareWith is null)
                    {
                        compareWith = [.. AiContext.CompareCandidates(view)];
                    }
                }
            }

            var request = new AiRequest(kind, text, language, context, kind == AiTaskKind.CompareWords ? compareWith : null, a.Get("domain"));
            if (a.Has("prompt"))
            {
                Console.WriteLine(PromptTemplates.Build(request));
                return 0;
            }

            var provider = new ClaudeCodeProvider(new ClaudeCodeOptions
            {
                Model = a.Get("model"),
                Effort = a.Get("effort"),
                Timeout = TimeSpan.FromSeconds(a.Int("timeout") ?? 90),
            });
            var service = new AiService(() => provider);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var printed = 0;
            var progress = new InlineProgress(partial =>
            {
                if (partial.Length > printed)
                {
                    Console.Write(partial[printed..]);
                    printed = partial.Length;
                }
            });

            Console.Error.WriteLine($"[{provider.Name} · {Locate()} · {kind}]");
            try
            {
                var result = await service.RunAsync(request, a.Has("no-cache"), progress, cts.Token);
                if (printed == 0)
                {
                    Console.Write(result.Text);
                }
                else if (result.Text.Length > printed)
                {
                    Console.Write(result.Text[printed..]);
                }

                Console.WriteLine();
                Console.Error.WriteLine(result.FromCache
                    ? $"[cache, created {result.CreatedAt:yyyy-MM-dd HH:mm}]"
                    : $"[{result.Model ?? "default model"}, {result.Elapsed.TotalSeconds:F1} s]");
                return 0;
            }
            catch (AiException ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("cancelled");
                return 1;
            }

            static string Locate() => ClaudeCodeProvider.Locate(null) ?? "claude not found";
        }

        private static string Trim(string? s, int n) => s is null ? string.Empty : s.Length <= n ? s : s[..n] + "…";

        /// <summary>
        /// Refuses to write under %LOCALAPPDATA% when Windows redirects this process's AppData writes into a
        /// packaged app's private folder: the files would land where Command Palette cannot see them.
        /// </summary>
        private static bool RefuseRedirectedWrites(string path)
        {
            var local = DataPaths.LocalAppData + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(local, StringComparison.OrdinalIgnoreCase) || DataPaths.DetectAppDataRedirection() is not { } package)
            {
                return false;
            }

            Console.Error.WriteLine($"""
                error: this terminal runs inside the packaged app "{package}". Windows redirects its writes to
                %LOCALAPPDATA% into {local}Packages\{package}\LocalCache\Local, where Command Palette cannot see them.
                Run this command from a normal terminal (PowerShell from the Start menu, or Windows Terminal),
                or pass --out / --raw with a folder outside %LOCALAPPDATA%.
                """);
            return true;
        }

        /// <summary>Synchronous progress: partial text arrives in order on the reader thread.</summary>
        private sealed class InlineProgress(Action<string> report) : IProgress<string>
        {
            public void Report(string value) => report(value);
        }
    }
}
