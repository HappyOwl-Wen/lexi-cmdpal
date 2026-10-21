using Lexi.Core.Models;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Lexi.Extension.Pages;

/// <summary>Native Command Palette filter drop-down: all languages / English / Deutsch.</summary>
internal sealed partial class LanguageFilters : Filters
{
    public LanguageFilters()
    {
        CurrentFilterId = "all";
    }

    public Lang? Selected => CurrentFilterId switch
    {
        "en" => Lang.En,
        "de" => Lang.De,
        _ => null,
    };

    public override IFilterItem[] GetFilters() =>
    [
        new Filter { Id = "all", Name = "全部语言", Icon = Glyphs.Globe },
        new Filter { Id = "en", Name = "English", Icon = Glyphs.Dictionary },
        new Filter { Id = "de", Name = "Deutsch", Icon = Glyphs.Dictionary },
    ];
}
