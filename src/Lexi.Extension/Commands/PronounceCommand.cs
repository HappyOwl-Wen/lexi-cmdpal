using Lexi.Core.Search;
using Lexi.Extension.Pages;
using Lexi.Extension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Lexi.Extension.Commands;

/// <summary>Plays the pronunciation in the background and reports what was played (recording / TTS / none).</summary>
internal sealed partial class PronounceCommand : InvokableCommand
{
    private readonly ServiceHub _hub;
    private readonly WordRecord _word;
    private readonly string? _region;

    public PronounceCommand(ServiceHub hub, WordRecord word, string? region = null)
    {
        _hub = hub;
        _word = word;
        _region = region;
        Name = region is null ? "发音" : $"发音（{region}）";
        Icon = Glyphs.Speaker;
    }

    public override ICommandResult Invoke()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var view = _hub.Dictionary.GetView(_word);
                if (view is null)
                {
                    Status.Show("词典不可用，无法发音", MessageState.Error);
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var outcome = await _hub.Pronouncer.PlayAsync(view, _region, cts.Token);
                Status.Show(outcome.Message, outcome.Kind == PronunciationKind.None ? MessageState.Warning : MessageState.Info);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Status.Show("发音失败：" + ex.Message, MessageState.Error);
            }
        });
        return CommandResult.KeepOpen();
    }
}

/// <summary>Copies text and keeps the palette open (a dictionary lookup usually continues).</summary>
internal sealed partial class CopyCommand : InvokableCommand
{
    private readonly Func<string?> _text;
    private readonly string _what;

    public CopyCommand(string name, string what, Func<string?> text)
    {
        _text = text;
        _what = what;
        Name = name;
        Icon = Glyphs.Copy;
    }

    public override ICommandResult Invoke()
    {
        var text = _text();
        if (string.IsNullOrEmpty(text))
        {
            return CommandResult.ShowToast(new ToastArgs { Message = "没有可复制的内容", Result = CommandResult.KeepOpen() });
        }

        ClipboardHelper.SetText(text);
        return CommandResult.ShowToast(new ToastArgs { Message = $"已复制{_what}", Result = CommandResult.KeepOpen() });
    }
}

internal static class Status
{
    public static void Show(string message, MessageState state = MessageState.Info, int durationMs = 3500) =>
        new ToastStatusMessage(new StatusMessage { Message = message, State = state }) { Duration = durationMs }.Show();
}
