namespace Lexi.Core.Models;

public enum Lang : byte
{
    Unknown = 0,
    En = 1,
    De = 2,
}

public static class LangExtensions
{
    public static string Code(this Lang lang) => lang switch
    {
        Lang.En => "en",
        Lang.De => "de",
        _ => "",
    };

    /// <summary>Short tag shown in the result list.</summary>
    public static string Tag(this Lang lang) => lang switch
    {
        Lang.En => "EN",
        Lang.De => "DE",
        _ => "?",
    };

    public static string DisplayName(this Lang lang) => lang switch
    {
        Lang.En => "English",
        Lang.De => "Deutsch",
        _ => "Unknown",
    };

    public static Lang FromCode(string? code) => code switch
    {
        "en" => Lang.En,
        "de" => Lang.De,
        _ => Lang.Unknown,
    };
}
