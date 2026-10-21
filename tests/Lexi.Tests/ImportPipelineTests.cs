using System.IO.Compression;
using System.Text;
using Lexi.Core.Models;
using Lexi.Core.Search;
using Lexi.Dictionary.Import;
using Lexi.Dictionary.Storage;
using Lexi.Dictionary.View;

namespace Lexi.Tests;

/// <summary>
/// Runs the real import pipeline on tiny fixture files in the exact formats of the upstream sources
/// (ECDICT CSV, wiktextract JSONL.gz of the en/de/zh Wiktionary editions, FrequencyWords lists).
/// </summary>
public sealed class ImportPipelineTests : IClassFixture<ImportPipelineTests.Fixture>
{
    private readonly Fixture _f;

    public ImportPipelineTests(Fixture fixture) => _f = fixture;

    [Fact]
    public void Database_has_sources_words_and_fuzzy_index()
    {
        Assert.Equal(Schema.Version, _f.Db.Info.SchemaVersion);
        Assert.Contains(_f.Db.Sources, s => s.Code == "ecdict" && !s.Redistributable);
        Assert.Contains(_f.Db.Sources, s => s.Code == "wikt-de" && s.Redistributable);
        Assert.NotNull(_f.Db.GetFuzzyIndex());
    }

    [Theory]
    [InlineData("Schwachstellen", "Schwachstelle", MatchKind.Form)]
    [InlineData("Schwachstele", "Schwachstelle", MatchKind.Fuzzy)]
    [InlineData("ging", "gehen", MatchKind.Form)]
    [InlineData("gehst", "gehen", MatchKind.Form)] // from the full conjugation table, pronoun stripped
    [InlineData("went", "go", MatchKind.Form)]
    [InlineData("children", "child", MatchKind.Form)]
    [InlineData("Häuser", "Haus", MatchKind.Form)]
    [InlineData("Uebertragung", "Übertragung", MatchKind.Translit)]
    public void Search_over_imported_data(string query, string expected, MatchKind kind)
    {
        var hit = new SearchEngine(_f.Db).Search(new SearchRequest { Text = query }, TestContext.Current.CancellationToken).Hits[0];
        Assert.Equal(expected, hit.Word.Word);
        Assert.Equal(kind, hit.Kind);
    }

    [Fact]
    public void Machine_generated_ecdict_inflection_without_shared_prefix_is_rejected()
    {
        // ECDICT lists "ging" as a present participle of "ge"; that link must not be imported.
        var forms = _f.Db.GetFormsByFold("ging", null);
        Assert.DoesNotContain(forms, f => f.Lemma.Word == "ge");
        Assert.Contains(forms, f => f.Lemma.Word == "gehen");
    }

    [Fact]
    public void Compound_tenses_are_not_indexed_as_forms()
        => Assert.Empty(_f.Db.GetFormsByFold("bin gegangen", null));

    [Fact]
    public void German_noun_view_has_grammar_chinese_and_sources()
    {
        var word = _f.Db.GetWordsByFold("schwachstelle", Lang.De).Single();
        Assert.Equal("die", word.Badge);
        var view = WordViewBuilder.Build(_f.Db, word);
        Assert.Equal("die Schwachstelle", view.Title);
        Assert.Contains(view.Grammar, g => g.Label.StartsWith("复数", StringComparison.Ordinal) && g.Value.Contains("Schwachstellen", StringComparison.Ordinal));
        Assert.NotNull(view.Cases);

        // zh.wiktionary gloss "〈阴〉 pl.Schwachstellen 缺点，弱点" is cleaned to the meaning only.
        var zh = view.Chinese.First(b => b.Source.Code == "wikt-zh");
        Assert.Equal("缺点，弱点", zh.Senses[0].Gloss);
        Assert.Contains(view.English, b => b.Senses.Any(s => s.Gloss == "vulnerability"));
        Assert.Contains(view.Sources, s => s.Code == "wikt-de");
        Assert.Contains(view.RawRefs, r => r.StartsWith("kaikki:dewiktionary:Schwachstelle", StringComparison.Ordinal));
    }

    [Fact]
    public void German_verb_view_has_principal_parts_and_auxiliary()
    {
        var word = _f.Db.GetWordsByFold("gehen", Lang.De).Single();
        var view = WordViewBuilder.Build(_f.Db, word);
        Assert.Contains(view.Grammar, g => g.Label.StartsWith("过去时", StringComparison.Ordinal) && g.Value == "ging");
        Assert.Contains(view.Grammar, g => g.Label.StartsWith("过去分词", StringComparison.Ordinal) && g.Value == "gegangen");
        Assert.Contains(view.Grammar, g => g.Label.StartsWith("助动词", StringComparison.Ordinal) && g.Value == "sein");
    }

