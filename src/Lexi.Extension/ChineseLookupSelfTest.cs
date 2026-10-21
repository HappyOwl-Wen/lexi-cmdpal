using Lexi.Extension.Pages;
using Lexi.Extension.Services;
using Microsoft.CommandPalette.Extensions;

namespace Lexi.Extension;

internal static partial class SelfTest
{
    /// <summary>
    /// Chinese input (中 → 英/德): language sections, placement of the Google and AI translations, details note and the
    /// main-page fallback.
    /// </summary>
    private static void RunChineseChecks(Report r, ServiceHub hub, LexiCommandsProvider provider, PageDriver driver, IFallbackCommandItem fallback)
    {
        if (hub.Dictionary.Info is { ChineseTerms: 0 })
        {
            r.Step("chinese: database without the Chinese index", () =>
            {
                var items = driver.Search("房子", out _);
                Expect(items.Any(i => i.Title.Contains("中文反查", StringComparison.Ordinal)), $"no rebuild hint: {Top(items)}");
                return $"{WarnPrefix}this database has no Chinese index; the page shows the rebuild hint (scripts\\import-data.ps1)";
            });
            return;
        }

        r.Step("chinese: 房子 → English and German sections", () =>
        {
            var items = driver.Search("房子", out var ms);

            // Command Palette draws a heading for an item without a command; every heading opens its section.
            var headings = items.Where(i => i.Command is null).Select(i => i.Section).ToArray();
            var expected = hub.AiEnabled || hub.SentenceTranslationEnabled
                ? new[] { DictionaryPage.EnglishSection, DictionaryPage.GermanSection, DictionaryPage.TranslationSection }
                : [DictionaryPage.EnglishSection, DictionaryPage.GermanSection];
            Expect(headings.SequenceEqual(expected), $"headings: {string.Join(" | ", headings)}");
            for (var i = 0; i < items.Length; i++)
            {
                Expect(items[i].Command is not null || (i + 1 < items.Length && items[i + 1].Command is not null && items[i + 1].Section == items[i].Section), $"heading {items[i].Section} is not followed by its results");
            }

            var rows = items.Where(i => i.Command is not null).ToArray();
            var english = rows.Where(i => i.Section == DictionaryPage.EnglishSection).ToArray();
            var german = rows.Where(i => i.Section == DictionaryPage.GermanSection).ToArray();
            Expect(rows[0].Title == "house", $"the best English equivalent must be the first row: {Top(rows)}");
            Expect(german.Length > 0 && german[0].Title == "das Haus", $"German section: {Top(german)}");
            if (hub.AiEnabled)
            {
                Expect(HasTag(rows[^1], "AI"), $"the AI translation should follow the dictionary results: {Top(rows, 20)}");
            }

            var body = rows[0].Details?.Body ?? string.Empty;
            Expect(body.Contains("中文反查", StringComparison.Ordinal), $"details note missing: {Snippet(body, 120)}");
            return $"headings {string.Join(" / ", headings)}; {english[0].Title} · {german[0].Title}; {english.Length} EN + {german.Length} DE; {ms:F0} ms";
        });

        r.Step("chinese: both languages on the first screen", () =>
        {
            // 学习 has many English equivalents; the German heading must still come within the first eight list items.
            var items = driver.Search("学习", out _);
            var english = items.Count(i => i.Command is not null && i.Section == DictionaryPage.EnglishSection);
            var germanHeading = Array.FindIndex(items, i => i.Command is null && i.Section == DictionaryPage.GermanSection);
            Expect(english is > 0 and <= 6, $"{english} English rows: {Top(items, 12)}");
            Expect(germanHeading is > 0 and <= 7, $"German heading is item {germanHeading}: {Top(items, 12)}");
            var sentence = driver.Search("我想学习德语。", out _);
            var leading = sentence.Count(i => i.Command is not null && i.Section == DictionaryPage.TranslationSection);
            var belowTranslations = sentence.Count(i => i.Command is not null && i.Section == DictionaryPage.EnglishSection);
            Expect(leading == 0 || belowTranslations <= 6 - leading - 1, $"{belowTranslations} English rows below {leading} translation items");
            return $"学习: {english} English rows, German heading at item {germanHeading}; sentence: {belowTranslations} English rows below {leading} translation item(s)";
        });

        r.Step("chinese: Google and Claude translations side by side", () =>
        {
            try
            {
                SubmitSentenceSettings(provider, """{"ai.sentenceTranslation":"true","ai.enabled":"true"}""");

                // A word: dictionary first, then the translation section with Google before Claude Code.
                var word = driver.Search("房子", out _).Where(i => i.Command is not null).ToArray();
                var translations = word.Where(i => i.Section == DictionaryPage.TranslationSection).ToArray();
                Expect(translations is [{ Command: TranslationResultPage }, { Command: AiPage }], $"translation rows: {Top(translations)}");
                Expect(word[0].Title == "house" && word[^1].Command is AiPage, $"translations should follow the dictionary results: {Top(word, 20)}");
                var online = translations[0].Command.Name;
                Expect(online == "Google 翻译成英语和德语" && HasTag(translations[0], "在线翻译") && !HasTag(translations[0], "AI"), $"online item: {online}");

                // A sentence: both translations first, and the German heading still within the first eight items.
                var sentence = driver.Search("我想学习德语。", out _);
                var rows = sentence.Where(i => i.Command is not null).ToArray();
                Expect(rows is [{ Command: TranslationResultPage }, { Command: AiPage }, ..], $"a sentence should start with both translations: {Top(rows)}");
                var english = rows.Count(i => i.Section == DictionaryPage.EnglishSection);
                var germanHeading = Array.FindIndex(sentence, i => i.Command is null && i.Section == DictionaryPage.GermanSection);
                Expect(english <= 3 && germanHeading is > 0 and <= 7, $"{english} English rows, German heading at item {germanHeading}");
                Expect(!hub.Ai.IsBusy, "listing translations started an AI request");
                return $"房子 → {Top(translations)}; sentence: {Top(rows, 2)} then {english} English rows, German heading at item {germanHeading}";
            }
            finally
            {
                SubmitSentenceSettings(provider, """{"ai.sentenceTranslation":"false"}""");
            }
        });

        r.Step("chinese: sentence → AI first, then partial matches", () =>
        {
            var items = driver.Search("我想学习德语。", out var ms);
            var rows = items.Where(i => i.Command is not null).ToArray();
            var words = rows.Where(i => !HasTag(i, "AI")).ToArray();
            Expect(words.Length > 0 && words.All(i => HasTag(i, "部分匹配")), $"expected partial matches: {Top(items)}");
            Expect(words.Any(i => i.Title == "lernen"), $"lernen missing: {Top(words, 12)}");
            if (hub.AiEnabled)
            {
                Expect(HasTag(rows[0], "AI"), $"the AI translation should come first for a sentence: {Top(items)}");
            }

            return $"{Top(items, 4)}; {ms:F0} ms";
        });

        r.Step("fallback: Chinese on the main page", () =>
        {
            var handler = fallback.FallbackHandler;
            var t1 = Time(() => handler.UpdateQuery("dict 房子"));
            var withPrefix = fallback.Title;
            Expect(withPrefix.Contains("house", StringComparison.Ordinal) && withPrefix.Contains("das Haus", StringComparison.Ordinal), $"\"{withPrefix}\"");
            var t2 = Time(() => handler.UpdateQuery("我想学习德语。"));
            Expect(fallback.Title.Length == 0, $"a sentence without prefix produced \"{fallback.Title}\"");
            string? bare = null;
            if (hub.Settings.FallbackEnabled)
            {
                handler.UpdateQuery("苹果");
                bare = fallback.Title;
                Expect(bare.Contains("apple", StringComparison.Ordinal) && bare.Contains("der Apfel", StringComparison.Ordinal), $"\"{bare}\"");
            }

            handler.UpdateQuery(string.Empty);
            return $"\"{withPrefix}\" {t1:F1} ms · sentence ignored {t2:F1} ms" + (bare is null ? string.Empty : $" · \"{bare}\"");
        });
    }
}
