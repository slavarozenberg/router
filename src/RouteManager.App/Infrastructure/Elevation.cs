using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace RouteManager.App.Infrastructure;

public static class Elevation
{
    public const string RestartArgument = "--elevated-restart";

    public static bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Starts an elevated copy of this app. Returns false if the user declined the UAC prompt.</summary>
    public static bool TryRestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, RestartArgument)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }
}
