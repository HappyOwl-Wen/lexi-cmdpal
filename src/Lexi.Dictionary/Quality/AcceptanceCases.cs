using Lexi.Core.Models;

namespace Lexi.Dictionary.Quality;

/// <summary>An input and the headword expected within the top <see cref="WithinTop"/> results.</summary>
public sealed record AcceptanceCase(string Query, string Expected, Lang Lang, int WithinTop, string Description);

/// <summary>
/// The acceptance list from the project brief. Evaluated against the real database by `lexi quality`
/// and by Lexi.Tests (integration); no search code special-cases these inputs.
/// </summary>
public static class AcceptanceCases
{
    public static IReadOnlyList<AcceptanceCase> All { get; } =
    [
        new("authentication", "authentication", Lang.En, 1, "exact English"),
        new("vulnerability", "vulnerability", Lang.En, 1, "exact English"),
        new("vulnerabilty", "vulnerability", Lang.En, 1, "typo: missing letter"),
        new("authentcation", "authentication", Lang.En, 1, "typo: missing letter"),
        // Completions are ranked by corpus frequency; authentication (COCA #25,693) follows the more common
        // authentic / authenticity / authenticate / authentically, so it is expected on the first screen, not first.
        new("authen", "authentication", Lang.En, 8, "prefix completion (first screen)"),
        new("Schwachstelle", "Schwachstelle", Lang.De, 1, "exact German"),
        new("Schwachstele", "Schwachstelle", Lang.De, 1, "typo: missing letter"),
        new("Schwachstellen", "Schwachstelle", Lang.De, 1, "plural → lemma"),
        new("Bedrohung", "Bedrohung", Lang.De, 1, "exact German"),
        new("Übertragung", "Übertragung", Lang.De, 1, "exact with umlaut"),
        new("Uebertragung", "Übertragung", Lang.De, 1, "ue → ü transliteration"),
        new("Häuser", "Haus", Lang.De, 1, "umlaut plural → lemma"),
        new("hauser", "Haus", Lang.De, 3, "plural without umlaut → Häuser → Haus"),
        new("gehen", "gehen", Lang.De, 1, "exact verb"),
        new("ging", "gehen", Lang.De, 1, "Präteritum → infinitive"),
    ];
}
