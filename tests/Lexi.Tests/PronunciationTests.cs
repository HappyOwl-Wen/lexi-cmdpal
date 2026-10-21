using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Dictionary.Import;
using Lexi.Dictionary.Storage;
using Lexi.Dictionary.View;

namespace Lexi.Tests;

public class AccentDetectionTests
{
    // Tags, notes and file names as they appear in the 2026-10 wiktextract dump of the English Wiktionary.
    [Theory]
    [InlineData(new[] { "General-American", "Received-Pronunciation" }, null, null, "UK/US")] // house /haʊ̯s/
    [InlineData(new[] { "Received-Pronunciation", "General-American" }, null, null, "UK/US")]
    [InlineData(new[] { "Australia", "Received-Pronunciation" }, null, null, "UK")] // tomato /təˈmɑː.təʊ/
    [InlineData(new[] { "British", "Southern", "Standard" }, null, null, "UK")] // house /haws/
    [InlineData(new[] { "Received-Pronunciation" }, null, "En-uk-water.ogg", "UK")]
    [InlineData(new string[0], "SSB", "LL-Q1860 (eng)-Vealhurl-house.wav", "UK")]
    [InlineData(new[] { "US" }, "non-cot-caught", "LL-Q1860 (eng)-Nail123Real-water.wav", "US")]
    [InlineData(new[] { "California" }, null, "en-us-tomato.ogg", "US")]
    [InlineData(new[] { "Philadelphia" }, "Baltimore", "LL-Q1860 (eng)-Byrhtnoð-water.wav", "US")]
    [InlineData(new string[0], "of the US", null, "US")]
    [InlineData(new string[0], "Upper Midwestern US, Northwestern US, Canada", null, "US")]
    [InlineData(new[] { "cot-caught-merger" }, null, null, "US")] // water /ˈwɑ.tɚ/
    [InlineData(new string[0], "without the cot–caught merger", null, "US")] // water /ˈwɔ.tɚ/
    [InlineData(new[] { "Scotland", "cot-caught-merger" }, null, null, "SC")] // a named region wins
    [InlineData(new[] { "Australia" }, null, "En-au-tomato.ogg", "AU")]
    [InlineData(new string[0], "Queensland", "LL-Q1860 (eng)-Exampleuser-house.wav", "AU")]
    [InlineData(new[] { "Canada" }, null, "En-ca-raising-house.ogg", "CA")]
    [InlineData(new string[0], null, "En-uk-house.ogg", "UK")]
    [InlineData(new string[0], null, "en-us-house-noun.ogg", "US")]
    [InlineData(new string[0], "Baltimore, Philadelphia", null, null)] // regional, but no list entry names it as a whole
    [InlineData(new string[0], "Lancashire, fair–fur merger", null, null)] // a British dialect is not the British standard
    [InlineData(new string[0], null, "LL-Q1860 (eng)-Back ache-water.wav", null)]
    public void English_region_from_tags_note_and_file(string[] tags, string? note, string? file, string? expected)
        => Assert.Equal(expected, KaikkiImporter.EnglishRegion([.. tags], note, file));
}

public class PronunciationPlanTests
{
    private static readonly SourceInfo WiktEn = new(3, "wikt-en", "Wiktionary (English edition)", "CC BY-SA 4.0", "https://en.wiktionary.org", null, "Wiktionary contributors", true);

    private static readonly AudioView Uk = new("En-uk-house.ogg", "UK", null, WiktEn);
    private static readonly AudioView Us = new("en-us-house-noun.ogg", "US", null, WiktEn);
    private static readonly AudioView Both = new("En-house.ogg", "UK/US", null, WiktEn);
    private static readonly AudioView Ca = new("En-ca-raising-house.ogg", "CA", null, WiktEn);

    private static WordView View(Lang lang, params AudioView[] audio) => new()
    {
        Word = new WordRecord(1, lang, "house", "house", 5f, WordFlags.None, null, null),
        Title = "house",
        Audio = audio,
    };

    private static string[] Describe(IReadOnlyList<PronunciationStep> plan)
        => [.. plan.Select(s => (s.Recording?.File ?? "voice " + s.VoiceLanguage) + (s.OtherAccent ? " (other accent)" : string.Empty))];

    [Fact]
    public void English_is_british_first_then_other_accents()
        => Assert.Equal(
            ["En-uk-house.ogg", "voice en-GB", "en-us-house-noun.ogg (other accent)", "En-ca-raising-house.ogg (other accent)", "voice en-US (other accent)"],
            Describe(PronunciationPlan.For(View(Lang.En, Ca, Us, Uk), null, ["de-DE", "en-US", "en-GB"])));

    [Fact]
    public void A_british_voice_comes_before_an_american_recording()
        => Assert.Equal(
            ["voice en-GB", "en-us-house-noun.ogg (other accent)", "voice en-US (other accent)"],
            Describe(PronunciationPlan.For(View(Lang.En, Us), null, ["en-US", "en-GB"])));