    [Fact]
    public void English_view_prefers_wiktionary_ipa_with_region_and_ecdict_chinese()
    {
        var word = _f.Db.GetWordsByFold("authentication", Lang.En).Single();
        var view = WordViewBuilder.Build(_f.Db, word);
        Assert.Equal("UK", view.Ipa[0].Region);
        Assert.Equal("wikt-en", view.Ipa[0].Source.Code);
        Assert.Contains(view.Chinese, b => b.Source.Code == "ecdict" && b.Senses.Any(s => s.Gloss!.Contains("[计] 验证", StringComparison.Ordinal)));
        var md = MarkdownRenderer.Full(view);
        Assert.Contains("ECDICT", md, StringComparison.Ordinal);
        Assert.Contains("Wiktionary", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Traditional_chinese_is_converted_to_simplified_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "LCMapStringEx is Windows-only");
        var word = _f.Db.GetWordsByFold("bedrohung", Lang.De).Single();
        var view = WordViewBuilder.Build(_f.Db, word);
        Assert.Equal("威胁、胁迫", view.Chinese[0].Senses[0].Gloss);
    }

    public sealed class Fixture : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lexi-test-" + Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            var raw = Path.Combine(_dir, "raw");
            Write(raw, "ecdict/ecdict.csv", """
                word,phonetic,definition,translation,pos,collins,oxford,tag,bnc,frq,exchange,detail,audio
                authentication,ɒ:θenti'keiʃәn,n. validating the authenticity of something or someone,"n. 证明可信, 证实为真\n[计] 验证",,,,,35132,25693,,,
                go,gou,v. move away from a place,"vi. 去, 走",,5,1,zk gk,44,35,i:going/p:went/d:gone/3:goes,,
                went,went,,v. go的过去式,,,,,100,100,0:go/1:p,,
                child,tʃaild,n. a young person,n. 孩子,,5,1,zk,500,400,s:children,,
                children,'tʃildrәn,,"pl. 孩子, 孩子们",,,,,0,0,0:child/1:s,,
                ge,dʒi:,,n. 通用电气公司,,,,,0,0,i:ging,,
                """);
            Write(raw, "freq/en_full.txt", "go 50000\nwent 30000\nchild 20000\nchildren 25000\nauthentication 300\nging 5\nge 100\n");
            Write(raw, "freq/de_full.txt", "haus 50000\nhäuser 8000\ngehen 40000\nging 30000\ngehst 2000\nschwachstelle 300\nschwachstellen 200\nbedrohung 400\nübertragung 900\n");

            WriteGz(raw, "kaikki/kaikki.org-dictionary-English.jsonl.gz",
                """{"word": "authentication", "pos": "noun", "lang": "English", "lang_code": "en", "sounds": [{"ipa": "/ɔːˌθɛntɪˈkeɪʃn̩/", "tags": ["Received-Pronunciation"]}], "senses": [{"glosses": ["Proof of identity required by a user to log on."], "topics": ["computing", "engineering"]}], "forms": [{"form": "authentications", "tags": ["plural"]}]}""",
                """{"word": "went", "pos": "verb", "lang": "English", "lang_code": "en", "senses": [{"glosses": ["simple past of go"], "tags": ["form-of", "past"], "form_of": [{"word": "go"}]}]}""",
                """{"word": "go", "pos": "verb", "lang": "English", "lang_code": "en", "senses": [{"glosses": ["To move from one place to another."]}], "forms": [{"form": "goes", "tags": ["present", "singular", "third-person"]}, {"form": "went", "tags": ["past"]}, {"form": "gone", "tags": ["participle", "past"]}]}""");

            WriteGz(raw, "kaikki/kaikki.org-dictionary-German.jsonl.gz",
                """{"word": "Schwachstelle", "pos": "noun", "lang": "German", "lang_code": "de", "senses": [{"glosses": ["weakness, weak point"], "tags": ["feminine"]}, {"glosses": ["vulnerability"], "tags": ["feminine"]}], "forms": [{"form": "Schwachstellen", "tags": ["plural"]}]}""",
                """{"word": "Häuser", "pos": "noun", "lang": "German", "lang_code": "de", "senses": [{"glosses": ["plural of Haus"], "tags": ["form-of", "plural"], "form_of": [{"word": "Haus"}]}]}""",
                """{"word": "Haus", "pos": "noun", "lang": "German", "lang_code": "de", "senses": [{"glosses": ["house, building"], "tags": ["neuter"]}], "forms": [{"form": "Häuser", "tags": ["plural"]}]}""");

