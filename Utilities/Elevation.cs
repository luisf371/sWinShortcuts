using System.Security.Principal;

namespace sWinShortcuts.Utilities;

/// <summary>
/// Reports whether the current process is running with administrator (elevated) privileges.
/// Used to gray out the "Start as administrator" startup option when the app is not elevated —
/// a non-admin user cannot create or remove the HIGHEST scheduled task that option relies on.
/// </summary>
public static class Elevation
{
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // If the identity cannot be determined, assume non-admin (the safe/conservative choice for
            // gating an option that requires elevation).
            return false;
        }
    }
}
