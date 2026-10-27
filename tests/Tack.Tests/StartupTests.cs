using System.IO;
using Tack.Services;

namespace Tack.Tests;

/// <summary>
/// Launch at startup: the HKCU Run value "Tack" is shared by every copy of Tack (installed, portable, test copies), so a
/// copy only ever removes it when it names that copy. Pure checks; the registry itself is never touched here.
/// </summary>
internal static class StartupTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("startup: a copy with launch at startup off removes the Run value only when it names that copy (or a Tack that's gone)", OnlyItsOwn);
    }

    private static void OnlyItsOwn()
    {
        string folder = Path.Combine(Path.GetTempPath(), "tack-startup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "installed"));
        Directory.CreateDirectory(Path.Combine(folder, "portable"));
        try
        {
            string installed = Path.Combine(folder, "installed", "Tack.exe");
            string portable = Path.Combine(folder, "portable", "Tack.exe");
            File.WriteAllBytes(installed, []);
            File.WriteAllBytes(portable, []);

            Check.True(StartupRegistration.MayRemove($"\"{portable}\" --startup", portable), "its own value: removed");
            Check.True(StartupRegistration.MayRemove($"\"{portable.ToUpperInvariant()}\" --startup", portable), "whatever the case of its path");
            Check.True(!StartupRegistration.MayRemove($"\"{installed}\" --startup", portable), "the installed copy's: kept");
            Check.True(!StartupRegistration.MayRemove($"{installed} --startup", portable), "unquoted, still another copy's: kept");
            Check.True(StartupRegistration.MayRemove($"\"{Path.Combine(folder, "moved", "Tack.exe")}\" --startup", portable), "a Tack that isn't there any more: removed");
            Check.True(!StartupRegistration.MayRemove(null, portable), "no value: nothing to remove");
            Check.True(!StartupRegistration.MayRemove("\"\" --startup", portable), "a damaged value naming nothing: left alone");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
