using Lexi.Core.Models;

namespace Lexi.Dictionary.View;

/// <summary>One way to pronounce a word: a recording, or a system voice of a language ("en-GB").</summary>
/// <param name="OtherAccent">A fallback in an accent other than the one wanted (an American recording for "British first").</param>
public sealed record PronunciationStep(AudioView? Recording, string? VoiceLanguage, bool OtherAccent);

/// <summary>
/// The order in which to try pronunciations. English is British first: British recordings, then a British system
/// voice, and only then — unless an accent was asked for explicitly — recordings and voices of other accents.
/// German: recordings (Germany, then Austria and Switzerland), then a German voice. Pure logic, so the order can be
/// tested without audio devices or installed voices.
/// </summary>
public static class PronunciationPlan
{
    public const string British = "UK";
    public const string American = "US";

    /// <summary>One transcription given for both Received Pronunciation and General American (house /haʊs/).</summary>
    public const string BritishAndAmerican = "UK/US";

    /// <param name="accent">Null for the default (British first, other accents as a fallback); "UK" or "US" for that accent only.</param>
    /// <param name="voiceLanguages">Languages of the installed system voices ("en-US", "de-DE" …).</param>
    public static IReadOnlyList<PronunciationStep> For(WordView view, string? accent, IReadOnlyCollection<string> voiceLanguages)
    {
        var lang = view.Word.Lang;
        var steps = new List<PronunciationStep>();
        if (lang == Lang.De)
        {
            steps.AddRange(view.Audio.OrderBy(a => AccentRank(lang, a.Region)).Select(a => new PronunciationStep(a, null, false)));
            if ((Voice(voiceLanguages, "de-DE") ?? Voice(voiceLanguages, "de")) is { } german)
            {
                steps.Add(new PronunciationStep(null, german, false));
            }

            return steps;
        }

        var wanted = accent == American ? American : British;
        var own = view.Audio.Where(a => IsAccent(a.Region, wanted)).ToList();
        steps.AddRange(own.Select(a => new PronunciationStep(a, null, false)));
        if (Voice(voiceLanguages, wanted == American ? "en-US" : "en-GB") is { } ownVoice)
        {
            steps.Add(new PronunciationStep(null, ownVoice, false));
        }

        if (accent is null)
        {
            steps.AddRange(view.Audio.Except(own).OrderBy(a => AccentRank(lang, a.Region)).Select(a => new PronunciationStep(a, null, true)));
            if ((Voice(voiceLanguages, "en-US") ?? Voice(voiceLanguages, "en")) is { } otherVoice && steps.All(s => s.VoiceLanguage != otherVoice))
            {
                steps.Add(new PronunciationStep(null, otherVoice, true));
            }
        }

        return steps;
    }

    /// <summary>True when a recording or transcription of this region is in the accent ("UK/US" is in both).</summary>
    public static bool IsAccent(string? region, string accent) => region == accent || region == BritishAndAmerican;

    /// <summary>Display and playback order: English British, American, unspecified, others; German Germany first.</summary>
    public static int AccentRank(Lang lang, string? region) => lang == Lang.En
        ? region switch { British or BritishAndAmerican => 0, American => 1, null => 2, _ => 3 }
        : region switch { null or "DE" => 0, "AT" => 1, "CH" => 2, _ => 3 };

    /// <summary>"英式", "美式" … for messages; null for an unspecified region.</summary>
    public static string? AccentName(string? region) => region switch
    {
        British => "英式",
        American => "美式",
        BritishAndAmerican => "英/美",
        "AU" => "澳式",
        "CA" => "加拿大",
        "NZ" => "新西兰",
        "IE" => "爱尔兰",
        "SC" => "苏格兰",
        "IN" => "印度",
        "AT" => "奥地利",
        "CH" => "瑞士",
        _ => null,
    };

    /// <summary>The first installed voice of exactly this language ("en-GB"), or of the language family ("en").</summary>
    private static string? Voice(IReadOnlyCollection<string> voiceLanguages, string language) => language.Contains('-', StringComparison.Ordinal)
        ? voiceLanguages.FirstOrDefault(v => v.Equals(language, StringComparison.OrdinalIgnoreCase))
        : voiceLanguages.FirstOrDefault(v => v.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));
}
