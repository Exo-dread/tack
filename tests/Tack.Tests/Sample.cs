using System.IO;
using System.Text.Json;
using Tack.Model;
using Tack.Services;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Not a test: a workspace with three spaces, written as a tack.json, for looking at the panel by
/// hand (Tack.Tests.exe --sample &lt;TackData\tack.json&gt; [light|dark]). Put it in a test copy's
/// TackData folder; never in your own.
/// </summary>
internal static class Sample
{
    public static int Write(string path, string theme)
    {
        // No shortcut (a test copy shouldn't take a key from your own Tack), solid and pinned, so screenshots are steady.
        var settings = new Settings { Theme = theme, Glass = false, Pinned = true, DoneExpanded = true, Hotkey = "" };
        var ws = new Workspace(settings, new Hlc("sample01"));
        var work = ws.AddSpace("Work");
        var dev = ws.AddSpace("Dev");
        var home = ws.AddSpace("Personal");

        ws.AddTask("Renew the domain", space: home.Id);
        ws.AddTask("Book the dentist", space: home.Id);
        var groceries = ws.AddTask("Groceries for the weekend", space: home.Id);
        ws.AddTask("Write the release notes", space: dev.Id);
        ws.AddTask("Fix the flaky sync test", space: dev.Id);
        ws.AddTask("Send the clash report to the MEP team", space: work.Id);
        ws.AddTask("Update the BIM execution plan", space: work.Id);
        var federated = ws.AddTask("Federate the latest structural model", space: work.Id);
        ws.AddTask("Back up the laptop");
        ws.AddTask("Answer the landlord");
        ws.AddTask("Review the coordination clashes", asNow: true, space: work.Id);
        ws.Complete(federated);
        ws.Complete(groceries);

        var main = ws.Sheets[0];
        main.Text = "Ideas\r\n- a reading list\r\n- sort the photos";
        ws.AddSheet(work.Id, "Coordination meeting\r\n- level 3 ducts vs beams\r\n- next model drop: Friday");
        ws.AddSheet(home.Id, "Weekend\r\n- market\r\n- call Mum");
        ws.ActiveSheet = main;
        settings.SpaceOnShow = SpaceView.All;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(StateFile.ToDocument(ws), StateJsonContext.Default.StateDocument));
        Console.WriteLine($"Wrote a sample workspace with three spaces to {path}");
        return 0;
    }
}
