using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Tack.Drop.Core;
using Tack.Launcher.Core;
using static Tack.Interop.NativeMethods;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Tack.Drop;

/// <summary>What reading a drop asks of this PC: its disk, its temporary folders and Windows' list of program types.</summary>
internal static class DesktopDrop
{
    public static DropContext Context() => new()
    {
        Probe = RealFileProbe.Instance,
        IsNetwork = RealFileProbe.IsNetwork,
        IsTemporary = IsTemporary,
        IsProgram = IsProgram,
        EmailSubject = EmailSubject,
    };

    /// <summary>Tack's list of program types, and Windows' own (what Attachment Manager warns about).</summary>
    public static bool IsProgram(string extension)
    {
        if (extension.Length == 0) return false;
        if (DropNames.ProgramExtensions.Contains(extension)) return true;
        try { return AssocIsDangerous(extension); }
        catch (Exception) { return false; }
    }

    private static readonly Lazy<string[]> TemporaryRoots = new(() =>
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { roots.Add(Path.GetFullPath(path).TrimEnd('\\') + "\\"); }
            catch (Exception) { /* not a path */ }
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Add(Path.GetTempPath());
        Add(Path.Combine(local, "Temp"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.InternetCache));
        Add(Path.Combine(local, "Microsoft", "Windows", "INetCache"));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    });

    /// <summary>
    /// A folder whose files are cleaned away by whoever put them there: %TEMP%, Windows' Temp, the internet
    /// cache (where Outlook opens attachments), and a Store app's temporary folders (the new Outlook's).
    /// </summary>
    public static bool IsTemporary(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (TemporaryRoots.Value.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase))) return true;
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages") + "\\";
            return full.StartsWith(packages, StringComparison.OrdinalIgnoreCase)
                && (full.Contains(@"\AC\Temp\", StringComparison.OrdinalIgnoreCase)
                    || full.Contains(@"\AC\INetCache\", StringComparison.OrdinalIgnoreCase)
                    || full.Contains(@"\TempState\", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    /// <summary>A local .eml or .msg file's subject (the header of one, the subject property of the other).</summary>
    public static string? EmailSubject(string path) => WinPath.Extension(path) switch
    {
        ".eml" => MailSubject.FromEml(path),
        ".msg" => MsgSubject(path),
        _ => null,
    };

    /// <summary>
    /// An Outlook .msg's subject: PidTagSubject, as Unicode (0037001F) or in the message's code page (0037001E),
    /// read from the compound file by Windows' own reader. At most 8 KB of it.
    /// </summary>
    public static string? MsgSubject(string path)
    {
        try
        {
            if (StgOpenStorage(path, IntPtr.Zero, STGM_READ | STGM_SHARE_DENY_WRITE, IntPtr.Zero, 0, out var storage) != 0 || storage == null) return null;
            try
            {
                string? subject = ReadProperty(storage, "__substg1.0_0037001F", wide: true) ?? ReadProperty(storage, "__substg1.0_0037001E", wide: false);
                subject = DropNames.OneLine(subject, DropLimits.MaxTitle);
                return subject.Length > 0 ? subject : null;
            }
            finally { Marshal.ReleaseComObject(storage); }
        }
        catch (Exception) { return null; }
    }

    private static string? ReadProperty(IStorage storage, string name, bool wide)
    {
        if (storage.OpenStream(name, IntPtr.Zero, STGM_READ | STGM_SHARE_EXCLUSIVE, 0, out var stream) != 0 || stream == null) return null;
        IntPtr read = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            var buffer = new byte[8192];
            Marshal.WriteInt32(read, 0);
            stream.Read(buffer, buffer.Length, read);
            int n = Math.Clamp(Marshal.ReadInt32(read), 0, buffer.Length);
            string text = wide ? Encoding.Unicode.GetString(buffer, 0, n & ~1) : Encoding.Latin1.GetString(buffer, 0, n);
            int z = text.IndexOf('\0');
            return z >= 0 ? text[..z] : text;
        }
        catch (Exception) { return null; }
        finally
        {
            Marshal.FreeHGlobal(read);
            Marshal.ReleaseComObject(stream);
        }
    }

    /// <summary>
    /// Marks a copy Tack saved from a drag as come from outside this PC (the Mark of the Web, as a browser or
    /// Outlook marks what they save), so the app that opens it treats it with the same care.
    /// </summary>
    public static void MarkFromOutside(string path)
    {
        try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n", Encoding.ASCII); }
        catch (Exception) { /* not NTFS (a USB stick in portable mode): nothing to mark with */ }
    }
}
