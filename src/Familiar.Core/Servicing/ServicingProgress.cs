namespace Familiar.Core.Servicing;

/// <summary>Progress for one servicing operation.</summary>
/// <param name="Operation">What is running, e.g. "Language pack de-DE".</param>
/// <param name="Percent">0-100, or <c>null</c> when the operation does not report progress.</param>
public sealed record ServicingProgress(string Operation, double? Percent)
{
    public static ServicingProgress Started(string operation) => new(operation, null);
}