    [Fact]
    public void Without_british_recording_or_voice_the_american_recording_is_a_marked_fallback()
        => Assert.Equal(
            ["en-us-house-noun.ogg (other accent)", "voice en-US (other accent)"],
            Describe(PronunciationPlan.For(View(Lang.En, Us), null, ["en-US", "zh-CN"])));

    [Fact]
    public void A_recording_for_both_accents_counts_as_british_and_as_american()
    {
        Assert.Equal("En-house.ogg", PronunciationPlan.For(View(Lang.En, Us, Both), null, [])[0].Recording?.File);
        Assert.Equal(
            ["en-us-house-noun.ogg", "En-house.ogg", "voice en-US"],
            Describe(PronunciationPlan.For(View(Lang.En, Uk, Us, Both), PronunciationPlan.American, ["en-GB", "en-US"])));
    }

    [Fact]
    public void An_explicit_accent_has_no_fallback()
    {
        Assert.Empty(PronunciationPlan.For(View(Lang.En, Us), PronunciationPlan.British, ["en-US"]));
        Assert.Equal(["voice en-GB"], Describe(PronunciationPlan.For(View(Lang.En, Us), PronunciationPlan.British, ["en-US", "en-GB"])));
    }

    [Fact]
    public void Any_english_voice_is_the_last_resort()
        => Assert.Equal(["voice en-AU (other accent)"], Describe(PronunciationPlan.For(View(Lang.En), null, ["de-DE", "en-AU"])));

    [Fact]
    public void German_plays_germany_before_austria_then_a_german_voice()
    {
        var at = new AudioView("De-at-Haus.ogg", "AT", null, WiktEn);
        var de = new AudioView("De-Haus.ogg", null, null, WiktEn);
        Assert.Equal(["De-Haus.ogg", "De-at-Haus.ogg", "voice de-DE"], Describe(PronunciationPlan.For(View(Lang.De, at, de), null, ["en-GB", "de-DE"])));
        Assert.Equal(["voice de-CH"], Describe(PronunciationPlan.For(View(Lang.De), null, ["de-CH"])));
    }
}

public class PronunciationDisplayTests
{
    private static readonly SourceInfo WiktEn = new(3, "wikt-en", "Wiktionary (English edition)", "CC BY-SA 4.0", "https://en.wiktionary.org", null, "Wiktionary contributors", true);

    private static WordView Build(string headword, List<Pron> ipa, List<AudioRef>? audio = null)
    {
        var data = new EntryData { Headword = headword, Pos = "noun", Ipa = ipa, Audio = audio, Senses = [new Sense { Gloss = "x", GlossLang = "en" }] };
        var word = new WordRecord(1, Lang.En, headword, headword, 5f, WordFlags.None, "n.", null);
        return WordViewBuilder.Build(word, [new StoredEntry(WiktEn, 0, "noun", data)], []);
    }

    [Fact]
    public void Transcriptions_and_recordings_are_british_first()
    {
        var view = Build(
            "tomato",
            [new() { Text = "/təˈmeɪ.toʊ/", Region = "US" }, new() { Text = "[tʰəˈmeɪɾo]", Region = "CA" }, new() { Text = "[təˈmɑː.təʊ]", Region = "UK" }, new() { Text = "/təˈmɑː.təʊ/", Region = "UK" }],
            [new() { File = "en-us-tomato.ogg", Region = "US" }, new() { File = "en-uk-tomato.ogg", Region = "UK" }]);

        Assert.Equal(["/təˈmɑː.təʊ/", "[təˈmɑː.təʊ]", "/təˈmeɪ.toʊ/", "[tʰəˈmeɪɾo]"], view.Ipa.Select(p => p.Text));
        Assert.Equal("en-uk-tomato.ogg", view.Audio[0].File);
        Assert.Contains("英 `/təˈmɑː.təʊ/` · 美 `/təˈmeɪ.toʊ/` · 名词", MarkdownRenderer.Details(view, null, includeTitle: false), StringComparison.Ordinal);
        Assert.Contains("加 `[tʰəˈmeɪɾo]`", MarkdownRenderer.Full(view), StringComparison.Ordinal);
    }

    [Fact]
    public void A_transcription_for_both_accents_stands_alone_in_the_details_header()
    {
        var view = Build("house", [new() { Text = "/haʊ̯s/", Region = "UK/US" }, new() { Text = "/haws/", Region = "UK" }]);

        var details = MarkdownRenderer.Details(view, null, includeTitle: false);
        Assert.Contains("英/美 `/haʊ̯s/` · 名词", details, StringComparison.Ordinal);
        Assert.DoesNotContain("/haws/", details, StringComparison.Ordinal);
        Assert.Contains("Ctrl+P：发音（英式优先）", details, StringComparison.Ordinal);
    }
}