            WriteGz(raw, "kaikki/de-extract.jsonl.gz",
                """{"word": "Schwachstelle", "pos": "noun", "pos_title": "Substantiv", "lang": "Deutsch", "lang_code": "de", "tags": ["feminine"], "senses": [{"glosses": ["der Punkt, an dem etwas verletzlich ist"], "sense_index": "1"}], "translations": [{"word": "weak point", "lang_code": "en", "sense_index": "1"}], "sounds": [{"ipa": "[ˈʃvaxˌʃtɛlə]"}, {"audio": "De-Schwachstelle.ogg", "mp3_url": "https://upload.wikimedia.org/x.mp3"}], "forms": [{"form": "Schwachstelle", "tags": ["nominative", "singular"], "article": "die"}, {"form": "Schwachstellen", "tags": ["nominative", "plural"], "article": "die"}, {"form": "Schwachstelle", "tags": ["genitive", "singular"], "article": "der"}, {"form": "Schwachstellen", "tags": ["genitive", "plural"], "article": "der"}]}""",
                """{"word": "gehen", "pos": "verb", "pos_title": "Verb", "lang": "Deutsch", "lang_code": "de", "senses": [{"glosses": ["sich schreitend fortbewegen"], "sense_index": "1"}], "translations": [{"word": "走", "lang_code": "zh", "sense_index": "1"}, {"word": "walk", "lang_code": "en", "sense_index": "1"}], "forms": [{"form": "gehe", "tags": ["present"]}, {"form": "gehst", "tags": ["present"]}, {"form": "geht", "tags": ["present"]}, {"form": "ging", "tags": ["past"]}, {"form": "gegangen", "tags": ["participle-2", "perfect"]}, {"form": "sein", "tags": ["auxiliary", "perfect"]}, {"form": "du gehst", "tags": ["second-person", "singular", "present"], "source": "Flexion:gehen"}, {"form": "ich bin gegangen", "tags": ["first-person", "singular", "perfect"], "source": "Flexion:gehen"}]}""",
                """{"word": "ging", "pos": "verb", "pos_title": "Konjugierte Form", "lang": "Deutsch", "lang_code": "de", "tags": ["form-of"], "senses": [{"glosses": ["1. Person Singular Präteritum von gehen"], "tags": ["form-of", "past", "singular"], "form_of": [{"word": "gehen"}]}]}""",
                """{"word": "Haus", "pos": "noun", "pos_title": "Substantiv", "lang": "Deutsch", "lang_code": "de", "tags": ["neuter"], "senses": [{"glosses": ["Gebäude"], "sense_index": "1"}], "translations": [{"word": "房子", "lang_code": "zh", "sense_index": "1"}]}""",
                """{"word": "Häuser", "pos": "noun", "pos_title": "Deklinierte Form", "lang": "Deutsch", "lang_code": "de", "tags": ["form-of"], "senses": [{"glosses": ["Nominativ Plural des Substantivs Haus"], "tags": ["form-of", "nominative", "plural"], "form_of": [{"word": "Haus"}]}]}""",
                """{"word": "Übertragung", "pos": "noun", "pos_title": "Substantiv", "lang": "Deutsch", "lang_code": "de", "tags": ["feminine"], "senses": [{"glosses": ["Sendung, Übermittlung"], "sense_index": "1"}]}""",
                """{"word": "house", "pos": "noun", "lang": "Englisch", "lang_code": "en", "senses": [{"glosses": ["Haus"]}]}""");

            WriteGz(raw, "kaikki/zh-extract.jsonl.gz",
                """{"word": "Schwachstelle", "pos": "noun", "lang": "德语", "lang_code": "de", "senses": [{"glosses": ["〈阴〉 pl.Schwachstellen 缺点，弱点"]}]}""",
                """{"word": "Bedrohung", "pos": "noun", "lang": "德語", "lang_code": "de", "tags": ["feminine"], "senses": [{"glosses": ["威脅、脅迫"]}]}""");

            var output = Path.Combine(_dir, "lexi.db");
            LexiImporter.Run(new ImportOptions { RawDirectory = raw, OutputPath = output, Log = _ => { } });
            Db = LexiDatabase.Open(output);
        }

        public LexiDatabase Db { get; }

        public void Dispose()
        {
            Db.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static void Write(string root, string relative, string content)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
        }

        private static void WriteGz(string root, string relative, params string[] lines)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var gz = new GZipStream(File.Create(path), CompressionLevel.Fastest);
            var bytes = new UTF8Encoding(false).GetBytes(string.Join("\n", lines) + "\n");
            gz.Write(bytes);
        }
    }
}
