using System.Globalization;
using Lexi.AI;
using Lexi.Core.Search;
using Lexi.Dictionary.Storage;
using Lexi.Extension.Commands;
using Lexi.Extension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Lexi.Extension.Pages;

/// <summary>
/// The dictionary search page. Every keystroke cancels the previous lookup; results are published only if they
/// belong to the latest query (generation check), so a slow old query can never overwrite newer results.
/// </summary>
internal sealed partial class DictionaryPage : DynamicListPage, IDisposable
{
    /// <summary>ID of the top-level command: Command Palette stores the user's hotkey and alias for Lexi under it.</summary>
    internal const string CommandId = "com.lexi.dictionary";

    /// <summary>
    /// ID of the pre-filled pages the main-page fallback opens. Command Palette 0.100 identifies a fallback by the
    /// ID of its current command and resolves a hotkey to the first item with that ID, so with the top-level ID the
    /// fallback (or a stale copy left from an earlier extension process) took over the Lexi hotkey.
    /// </summary>
    internal const string LookupId = "com.lexi.lookup";

    private const int DebounceMs = 40;
    private const int ResultLimit = 20;

    private readonly ServiceHub _hub;
    private readonly LanguageFilters _filters = new();
    private readonly bool _resetOnOpen;
    private readonly Lock _gate = new();
    private IListItem[] _items = [];
    private CancellationTokenSource? _cts;
    private string? _pendingQuery;
    private long _generation;
    private long _lastActivity;

