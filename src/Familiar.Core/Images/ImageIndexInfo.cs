namespace Familiar.Core.Images;

/// <summary>One index inside a WIM or ESD file.</summary>
public sealed record ImageIndexInfo(
    int Index,
    string Name,
    string EditionId,
    string InstallationType,
    string Architecture,
    Version Version,
    string? DefaultLanguage);
