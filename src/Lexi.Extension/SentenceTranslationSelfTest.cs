using System.Text.Json;
using Lexi.Extension.Pages;
using Lexi.Extension.Services;
using Lexi.Translation;
using Microsoft.CommandPalette.Extensions;

namespace Lexi.Extension;

internal static partial class SelfTest
{
    private static readonly string[] SentenceInputs = ["Guten Morgen!", "I am ready.", "Wie geht's?", "Hello, how are you?", "Please use /help here", "  Guten\tMorgen\n  "];
    private static readonly string[] LiveSentenceInputs = ["The weather is nice today.", "Wie geht es dir heute?"];
    private const string LiveChineseInput = "我想学习德语。";
    private static readonly string[] NonSentenceInputs = ["", "   ", "Haus", "vulnerability", "Haus !", "Haus 123", "... !", "你好", "这是一个句子。", "中文 query"];

    private static void RunSentenceTranslationChecks(Report r, ServiceHub hub, LexiCommandsProvider provider, string dataDirectory)
    {
        SentenceTranslationPage? sentencePage = null;
        try
        {
            r.Step("sentence translation: independent setting and command", () =>
            {
                Expect(!hub.Settings.SentenceTranslationEnabled, "sentence translation must default to off");
                Expect(provider.TopLevelCommands().Length == 1, "disabled sentence command was exposed");
                var form = provider.Settings!.SettingsPage.GetContent().OfType<IFormContent>().Single();
                using var template = JsonDocument.Parse(form.TemplateJson);
                Expect(form.TemplateJson.Contains("ai.sentenceTranslation", StringComparison.Ordinal), "sentence switch missing from settings form");
                var changes = 0;
                void OnChanged(object sender, IItemsChangedEventArgs args) => changes++;
                provider.ItemsChanged += OnChanged;
                try
                {
                    SubmitSentenceSettings(provider, """{"ai.sentenceTranslation":"true"}""");
                }
                finally
                {
                    provider.ItemsChanged -= OnChanged;
                }

                Expect(changes > 0, "provider did not notify the host about its new command");
                Expect(hub.AiEnabled && hub.Settings.FallbackEnabled, "sentence switch changed AI or dictionary settings");
                Expect(new LexiSettings(dataDirectory).SentenceTranslationEnabled, "sentence switch did not persist");
                var commands = provider.TopLevelCommands();
                Expect(commands.Length == 2 && commands[0].Title == "Lexi", "dictionary command was replaced");
                sentencePage = commands[1].Command as SentenceTranslationPage ?? throw new InvalidOperationException("sentence command missing");
                Expect(sentencePage.Id != commands[0].Command.Id, "commands must have separate IDs for aliases");
                return "off by default; saved; host notified; separate dictionary and sentence commands";
            });

            r.Step("sentence translation: short sentences, no word or Chinese overlap", () =>
            {
                var page = sentencePage ?? throw new InvalidOperationException("sentence page unavailable");
                foreach (var text in SentenceInputs)
                {
                    page.SearchText = text;
                    var items = page.GetItems();

                    // Google first; with AI on, the Claude Code translation of the same sentence next to it.
                    var expected = hub.AiEnabled ? items is [{ Command: TranslationResultPage }, { Command: AiPage }] : items is [{ Command: TranslationResultPage }];
                    Expect(expected, $"sentence did not offer Google{(hub.AiEnabled ? " and Claude" : string.Empty)}: {text}");
                    Expect(!HasTag(items[0], "AI") && (!hub.AiEnabled || HasTag(items[1], "AI")), "online and Claude translations are labelled wrongly");
                }

                foreach (var text in NonSentenceInputs)
                {
                    page.SearchText = text;
                    Expect(!page.GetItems().Any(i => i.Command is TranslationResultPage or AiPage), $"unexpected translation entry: {text}");
                }

                Expect(!hub.Dictionary.IsLoaded, "sentence typing opened the dictionary");
                Expect(hub.History.Items.Count == 0, "sentence typing changed dictionary history");
                Expect(!hub.Ai.IsBusy, "typing started an AI request");
                return "EN/DE short sentences get Google + Claude; words, punctuation and Chinese excluded; no DB or AI request";
            });

            r.Step("sentence translation: disabling updates open pages and blocks stale actions", () =>
            {
                var page = sentencePage ?? throw new InvalidOperationException("sentence page unavailable");
                page.SearchText = "The weather is nice today.";
                var stale = page.GetItems()[0].Command as TranslationResultPage ?? throw new InvalidOperationException("translation action missing");
                SubmitSentenceSettings(provider, """{"ai.sentenceTranslation":"false"}""");
                Expect(provider.TopLevelCommands().Length == 1, "disabled command is still listed");
                Expect(!page.GetItems().Any(i => i.Command is TranslationResultPage), "open sentence page did not refresh");
                var body = stale.GetContent().OfType<IMarkdownContent>().Single().Body;
                Expect(body.Contains("已关闭", StringComparison.Ordinal) && !stale.IsLoading, "a stale action bypassed the sentence switch");
                Expect(hub.AiEnabled && !hub.Ai.IsBusy, "sentence switch disabled other AI or started a request");

                SubmitSentenceSettings(provider, """{"ai.sentenceTranslation":"true","ai.enabled":"false"}""");
                Expect(provider.TopLevelCommands().Length == 2 && page.GetItems() is [{ Command: TranslationResultPage }], "online translation must work with AI disabled");
                Expect(hub.Settings.SentenceTranslationEnabled, "AI master switch erased sentence preference");
                SubmitSentenceSettings(provider, """{"ai.enabled":"true","search.fallback":"false"}""");
                Expect(provider.TopLevelCommands().Length == 2 && page.GetItems() is [{ Command: TranslationResultPage }, { Command: AiPage }], "dictionary fallback switch affected sentence translation");
                return "sentence switch blocks stale actions; AI and dictionary switches do not disable online translation; AI adds the Claude item";
            });
        }
        finally
        {
            SubmitSentenceSettings(provider, """{"ai.sentenceTranslation":"false","ai.enabled":"true","search.fallback":"true"}""");
        }
    }