    /// <param name="initialQuery">Pre-filled query (from the main-page fallback); null for the top-level page.</param>
    public DictionaryPage(ServiceHub hub, string? initialQuery = null)
    {
        _hub = hub;
        _resetOnOpen = initialQuery is null;
        Id = _resetOnOpen ? CommandId : LookupId;
        Name = "打开词典";
        Title = "Lexi 词典";
        Icon = IconHelpers.FromRelativePath("Assets\\LexiIcon.png");
        PlaceholderText = "输入英语或德语单词，或输入中文查英语/德语说法…";
        ShowDetails = true;
        Filters = _filters;
        _filters.PropChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Filters.CurrentFilterId))
            {
                StartSearch(base.SearchText);
            }
        };
        EmptyContent = new CommandItem(new NoOpCommand()) { Title = "没有找到结果", Subtitle = "检查拼写，或换用德语/英语筛选", Icon = Glyphs.Info };

        if (!string.IsNullOrWhiteSpace(initialQuery))
        {
            // Searched on the first GetItems(): the fallback creates a page per keystroke and most are never opened.
            SetSearchNoUpdate(initialQuery);
            _pendingQuery = initialQuery;
        }
        else
        {
            _items = BuildHome();
        }
    }

    public override string SearchText
    {
        get
        {
            // The host reads SearchText when it (re)creates the view for this page, i.e. when the page is opened
            // from the main list or by a hotkey. The top-level page then starts fresh instead of showing the
            // previous query, and makes sure the palette that shows it has the keyboard.
            if (_resetOnOpen)
            {
                PaletteFocus.OnPageOpened();
                if (Environment.TickCount64 - Interlocked.Read(ref _lastActivity) > 1000)
                {
                    ResetToHome();
                }
            }

            return base.SearchText;
        }

        set => base.SearchText = value;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
        if (oldSearch != newSearch)
        {
            StartSearch(newSearch);
        }
    }

    public override IListItem[] GetItems()
    {
        if (Interlocked.Exchange(ref _pendingQuery, null) is { } initial)
        {
            // A pre-filled page is shown for the first time: search synchronously so the results are there on open.
            long gen;
            lock (_gate)
            {
                gen = ++_generation;
            }

            IListItem[] items;
            try
            {
                items = BuildResults(initial, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                items = [ErrorItem(ex)];
            }

            lock (_gate)
            {
                if (gen == _generation)
                {
                    _items = items;
                }
            }
        }

        lock (_gate)
        {
            return _items;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ResetToHome()
    {
        _hub.Dictionary.Prewarm();
        if (base.SearchText.Length == 0 && _items.Length > 0)
        {
            return;
        }

        SetSearchNoUpdate(string.Empty);
        long gen;
        lock (_gate)
        {
            _cts?.Cancel();
            gen = ++_generation;
        }

        Publish(gen, BuildHome());
    }

    private void StartSearch(string text)
    {
        Interlocked.Exchange(ref _pendingQuery, null);
        CancellationTokenSource cts;
        long gen;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = cts = new CancellationTokenSource();
            gen = ++_generation;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Publish(gen, BuildHome());
            return;
        }

        if (!_hub.Dictionary.IsLoaded)
        {
            IsLoading = true;
        }

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs, token);
                var items = BuildResults(text, token);
                Publish(gen, items);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer query.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Publish(gen, [ErrorItem(ex)]);
            }
        });
    }

    private void Publish(long gen, IListItem[] items)
    {
        lock (_gate)
        {
            if (gen != _generation)
            {
                return;
            }

            _items = items;
        }

        IsLoading = false;
        RaiseItemsChanged(items.Length);
    }

    private IListItem[] BuildResults(string text, CancellationToken ct)
    {
        var q = QueryInfo.Parse(text);
        if (q.HasCjk)
        {
            return BuildChineseResults(q, ct);
        }

        var items = new List<IListItem>();
        var ai = _hub.AiEnabled;
        var lookup = text;

        if (ai && SpecialQuery.TryParseCompare(q.Clean, out var words))
        {
            items.Add(AiItem(AiTaskKind.CompareWords, words[0], words.Skip(1).ToList(), null, $"AI 辨析：{string.Join(" · ", words)}", "比较这些易混词" + DomainHint()));
            lookup = words[0];
        }
        else if (ai && SpecialQuery.TryParseContext(q.Clean, out var term, out var domain))
        {
            items.Add(AiItem(AiTaskKind.ExplainInContext, term, null, domain, $"AI 专业语境：{term} @ {domain}", "解释该术语在此领域中的含义"));
            lookup = term;
        }
        else if (q.IsSentence)
        {
            // A sentence: online translation when enabled, then Claude Code when AI is on; dictionary results follow.
            if (_hub.SentenceTranslationEnabled && SentenceTranslationPage.CanTranslate(q))
            {
                items.Add(SentenceTranslationPage.TranslationItem(_hub, q.Clean));
            }

            if (ai)
            {
                items.Add(SentenceTranslationPage.AiTranslationItem(_hub, q.Clean));
            }
        }

        if (!_hub.Dictionary.EnsureLoaded())
        {
            items.Add(MissingDatabaseItem());
            return [.. items];
        }

        var result = _hub.Dictionary.Search(lookup, _filters.Selected, ResultLimit, ct);
        ct.ThrowIfCancellationRequested();
        foreach (var hit in result.Hits)
        {
            items.Add(WordItems.Create(_hub, hit, result.Query));
        }

        if (result.Hits.Count == 0 && items.Count == 0)
        {
            EmptyContent = ai
                ? new CommandItem(new AiPage(_hub, "AI 解释", () => new AiRequest(AiTaskKind.ExplainWord, q.Clean)))
                {
                    Title = $"词典中没有找到「{q.Clean}」",
                    Subtitle = "检查拼写；或按 Enter 让 AI 解释（结果标注为 AI 生成）",
                    Icon = Glyphs.Info,
                }
                : new CommandItem(new NoOpCommand()) { Title = $"词典中没有找到「{q.Clean}」", Subtitle = "检查拼写，或切换语言筛选", Icon = Glyphs.Info };
        }

        return [.. items];
    }

    private string DomainHint() => _hub.Settings.Domain is { } d ? $"（侧重 {d}）" : string.Empty;

    private ListItem AiItem(AiTaskKind kind, string input, List<string>? compareWith, string? domain, string title, string subtitle)
    {
        var page = new AiPage(_hub, title, () => new AiRequest(kind, input, null, null, compareWith, domain ?? (kind == AiTaskKind.CompareWords ? _hub.Settings.Domain : null)));
        return new ListItem(page)
        {
            Title = title,
            Subtitle = subtitle,
            Icon = Glyphs.Ai,
            Tags = [new Tag("AI")],
            Details = new Details
            {
                Title = title,
                Body = "按 **Enter** 调用本机 **Claude Code**（`claude -p`）。\n\n- 只在你按下回车时请求，会消耗你的 Claude Code 用量；\n- 回答会明确标注为 **AI 生成**，不会写入词典数据；\n- 支持取消、重新生成，结果会缓存 30 天。",
            },
        };
    }

    private IListItem[] BuildHome()
    {
        var items = new List<IListItem>();
        if (_hub.Dictionary.State is DictionaryState.Missing or DictionaryState.Error)
        {
            items.Add(MissingDatabaseItem());
        }

        foreach (var h in _hub.History.Items.Take(8))
        {
            items.Add(new ListItem(new EntryPage(_hub, h.Word, h.Lang))
            {
                Title = h.Word,
                Subtitle = h.Brief ?? string.Empty,
                Icon = Glyphs.History,
                Section = "最近查询",
                Tags = [new Tag(h.Lang == Core.Models.Lang.De ? "DE" : "EN")],
            });
        }

        items.Add(new ListItem(new NoOpCommand())
        {
            Title = "输入英语或德语单词开始查询，也可以输入中文",
            Subtitle = "拼写纠错（vulnerabilty）· 词形还原（Häuser→Haus、ging→gehen）· ae/oe/ue→ä/ö/ü · 中文反查（房子→house / das Haus）",
            Icon = Glyphs.Lightbulb,
            Section = "提示",
        });

        if (_hub.AiEnabled)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = "AI（Claude Code，按需调用）",
                Subtitle = "选中词条后 Ctrl+I 解释 · Ctrl+G 德语语法 · 输入「A vs B」辨析 · 「词 @领域」· 输入句子或中文可让 AI 翻译",
                Icon = Glyphs.Ai,
                Section = "提示",
            });
        }

        if (_hub.SentenceTranslationEnabled)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = "在线翻译（Google）",
                Subtitle = "英语/德语句子 → 中文 · 中文 → 英语和德语 · 短句可用独立「Lexi 句子翻译」命令 · 按 Enter 才联网",
                Icon = Glyphs.Translate,
                Section = "提示",
            });
        }

        var info = _hub.Dictionary.Info;
        items.Add(new ListItem(new NoOpCommand())
        {
            Title = info is null ? "词典：首次查询时加载" : $"词典：{info.WordCount.ToString("N0", CultureInfo.InvariantCulture)} 个词头 · {info.EntryCount.ToString("N0", CultureInfo.InvariantCulture)} 条释义",
            Subtitle = info is null ? DataPaths.DefaultDatabasePath : $"构建于 {info.BuiltAt} · {_hub.Dictionary.DatabasePath}",
            Icon = Glyphs.Info,
            Section = "提示",
        });
        return WithSectionHeaders(items);
    }

    private ListItem MissingDatabaseItem()
    {
        if (_hub.Dictionary.RedirectedRoot is { } redirected)
        {
            var install = "powershell -ExecutionPolicy Bypass -File scripts\\install.ps1 -SkipBuild";
            return new ListItem(new CopyCommand("复制安装命令", "安装命令", () => install))
            {
                Title = "词典数据在其他应用的私有目录中",
                Subtitle = redirected,
                Icon = Glyphs.Warning,
                Details = new Details
                {
                    Title = "把词典数据移到正确位置",
                    Body = $"词典已经构建好，但位于 `{redirected}`。\n\n导入脚本当时是在打包应用（例如 Claude 桌面版）启动的终端里运行的，Windows 把它对 AppData 的写入重定向到了该应用的私有目录，Command Palette 看不到这份数据。\n\n在**普通终端**（开始菜单里的 PowerShell 或 Windows Terminal）中，进入 Lexi 源码目录运行：\n\n`{install}`\n\n脚本会把数据移到 `{DataPaths.Root}`，然后重新打开 Lexi 即可。",
                },
            };
        }

        var command = "powershell -ExecutionPolicy Bypass -File scripts\\import-data.ps1";
        return new ListItem(new CopyCommand("复制导入命令", "导入命令", () => command))
        {
            Title = _hub.Dictionary.State == DictionaryState.Error ? "词典数据库无法打开" : "未找到词典数据库",
            Subtitle = _hub.Dictionary.ErrorMessage ?? DataPaths.DefaultDatabasePath,
            Icon = Glyphs.Warning,
            Details = new Details
            {
                Title = "安装词典数据",
                Body = $"Lexi 的词典数据需要在本机构建一次（不随扩展分发，见 docs/DATA_SOURCES.md）：\n\n1. 在 Lexi 源码目录打开 PowerShell\n2. 运行 `{command}`\n   （下载约 1.2 GB 原始数据并构建约 500 MB 的 SQLite 词典，导入约需 10 分钟）\n3. 重新打开 Lexi\n\n数据库位置：`{DataPaths.DefaultDatabasePath}`\n\n{_hub.Dictionary.ErrorMessage}",
            },
        };
    }

    private static ListItem ErrorItem(Exception ex) => new(new NoOpCommand())
    {
        Title = "查询出错",
        Subtitle = ex.Message,
        Icon = Glyphs.Warning,
    };
}

/// <summary>Recognizes the special query forms "A vs B (vs C)" and "word @domain".</summary>
internal static class SpecialQuery
{
    private static readonly string[] CompareSeparators = [" vs. ", " vs ", " VS ", " / ", "/", " | ", "，", ", "];

    public static bool TryParseCompare(string text, out List<string> words)
    {
        words = [];
        foreach (var sep in CompareSeparators)
        {
            if (!text.Contains(sep, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = text.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is >= 2 and <= 5 && parts.All(p => p.Length <= 40 && p.Count(c => c == ' ') <= 2 && p.Any(char.IsLetter)))
            {
                words = parts.ToList();
                return true;
            }
        }

        return false;
    }

    public static bool TryParseContext(string text, out string term, out string domain)
    {
        var at = text.IndexOf(" @", StringComparison.Ordinal);
        if (at > 0 && at < text.Length - 2)
        {
            term = text[..at].Trim();
            domain = text[(at + 2)..].Trim();
            return term.Length > 0 && domain.Length > 0;
        }

        term = domain = string.Empty;
        return false;
    }
}
