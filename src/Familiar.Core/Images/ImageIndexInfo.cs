namespace Familiar.Core.Images;

/// <summary>One index inside a WIM or ESD file.</summary>
public sealed record ImageIndexInfo(
    int Index,
    string Name,
    string EditionId,
    string InstallationType,
    string Architecture,
    Version Version,
    string? DefaultLanguage,
    IReadOnlyList<string> Languages);

/// <summary>The Windows Recovery image inside an index.</summary>
public sealed record WinREInfo(Version Version, string? DefaultLanguage, IReadOnlyList<string> Languages, long SizeBytes);
