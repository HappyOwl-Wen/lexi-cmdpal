namespace Lexi.Dictionary.Storage;

/// <summary>SQLite schema of lexi.db. Bump <see cref="Version"/> on incompatible changes.</summary>
internal static class Schema
{
    public const int Version = 1;

    public const string CreateTables = """
        CREATE TABLE meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE sources (
            id              INTEGER PRIMARY KEY,
            code            TEXT NOT NULL UNIQUE,
            name            TEXT NOT NULL,
            license         TEXT NOT NULL,
            url             TEXT NOT NULL,
            version         TEXT,
            attribution     TEXT NOT NULL,
            redistributable INTEGER NOT NULL
        );

        -- One row per headword (lemma) and language: the unit of search results.
        CREATE TABLE words (
            id    INTEGER PRIMARY KEY,
            lang  INTEGER NOT NULL,   -- 1 = en, 2 = de
            word  TEXT NOT NULL,      -- display headword
            fold  TEXT NOT NULL,      -- lower-case, diacritics removed, ß -> ss
            zipf  REAL NOT NULL,      -- popularity on the Zipf scale (0..8)
            flags INTEGER NOT NULL,   -- WordFlags
            badge TEXT,               -- short grammar badge: der/die/das, n., v. ...
            brief TEXT                -- one-line gloss for the result list
        );

        -- Per-source entries, never merged at import time so every field stays attributable.
        CREATE TABLE entries (
            word_id   INTEGER NOT NULL,
            source_id INTEGER NOT NULL,
            seq       INTEGER NOT NULL,
            pos       TEXT,
            data      BLOB NOT NULL,  -- EntryCodec: Brotli-compressed JSON (EntryData)
            PRIMARY KEY (word_id, source_id, seq)
        ) WITHOUT ROWID;

        CREATE TABLE form_tags (
            id   INTEGER PRIMARY KEY,
            tags TEXT NOT NULL UNIQUE
        );

        -- Inflected / variant forms pointing at a lemma ("Häuser" -> Haus, "went" -> go).
        CREATE TABLE forms (
            fold      TEXT NOT NULL,
            form      TEXT NOT NULL,
            lemma_id  INTEGER NOT NULL,
            tags_id   INTEGER NOT NULL,
            source_id INTEGER NOT NULL,
            PRIMARY KEY (fold, lemma_id, form)
        ) WITHOUT ROWID;

        CREATE TABLE blobs (
            name TEXT PRIMARY KEY,
            data BLOB NOT NULL
        ) WITHOUT ROWID;
        """;

    /// <summary>Created after the bulk load (faster import).</summary>
    public const string CreateIndexes = """
        CREATE INDEX ix_words_fold ON words(fold, zipf, lang);
        CREATE INDEX ix_forms_lemma ON forms(lemma_id);
        """;
}
