using Lexi.Core.Models;
using Lexi.Dictionary.View;
using Lexi.Extension.Services;
using Windows.Media.SpeechSynthesis;

namespace Lexi.Extension;

internal static partial class SelfTest
{
    /// <summary>
    /// English pronunciation is British first: the order for a word with a British recording (house) and one with only
    /// an American recording (weakness), the details header, and what Ctrl+P reports. Nothing is played: the pronouncer
    /// under test hands its audio to a sink instead of the speakers. Recordings are downloaded only with --network.
    /// </summary>
    private static void RunPronunciationChecks(Report r, ServiceHub hub, string dataDirectory, bool withNetwork)
    {
        var voices = SpeechSynthesizer.AllVoices.Select(v => v.Language).ToList();
        var britishVoice = voices.Any(v => v.Equals("en-GB", StringComparison.OrdinalIgnoreCase));

        r.Step("pronunciation: British first (house, weakness)", () =>
        {
            var house = EnglishView(hub, "house");
            var first = PronunciationPlan.For(house, null, voices)[0];
            Expect(first.Recording is { } recording && PronunciationPlan.IsAccent(recording.Region, PronunciationPlan.British), $"house starts with {StepText(first)}");
            Expect(house.Ipa.Count > 0 && PronunciationPlan.IsAccent(house.Ipa[0].Region, PronunciationPlan.British), $"house: first transcription is {(house.Ipa.Count > 0 ? house.Ipa[0] : null)}");
            var details = MarkdownRenderer.Details(house, null, includeTitle: false);
            var header = details.Split('\n').First(l => l.Contains('`', StringComparison.Ordinal)).Trim();
            Expect(header.StartsWith('英'), $"details header: {header}");
            Expect(details.Contains("Ctrl+P：发音（英式优先）", StringComparison.Ordinal), "details footer does not say 英式优先");

            var weakness = EnglishView(hub, "weakness");
            var plan = PronunciationPlan.For(weakness, null, voices);
            var summary = $"house: {StepText(first)}, header \"{header}\"; weakness: {string.Join(" → ", plan.Select(StepText))}";
            if (weakness.Audio.Any(a => PronunciationPlan.IsAccent(a.Region, PronunciationPlan.British)))
            {
                return $"{WarnPrefix}weakness now has a British recording, so the fallback is not exercised · {summary}";
            }

            Expect(plan.Count > 0 && (britishVoice ? plan[0].VoiceLanguage == "en-GB" : plan[0].OtherAccent), $"weakness: {summary}");
            return britishVoice ? summary : $"{summary} (no en-GB voice installed)";
        });

        r.Step("pronunciation: what Ctrl+P reports (nothing played)", () =>
        {
            var played = 0;
            using var pronouncer = new Pronouncer(dataDirectory, source =>
            {
                played++;
                source.Dispose();
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            // Each request hands exactly one source to the sink unless nothing is available. A rate-limited download is
            // retried once after Wikimedia's Retry-After.
            PronunciationOutcome Play(WordView view, string? accent)
            {
                for (var attempt = 1; ; attempt++)
                {
                    WaitForCommons();
                    var before = played;
                    var outcome = pronouncer.PlayAsync(view, accent, cts.Token).GetAwaiter().GetResult();
                    Expect(played - before == (outcome.Kind == PronunciationKind.None ? 0 : 1), $"{played - before} sources for \"{outcome.Message}\"");
                    if (attempt == 1 && outcome.Message.Contains("限流", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return outcome;
                }
            }

            var weakness = EnglishView(hub, "weakness");

            // Explicitly British without a British recording: the British voice, or a clear "none" with how to add one.
            var british = Play(weakness, PronunciationPlan.British);
            Expect(
                britishVoice
                    ? british.Kind == PronunciationKind.Synthesized && british.Message.Contains("英式合成语音", StringComparison.Ordinal)
                    : british.Kind == PronunciationKind.None && british.Message.StartsWith("没有英式发音", StringComparison.Ordinal) && british.Message.Contains("英语(英国)", StringComparison.Ordinal),
                $"weakness 英式发音: {british.Message}");
            var lines = new List<string> { $"weakness 英式发音: {british.Message}" };

            if (withNetwork)
            {
                var house = Play(EnglishView(hub, "house"), null);
                Expect(house.Kind == PronunciationKind.Recording && house.Message.StartsWith("🔊 英", StringComparison.Ordinal), $"house: {house.Message}");
                var fallback = Play(weakness, null);
                Expect(
                    britishVoice
                        ? fallback.Message.Contains("英式合成语音", StringComparison.Ordinal)
                        : fallback.Kind == PronunciationKind.Recording && fallback.Message.Contains("没有英式录音", StringComparison.Ordinal),
                    $"weakness: {fallback.Message}");
                lines.Add($"house: {house.Message}");
                lines.Add($"weakness: {fallback.Message}");
            }

            return string.Join(" | ", lines);
        });
    }

    /// <summary>Waits out a Wikimedia pause (HTTP 429 Retry-After) before the next download, as its robot policy asks.</summary>
    private static void WaitForCommons()
    {
        var wait = Pronouncer.CommonsPause;
        if (wait > TimeSpan.Zero && wait <= TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(wait + TimeSpan.FromMilliseconds(200));
        }
    }

    private static WordView EnglishView(ServiceHub hub, string word)
    {
        var hit = hub.Dictionary.Lookup(word).Hits.FirstOrDefault(h => h.Word.Lang == Lang.En && h.Word.Word == word)
            ?? throw new InvalidOperationException($"{word} not found");
        return hub.Dictionary.GetView(hit.Word) ?? throw new InvalidOperationException($"no view for {word}");
    }

    private static string StepText(PronunciationStep step) =>
        (step.Recording is { } a ? $"{a.Region ?? "?"} recording {a.File}" : $"voice {step.VoiceLanguage}") + (step.OtherAccent ? " (other accent)" : string.Empty);
}
