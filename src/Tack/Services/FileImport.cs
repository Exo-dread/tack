using System.IO;
using Tack.Model;
using Tack.Sync;

namespace Tack.Services;

/// <summary>
/// Settings → Import: merges a tack.json (exported from Tack Focus, copied from another PC, or a
/// backup) into the workspace. Anything new is added; for anything edited in both places the newer
/// edit wins; a note whose text differs is kept twice. Nothing is ever sent anywhere.
/// </summary>
internal static class FileImport
{
    private const long MaxBytes = 20 * 1024 * 1024;

    /// <summary>Imports a file. Returns what to tell the user, and how to undo it (null if nothing changed).</summary>
    public static (string Message, Action? Undo) Run(Workspace ws, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) return ("That file is too large to be a Tack file", null);
            byte[] bytes = File.ReadAllBytes(path);
            return Run(ws, bytes, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Import failed", ex);
            return ("Couldn't read that file", null);
        }
    }

    /// <param name="lastModified">When the file was last saved (ms since the epoch), to settle edits made in both places.</param>
    public static (string Message, Action? Undo) Run(Workspace ws, byte[] bytes, long? lastModified)
    {
        try
        {
            using var doc = TackFile.Parse(bytes);
            var before = ws.ToData();
            bool changed = ws.Import(doc.RootElement, lastModified);
            var after = ws.ToData();

            int tasks = Live(after.Tasks) - Live(before.Tasks);
            int sheets = Live(after.Sheets) - Live(before.Sheets);
            var parts = new List<string>();
            if (tasks > 0) parts.Add(tasks == 1 ? "1 task" : $"{tasks} tasks");
            if (sheets > 0) parts.Add(sheets == 1 ? "1 sheet" : $"{sheets} sheets");
            string message = parts.Count > 0 ? $"Imported {string.Join(" and ", parts)}"
                : changed ? "Imported the latest changes"
                : "Nothing new in that file";
            return (message, changed ? () => ws.Restore(before) : null);
        }
        catch (TackFileException ex)
        {
            return (ex.Message, null);
        }
    }

    private static int Live(IEnumerable<TaskData> tasks) => tasks.Count(t => !t.Deleted);

    private static int Live(IEnumerable<SheetData> sheets) => sheets.Count(s => !s.Deleted);
}
