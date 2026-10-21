namespace Lexi.Core.Models;

/// <summary>Provenance of dictionary content. Shown in the UI for every block of dictionary data.</summary>
public sealed record SourceInfo(
    int Id,
    string Code,
    string Name,
    string License,
    string Url,
    string? Version,
    string Attribution,
    bool Redistributable);
