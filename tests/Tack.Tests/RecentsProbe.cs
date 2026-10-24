using System.IO;
using Tack.Launcher;
using Tack.Launcher.Core;

namespace Tack.Tests;

/// <summary>
/// Not a test: reads this PC's recent files for some programs the way the launcher does, and says where they came
/// from (Tack.Tests.exe --recents &lt;program.exe&gt;…). Read-only. It prints counts, extensions and app IDs, never
/// file names or folders.
/// </summary>
internal static class RecentsProbe
{
    public static int Run(IEnumerable<string> programs)
    {
        var system = new WindowsRecentSystem();
        Console.WriteLine($"Windows keeps recent items: {system.TrackingAllowed}");
        foreach (string target in programs)
        {
            var app = new Shortcut { Kind = "app", Target = target, Label = Path.GetFileNameWithoutExtension(target) };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string? program = RecentReader.ProgramOf(app, system);
            Console.WriteLine($"== {target}");
            if (program == null) { Console.WriteLine("   not a program Tack can find"); continue; }
            foreach (string id in system.AppIdsFor(program, app))
            {
                var listed = system.JumpList(id).ToList();
                int there = listed.Select(RecentFiles.Clean).Count(p => p != null && system.Exists(p));
                Console.WriteLine($"   Jump List {id}: {listed.Count} listed, {there} there");
            }
            var own = AppMru.For(program, system).ToList();
            Console.WriteLine($"   app's own list: {own.Count} listed, {own.Select(RecentFiles.Clean).Count(p => p != null && system.Exists(p))} there");
            var picked = RecentReader.Read(app, system);
            Console.WriteLine($"   shown: {picked.Count} ({string.Join(" ", picked.Select(f => f.Extension.ToLowerInvariant()))}) in {clock.ElapsedMilliseconds} ms");
        }
        return 0;
    }
}
