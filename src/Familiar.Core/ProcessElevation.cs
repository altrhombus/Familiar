using System.Security.Principal;

namespace Familiar.Core;

public static class ProcessElevation
{
    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
