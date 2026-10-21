using System.IO;
using System.Text;
using Tack.Sync;

namespace Tack.Services;

/// <summary>
/// The sync folder on this PC: normally OneDrive's "Apps\Tack Focus" folder, which the OneDrive app
/// keeps in step with the cloud. Tack only reads and writes files here; it never goes online itself.
///
///   tack-sync.json     marker (how Tack finds the folder)
///   README.txt         what this folder is
///   devices\&lt;id&gt;.json  one file per device
///
/// Writes go to a "~$" temp file first (the OneDrive app never uploads those), then replace the
/// device file in one step, so the OneDrive app and other readers never see half a file.
/// </summary>
internal sealed class LocalSyncFolder(string root) : ISyncFolder
{
    public const string Marker = "tack-sync.json";
    private const string Readme = "README.txt";
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);

    public string Root { get; } = root;

    public string Devices => Path.Combine(Root, "devices");

    /// <summary>Is this a Tack sync folder (made by Tack Focus, or by Tack when you chose it)?</summary>
    public static bool IsSyncFolder(string path) => File.Exists(Path.Combine(path, Marker));

    /// <summary>Makes a chosen folder a sync folder (marker and README), if it isn't one yet.</summary>
    public void Prepare()
    {
        Directory.CreateDirectory(Devices);
        WriteIfMissing(Marker, "{\n  \"format\": \"tack-sync\",\n  \"version\": 1\n}\n");
        WriteIfMissing(Readme, string.Join("\r\n",
            "This folder keeps Tack in sync across your devices (Tack Focus on your phone, Tack for Windows).",
            "",
            "Each device keeps its own file in \"devices\". Please don't edit or move them; if you delete",
            "this folder, your devices keep their tasks and simply write it again.",
            "",
            "Tack has no server: these files go only between your devices and your OneDrive.",
            "More: https://tackfocus.app/ and https://github.com/Exo-dread/tack/blob/main/docs/SYNC.md",
            ""));
    }

    public Task<IReadOnlyList<FolderEntry>> ListAsync()
    {
        try
        {
            if (!Directory.Exists(Root)) throw new SyncException(SyncErrorKind.Gone, "The sync folder isn't there");
            if (!Directory.Exists(Devices)) return Task.FromResult<IReadOnlyList<FolderEntry>>([]);
            var entries = new List<FolderEntry>();
            foreach (var file in new DirectoryInfo(Devices).EnumerateFiles("*.json"))
            {
                // Size and time stand in for a content tag; reading the metadata never downloads a file.
                entries.Add(new FolderEntry(file.Name, $"{file.LastWriteTimeUtc.Ticks:x}-{file.Length:x}", file.Length));
            }
            return Task.FromResult<IReadOnlyList<FolderEntry>>(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex);
        }
    }

    public async Task<byte[]> ReadAsync(FolderEntry entry)
    {
        string path = Path.Combine(Devices, entry.Name);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // Shared with everyone, so the OneDrive app is never locked out by Tack.
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                return buffer.ToArray();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new SyncException(SyncErrorKind.Gone, ex.Message);
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(150); // the OneDrive app is writing it right now
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Translate(ex);
            }
        }
    }

    public async Task<FolderEntry> WriteAsync(string name, byte[] content)
    {
        string target = Path.Combine(Devices, name);
        string temp = Path.Combine(Devices, $"~${name}.tmp");
        try
        {
            if (!Directory.Exists(Root)) throw new SyncException(SyncErrorKind.Gone, "The sync folder isn't there");
            Directory.CreateDirectory(Devices);
            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await fs.WriteAsync(content);
                await fs.FlushAsync();
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, target, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    await Task.Delay(100); // the OneDrive app (or antivirus) has it open for a moment
                }
            }
            var info = new FileInfo(target);
            return new FolderEntry(name, $"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw Translate(ex);
        }
    }

    /// <summary>
    /// Asks the OneDrive app to keep this folder on the PC ("Always keep on this device"), so Tack
    /// can read the other devices' files offline too. Harmless anywhere else.
    /// </summary>
    public void KeepOnDevice()
    {
        const FileAttributes Pinned = (FileAttributes)0x00080000;
        const FileAttributes Unpinned = (FileAttributes)0x00100000;
        try
        {
            var attributes = File.GetAttributes(Root);
            if ((attributes & Pinned) == 0) File.SetAttributes(Root, (attributes & ~Unpinned) | Pinned);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Not a OneDrive folder, or Files On-Demand is off: nothing to do.
        }
    }

    private void WriteIfMissing(string name, string text)
    {
        string path = Path.Combine(Root, name);
        if (!File.Exists(path)) File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static SyncException Translate(Exception ex) => ex switch
    {
        SyncException s => s,
        UnauthorizedAccessException => new SyncException(SyncErrorKind.Denied, ex.Message),
        FileNotFoundException or DirectoryNotFoundException => new SyncException(SyncErrorKind.Gone, ex.Message),
        IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull => new SyncException(SyncErrorKind.Full, ex.Message),
        // Locked for too long, or a cloud-only file the OneDrive app can't fetch while offline: later.
        _ => new SyncException(SyncErrorKind.Offline, ex.Message),
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
