using Lexi.AI;
using Lexi.Core.Models;
using Lexi.Core.Morphology;
using Lexi.Dictionary.Import;
using Lexi.Dictionary.Storage;

namespace Lexi.Tests;

public class EntryCodecTests
{
    [Fact]
    public void Round_trips_compressed_and_raw_entries()
    {
        var big = new EntryData
        {
            Headword = "Schwachstelle",
            Pos = "noun",
            Grammar = new Grammar { Gender = "f", Plural = ["Schwachstellen"] },
            Senses = Enumerable.Range(1, 20).Select(i => new Sense { Number = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Gloss = "der Punkt, an dem etwas verletzlich ist " + i, GlossLang = "de" }).ToList(),
        };
        var small = new EntryData { Headword = "a" };

        foreach (var e in new[] { big, small })
        {
            var decoded = EntryCodec.Decode(EntryCodec.Encode(e));
            Assert.Equal(e.Headword, decoded.Headword);
            Assert.Equal(e.Senses?.Count, decoded.Senses?.Count);
            Assert.Equal(e.Grammar?.Plural, decoded.Grammar?.Plural);
        }

        Assert.True(EntryCodec.Encode(big).Length < System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(big.Senses!.Count).Length + 2000);
    }

    [Fact]
    public void Reused_buffers_preserve_payloads_of_different_sizes()
    {
        var blobs = new List<(string Gloss, byte[] Blob)>();
        foreach (var size in new[] { 5000, 10, 200_000, 120, 1000 })
        {
            var gloss = new string('中', size) + size;
            var entry = new EntryData { Headword = "test", Senses = [new Sense { Gloss = gloss, GlossLang = "zh" }] };
            blobs.Add((gloss, EntryCodec.Encode(entry)));
        }

        // Decode out of order after further encodes have reused the temporary buffers.
        foreach (var (gloss, blob) in blobs.AsEnumerable().Reverse())
        {
            Assert.Equal(gloss, Assert.Single(EntryCodec.Decode(blob).Senses!).Gloss);
        }
    }
}

public class FormTests
{
    [Theory]
    [InlineData("nominative plural", Lang.De, "复数 Plural")]
    [InlineData("accusative genitive nominative plural", Lang.De, "复数 Plural")]
    [InlineData("genitive singular", Lang.De, "单数属格 Genitiv Sg.")]
    [InlineData("past", Lang.De, "过去时 Präteritum")]
    [InlineData("participle-2 perfect", Lang.De, "过去分词 Partizip II")]
    [InlineData("comparative", Lang.De, "比较级 Komparativ")]
    [InlineData("past", Lang.En, "过去式")]
    [InlineData("participle past", Lang.En, "过去分词")]
    [InlineData("present singular third-person", Lang.En, "第三人称单数")]
    [InlineData("plural", Lang.En, "复数")]
    public void Describes_form_tags(string tags, Lang lang, string expected)
        => Assert.Equal(expected, FormDescriber.Describe(tags, lang));

    [Theory]
    [InlineData("du gehst", null, "gehst")]
    [InlineData("er/sie/es geht", null, "geht")]
    [InlineData("geh!", null, "geh")]
    [InlineData("ich bin gegangen", null, null)]
    [InlineData("zu gehen", null, null)]
    [InlineData("ich fange an", "an", "fange an")]
    [InlineData("am besten", null, "am besten")]
    [InlineData("zu gehende …", null, null)]
    public void Cleans_german_conjugation_table_cells(string cell, string? separable, string? expected)
        => Assert.Equal(expected, KaikkiImporter.CleanGermanForm(cell, separable));

    [Fact]
    public void Zh_gloss_cleanup_extracts_gender_and_meaning()
    {
        var (gloss, gender) = TextCleanup.CleanZhGloss("〈阳〉 pl.Hunde 狗");
        Assert.Equal("狗", gloss);
        Assert.Equal("m", gender);
    }

    [Fact]
    public void Zh_example_lines_are_recognized()
    {
        Assert.True(TextCleanup.TrySplitExample("Morgen fange ich an.\u3000明天开始。", out var text, out var tr));
        Assert.Equal("Morgen fange ich an.", text);
        Assert.Equal("明天开始。", tr);
        Assert.False(TextCleanup.TrySplitExample("开始，开始从事", out _, out _));
    }

    [Fact]
    public void Rank_to_zipf_is_monotonic()
    {
        Assert.True(EcdictImporter.RankToZipf(1) > EcdictImporter.RankToZipf(1000));
        Assert.True(EcdictImporter.RankToZipf(1000) > EcdictImporter.RankToZipf(50_000));
        Assert.Equal(0, EcdictImporter.RankToZipf(0));
    }
}

