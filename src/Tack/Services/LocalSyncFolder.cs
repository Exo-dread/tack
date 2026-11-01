using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
///
/// <para>
/// <b>Claim mode</b> (<paramref name="claim"/>; a marker with <c>"spares": true</c>, as Tack Focus makes
/// in Google Drive, docs/GOOGLE-DRIVE.md): Tack Focus can see only files it made itself, so Tack never
/// makes the folder, the marker, the README or <c>devices</c>, and never a device file of its own.
/// Its own file is a spare Tack Focus made (<c>devices\spare-&lt;16 hex&gt;.json</c>), renamed to this
/// device's name the first time, then written as above (a write over a file keeps the file Tack
/// Focus made). No <c>devices</c> or no spare: <see cref="SyncErrorKind.NoSpare"/>, until Tack Focus
/// has been opened once.
/// </para>
/// </summary>
internal sealed partial class LocalSyncFolder(string root, bool claim = false) : ISyncFolder
{
    public const string Marker = "tack-sync.json";
    private const string Readme = "README.txt";
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);

    /// <summary>A marker is a line of JSON: anything bigger isn't Tack Focus's, and counts as having no spares.</summary>
    public const int MaxMarkerBytes = 4 * 1024;

    /// <summary>Directory entries looked at, and spare names kept, when choosing a spare: a folder holding thousands stays quick.</summary>
    private const int MaxLooked = 20_000;
    private const int MaxSpares = 1_000;

    /// <summary>Spares tried in one cycle (another PC may take the one picked at the same moment).</summary>
    private const int ClaimTries = 3;

    public string Root { get; } = root;

    public string Devices => Path.Combine(Root, "devices");

    /// <summary>Claim mode: never creates anything here; this device's file is a spare it renamed (see above).</summary>
    public bool Claim { get; } = claim;

    /// <summary>Claim mode: told the name a spare was renamed to as this device's own, right after the rename (before it's written).</summary>
    public Action<string>? Claimed { get; init; }

    /// <summary>Is this a Tack sync folder (made by Tack Focus, or by Tack when you chose it)?</summary>
    public static bool IsSyncFolder(string path) => File.Exists(Path.Combine(path, Marker));

    /// <summary>A spare's name, exactly: "spare-", 16 lowercase hex digits, ".json". Nothing else is ever renamed.</summary>
    public static bool IsSpareName(string name) => SparePattern().IsMatch(name);

    [GeneratedRegex(@"^spare-[0-9a-f]{16}\.json\z")]
    private static partial Regex SparePattern();

    /// <summary>The name an old device file is given once a claimed one replaces it: kept, never read as a device file.</summary>
    public static string RetiredName(string device) => $"retired-{device}.json";

    /// <summary>
    /// Does the marker here say <c>"spares": true</c> (claim mode)? A marker that's missing, bigger than
    /// <see cref="MaxMarkerBytes"/>, not JSON, or says anything else counts as no. One that's there but
    /// can't be read right now (Google Drive is fetching it, say) throws <see cref="SyncException"/>, so
    /// the folder is never mistaken for one Tack may write new files in.
    /// </summary>
    public static bool HasSpares(string root)
    {
        string path = Path.Combine(root, Marker);
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxMarkerBytes) return false;
            bytes = new byte[MaxMarkerBytes + 1];
            int n = 0, read;
            while (n < bytes.Length && (read = stream.Read(bytes, n, bytes.Length - n)) > 0) n += read;
            if (n > MaxMarkerBytes) return false; // grew since it was opened
            Array.Resize(ref bytes, n);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncException(SyncErrorKind.Offline, ex.Message);
        }
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) json = json[3..];
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("spares", out var spares) && spares.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The spare <paramref name="device"/> tries first, of <paramref name="count"/> sorted ones: a stable
    /// hash of its id (the first 4 bytes of its SHA-256), so two PCs rarely pick the same one.
    /// </summary>
    public static int SpareIndex(string device, int count) =>
        count <= 0 ? 0 : (int)(BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(device))) % (uint)count);

    /// <summary>The spare files in <c>devices</c> (only files named exactly as spares), sorted; bounded.</summary>
    public List<string> Spares()
    {
        var names = new List<string>();
        if (!Directory.Exists(Devices)) return names;
        int looked = 0;
        // Files only: a directory with a spare's name is never taken.
        foreach (var file in new DirectoryInfo(Devices).EnumerateFiles("spare-*"))
        {
            if (++looked > MaxLooked) break;
            if (!IsSpareName(file.Name)) continue;
            names.Add(file.Name);
            if (names.Count >= MaxSpares) break;
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>Makes a chosen folder a sync folder (marker and README), if it isn't one yet.</summary>
    public void Prepare()
    {
        // Anything Tack made in a claim-mode folder would be invisible to Tack Focus.
        if (Claim) throw new InvalidOperationException("A folder in claim mode is never prepared by Tack");
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
                // The listing's size may be out of date (the file grew since): never read more than a
                // device file may hold. Every device refuses a file that big anyway.
                if (stream.Length > DeviceFile.MaxBytes) throw new DeviceFileException(DeviceFileProblem.Unreadable);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int n;
                while ((n = await stream.ReadAsync(chunk)) > 0)
                {
                    if (buffer.Length + n > DeviceFile.MaxBytes) throw new DeviceFileException(DeviceFileProblem.Unreadable);
                    buffer.Write(chunk, 0, n);
                }
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
            if (!Claim) Directory.CreateDirectory(Devices);
            else if (!Directory.Exists(Devices)) throw new SyncException(SyncErrorKind.NoSpare, "Tack Focus hasn't made the devices folder yet");
            else if (!File.Exists(target)) ClaimSpare(name, target);
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
    /// Claim mode: renames a spare to <paramref name="name"/> (never over a file), the one this device's
    /// id picks first, then the next ones if another device takes it at the same moment. <see cref="Claimed"/>
    /// is told right after the rename, before anything is written into it.
    /// </summary>
    private void ClaimSpare(string name, string target)
    {
        var spares = Spares();
        if (spares.Count == 0) throw new SyncException(SyncErrorKind.NoSpare, "No spare file to take yet");
        int first = SpareIndex(DeviceFile.DeviceOfFile(name) ?? name, spares.Count);
        for (int i = 0; i < Math.Min(ClaimTries, spares.Count); i++)
        {
            string spare = Path.Combine(Devices, spares[(first + i) % spares.Count]);
            bool moved = false;
            try
            {
                File.Move(spare, target, overwrite: false);
                moved = true;
                Claimed?.Invoke(name);
                return;
            }
            catch when (moved)
            {
                throw; // renamed, but noting it failed: the write stops here, and the next cycle notes it
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // This device's file appeared meanwhile (Google Drive bringing it back): never renamed over,
                // and read as its own next cycle.
                if (File.Exists(target)) throw new SyncException(SyncErrorKind.Offline, "This device's file appeared while claiming a spare");
                // Otherwise that spare went (another PC took it): the next one.
            }
        }
        throw new SyncException(SyncErrorKind.NoSpare, "Every spare tried was taken");
    }

    /// <summary>
    /// After a migration (<see cref="FolderSync"/>): renames <paramref name="device"/>'s file to its
    /// retired name, so no reader takes it for a device file, but it can be put back. Never over a file;
    /// if it can't be done, it's left where it is (harmless: a device that no longer writes).
    /// </summary>
    public bool Retire(string device)
    {
        string from = Path.Combine(Devices, DeviceFile.FileName(device));
        try
        {
            if (!File.Exists(from)) return false;
            File.Move(from, Path.Combine(Devices, RetiredName(device)), overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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
