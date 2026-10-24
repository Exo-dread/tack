using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tack.Interop;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using IDataObject = System.Windows.IDataObject;

namespace Tack.Snippets;

/// <summary>An image ready to keep as a snippet: PNG bytes, its size in pixels and a thumbnail.</summary>
internal sealed record SnippetPng(byte[] Bytes, int Width, int Height, BitmapSource? Thumb);

/// <summary>Why an image that was pasted isn't kept.</summary>
internal enum PasteProblem
{
    None,
    /// <summary>More pixels or bytes than Tack takes in (<see cref="SnippetImage.MaxSourcePixels"/>, <see cref="SnippetImage.MaxEncodedBytes"/>).</summary>
    TooBig,
    /// <summary>Damaged, or not the image it says it is.</summary>
    Unreadable,
    /// <summary>A copied file on a network drive or share, or a cloud file that isn't downloaded: never fetched.</summary>
    NotOnThisPc,
}

/// <summary>What a paste held: the images to keep, and the first reason one wasn't.</summary>
internal sealed record PastedImages(IReadOnlyList<SnippetPng> Images, PasteProblem Problem)
{
    public static readonly PastedImages None = new([], PasteProblem.None);

    /// <summary>The paste was an image (kept or not), so a text box mustn't paste it as well.</summary>
    public bool IsImage => Images.Count > 0 || Problem != PasteProblem.None;
}

/// <summary>
/// Pasted images (a snip from Win+Shift+S, a screenshot, an image file copied in Explorer) turned into
/// snippets, and snippets read back for showing. Everything here is untrusted input: sizes are checked
/// from headers before any pixels are decoded or bytes copied, only Windows' own PNG, BMP, JPEG, GIF and
/// TIFF decoders are used (never whichever codec claims a file), and anything bigger than a snippet may
/// be is scaled down and saved again as a plain PNG.
/// </summary>
internal static class SnippetImage
{
    /// <summary>The most pixels a pasted image may have (a snip of three 4K screens is 25 million).</summary>
    public const long MaxSourcePixels = 40_000_000;

    /// <summary>The most bytes taken in from a PNG on the clipboard or an image file.</summary>
    public const long MaxEncodedBytes = 50L * 1024 * 1024;

    /// <summary>A kept snippet is scaled to fit this on its longer side, and <see cref="MaxStoredPixels"/> in all.</summary>
    public const int MaxStoredSide = 8192;

    public const long MaxStoredPixels = 4096L * 4096;

    /// <summary>The largest PNG one snippet is kept as; bigger ones are scaled down until they fit.</summary>
    public const long MaxSnippetBytes = 20L * 1024 * 1024;

    /// <summary>Image files taken from one paste.</summary>
    public const int MaxFilesPerPaste = 10;

    /// <summary>Thumbnails are made this many pixels tall (twice the strip's height, for crisp scaling).</summary>
    public const int ThumbPixels = 104;

    // A DIB's header (BITMAPV5HEADER at most) and the largest a DIB may be: every pixel at 32 bits, plus its header.
    private const int DibHeadMax = 124;
    private const long MaxDibBytes = MaxSourcePixels * 4 + 4096;

    private static readonly string[] PngFormats = ["PNG", "image/png"];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private sealed class Refused(PasteProblem problem) : Exception
    {
        public PasteProblem Problem { get; } = problem;
    }

    private static Refused TooBig => new(PasteProblem.TooBig);
    private static Refused Unreadable => new(PasteProblem.Unreadable);

    // ================= From a paste =================

    /// <summary>
    /// The images in a paste: a PNG, then a bitmap (DIB), then image files copied in Explorer. A paste
    /// with text in it is left alone (copying cells or a slide brings a picture of them too, and the
    /// text is what was meant), and so is one without an image.
    /// </summary>
    public static PastedImages FromPaste(IDataObject? data)
    {
        if (data == null || HasText(data)) return PastedImages.None;
        try
        {
            foreach (string format in PngFormats)
                if (Present(data, format)) return One(() => FromPng(Take(data, format, PngHeadBytes, PngLength, prefix: 0)));
            if (Present(data, DataFormats.Dib)) return One(() => FromDib(Take(data, DataFormats.Dib, DibHeadMax, DibLength, prefix: BmpFileHeader)));
            // Only an image made in this process comes as a bitmap alone: the clipboard always offers a DIB with one.
            if (Present(data, DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap, false) is BitmapSource bitmap) return One(() => FromBitmap(bitmap));
            if (Present(data, DataFormats.FileDrop) && FileList(data) is { } files) return FromFiles(files);
        }
        catch (Exception)
        {
            return new([], PasteProblem.Unreadable);
        }
        return PastedImages.None;
    }

