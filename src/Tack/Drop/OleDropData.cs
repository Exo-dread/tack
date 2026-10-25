using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Tack.Drop.Core;
using static Tack.Interop.NativeMethods;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Tack.Drop;

/// <summary>
/// A drag from another app (or from this one), read through OLE with every size checked before anything is
/// copied: the data object behind WPF's, asked directly so a virtual file can be fetched by its index and an
/// oversized block refused unread. Data made in this process that has no OLE form is read through WPF instead.
/// </summary>
internal sealed class OleDropData : IDropData
{
    private const int MaxDropFilesBytes = 2 * 1024 * 1024; // CF_HDROP: 20 paths of any length fit easily

    private readonly System.Windows.IDataObject _data;
    private readonly ComTypes.IDataObject? _com;
    private IReadOnlyList<string>? _formats;

    public OleDropData(System.Windows.IDataObject data)
    {
        _data = data;
        _com = data as ComTypes.IDataObject;
    }

    public IReadOnlyList<string> Formats
    {
        get
        {
            if (_formats != null) return _formats;
            try { _formats = _data.GetFormats(false) ?? []; }
            catch (Exception) { _formats = []; }
            return _formats;
        }
    }

    public bool Has(string format)
    {
        try { return _data.GetDataPresent(format, false); }
        catch (Exception) { return false; }
    }

    public IReadOnlyList<string>? Files(int max, out int count)
    {
        count = 0;
        var v = ReadOle(CF_HDROP, MaxDropFilesBytes);
        if (v.Status == DataRead.TooBig)
        {
            count = int.MaxValue;
            return null;
        }
        if (v.Bytes != null) return ParseDropFiles(v.Bytes, max, out count);

        // Made in this process without an OLE form.
        try
        {
            if (_data.GetData(DataFormats.FileDrop, false) is not string[] files) return null;
            count = files.Length;
            return count > max ? null : files;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// DROPFILES, read by Tack itself within the block's own size (rather than DragQueryFile, which trusts the
    /// offsets in it): a header, then the paths, each ending in a NUL, the list in an extra NUL.
    /// </summary>
    internal static IReadOnlyList<string>? ParseDropFiles(byte[] block, int max, out int count)
    {
        count = 0;
        if (block.Length < 20) return null;
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(block);
        bool wide = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(16)) != 0;
        if (offset < 20 || offset >= block.Length) return null;
        var list = new List<string>();
        int at = (int)offset;
        int unit = wide ? 2 : 1;
        while (at + unit <= block.Length)
        {
            int end = at;
            while (end + unit <= block.Length && (wide ? BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(end)) : block[end]) != 0) end += unit;
            if (end == at) break; // the extra NUL: the end of the list
            count++;
            if (count <= max)
                list.Add(wide ? System.Text.Encoding.Unicode.GetString(block, at, end - at) : DropReader.AnsiEncoding.GetString(block, at, end - at));
            at = end + unit;
        }
        return count > max ? null : list;
    }

    public DataValue Read(string format, int maxBytes)
    {
        var v = ReadOle(FormatId(format), maxBytes);
        if (v.Status != DataRead.Missing) return v;

        object? value;
        try { value = _data.GetData(format, false); }
        catch (Exception) { return DataValue.Missing; }
        switch (value)
        {
            case string s:
                return (long)s.Length * 2 > maxBytes ? DataValue.TooBig : new DataValue(DataRead.Ok, Text: s);
            case byte[] b:
                return b.Length > maxBytes ? DataValue.TooBig : new DataValue(DataRead.Ok, b);
            case Stream stream:
                using (var copy = new MemoryStream())
                {
                    var r = CopyLimited(stream, copy, maxBytes);
                    return r == DataRead.Ok ? new DataValue(DataRead.Ok, copy.ToArray()) : r == DataRead.TooBig ? DataValue.TooBig : DataValue.Missing;
                }
            default:
                return DataValue.Missing;
        }
    }

    public DataRead SaveContents(int index, string path, long maxBytes)
    {
        DataRead result = DataRead.Missing;
        try
        {
            if (TryGet(FormatId(DropFormats.Contents), index, ComTypes.TYMED.TYMED_ISTREAM | ComTypes.TYMED.TYMED_HGLOBAL | ComTypes.TYMED.TYMED_ISTORAGE, out var medium))
            {
                try { result = SaveMedium(medium, path, maxBytes); }
                finally { ReleaseStgMedium(ref medium); }
            }
            else if (index == 0 && _data.GetData(DropFormats.Contents, false) is { } value)
            {
                // Made in this process: one virtual file, as WPF holds it.
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                result = value switch
                {
                    Stream s => CopyLimited(s, file, maxBytes),
                    byte[] b when b.Length <= maxBytes => Write(file, b),
                    byte[] => DataRead.TooBig,
                    _ => DataRead.Missing,
                };
            }
        }
        catch (Exception)
        {
            result = DataRead.Missing;
        }
        if (result != DataRead.Ok) TryDelete(path);
        return result;
    }

    private static DataRead Write(Stream to, byte[] bytes)
    {
        to.Write(bytes);
        return DataRead.Ok;
    }

    // ================= OLE =================

    private static short FormatId(string format) => unchecked((short)DataFormats.GetDataFormat(format).Id);

