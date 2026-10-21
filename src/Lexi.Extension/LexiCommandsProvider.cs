using Lexi.Extension.Pages;
using Lexi.Extension.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Lexi.Extension;

/// <summary>
/// Entry point for Command Palette. Construction must stay cheap: Command Palette loads extensions at its own
/// startup, so the dictionary database is only opened when the Lexi page (or the fallback) is first used.
/// </summary>
public sealed partial class LexiCommandsProvider : CommandProvider
{
    private readonly ServiceHub _hub;
    private readonly ICommandItem[] _commands;
    private readonly IFallbackCommandItem[] _fallbacks;
    private readonly DictionaryPage _page;

    public LexiCommandsProvider()
        : this(new ServiceHub())
    {
    }

    internal LexiCommandsProvider(ServiceHub hub)
    {
        _hub = hub;
        Id = "Lexi.Dictionary";
        DisplayName = "Lexi Dictionary";
        Icon = IconHelpers.FromRelativePath("Assets\\LexiIcon.png");
        Settings = _hub.Settings.Settings;

        _page = new DictionaryPage(_hub);
        _commands =
        [
            new CommandItem(_page)
            {
                Title = "Lexi",
                // "dict" in the subtitle lets users find the command by typing "dict" on the main page.
                Subtitle = "dict · 英语/德语 → 中文词典（拼写纠错、词形还原、发音、AI）",
                Icon = Icon,
            },
        ];
        _fallbacks = [new LookupFallbackItem(_hub)];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override IFallbackCommandItem[] FallbackCommands() => _fallbacks;

    public override void Dispose()
    {
        _page.Dispose();
        _hub.Dispose();
        base.Dispose();
    }
}
