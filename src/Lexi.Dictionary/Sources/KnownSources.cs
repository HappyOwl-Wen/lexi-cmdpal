namespace Lexi.Dictionary.Sources;

/// <summary>Static description of a data source. License text is what we verified, not just the repository LICENSE file.</summary>
public sealed record SourceDefinition(
    string Code,
    string Name,
    string License,
    string Url,
    string Attribution,
    bool Redistributable,
    string Note);

public static class KnownSources
{
    public static readonly SourceDefinition Ecdict = new(
        "ecdict",
        "ECDICT 英汉双解词典",
        "Repository: MIT. Content: compiled from several sources (EDictAZ word list, cdict, crawled phonetics, user contributions); per-entry licensing is not documented.",
        "https://github.com/skywind3000/ECDICT",
        "ECDICT by Linwei (skywind3000) and contributors",
        Redistributable: false,
        Note: "仅用于本地个人构建；未逐条确认内容授权，默认不随安装包再分发。");

    public static readonly SourceDefinition WiktionaryEn = new(
        "wikt-en",
        "Wiktionary (English edition)",
        "CC BY-SA 4.0 (Wiktionary text), extracted with wiktextract by Kaikki.org",
        "https://en.wiktionary.org/",
        "Wiktionary contributors; machine-readable data by Kaikki.org / wiktextract (Tatu Ylonen)",
        Redistributable: true,
        Note: "英文释义、英/美式 IPA、词形、发音文件名（音频本身托管于 Wikimedia Commons，各文件单独授权）。");

    public static readonly SourceDefinition WiktionaryDe = new(
        "wikt-de",
        "Wiktionary (Deutsch)",
        "CC BY-SA 4.0 (Wiktionary text), extracted with wiktextract by Kaikki.org",
        "https://de.wiktionary.org/",
        "Wiktionary-Autoren; maschinenlesbare Daten von Kaikki.org / wiktextract",
        Redistributable: true,
        Note: "德语词条：冠词、格变化、动词变位、IPA、德语释义及按义项对齐的中/英翻译。");

    public static readonly SourceDefinition WiktionaryZh = new(
        "wikt-zh",
        "维基词典（中文版）",
        "CC BY-SA 4.0 (Wiktionary text), extracted with wiktextract by Kaikki.org",
        "https://zh.wiktionary.org/",
        "维基词典贡献者；Kaikki.org / wiktextract 提取",
        Redistributable: true,
        Note: "德语词条的直接中文释义（繁体在导入时按字符转换为简体）。");

    public static readonly SourceDefinition FrequencyWords = new(
        "freq",
        "FrequencyWords (OpenSubtitles 2018)",
        "Content CC BY-SA 4.0, code MIT",
        "https://github.com/hermitdave/FrequencyWords",
        "Hermit Dave, from OPUS OpenSubtitles2018",
        Redistributable: true,
        Note: "仅用于排序的词频，不展示内容。");

    public static IReadOnlyList<SourceDefinition> All { get; } = [Ecdict, WiktionaryEn, WiktionaryDe, WiktionaryZh, FrequencyWords];
}