    private DataValue ReadOle(short id, long maxBytes)
    {
        if (!TryGet(id, -1, ComTypes.TYMED.TYMED_HGLOBAL | ComTypes.TYMED.TYMED_ISTREAM, out var medium)) return DataValue.Missing;
        try
        {
            switch (medium.tymed)
            {
                case ComTypes.TYMED.TYMED_HGLOBAL:
                {
                    long size = (long)(ulong)GlobalSize(medium.unionmember);
                    if (size > maxBytes) return DataValue.TooBig;
                    IntPtr p = GlobalLock(medium.unionmember);
                    if (p == IntPtr.Zero) return DataValue.Missing;
                    try
                    {
                        var bytes = new byte[size];
                        Marshal.Copy(p, bytes, 0, (int)size);
                        return new DataValue(DataRead.Ok, bytes);
                    }
                    finally { GlobalUnlock(medium.unionmember); }
                }
                case ComTypes.TYMED.TYMED_ISTREAM:
                {
                    using var copy = new MemoryStream();
                    var r = WithStream(medium.unionmember, s => CopyLimited(s, copy, maxBytes));
                    return r == DataRead.Ok ? new DataValue(DataRead.Ok, copy.ToArray()) : r == DataRead.TooBig ? DataValue.TooBig : DataValue.Missing;
                }
                default:
                    return DataValue.Missing;
            }
        }
        catch (Exception) { return DataValue.Missing; }
        finally { ReleaseStgMedium(ref medium); }
    }

    private bool TryGet(short id, int index, ComTypes.TYMED tymed, out ComTypes.STGMEDIUM medium)
    {
        medium = default;
        if (_com == null) return false;
        var format = new ComTypes.FORMATETC
        {
            cfFormat = id,
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            ptd = IntPtr.Zero,
            tymed = tymed,
        };
        try
        {
            _com.GetData(ref format, out medium);
            if ((medium.tymed & tymed) != 0 && medium.unionmember != IntPtr.Zero) return true;
            ReleaseStgMedium(ref medium);
            medium = default;
            return false;
        }
        catch (Exception)
        {
            medium = default;
            return false;
        }
    }

    private static DataRead SaveMedium(ComTypes.STGMEDIUM medium, string path, long maxBytes)
    {
        switch (medium.tymed)
        {
            case ComTypes.TYMED.TYMED_HGLOBAL:
            {
                long size = (long)(ulong)GlobalSize(medium.unionmember);
                if (size > maxBytes) return DataRead.TooBig;
                IntPtr p = GlobalLock(medium.unionmember);
                if (p == IntPtr.Zero) return DataRead.Missing;
                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(p, bytes, 0, (int)size);
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                    file.Write(bytes);
                    return DataRead.Ok;
                }
                finally { GlobalUnlock(medium.unionmember); }
            }
            case ComTypes.TYMED.TYMED_ISTREAM:
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                return WithStream(medium.unionmember, s => CopyLimited(s, file, maxBytes));
            }
            case ComTypes.TYMED.TYMED_ISTORAGE:
            {
                // An Outlook .msg as classic Outlook drags it: copied into a compound file of its own.
                var source = Marshal.GetObjectForIUnknown(medium.unionmember) as IStorage;
                if (source == null) return DataRead.Missing;
                IStorage? target = null;
                try
                {
                    if (StgCreateDocfile(path, STGM_CREATE | STGM_READWRITE | STGM_SHARE_EXCLUSIVE, 0, out target) != 0 || target == null) return DataRead.Missing;
                    if (source.CopyTo(0, IntPtr.Zero, IntPtr.Zero, target) != 0 || target.Commit(0) != 0) return DataRead.Missing;
                }
                finally
                {
                    if (target != null) Marshal.ReleaseComObject(target);
                    if (Marshal.IsComObject(source)) Marshal.ReleaseComObject(source);
                }
                return new FileInfo(path).Length > maxBytes ? DataRead.TooBig : DataRead.Ok;
            }
            default:
                return DataRead.Missing;
        }
    }

    private static DataRead WithStream(IntPtr unknown, Func<Stream, DataRead> use)
    {
        object o = Marshal.GetObjectForIUnknown(unknown);
        try
        {
            if (o is not ComTypes.IStream com) return DataRead.Missing;
            using var stream = new ComStream(com);
            return use(stream);
        }
        finally
        {
            if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }

    /// <summary>Copies at most <paramref name="maxBytes"/>; a stream that goes on past that (or never ends) is TooBig.</summary>
    internal static DataRead CopyLimited(Stream from, Stream to, long maxBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int n = from.Read(buffer, 0, buffer.Length);
            if (n <= 0) return DataRead.Ok;
            total += n;
            if (total > maxBytes) return DataRead.TooBig;
            to.Write(buffer, 0, n);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* swept up later */ }
    }

    /// <summary>A COM IStream read as a .NET stream (from its start, when it can seek).</summary>
    private sealed class ComStream : Stream
    {
        private readonly ComTypes.IStream _com;
        private readonly IntPtr _read = Marshal.AllocHGlobal(sizeof(int));

        public ComStream(ComTypes.IStream com)
        {
            _com = com;
            try { _com.Seek(0, 0 /* STREAM_SEEK_SET */, IntPtr.Zero); }
            catch (Exception) { /* not every stream can seek: read it from where it is */ }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (offset != 0)
            {
                var chunk = new byte[count];
                int got = Read(chunk, 0, count);
                Array.Copy(chunk, 0, buffer, offset, got);
                return got;
            }
            Marshal.WriteInt32(_read, 0);
            _com.Read(buffer, count, _read);
            return Math.Clamp(Marshal.ReadInt32(_read), 0, count);
        }

        protected override void Dispose(bool disposing)
        {
            Marshal.FreeHGlobal(_read);
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
