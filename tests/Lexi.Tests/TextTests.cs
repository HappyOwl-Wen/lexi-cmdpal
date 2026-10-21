using Lexi.Core.Text;

namespace Lexi.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("Häuser", "häuser")]
    [InlineData("  Übertragung  ", "übertragung")]
    [InlineData("ＡＢＣ", "abc")] // full-width input from a Chinese IME
    [InlineData("ﬁnal", "final")] // ligature
    [InlineData("STRAẞE", "straße")]
    [InlineData("don’t", "don't")]
    [InlineData("weak   point", "weak point")]
    public void ToKey_normalizes_case_width_and_spacing_but_keeps_diacritics(string input, string expected)
        => Assert.Equal(expected, TextNormalizer.ToKey(input));

    [Theory]
    [InlineData("Häuser", "hauser")]
    [InlineData("Straße", "strasse")]
    [InlineData("Übertragung", "ubertragung")]
    [InlineData("café", "cafe")]
    [InlineData("Ærø", "aero")]
    [InlineData("vulnerability", "vulnerability")]
    public void ToFold_removes_diacritics(string input, string expected)
        => Assert.Equal(expected, TextNormalizer.ToFold(input));

    [Theory]
    [InlineData("Übertragung", "uebertragung")]
    [InlineData("Häuser", "haeuser")]
    [InlineData("Straße", "strasse")]
    [InlineData("schön", "schoen")]
    [InlineData("Haus", "haus")]
    public void ToTranslit_uses_german_digraphs(string input, string expected)
        => Assert.Equal(expected, TextNormalizer.ToTranslit(input));

    [Fact]
    public void Lone_surrogates_do_not_throw()
    {
        var broken = "a\uD800b";
        Assert.Equal("ab", TextNormalizer.ToKey(broken));
    }

    [Fact]
    public void Characters_outside_the_BMP_are_kept_when_folding()
        => Assert.Equal("x😀", TextNormalizer.ToFold("X😀"));

    [Fact]
    public void HasGermanLetters_detects_umlauts_and_eszett()
    {
        Assert.True(TextNormalizer.HasGermanLetters("Häuser"));
        Assert.True(TextNormalizer.HasGermanLetters("Straße"));
        Assert.False(TextNormalizer.HasGermanLetters("Haus"));
    }
}

public class EditDistanceTests
{
    [Theory]
    [InlineData("vulnerabilty", "vulnerability", 1)]
    [InlineData("authentcation", "authentication", 1)]
    [InlineData("schwachstele", "schwachstelle", 1)]
    [InlineData("teh", "the", 1)] // adjacent transposition costs 1
    [InlineData("kitten", "sitting", 3)]
    [InlineData("haus", "haus", 0)]
    [InlineData("", "abc", 3)]
    public void Osa_distance(string a, string b, int expected)
        => Assert.Equal(expected, EditDistance.Osa(a, b, 5));

    [Fact]
    public void Osa_stops_early_above_the_bound()
        => Assert.Equal(3, EditDistance.Osa("abcdef", "uvwxyz", 2));

    [Fact]
    public void Osa_handles_long_strings_without_stack_buffer()
    {
        var a = new string('a', 200) + "b";
        var b = new string('a', 200) + "c";
        Assert.Equal(1, EditDistance.Osa(a, b, 2));
    }
}

public class GermanTransliterationTests
{
    [Fact]
    public void Generates_umlaut_candidates_for_digraphs()
    {
        var variants = GermanTransliteration.ReverseVariants("uebertragung").ToList();
        Assert.Contains("ubertragung", variants);
    }

    [Fact]
    public void Generates_all_combinations()
    {
        var variants = GermanTransliteration.ReverseVariants("haeuser").ToList();
        Assert.Contains("hauser", variants);
        Assert.Single(variants);

        var two = GermanTransliteration.ReverseVariants("aeoe").ToList();
        Assert.Equal(3, two.Count); // a+oe, ae+o, a+o
    }

    [Fact]
    public void No_variants_without_digraphs()
        => Assert.Empty(GermanTransliteration.ReverseVariants("haus"));
}
