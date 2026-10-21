using System.Globalization;
using System.Text;
using Lexi.AI;
using Lexi.Extension.Commands;
using Lexi.Extension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Lexi.Extension.Pages;

/// <summary>
/// Shows an AI answer (streamed from Claude Code) on its own page. Started only when the user opens the page;
/// clearly labelled as AI-generated and never mixed into dictionary data. Cancel / regenerate / copy are in the command bar.
/// </summary>
internal sealed partial class AiPage : ContentPage
{
    private readonly ServiceHub _hub;
    private readonly Func<AiRequest> _requestFactory;
    private readonly MarkdownContent _content = new();
    private readonly Lock _lock = new();
    private readonly CommandContextItem _cancelItem;
    private readonly CommandContextItem _regenerateItem;
    private readonly CommandContextItem _copyItem;
    private AiRequest? _request;
    private CancellationTokenSource? _cts;
    private bool _started;
    private string? _answer;
    private long _lastPartialTicks;

    /// <param name="requestFactory">Built when the page is opened (it may read the dictionary), not when the list item is created.</param>
    public AiPage(ServiceHub hub, string commandName, Func<AiRequest> requestFactory)
    {
        _hub = hub;
        _requestFactory = requestFactory;
        Name = commandName;
        Title = commandName;
        Icon = Glyphs.Ai;
        _cancelItem = new CommandContextItem(new AnonymousCommand(Cancel) { Name = "取消请求", Icon = Glyphs.Cancel, Result = CommandResult.KeepOpen() });
        _regenerateItem = new CommandContextItem(new AnonymousCommand(() => Start(bypassCache: true)) { Name = "重新生成（忽略缓存）", Icon = Glyphs.Refresh, Result = CommandResult.KeepOpen() });
        _copyItem = new CommandContextItem(new CopyCommand("复制 AI 回答", "AI 回答", () => _answer));
        ShowCommands(loading: true, answered: false);
    }

    public override IContent[] GetContent()
    {
        lock (_lock)
        {
            if (!_started)
            {
                _started = true;
                _request = _requestFactory();
                Title = $"AI · {_request.Input}";
                Start(bypassCache: false);
            }
        }

        return [_content];
    }

    private string Header(string? model, string state)
    {
        var provider = _hub.Ai.CurrentProvider.Name;
        var m = string.IsNullOrEmpty(model) ? string.Empty : $" · 模型 {model}";
        return $"> 🤖 **AI 生成内容**（{provider}{m}）— 不是词典数据，可能有误，仅供参考。{state}\n\n";
    }

    private void Start(bool bypassCache)
    {
        if (_request is not { } request)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = cts = new CancellationTokenSource();
            _answer = null;
        }

        IsLoading = true;
        ShowCommands(loading: true, answered: false);
        var queued = _hub.Ai.IsBusy ? "（排队中：另一个 AI 请求正在进行）" : string.Empty;
        _content.Body = Header(null, string.Empty) + $"⏳ 正在请求 Claude Code…{queued}\n\n*本地词典功能不受影响；按 Enter 可取消。*";

        var progress = new InlineProgress(text =>
        {
            // Throttle UI updates to ~10 per second.
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastPartialTicks) < 100)
            {
                return;
            }

            Interlocked.Exchange(ref _lastPartialTicks, now);
            if (!cts.IsCancellationRequested)
            {
                _content.Body = Header(null, " ⏳ 生成中…") + text + " ▍";
            }
        });

        _ = Task.Run(async () =>
        {
            // A request replaced by "regenerate" must not touch the page any more.
            string body;
            var answered = false;
            try
            {
                var result = await _hub.Ai.RunAsync(request, bypassCache, progress, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }

                _answer = result.Text;
                answered = true;
                var footer = new StringBuilder("\n\n---\n*");
                footer.Append(result.FromCache ? "来自本地缓存（生成于 " + result.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "）" : $"耗时 {result.Elapsed.TotalSeconds:F1} 秒");
                footer.Append(" · 由 Claude Code CLI 生成；本地词典内容未被修改。*");
                body = Header(result.Model, string.Empty) + result.Text + footer;
            }
            catch (OperationCanceledException)
            {
                body = Header(null, string.Empty) + "已取消。可在命令栏选择「重新生成」。";
            }
            catch (AiException ex)
            {
                body = Header(null, string.Empty) + "⚠️ " + ex.Message + "\n\n*本地词典不受影响。*";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                body = Header(null, string.Empty) + "⚠️ AI 请求失败：" + ex.Message + "\n\n*本地词典不受影响。*";
            }

            lock (_lock)
            {
                if (!ReferenceEquals(_cts, cts))
                {
                    return;
                }
            }

            _content.Body = body;
            ShowCommands(loading: false, answered);
            IsLoading = false;
        });
    }

    /// <summary>Enter runs the first command: cancel while the answer streams, copy once it is there, retry after a failure.</summary>
    private void ShowCommands(bool loading, bool answered) =>
        Commands = loading ? [_cancelItem, _regenerateItem, _copyItem] : answered ? [_copyItem, _regenerateItem] : [_regenerateItem];

    private void Cancel()
    {
        lock (_lock)
        {
            _cts?.Cancel();
        }

        Status.Show("已取消 AI 请求");
    }

    /// <summary>Synchronous progress (no SynchronizationContext in the COM server).</summary>
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