public class ClaudeCodeProviderTests
{
    [Fact]
    public void Arguments_disable_tools_and_persistence_and_omit_model_by_default()
    {
        var args = ClaudeCodeProvider.BuildArguments(new ClaudeCodeOptions()).ToList();
        Assert.Equal("-p", args[0]);
        var tools = args.IndexOf("--tools");
        Assert.True(tools >= 0);
        Assert.Equal(string.Empty, args[tools + 1]);
        Assert.Contains("--no-session-persistence", args);
        Assert.Contains("stream-json", args);
        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public void Arguments_pass_model_and_effort_when_configured()
    {
        var args = ClaudeCodeProvider.BuildArguments(new ClaudeCodeOptions { Model = "sonnet", Effort = "low" }).ToList();
        Assert.Equal("sonnet", args[args.IndexOf("--model") + 1]);
        Assert.Equal("low", args[args.IndexOf("--effort") + 1]);
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"init","model":"claude-x"}""", "init", null, "claude-x", false)]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"弱点"}}}""", "delta", "弱点", null, false)]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"result":"完成"}""", "result", "完成", null, false)]
    [InlineData("""{"type":"result","is_error":true,"result":"Failed to authenticate"}""", "result", "Failed to authenticate", null, true)]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"..."}}}""", "other", null, null, false)]
    [InlineData("not json", "other", null, null, false)]
    public void Parses_stream_json_events(string line, string type, string? text, string? model, bool isError)
    {
        var ev = ClaudeCodeProvider.StreamEvent.Parse(line);
        Assert.Equal(type, ev.Type);
        Assert.Equal(text, ev.Text);
        Assert.Equal(model, ev.Model);
        Assert.Equal(isError, ev.IsError);
    }

    [Fact]
    public void Prompt_includes_dictionary_context_and_comparison_words()
    {
        var prompt = PromptTemplates.Build(new AiRequest(AiTaskKind.CompareWords, "Schwachstelle", "德语", "词条：die Schwachstelle", ["Bedrohung", "Angriff"], "IT Security"));
        Assert.Contains("「Schwachstelle」", prompt, StringComparison.Ordinal);
        Assert.Contains("「Bedrohung」", prompt, StringComparison.Ordinal);
        Assert.Contains("「Angriff」", prompt, StringComparison.Ordinal);
        Assert.Contains("IT Security", prompt, StringComparison.Ordinal);
        Assert.Contains("词条：die Schwachstelle", prompt, StringComparison.Ordinal);
        Assert.Contains("不要编造音标", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_service_reports_missing_cli_without_throwing_other_errors()
    {
        var service = new AiService(() => new ClaudeCodeProvider(new ClaudeCodeOptions { ExecutablePath = @"C:\nope\claude.exe" }), Path.Combine(Path.GetTempPath(), "lexi-ai-test-" + Guid.NewGuid().ToString("N")));
        var ex = await Assert.ThrowsAsync<AiException>(() => service.RunAsync(new AiRequest(AiTaskKind.ExplainWord, "Haus"), false, null, CancellationToken.None));
        Assert.Contains("Claude Code", ex.Message, StringComparison.Ordinal);
    }
}

public class WordViewTests
{
    private static readonly SourceInfo WiktDe = new(2, "wikt-de", "Wiktionary (Deutsch)", "CC BY-SA 4.0", "https://de.wiktionary.org", null, "Wiktionary contributors", true);

    [Fact]
    public void Sense_aligned_translations_list_each_equivalent_once()
    {
        var data = new EntryData
        {
            Headword = "Haus",
            Pos = "noun",
            Senses =
            [
                new Sense { Number = "1", Gloss = "Gebäude zum Wohnen", GlossLang = "de", Zh = ["房子"], En = ["house"] },
                new Sense { Number = "2", Gloss = "Bewohner eines Hauses", GlossLang = "de", Zh = ["房子"], En = ["house", "household"] },
                new Sense { Number = "3", Gloss = "Familie, Dynastie", GlossLang = "de", Zh = ["家族"], En = ["house"] },
            ],
        };
        var word = new Lexi.Core.Search.WordRecord(1, Lang.De, "Haus", "haus", 5f, Lexi.Core.Search.WordFlags.None, "das", null);

        var view = Lexi.Dictionary.View.WordViewBuilder.Build(word, [new StoredEntry(WiktDe, 0, "noun", data)], []);

        Assert.Equal(["房子", "家族"], view.Chinese.Single(b => b.Kind == "translation").Senses.Select(s => s.Gloss));
        Assert.Equal(["house", "house, household"], view.English.Single(b => b.Kind == "translation").Senses.Select(s => s.Gloss));
        Assert.Equal(3, view.German.Single().Senses.Count);
    }

    [Fact]
    public void Full_entry_shows_examples_inside_the_sense_and_skips_synonym_notes()
    {
        var wiktEn = new SourceInfo(3, "wikt-en", "Wiktionary (English edition)", "CC BY-SA 4.0", "https://en.wiktionary.org", null, "Wiktionary contributors", true);
        var data = new EntryData
        {
            Headword = "vulnerability",
            Pos = "noun",
            Senses =
            [
                new Sense
                {
                    Gloss = "The state of being vulnerable.",
                    GlossLang = "en",
                    Examples = [new Example { Text = "Near-synonym: weakness" }, new Example { Text = "The update fixes a vulnerability. " }],
                },
            ],
        };
        var word = new Lexi.Core.Search.WordRecord(2, Lang.En, "vulnerability", "vulnerability", 4f, Lexi.Core.Search.WordFlags.None, "n.", null);

        var markdown = Lexi.Dictionary.View.MarkdownRenderer.Full(Lexi.Dictionary.View.WordViewBuilder.Build(word, [new StoredEntry(wiktEn, 0, "noun", data)], []));

        Assert.Contains("1. The state of being vulnerable.  \n   *The update fixes a vulnerability.*\n", markdown.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("Near-synonym", markdown, StringComparison.Ordinal);
    }
}