    private static void SubmitSentenceSettings(LexiCommandsProvider provider, string json) =>
        provider.Settings!.SettingsPage.GetContent().OfType<IFormContent>().Single().SubmitForm(json, "{}");

    private static void RunSentenceTranslationNetworkCheck(Report r, ServiceHub hub, LexiCommandsProvider provider)
    {
        r.Step("sentence translation: live Google results with Claude disabled", () =>
        {
            try
            {
                SubmitSentenceSettings(provider, """{"ai.sentenceTranslation":"true","ai.enabled":"false","ai.claudePath":"C:\\lexi-not-used\\claude.exe"}""");
                var summaries = new List<string>();
                foreach (var sentence in LiveSentenceInputs)
                {
                    using var page = new TranslationResultPage(hub, sentence);
                    using var done = new ManualResetEventSlim();
                    page.PropChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(page.IsLoading) && !page.IsLoading)
                        {
                            done.Set();
                        }
                    };
                    var content = page.GetContent().OfType<IMarkdownContent>().Single();
                    Expect(done.Wait(TimeSpan.FromSeconds(25)), "online translation did not finish");
                    Expect(content.Body.StartsWith("**Google 翻译", StringComparison.Ordinal), content.Body);
                    Expect(page.Commands.OfType<ICommandContextItem>().First().Command.Name == "复制译文", "Enter did not switch to copy");
                    var translated = content.Body.Split("\n\n")[1];
                    Expect(HasCjk(translated), "translation was not Chinese");
                    Expect(!hub.AiEnabled && !hub.Ai.IsBusy, "online translation used the AI service");
                    summaries.Add(sentence + " → " + translated);
                }

                return string.Join(" | ", summaries);
            }
            finally
            {
                SubmitSentenceSettings(provider, """{"ai.sentenceTranslation":"false","ai.enabled":"true","ai.claudePath":""}""");
            }
        });

        r.Step("online translation: live Google, Chinese → English and German", () =>
        {
            try
            {
                SubmitSentenceSettings(provider, """{"ai.sentenceTranslation":"true","ai.enabled":"false","ai.claudePath":"C:\\lexi-not-used\\claude.exe"}""");
                using var page = new TranslationResultPage(hub, LiveChineseInput, [GoogleTranslator.English, GoogleTranslator.German]);
                using var done = new ManualResetEventSlim();
                page.PropChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(page.IsLoading) && !page.IsLoading)
                    {
                        done.Set();
                    }
                };
                var content = page.GetContent().OfType<IMarkdownContent>().Single();
                Expect(done.Wait(TimeSpan.FromSeconds(40)), "online translation did not finish");
                var body = content.Body;
                Expect(body.Contains("中文 → 英语**", StringComparison.Ordinal) && body.Contains("中文 → 德语**", StringComparison.Ordinal) && !body.Contains("⚠️", StringComparison.Ordinal), Snippet(body, 300));
                var names = page.Commands.OfType<ICommandContextItem>().Select(c => c.Command.Name).ToList();
                Expect(names.Take(2).SequenceEqual(["复制英语译文", "复制德语译文"]), $"commands: {string.Join(", ", names)}");
                Expect(!hub.AiEnabled && !hub.Ai.IsBusy, "online translation used the AI service");
                var sections = body.Split("\n\n");
                return $"{LiveChineseInput} → {sections[1]} | {sections[3]}";
            }
            finally
            {
                SubmitSentenceSettings(provider, """{"ai.sentenceTranslation":"false","ai.enabled":"true","ai.claudePath":""}""");
            }
        });
    }
}
