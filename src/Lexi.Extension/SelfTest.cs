using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Lexi.AI;
using Lexi.Core.Models;
using Lexi.Dictionary.Quality;
using Lexi.Extension.Pages;
using Lexi.Extension.Services;
using Microsoft.CommandPalette.Extensions;
using Windows.Media.Core;
using Windows.Media.SpeechSynthesis;
using Windows.System;

namespace Lexi.Extension;

/// <summary>
/// <c>Lexi.Extension.exe --self-test [report-file] [--ai] [--network] [--translation]</c>
/// Drives the real provider, list page, details, entry page, fallback and settings form in-process — the calls
/// Command Palette makes — against the installed dictionary and writes a PASS/FAIL report. Checks a release
/// (trimmed) build without a host and helps troubleshooting. Uses a temporary settings folder, never touches the
/// clipboard and plays no sound. --ai runs a Claude request; --network downloads a recording; --translation tests Google.
/// </summary>
internal static partial class SelfTest
{
    private const string WarnPrefix = "WARN ";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);
    private static readonly string[] EnglishWordCommands = ["复制释义", "发音", "复制单词", "发音（UK）", "发音（US）"];

    public static int Run(string[] args)
    {
        var withAi = args.Contains("--ai");
        var withNetwork = args.Contains("--network");
        var withTranslation = args.Contains("--translation");
        var reportPath = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? Path.Combine(Path.GetTempPath(), "lexi-selftest.txt"));
        var dataDirectory = Path.Combine(Path.GetTempPath(), "lexi-selftest-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        var report = new Report();
        report.Line($"Lexi self-test  {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        report.Line($"executable      {Environment.ProcessPath}");
        report.Line($"runtime         .NET {Environment.Version}, {RuntimeInformation.ProcessArchitecture}, package identity: {PackageFullName() ?? "none"}");
        report.Line($"settings dir    {dataDirectory} (temporary, deleted afterwards)");
        report.Line(string.Empty);

        try
        {
            RunChecks(report, dataDirectory, withAi, withNetwork, withTranslation);
        }
        finally
        {
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }

        report.Line(string.Empty);
        report.Line($"{report.Passed} passed, {report.Failed} failed, {report.Warnings} warnings");
        var text = report.ToString();
        File.WriteAllText(reportPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.Out.Write(text);
        return report.Failed == 0 ? 0 : 1;
    }

    private static void RunChecks(Report r, string dataDirectory, bool withAi, bool withNetwork, bool withTranslation)
    {
        ServiceHub hub = null!;
        LexiCommandsProvider provider = null!;
        DictionaryPage page = null!;
        IFallbackCommandItem fallback = null!;
        var disposed = false;

        var ready = r.Step("provider: construct", () =>
        {
            hub = new ServiceHub(dataDirectory);
            provider = new LexiCommandsProvider(hub);
            Expect(!hub.Dictionary.IsLoaded, "the database must open lazily, not in the provider constructor");
            return "database not opened yet (lazy)";
        });
        ready = ready && r.Step("provider: top-level command, fallback, icon", () =>
        {
            var commands = provider.TopLevelCommands();
            Expect(commands.Length == 1 && commands[0].Title == "Lexi", "expected one top-level command \"Lexi\"");
            page = commands[0].Command as DictionaryPage ?? throw new InvalidOperationException("top-level command is not the dictionary page");
            var fallbacks = provider.FallbackCommands();
            Expect(fallbacks.Length == 1 && fallbacks[0].FallbackHandler is not null, "expected one fallback handler");
            fallback = fallbacks[0];
            var icon = page.Icon?.Light?.Icon;
            Expect(icon is not null && File.Exists(icon), $"icon file missing: {icon}");
            return $"\"{commands[0].Title}\" → {page.Title} ({page.Id}); fallback \"{fallback.DisplayTitle}\"";
        });
        if (!ready)
        {
            return;
        }

        var driver = new PageDriver(page);
        try
        {
            r.Step("settings: form renders and saves", () =>
            {
                var form = provider.Settings!.SettingsPage.GetContent().OfType<IFormContent>().Single();
                using (JsonDocument.Parse(form.TemplateJson))
                {
                }

                Expect(form.TemplateJson.Contains("ai.enabled", StringComparison.Ordinal), "form does not contain ai.enabled");
                form.SubmitForm("""{"ai.timeoutSeconds":"45"}""", "{}");
                Expect(hub.Settings.AiTimeout == TimeSpan.FromSeconds(45), "submitted value was not applied");
                Expect(File.Exists(Path.Combine(dataDirectory, "settings.json")), "settings.json was not written");
                return $"{form.TemplateJson.Length:N0} chars of Adaptive Card JSON; settings.json saved";
            });

            RunSentenceTranslationChecks(r, hub, provider, dataDirectory);
            if (withTranslation)
            {
                RunSentenceTranslationNetworkCheck(r, hub, provider);
            }

            r.Step("page: home view", () =>
            {
                var items = page.GetItems();
                Expect(items.Length >= 2, "home view is empty");
                Expect(items.Any(i => i.Command is null && i.Section == "提示"), $"no 提示 heading (a command-less item): {Top(items, 8)}");
                return Top(items, 4);
            });

            r.Step("search: first query (opens the database)", () =>
            {
                var items = driver.Search("vulnerability", out var ms);
                Expect(items.Length > 0 && items[0].Title == "vulnerability", Top(items));
                return $"{ms:F0} ms incl. 40 ms debounce · {hub.Dictionary.Info?.WordCount:N0} headwords · {hub.Dictionary.DatabasePath}";
            });

            foreach (var c in AcceptanceCases.All)
            {
                r.Step($"search: {c.Query} → {c.Expected}", () =>
                {
                    if (HasCjk(c.Query) && hub.Dictionary.Info is { ChineseTerms: 0 })
                    {
                        return $"{WarnPrefix}skipped: this database has no Chinese index (rebuild it with scripts\\import-data.ps1)";
                    }

                    var items = driver.Search(c.Query, out var ms);

                    // Chinese queries list English and German results in separate sections: rank within the language.
                    var words = items.Where(i => !HasTag(i, "AI") && (!HasCjk(c.Query) || HasTag(i, c.Lang.Tag()))).ToList();
                    var rank = words.FindIndex(i => IsHeadword(i, c.Expected, c.Lang)) + 1;
                    Expect(rank > 0 && rank <= c.WithinTop, $"rank {(rank == 0 ? "-" : rank)} (expected ≤ {c.WithinTop}); top: {Top(items)}");
                    return $"rank {rank} (≤ {c.WithinTop}), {ms:F0} ms";
                });
            }

            RunChineseChecks(r, hub, provider, driver, fallback);

            r.Step("list: one part-of-speech label per subtitle", () =>
            {
                var items = driver.Search("authen", out _);
                var repeated = items.Select(i => i.Subtitle).Where(s => s.Split(" · ").Any(HasRepeatedPos)).ToList();
                Expect(repeated.Count == 0, $"repeated part of speech: {string.Join(" | ", repeated.Take(3))}");
                return string.Join(" | ", items.Take(3).Select(i => i.Subtitle));
            });

            r.Step("details: vulnerability (IPA, Chinese, commands)", () =>
            {
                var item = driver.Search("vulnerability", out _)[0];
                var body = item.Details?.Body ?? string.Empty;
                Expect(HasCjk(body), "no Chinese definition in the details pane");
                Expect(body.Contains('/', StringComparison.Ordinal), "no IPA in the details pane");
                var commands = item.MoreCommands.OfType<ICommandContextItem>().ToList();
                var names = commands.Select(c => c.Command.Name).ToList();
                foreach (var expected in EnglishWordCommands)
                {
                    Expect(names.Contains(expected), $"missing context command \"{expected}\"");
                }

                var pronounce = commands.First(c => c.Command.Name == "发音").RequestedShortcut;
                Expect(pronounce.Vkey == (int)VirtualKey.P && pronounce.Modifiers == VirtualKeyModifiers.Control, "Ctrl+P is not bound to 发音");
                return $"{body.Length:N0} chars; {names.Count} commands: {string.Join(", ", names)}";
            });

            r.Step("details: Häuser → das Haus (form note)", () =>
            {
                var items = driver.Search("Häuser", out _);
                Expect(items[0].Title == "das Haus", $"top: {Top(items)}");
                Expect(items[0].Subtitle.Contains("Häuser", StringComparison.Ordinal), "subtitle does not explain the form");
                Expect((items[0].Details?.Body ?? string.Empty).Contains("Häuser", StringComparison.Ordinal), "details do not show the plural");
                return items[0].Subtitle;
            });

            r.Step("details: ging → gehen (conjugation)", () =>
            {
                var items = driver.Search("ging", out _);
                Expect(items[0].Title == "gehen", $"top: {Top(items)}");
                var body = items[0].Details?.Body ?? string.Empty;
                Expect(body.Contains("ging", StringComparison.Ordinal) && body.Contains("gegangen", StringComparison.Ordinal), "Präteritum / Partizip II missing from details");
                return items[0].Subtitle;
            });

            r.Step("details: Schwachstellen → die Schwachstelle", () =>
            {
                var items = driver.Search("Schwachstellen", out _);
                Expect(items[0].Title == "die Schwachstelle", $"top: {Top(items)}");
                Expect((items[0].Details?.Body ?? string.Empty).Length > 50, "details are empty");
                return items[0].Subtitle;
            });

            r.Step("entry page: full entry, history", () =>
            {
                var item = driver.Search("vulnerability", out _)[0];
                var entry = item.Command as IContentPage ?? throw new InvalidOperationException("result command is not a content page");
                var markdown = entry.GetContent().OfType<IMarkdownContent>().FirstOrDefault()?.Body ?? string.Empty;
                Expect(markdown.Contains("来源与许可", StringComparison.Ordinal), "sources/licence section missing");
                Expect(hub.History.Items is [{ Word: "vulnerability" }, ..], "history was not updated");
                return $"{markdown.Length:N0} chars of Markdown, {entry.Commands.Length} commands";
            });

            r.Step("page: home view lists history", () =>
            {
                var items = driver.Search(string.Empty, out _);
                Expect(items.Any(i => i.Section == "最近查询" && i.Title == "vulnerability"), $"history missing: {Top(items)}");
                return Top(items, 3);
            });

            r.Step("typing burst: latest query wins", () =>
            {
                var (publishes, ms) = driver.TypeQuickly("authentication");
                var final = page.GetItems();
                Expect(final.Length > 0 && final[0].Title == "authentication", $"top: {Top(final)}");
                Thread.Sleep(300);
                Expect(ReferenceEquals(final, page.GetItems()), "a stale query overwrote the latest results");
                return $"14 keystrokes → {publishes} publish(es); final results after {ms:F0} ms";
            });

            r.Step("filter: Deutsch only", () =>
            {
                var all = driver.Search("Hand", out _);
                Expect(all.Any(i => HasTag(i, "EN")) && all.Any(i => HasTag(i, "DE")), $"expected English and German results: {Top(all)}");
                var german = driver.Change(() => page.Filters!.CurrentFilterId = "de", out _);
                Expect(german.Length > 0 && german.All(i => HasTag(i, "DE")), $"non-German result with the Deutsch filter: {Top(german)}");
                driver.Change(() => page.Filters!.CurrentFilterId = "all", out _);
                return $"all languages: {all.Length} results; Deutsch: {german.Length}";
            });

            r.Step("entries: AI compare/context, Claude and online sentence translation", () =>
            {
                Expect(hub.AiEnabled, "AI is disabled in settings");
                var compare = driver.Search("affect vs effect", out _);
                Expect(HasTag(compare[0], "AI") && compare.Any(i => i.Title == "affect"), $"top: {Top(compare)}");
                var context = driver.Search("Schwachstelle @IT Security", out _);
                Expect(HasTag(context[0], "AI") && context.Any(i => i.Title == "die Schwachstelle"), $"top: {Top(context)}");
                var sentence = driver.Search("the weather is really nice today", out _);
                Expect(!sentence.Any(i => i.Command is TranslationResultPage), "online translation must be opt-in");
                Expect(sentence is [{ Command: AiPage }, ..] && HasTag(sentence[0], "AI"), $"Claude translation missing: {Top(sentence)}");
                try
                {
                    SubmitSentenceSettings(provider, """{"ai.sentenceTranslation":"true","ai.enabled":"false"}""");
                    sentence = driver.Search("the weather is really nice today", out _);
                    Expect(sentence is [{ Command: TranslationResultPage }, ..] && !sentence.Any(i => i.Command is AiPage), $"online only: {Top(sentence)}");
                    SubmitSentenceSettings(provider, """{"ai.enabled":"true"}""");
                    sentence = driver.Search("the weather is really nice today", out _);
                    Expect(sentence is [{ Command: TranslationResultPage }, { Command: AiPage }, ..], $"Google then Claude expected: {Top(sentence)}");
                    var singleWord = driver.Search("vulnerability", out _);
                    Expect(!singleWord.Any(i => i.Command is TranslationResultPage or AiPage), "a single word got a sentence translation");
                    return $"{compare[0].Title} | {context[0].Title} | {sentence[0].Title} + {sentence[1].Title}; single word stays in the dictionary";
                }
                finally
                {
                    SubmitSentenceSettings(provider, """{"ai.sentenceTranslation":"false","ai.enabled":"true"}""");
                }
            });

            r.Step("no result: empty state", () =>
            {
                var items = driver.Search("qxzvqxzv", out _);
                Expect(items.Length == 0, $"unexpected results: {Top(items)}");
                var title = page.EmptyContent?.Title ?? string.Empty;
                Expect(title.Contains("没有找到", StringComparison.Ordinal), $"empty content: \"{title}\"");
                return title;
            });

            r.Step("fallback: main-page queries", () =>
            {
                var handler = fallback.FallbackHandler;
                var t1 = Time(() => handler.UpdateQuery("dict vulnerability"));
                Expect(fallback.Title.Contains("vulnerability", StringComparison.Ordinal), $"\"{fallback.Title}\"");
                var opened = fallback.Command as IListPage ?? throw new InvalidOperationException("fallback command is not a list page");
                Expect(opened.SearchText == "vulnerability", $"pre-filled query is \"{opened.SearchText}\"");
                var openedItems = opened.GetItems();
                Expect(openedItems.Length > 0 && openedItems[0].Title == "vulnerability", $"pre-filled page shows: {Top(openedItems)}");
                var first = fallback.Title;
                var t2 = Time(() => handler.UpdateQuery("Häuser"));
                Expect(fallback.Title.Contains("das Haus", StringComparison.Ordinal), $"\"{fallback.Title}\"");
                var second = fallback.Title;
                var t3 = Time(() => handler.UpdateQuery("qxzvqxzv"));
                Expect(fallback.Title.Length == 0, $"nonsense query produced \"{fallback.Title}\"");
                handler.UpdateQuery(string.Empty);
                Expect(fallback.Title.Length == 0, "empty query left a title");
                return $"\"{first}\" {t1:F1} ms · \"{second}\" {t2:F1} ms · nonsense {t3:F1} ms";
            });

            r.Step("pronunciation: Windows TTS", () =>
            {
                var voices = SpeechSynthesizer.AllVoices;
                var english = voices.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                var german = voices.FirstOrDefault(v => v.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase));
                using var synth = new SpeechSynthesizer();
                var produced = new List<string>();
                foreach (var (voice, text) in new[] { (english, "vulnerability"), (german, "Schwachstelle") })
                {
                    if (voice is null)
                    {
                        continue;
                    }

                    synth.Voice = voice;
                    using var stream = synth.SynthesizeTextToStreamAsync(text).AsTask().GetAwaiter().GetResult();
                    Expect(stream.Size > 1000, $"no audio from {voice.DisplayName}");
                    produced.Add($"{voice.Language} {stream.Size / 1024} KB");
                }

                var detail = $"voices: {string.Join(", ", voices.Select(v => v.Language).Distinct())}; synthesized (not played): {string.Join(", ", produced)}";
                return english is null || german is null
                    ? $"{WarnPrefix}no {(german is null ? "German" : "English")} voice installed — that language falls back to recordings only · {detail}"
                    : detail;
            });

            if (withNetwork)
            {
                r.Step("pronunciation: Commons recording", () =>
                {
                    var word = hub.Dictionary.Lookup("Haus").Hits.First(h => h.Word.Lang == Lang.De).Word;
                    var view = hub.Dictionary.GetView(word) ?? throw new InvalidOperationException("no view for Haus");
                    var audio = view.Audio.Count > 0 ? view.Audio[0] : throw new InvalidOperationException("no recording referenced for Haus");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var path = hub.Pronouncer.EnsureCachedAsync(audio, cts.Token).GetAwaiter().GetResult();
                    Expect(path is not null && new FileInfo(path).Length > 1000, $"download failed ({hub.Pronouncer.LastDownloadError}): {Pronouncer.RecordingUrl(audio)}");
                    MediaSource.CreateFromUri(new Uri(path!)).Dispose();
                    return $"{audio.File} → {new FileInfo(path!).Length / 1024} KB (not played)";
                });
            }

            if (withAi)
            {
                r.Step("AI: Claude Code via the AI page", () =>
                {
                    var word = hub.Dictionary.Lookup("vulnerability").Hits[0].Word;
                    var aiPage = (AiPage)WordItems.AiCommand(hub, word, AiTaskKind.ExplainWord);
                    using var done = new ManualResetEventSlim();
                    aiPage.PropChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(aiPage.IsLoading) && !aiPage.IsLoading)
                        {
                            done.Set();
                        }
                    };
                    var sw = Stopwatch.StartNew();
                    var content = aiPage.GetContent().OfType<IMarkdownContent>().First();
                    Expect(done.Wait(hub.Settings.AiTimeout + TimeSpan.FromSeconds(15)), "no answer before the timeout");
                    var body = content.Body;
                    Expect(body.Contains("AI 生成内容", StringComparison.Ordinal) && !body.Contains("⚠️", StringComparison.Ordinal), Snippet(body, 300));
                    var primary = aiPage.Commands.OfType<ICommandContextItem>().FirstOrDefault()?.Command.Name;
                    Expect(primary == "复制 AI 回答", $"primary command after the answer is \"{primary}\"");
                    var answer = body[(body.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
                    return $"{sw.Elapsed.TotalSeconds:F1} s, Enter = {primary}: {Snippet(answer, 80)}";
                });
            }

            using (var process = Process.GetCurrentProcess())
            {
                r.Line($"memory          working set {process.WorkingSet64 / 1048576} MB, private {process.PrivateMemorySize64 / 1048576} MB, GC heap {GC.GetTotalMemory(false) / 1048576} MB");
            }

            r.Step("provider: dispose releases the database", () =>
            {
                provider.Dispose();
                disposed = true;
                Expect(!hub.Dictionary.IsLoaded, "database still open after Dispose");
                return "ok";
            });
        }
        finally
        {
            if (!disposed)
            {
                provider.Dispose();
            }
        }
    }

    private static void Expect([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static double Time(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static bool HasTag(IListItem item, string text) => item.Tags?.Any(t => t.Text == text) == true;

    private static bool IsHeadword(IListItem item, string word, Lang lang) =>
        (item.Title == word || item.Title.EndsWith(" " + word, StringComparison.Ordinal)) && HasTag(item, lang.Tag());

    private static bool HasCjk(string text) => text.Any(c => c is >= '一' and <= '鿿');

    /// <summary>"adj. a. 可靠的": two part-of-speech abbreviations in a row.</summary>
    private static bool HasRepeatedPos(string text) =>
        WordItems.StartsWithPosAbbreviation(text) && WordItems.StartsWithPosAbbreviation(text[(text.IndexOf(". ", StringComparison.Ordinal) + 2)..]);

    private static string Top(IListItem[] items, int count = 5) =>
        items.Length == 0 ? "(no items)" : string.Join(" | ", items.Take(count).Select(i => i.Title));

    private static string Snippet(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static string? PackageFullName()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.Id.FullName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            return null;
        }
    }

    /// <summary>Sets the search text like the host does and waits for the page to publish (ItemsChanged).</summary>
    private sealed class PageDriver(DictionaryPage page)
    {
        private string _current = string.Empty;

        public IListItem[] Search(string text, out double ms)
        {
            if (text == _current)
            {
                // Same text would not raise UpdateSearchText; go through a different text first.
                Change(() => page.SearchText = text + " ", out _);
            }

            var items = Change(() => page.SearchText = text, out ms);
            _current = text;
            return items;
        }

        public IListItem[] Change(Action change, out double ms)
        {
            using var changed = new ManualResetEventSlim();
            void OnChanged(object sender, IItemsChangedEventArgs args) => changed.Set();
            page.ItemsChanged += OnChanged;
            try
            {
                var sw = Stopwatch.StartNew();
                change();
                Expect(changed.Wait(WaitLimit), "the page did not publish results within 15 s");
                ms = sw.Elapsed.TotalMilliseconds;
                return page.GetItems();
            }
            finally
            {
                page.ItemsChanged -= OnChanged;
            }
        }

        /// <summary>One SearchText update per character without waiting, like fast typing.</summary>
        public (int Publishes, double Ms) TypeQuickly(string word)
        {
            var publishes = 0;
            using var changed = new AutoResetEvent(false);
            void OnChanged(object sender, IItemsChangedEventArgs args)
            {
                Interlocked.Increment(ref publishes);
                changed.Set();
            }

            page.ItemsChanged += OnChanged;
            try
            {
                var sw = Stopwatch.StartNew();
                for (var i = 1; i <= word.Length; i++)
                {
                    page.SearchText = word[..i];
                }

                _current = word;
                while (page.GetItems() is not [{ } first, ..] || first.Title != word)
                {
                    Expect(changed.WaitOne(WaitLimit), "the final query was never published");
                }

                return (Volatile.Read(ref publishes), sw.Elapsed.TotalMilliseconds);
            }
            finally
            {
                page.ItemsChanged -= OnChanged;
            }
        }
    }

    private sealed class Report
    {
        private readonly StringBuilder _text = new();

        public int Passed { get; private set; }

        public int Failed { get; private set; }

        public int Warnings { get; private set; }

        public void Line(string line) => _text.AppendLine(line);

        /// <summary>Runs one check; it returns a detail line (prefixed with WARN for a non-fatal problem) or throws.</summary>
        public bool Step(string name, Func<string> check)
        {
            var sw = Stopwatch.StartNew();
            string status;
            string detail;
            try
            {
                detail = check();
                if (detail.StartsWith(WarnPrefix, StringComparison.Ordinal))
                {
                    status = "WARN";
                    Warnings++;
                    detail = detail[WarnPrefix.Length..];
                }
                else
                {
                    status = "PASS";
                    Passed++;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                status = "FAIL";
                Failed++;
                detail = ex is InvalidOperationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            }

            _text.Append(CultureInfo.InvariantCulture, $"{status}  {name,-46} {sw.Elapsed.TotalMilliseconds,8:F1} ms  {detail}").AppendLine();
            return status != "FAIL";
        }

        public override string ToString() => _text.ToString();
    }
}
