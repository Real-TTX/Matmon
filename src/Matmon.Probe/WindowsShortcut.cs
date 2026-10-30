using System.Runtime.Versioning;

namespace Matmon.Probe;

/// <summary>
/// A Start menu entry (a .lnk) for Matmon's own Windows programs - the instance's tray (Matmon.Setup links this file) and the
/// agent's tray. WScript.Shell is on every Windows; IShellLink by hand would be a page of COM interop for one file.
/// Cosmetic: a failure is swallowed (the tray still starts at sign-in) and reported as false.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsShortcut
{
    /// <summary>The all-users Start menu folder (<c>%ProgramData%\Microsoft\Windows\Start Menu\Programs</c>).</summary>
    public static string StartMenuPath(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), name + ".lnk");

    public static bool Create(string path, string target, string arguments, string icon, string description, string? workingDirectory = null)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic link = shell.CreateShortcut(path);
                link.TargetPath = target;
                link.Arguments = arguments;
                link.WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(target) ?? string.Empty;
                link.IconLocation = icon;
                link.Description = description;
                link.Save();
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
                return true;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException
                                       or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind - harmless.
        }
    }
}