    private static PastedImages One(Func<SnippetPng> make)
    {
        try
        {
            return new([make()], PasteProblem.None);
        }
        catch (Refused r)
        {
            return new([], r.Problem);
        }
        catch (Exception)
        {
            // Decoders throw all sorts for a damaged image (format, COM, overflow, out of memory for one huge buffer).
            return new([], PasteProblem.Unreadable);
        }
    }

    /// <summary>Text in the paste (when in doubt, yes: the paste then goes on as it always has).</summary>
    private static bool HasText(IDataObject data)
    {
        try
        {
            foreach (string format in new[] { DataFormats.UnicodeText, DataFormats.Text })
                if (data.GetDataPresent(format, false) && data.GetData(format, false) is string s && !string.IsNullOrWhiteSpace(s)) return true;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool Present(IDataObject data, string format)
    {
        try { return data.GetDataPresent(format, false); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// A clipboard format's bytes, sized up first: <paramref name="measure"/> is given its first bytes and how
    /// many there are, and says how many to take (or refuses). Read straight from the clipboard's memory when the
    /// data offers it (the clipboard always does), so nothing oversized is ever copied. The bytes start
    /// <paramref name="prefix"/> bytes into the array returned.
    /// </summary>
    private static byte[] Take(IDataObject data, string format, int headBytes, Func<byte[], long, long> measure, int prefix)
    {
        if (data is ComDataObject com && Global(com, format) is { } global)
        {
            var medium = global;
            try
            {
                IntPtr handle = medium.unionmember;
                long size = (long)(ulong)NativeMethods.GlobalSize(handle);
                IntPtr p = NativeMethods.GlobalLock(handle);
                if (p == IntPtr.Zero) throw Unreadable;
                try
                {
                    var head = new byte[(int)Math.Min(size, headBytes)];
                    Marshal.Copy(p, head, 0, head.Length);
                    long take = measure(head, size);
                    if (take < 0 || take > size || take + prefix > Array.MaxLength) throw Unreadable;
                    var bytes = GC.AllocateUninitializedArray<byte>((int)take + prefix);
                    Array.Clear(bytes, 0, prefix);
                    Marshal.Copy(p, bytes, prefix, (int)take);
                    return bytes;
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }
            }
            finally
            {
                NativeMethods.ReleaseStgMedium(ref medium);
            }
        }

        // Not in global memory: a stream or an array, as an object in this process holds it.
        byte[] all = data.GetData(format, false) switch
        {
            byte[] b => b,
            Stream s => ReadAtMost(s, MaxDibBytes),
            _ => throw Unreadable,
        };
        long n = measure(all.AsSpan(0, Math.Min(all.Length, headBytes)).ToArray(), all.Length);
        if (n < 0 || n > all.Length) throw Unreadable;
        var copy = new byte[n + prefix];
        Buffer.BlockCopy(all, 0, copy, prefix, (int)n);
        return copy;
    }

    /// <summary>The format in global memory, if the data object offers it that way.</summary>
    private static STGMEDIUM? Global(ComDataObject com, string format)
    {
        var request = new FORMATETC
        {
            cfFormat = unchecked((short)DataFormats.GetDataFormat(format).Id),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            ptd = IntPtr.Zero,
            tymed = TYMED.TYMED_HGLOBAL,
        };
        try
        {
            if (com.QueryGetData(ref request) != 0) return null;
            com.GetData(ref request, out var medium);
            if (medium.tymed == TYMED.TYMED_HGLOBAL && medium.unionmember != IntPtr.Zero) return medium;
            NativeMethods.ReleaseStgMedium(ref medium);
        }
        catch (COMException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        return null;
    }

    private static byte[] ReadAtMost(Stream s, long limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = s.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > limit) throw TooBig;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // ---- PNG ----

    private const int PngHeadBytes = 33; // signature, then the IHDR chunk

    /// <summary>A PNG's size in pixels from its header, without decoding anything; null if it isn't a PNG.</summary>
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> head)
    {
        if (head.Length < 24 || !head[..8].SequenceEqual(PngSignature) || !head[12..16].SequenceEqual("IHDR"u8)) return null;
        uint w = BinaryPrimitives.ReadUInt32BigEndian(head[16..20]), h = BinaryPrimitives.ReadUInt32BigEndian(head[20..24]);
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return null;
        return ((int)w, (int)h);
    }

    private static long PngLength(byte[] head, long available)
    {
        if (PngSize(head) is not var (w, h)) throw Unreadable;
        if ((long)w * h > MaxSourcePixels || available > MaxEncodedBytes) throw TooBig;
        return available;
    }

    private static SnippetPng FromPng(byte[] bytes)
    {
        bytes = TrimPng(bytes);
        if (PngSize(bytes) is not var (w, h)) throw Unreadable;
        if ((long)w * h > MaxSourcePixels) throw TooBig;
        var frame = new PngBitmapDecoder(new MemoryStream(bytes, writable: false), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
        return Keep(frame, original: bytes);
    }

    /// <summary>Clipboard memory is rounded up: what follows the PNG's last chunk isn't kept.</summary>
    private static byte[] TrimPng(byte[] bytes)
    {
        int from = Math.Max(0, bytes.Length - 64);
        int end = bytes.AsSpan(from).LastIndexOf("IEND"u8);
        if (end < 0) return bytes;
        int length = from + end + 8; // the chunk type, then its CRC
        return length < bytes.Length ? bytes[..length] : bytes;
    }

    // ---- DIB (a bitmap on the clipboard) ----

    private const int BmpFileHeader = 14;

    /// <summary>
    /// A DIB's length from its header: its size in pixels, colour masks and table and pixel rows, all checked
    /// against what's there before anything is copied.
    /// </summary>
    private static long DibLength(byte[] head, long available)
    {
        var (size, pixels) = DibLayout(head);
        long total = size + pixels;
        if (total > available) throw Unreadable; // cut short
        if (total > MaxDibBytes) throw TooBig;
        return total;
    }

    /// <summary>The bytes before a DIB's pixels (header, masks, colours), and the pixels' own.</summary>
    private static (long Before, long Pixels) DibLayout(ReadOnlySpan<byte> h)
    {
        if (h.Length < 40) throw Unreadable;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(h);
        int width = BinaryPrimitives.ReadInt32LittleEndian(h[4..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(h[8..]);
        ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        uint imageSize = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
        uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(h[32..]);

        if (size is not (40 or 52 or 56 or 108 or 124) || h.Length < Math.Min(size, DibHeadMax)) throw Unreadable;
        if (width <= 0 || height == 0 || height == int.MinValue || planes != 1) throw Unreadable;
        long rows = Math.Abs((long)height);
        if (width * rows > MaxSourcePixels) throw TooBig;
        if (bits is not (1 or 4 or 8 or 16 or 24 or 32)) throw Unreadable;

        const uint Rgb = 0, Rle8 = 1, Rle4 = 2, BitFields = 3, AlphaBitFields = 6;
        bool rle = compression is Rle8 or Rle4;
        if (compression is not (Rgb or Rle8 or Rle4 or BitFields or AlphaBitFields)) throw Unreadable; // no JPEG or PNG inside
        if (compression is BitFields or AlphaBitFields && bits is not (16 or 32)) throw Unreadable;
        if (rle && (bits != (compression == Rle8 ? 8 : 4) || height < 0 || imageSize == 0)) throw Unreadable;
        if (colorsUsed > 256) throw Unreadable;

        long masks = size == 40 ? compression switch { BitFields => 12, AlphaBitFields => 16, _ => 0 } : 0;
        long colors = colorsUsed != 0 ? colorsUsed : bits <= 8 ? 1L << bits : 0;
        long stride = (width * (long)bits + 31) / 32 * 4;
        long pixels = rle ? imageSize : stride * rows;
        if (pixels > MaxDibBytes) throw TooBig;
        return (size + masks + colors * 4, pixels);
    }

    /// <summary>A DIB, given a BMP file header in front of it (<see cref="BmpFileHeader"/> bytes left free by <see cref="Take"/>).</summary>
    private static SnippetPng FromDib(byte[] bmp)
    {
        var (before, _) = DibLayout(bmp.AsSpan(BmpFileHeader, Math.Min(DibHeadMax, bmp.Length - BmpFileHeader)));
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), checked((int)(BmpFileHeader + before)));
        var frame = new BmpBitmapDecoder(new MemoryStream(bmp, writable: false), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
        // Screens have no transparency, and a DIB's alpha is often left at zero: drop it.
        return Keep(new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0), original: null);
    }

    private static SnippetPng FromBitmap(BitmapSource bitmap)
    {
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > MaxSourcePixels) throw TooBig;
        return Keep(bitmap, original: null);
    }

    // ---- Image files copied in Explorer ----

    private enum FileKind { Png, Jpeg, Bmp, Gif, Tiff }

    private static FileKind? KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => FileKind.Png,
        ".jpg" or ".jpeg" or ".jfif" => FileKind.Jpeg,
        ".bmp" => FileKind.Bmp,
        ".gif" => FileKind.Gif,
        ".tif" or ".tiff" => FileKind.Tiff,
        _ => null,
    };

    /// <summary>The most file names read from a paste (Explorer can copy tens of thousands).</summary>
    private const int MaxNamesRead = 1000;

    /// <summary>
    /// The copied files' names, the first <see cref="MaxNamesRead"/> of them: read straight from the clipboard's
    /// list (DROPFILES) in one pass, since asking WPF for the whole list takes time that grows with its square.
    /// </summary>
    private static IReadOnlyList<string>? FileList(IDataObject data)
    {
        if (data is not ComDataObject com || Global(com, DataFormats.FileDrop) is not { } global)
            return data.GetData(DataFormats.FileDrop, false) is string[] files ? files.Take(MaxNamesRead).ToList() : null;

        var medium = global;
        try
        {
            IntPtr handle = medium.unionmember;
            long size = (long)(ulong)NativeMethods.GlobalSize(handle);
            IntPtr p = NativeMethods.GlobalLock(handle);
            if (p == IntPtr.Zero) return null;
            try
            {
                // DROPFILES: where the names start, then (after a point and a flag) whether they're UTF-16.
                if (size < 20) return null;
                int start = Marshal.ReadInt32(p, 0);
                bool wide = Marshal.ReadInt32(p, 16) != 0;
                if (!wide || start < 20 || start >= size) return null;

                // Names one after another, each ending in a nul; an empty one ends the list. No more than 8 MB is looked at.
                var names = new List<string>();
                var name = new System.Text.StringBuilder();
                long end = Math.Min(size, start + 8L * 1024 * 1024);
                for (long at = start; at + 1 < end && names.Count < MaxNamesRead; at += 2)
                {
                    char c = (char)Marshal.ReadInt16(p, checked((int)at));
                    if (c != char.MinValue)
                    {
                        if (name.Length <= 32767) name.Append(c);
                        continue;
                    }
                    if (name.Length == 0) break;
                    names.Add(name.ToString());
                    name.Clear();
                }
                return names;
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.ReleaseStgMedium(ref medium);
        }
    }

    /// <summary>The image files among those copied (others are no business of Notes), up to <see cref="MaxFilesPerPaste"/>.</summary>
    private static PastedImages FromFiles(IReadOnlyList<string> paths)
    {
        var images = new List<SnippetPng>();
        var problem = PasteProblem.None;
        int seen = 0;
        foreach (string path in paths)
        {
            if (path == null || path.Length > 32767 || KindOf(path) is not { } kind) continue;
            if (++seen > MaxFilesPerPaste) break;
            var one = One(() => FromFile(path, kind));
            images.AddRange(one.Images);
            if (problem == PasteProblem.None) problem = one.Problem;
        }
        return seen == 0 ? PastedImages.None : new(images, problem);
    }

    /// <summary>
    /// A drive-letter path on one of this PC's own disks. Never a network share or mapped drive (Tack doesn't
    /// reach across the network, and a slow share would freeze the panel), nor a device path.
    /// </summary>
    internal static bool IsOnThisPc(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        try
        {
            return new DriveInfo(path[..1]).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;

    private static SnippetPng FromFile(string path, FileKind kind)
    {
        if (!IsOnThisPc(path)) throw new Refused(PasteProblem.NotOnThisPc);
        var info = new FileInfo(path);
        if (!info.Exists) throw Unreadable;
        // A cloud file that's only a placeholder: reading it would download it.
        if ((info.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0) throw new Refused(PasteProblem.NotOnThisPc);
        if (info.Length > MaxEncodedBytes) throw TooBig;
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            bytes = ReadAtMost(stream, MaxEncodedBytes);
        if (kind == FileKind.Png) return FromPng(bytes);

        var source = new MemoryStream(bytes, writable: false);
        const BitmapCreateOptions Options = BitmapCreateOptions.IgnoreColorProfile;
        BitmapDecoder decoder = kind switch
        {
            FileKind.Jpeg => new JpegBitmapDecoder(source, Options, BitmapCacheOption.None),
            FileKind.Bmp => new BmpBitmapDecoder(source, Options, BitmapCacheOption.None),
            FileKind.Gif => new GifBitmapDecoder(source, Options, BitmapCacheOption.None),
            _ => new TiffBitmapDecoder(source, Options, BitmapCacheOption.None),
        };
        if (decoder.Frames.Count == 0) throw Unreadable;
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxSourcePixels) throw TooBig;
        return Keep(frame, original: null);
    }

    // ================= Keeping =================

    /// <summary>How much an image is scaled to be kept: to fit <see cref="MaxStoredSide"/> and <see cref="MaxStoredPixels"/>.</summary>
    public static double Fit(int width, int height) =>
        Math.Min(1, Math.Min(Math.Min((double)MaxStoredSide / width, (double)MaxStoredSide / height), Math.Sqrt((double)MaxStoredPixels / ((double)width * height))));

    /// <summary>
    /// The image as a snippet. A PNG that fits as it is stays exactly as it came, once decoding it whole (for its
    /// thumbnail) has shown it's sound; anything else is drawn again as a plain PNG, scaled down until it fits.
    /// </summary>
    private static SnippetPng Keep(BitmapSource source, byte[]? original)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        if (w <= 0 || h <= 0) throw Unreadable;
        if ((long)w * h > MaxSourcePixels) throw TooBig;
        double scale = Fit(w, h);
        if (scale >= 1 && original != null && original.Length <= MaxSnippetBytes)
            return new SnippetPng(original, w, h, Thumbnail(source));

        var plain = Plain(source);
        for (int attempt = 0; attempt < 4; attempt++, scale *= 0.7)
        {
            BitmapSource sized = scale < 1 ? Scaled(plain, scale) : plain;
            var pixels = new CachedBitmap(sized, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            pixels.Freeze();
            var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
            encoder.Frames.Add(BitmapFrame.Create(pixels));
            using var png = new MemoryStream();
            encoder.Save(png);
            if (png.Length <= MaxSnippetBytes) return new SnippetPng(png.ToArray(), pixels.PixelWidth, pixels.PixelHeight, Thumbnail(pixels));
        }
        throw TooBig;
    }

    /// <summary>Scaled down (smoothly), never to less than a pixel either way, however long and thin.</summary>
    private static TransformedBitmap Scaled(BitmapSource s, double scale) =>
        new(s, new ScaleTransform(Math.Max(scale, 1.0 / s.PixelWidth), Math.Max(scale, 1.0 / s.PixelHeight)));

    /// <summary>32-bit BGR, with alpha only when the image has any.</summary>
    private static BitmapSource Plain(BitmapSource s)
    {
        var f = s.Format;
        bool alpha = f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64
            || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float
            || (s.Palette != null && s.Palette.Colors.Any(c => c.A < 255));
        var want = alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32;
        return f == want ? s : new FormatConvertedBitmap(s, want, null, 0);
    }

    /// <summary>A small copy for the strip: <see cref="ThumbPixels"/> tall, at most four times as wide. Decodes the whole image.</summary>
    private static BitmapSource Thumbnail(BitmapSource full)
    {
        double s = Math.Min(1, Math.Min((double)ThumbPixels / full.PixelHeight, 4.0 * ThumbPixels / full.PixelWidth));
        BitmapSource scaled = s < 1 ? Scaled(Plain(full), s) : Plain(full);
        var thumb = new CachedBitmap(scaled, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        thumb.Freeze();
        return thumb;
    }

    // ================= Showing a kept snippet =================

    /// <summary>
    /// A kept snippet's file, read for showing: its thumbnail (<paramref name="box"/> null), or scaled to fit
    /// <paramref name="box"/> (pixels) for the viewer. Null when the file is missing, too big or damaged; the
    /// file is checked as strictly as a paste, since anything on this PC could have replaced it.
    /// </summary>
    public static BitmapSource? Load(string path, (int Width, int Height)? box = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxEncodedBytes) return null;
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                bytes = ReadAtMost(stream, MaxEncodedBytes);
            if (PngSize(bytes) is not var (w, h) || (long)w * h > MaxSourcePixels) return null;
            var frame = new PngBitmapDecoder(new MemoryStream(bytes, writable: false), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            if (box is not var (bw, bh)) return Thumbnail(frame);

            double s = Math.Min(1, Math.Min((double)bw / frame.PixelWidth, (double)bh / frame.PixelHeight));
            BitmapSource sized = s < 1 ? Scaled(Plain(frame), s) : Plain(frame);
            var image = new CachedBitmap(sized, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
