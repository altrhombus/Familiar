namespace Familiar.Core.Servicing;

/// <summary>One change to apply to a mounted image. Plans are lists of these, so they can be shown before running.</summary>
public abstract record ServicingAction(string Description);

/// <summary>Adds a .cab or .msu package (language packs, cumulative updates, WinPE components).</summary>
public sealed record AddPackageAction(string PackagePath, string Description) : ServicingAction(Description);

/// <summary>Adds a Feature on Demand by capability name, resolving files from <paramref name="SourcePath"/>.</summary>
public sealed record AddCapabilityAction(string CapabilityName, string SourcePath, string Description) : ServicingAction(Description);

/// <summary>An ordered set of actions plus anything the user should know before running them.</summary>
public sealed record ServicingPlan(
    IReadOnlyList<ServicingAction> Actions,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
