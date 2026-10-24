using System.IO;

namespace Tack.Services;

/// <summary>
/// A file of Tack's own beside tack.json that couldn't be read (snippets.json, attachments.json): a copy is kept
/// beside it as <c>name.unreadable-&lt;time&gt;</c>, as an unreadable tack.json is, so whatever it held can still
/// be recovered by hand. Once per content: a start that finds the same file again adds no second copy.
/// </summary>
internal static class Unreadable
{
    /// <summary>Bigger than this, it was never Tack's (its own are a few hundred KB at most): it's left where it is, uncopied.</summary>
    private const long MaxCopyBytes = 20L * 1024 * 1024;

    public static void KeepAside(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxCopyBytes || file.DirectoryName is not { } folder) return;
            byte[] bytes = File.ReadAllBytes(path);
            foreach (string kept in Directory.EnumerateFiles(folder, file.Name + ".unreadable-*").Take(50))
                if (new FileInfo(kept).Length == bytes.Length && File.ReadAllBytes(kept).AsSpan().SequenceEqual(bytes)) return;
            string stamp = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            for (int n = 1; n <= 9; n++)
            {
                string target = n == 1 ? stamp : $"{stamp}-{n}";
                if (File.Exists(target)) continue; // another one this second
                using var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                copy.Write(bytes);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't keep a copy of {Path.GetFileName(path)}", ex);
        }
    }
}
