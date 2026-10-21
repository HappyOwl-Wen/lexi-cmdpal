# Third-party notices

## Software dependencies (bundled in the extension package)

| Component | License | Source |
|---|---|---|
| Microsoft.CommandPalette.Extensions (+ Toolkit) 0.11.260520004 | MIT | https://github.com/microsoft/PowerToys |
| Microsoft.Windows.CsWinRT 2.2.0 / WinRT.Runtime | MIT | https://github.com/microsoft/CsWinRT |
| Microsoft.Windows.SDK.NET (projection) | MIT | https://github.com/microsoft/CsWinRT |
| Shmuelie.WinRTServer 2.1.1 | MIT | https://github.com/shmuelie/Shmuelie.WinRTServer |
| Microsoft.Data.Sqlite 10.0.13 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw (bundle_e_sqlite3) | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite | Public domain | https://sqlite.org |
| .NET runtime (self-contained) | MIT | https://github.com/dotnet/runtime |

Build-time only: Microsoft.Windows.SDK.BuildTools / BuildTools.MSIX (Microsoft Software License Terms), xunit.v3 (Apache-2.0), Microsoft.NET.Test.Sdk (MIT).

## Dictionary data (built locally, not committed, not bundled by default)

- **Wiktionary** (English, German and Chinese editions) — text available under the Creative Commons Attribution-ShareAlike 4.0 License (and GFDL). © Wiktionary contributors. Machine-readable extraction by **Kaikki.org / wiktextract** (Tatu Ylonen: *Wiktextract: Wiktionary as Machine-Readable Structured Data*, LREC 2022). Databases built from these sources (including redistributable builds made with `--no-ecdict`) are adaptations and must be shared under CC BY-SA 4.0 with this attribution.
- **ECDICT** — https://github.com/skywind3000/ECDICT, © Linwei (skywind3000) and contributors. Repository license MIT; the README states the data was compiled from several third-party sources whose terms are not documented per entry. Lexi therefore treats ECDICT content as *not confirmed redistributable*: it is imported only into the user's own local database and never bundled.
- **FrequencyWords** — https://github.com/hermitdave/FrequencyWords, © Hermit Dave, content CC BY-SA 4.0 (derived from OPUS OpenSubtitles 2018). Used for ranking only.
- **Wikimedia Commons** audio — each file has its own license, shown by file name in Lexi; files are fetched on demand when the user plays a pronunciation and cached locally.

## AI

AI features call the user's own locally installed **Claude Code** CLI (`claude -p`). Lexi does not ship, store or read any credentials; requests run under the user's Claude Code configuration and usage terms.
