using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Tack.Interop;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Tack.Tests;

/// <summary>
/// A drag as another app hands it over through OLE: every format (and each virtual file, by its index) in an
/// HGLOBAL, an IStream or an IStorage, as Explorer, browsers and Outlook give them. Wrapped in a real WPF
/// <see cref="System.Windows.DataObject"/>, it reaches Tack exactly the way a drag from another process does.
/// </summary>
internal sealed class OleSource : ComTypes.IDataObject
{
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int DV_E_TYMED = unchecked((int)0x80040069);
    private const int DV_E_LINDEX = unchecked((int)0x80040068);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);

    private readonly List<(short Id, int Index, Func<ComTypes.STGMEDIUM> Make)> _items = new();

    /// <summary>Every request, as (format, index): what Tack asked for.</summary>
    public List<(string Format, int Index)> Asked { get; } = new();

    public static short Id(string format) => unchecked((short)System.Windows.DataFormats.GetDataFormat(format).Id);

    public OleSource Bytes(string format, byte[] bytes, int index = -1)
    {
        _items.Add((Id(format), index, () => Global(bytes)));
        return this;
    }

    public OleSource Wide(string format, string text) => Bytes(format, Encoding.Unicode.GetBytes(text + "\0"));

    public OleSource Stream(string format, Func<ComTypes.IStream> make, int index)
    {
        _items.Add((Id(format), index, () => new ComTypes.STGMEDIUM
        {
            tymed = ComTypes.TYMED.TYMED_ISTREAM,
            unionmember = Marshal.GetComInterfaceForObject(make(), typeof(ComTypes.IStream)),
        }));
        return this;
    }

    public OleSource StreamOf(string format, byte[] bytes, int index) => Stream(format, () => MemoryComStream(bytes), index);

    public OleSource Storage(string format, Func<NativeMethods.IStorage> make, int index)
    {
        _items.Add((Id(format), index, () => new ComTypes.STGMEDIUM
        {
            tymed = ComTypes.TYMED.TYMED_ISTORAGE,
            unionmember = Marshal.GetComInterfaceForObject(make(), typeof(NativeMethods.IStorage)),
        }));
        return this;
    }

    public System.Windows.DataObject ToData() => new(this);

    public void GetData(ref ComTypes.FORMATETC format, out ComTypes.STGMEDIUM medium)
    {
        var f = format;
        Asked.Add((System.Windows.DataFormats.GetDataFormat(f.cfFormat).Name, f.lindex));
        var found = _items.Where(i => i.Id == f.cfFormat).ToList();
        if (found.Count == 0) throw new COMException("no such format", DV_E_FORMATETC);
        var item = found.FirstOrDefault(i => i.Index == f.lindex);
        if (item.Make == null) throw new COMException("no such index", DV_E_LINDEX);
        medium = item.Make();
        if ((medium.tymed & f.tymed) == 0)
        {
            NativeMethods.ReleaseStgMedium(ref medium);
            throw new COMException("not in that medium", DV_E_TYMED);
        }
    }

    public void GetDataHere(ref ComTypes.FORMATETC format, ref ComTypes.STGMEDIUM medium) => throw new COMException("", E_NOTIMPL);

    public int QueryGetData(ref ComTypes.FORMATETC format)
    {
        short id = format.cfFormat;
        return _items.Any(i => i.Id == id) ? 0 : DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref ComTypes.FORMATETC formatIn, out ComTypes.FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return 0x00040130; // DATA_S_SAMEFORMATETC
    }

    public void SetData(ref ComTypes.FORMATETC formatIn, ref ComTypes.STGMEDIUM medium, bool release) => throw new COMException("", E_NOTIMPL);

    public ComTypes.IEnumFORMATETC EnumFormatEtc(ComTypes.DATADIR direction) =>
        new FormatList(_items.Select(i => i.Id).Distinct().Select(id => new ComTypes.FORMATETC
        {
            cfFormat = id,
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL | ComTypes.TYMED.TYMED_ISTREAM | ComTypes.TYMED.TYMED_ISTORAGE,
        }).ToArray());

    public int DAdvise(ref ComTypes.FORMATETC pFormatetc, ComTypes.ADVF advf, ComTypes.IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException("", OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out ComTypes.IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    private sealed class FormatList(ComTypes.FORMATETC[] formats) : ComTypes.IEnumFORMATETC
    {
        private int _at;

        public int Next(int celt, ComTypes.FORMATETC[] rgelt, int[] pceltFetched)
        {
            int n = 0;
            while (n < celt && _at < formats.Length) rgelt[n++] = formats[_at++];
            if (pceltFetched is { Length: > 0 }) pceltFetched[0] = n;
            return n == celt ? 0 : 1;
        }

        public int Skip(int celt)
        {
            _at += celt;
            return _at <= formats.Length ? 0 : 1;
        }

        public int Reset()
        {
            _at = 0;
            return 0;
        }

        public void Clone(out ComTypes.IEnumFORMATETC newEnum) => newEnum = new FormatList(formats);
    }

    // ================= Building data =================

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("ole32.dll")]
    private static extern int CreateStreamOnHGlobal(IntPtr hGlobal, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out ComTypes.IStream stream);

    public static ComTypes.STGMEDIUM Global(byte[] bytes)
    {
        IntPtr h = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (UIntPtr)Math.Max(1, bytes.Length));
        IntPtr p = NativeMethods.GlobalLock(h);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        NativeMethods.GlobalUnlock(h);
        return new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_HGLOBAL, unionmember = h };
    }

    public static ComTypes.IStream MemoryComStream(byte[] bytes)
    {
        var m = Global(bytes);
        Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(m.unionmember, true, out var stream));
        stream.SetSize(bytes.Length);
        return stream;
    }

    /// <summary>A FILEGROUPDESCRIPTORW listing virtual files (a folder entry, when <c>Folder</c>).</summary>
    public static byte[] Descriptor(params (string Name, long? Size, bool Folder)[] files) => Descriptor((uint)files.Length, files);

    public static byte[] Descriptor(uint claimed, params (string Name, long? Size, bool Folder)[] files)
    {
        const int size = 592;
        var bytes = new byte[4 + size * files.Length];
        BitConverter.GetBytes(claimed).CopyTo(bytes, 0);
        for (int i = 0; i < files.Length; i++)
        {
            int at = 4 + i * size;
            var (name, length, folder) = files[i];
            uint flags = 0x80000000 | 0x04 | (length != null ? 0x40u : 0);
            BitConverter.GetBytes(flags).CopyTo(bytes, at);
            BitConverter.GetBytes(folder ? 0x10u : 0x80u).CopyTo(bytes, at + 36);
            if (length is { } l)
            {
                BitConverter.GetBytes((uint)(l >> 32)).CopyTo(bytes, at + 64);
                BitConverter.GetBytes((uint)l).CopyTo(bytes, at + 68);
            }
            var n = Encoding.Unicode.GetBytes(name.Length > 259 ? name[..259] : name);
            n.CopyTo(bytes, at + 72);
        }
        return bytes;
    }

    /// <summary>DROPFILES (Unicode), as Explorer puts files in CF_HDROP.</summary>
    public static byte[] DropFiles(IEnumerable<string> paths)
    {
        using var m = new MemoryStream();
        m.Write(BitConverter.GetBytes(20u));
        m.Write(new byte[12]); // pt, fNC
        m.Write(BitConverter.GetBytes(1u)); // fWide
        foreach (string p in paths) m.Write(Encoding.Unicode.GetBytes(p + "\0"));
        m.Write(new byte[2]);
        return m.ToArray();
    }
}

/// <summary>A stream that never ends (a hostile or broken source).</summary>
internal sealed class EndlessComStream : ComTypes.IStream
{
    public long Served { get; private set; }

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        Array.Fill(pv, (byte)'x', 0, cb);
        Served += cb;
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, cb);
    }

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition) { }
    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();
    public void SetSize(long libNewSize) => throw new NotSupportedException();
    public void CopyTo(ComTypes.IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();
    public void Commit(int grfCommitFlags) { }
    public void Revert() { }
    public void LockRegion(long libOffset, long cb, int dwLockType) { }
    public void UnlockRegion(long libOffset, long cb, int dwLockType) { }
    public void Stat(out ComTypes.STATSTG pstatstg, int grfStatFlag) => pstatstg = new ComTypes.STATSTG { type = 2 };
    public void Clone(out ComTypes.IStream ppstm) => throw new NotSupportedException();
}
