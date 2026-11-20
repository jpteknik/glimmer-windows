// The Windows clipboard for pixels and text (the Mac's NSPasteboard). Pictures go out as PNG (keeps transparency for
// apps that read it: browsers, Office, Paint.NET, Photoshop) and as CF_DIBV5 with an alpha mask (Windows makes CF_DIB and
// CF_BITMAP from it for older apps). Pictures come in from PNG first, then CF_DIBV5, then CF_DIB, and are handed to the
// editor as PNG bytes. Uses the Win32 clipboard directly: the formats and their bytes have to be exact.
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glimmer.Editor;

namespace Glimmer.App.Platform;

public sealed class ClipboardService : IClipboardService
{
    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxr", ".wdp", ".hdp",
         ".psd", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".srw", ".pef", ".nrw"];

    private readonly uint pngFormat = RegisterClipboardFormatW("PNG");

    public uint SequenceNumber => GetClipboardSequenceNumber();

    public bool ContainsImage =>
        IsClipboardFormatAvailable(pngFormat) || IsClipboardFormatAvailable(CF_DIBV5) || IsClipboardFormatAvailable(CF_DIB);

    public byte[]? GetImagePng()
    {
        using var clipboard = OpenClipboardWithRetry();
        if (Read(pngFormat) is { Length: > 8 } png) return png;
        var dib = Read(CF_DIBV5) ?? Read(CF_DIB);
        return dib is null ? null : DibToPng(dib);
    }

    public IReadOnlyList<string> GetImageFiles()
    {
        if (!System.Windows.Clipboard.ContainsFileDropList()) return [];
        return [.. System.Windows.Clipboard.GetFileDropList().Cast<string>()
                       .Where(p => ImageExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()) || p.EndsWith(".glimmer", StringComparison.OrdinalIgnoreCase))];
    }

    public void SetImage(RgbaImage image)
    {
        byte[] png = EncodePng(image);
        byte[] dib = EncodeDibV5(image);
        using var clipboard = OpenClipboardWithRetry();
        if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
        Put(pngFormat, png);
        Put(CF_DIBV5, dib);
    }

    public void SetText(string text)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
        using var clipboard = OpenClipboardWithRetry();
        if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
        Put(CF_UNICODETEXT, bytes);
    }

    // ------------------------------------------------------------------------------------------------- reading

    private static byte[]? Read(uint format)
    {
        if (!IsClipboardFormatAvailable(format)) return null;
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = (int)GlobalSize(handle);
        var p = GlobalLock(handle);
        if (p == IntPtr.Zero || size <= 0) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(p, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>A DIB (BITMAPINFOHEADER, V4 or V5, then the pixels) as PNG bytes. 32-bit DIBs are read here so their
    /// alpha survives (WIC's BMP reader drops it for BI_RGB); everything else goes through WIC as a BMP file.</summary>
    internal static byte[]? DibToPng(byte[] dib)
    {
        if (dib.Length < 40) return null;
        int headerSize = BitConverter.ToInt32(dib, 0);
        int width = BitConverter.ToInt32(dib, 4);
        int height = BitConverter.ToInt32(dib, 8);
        int bits = BitConverter.ToUInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        if (width <= 0 || height == 0 || headerSize < 40 || headerSize > dib.Length) return null;
        bool topDown = height < 0;
        height = Math.Abs(height);
        if (bits == 32 && compression is BI_RGB or BI_BITFIELDS)
        {
            uint rMask = 0x00FF0000, gMask = 0x0000FF00, bMask = 0x000000FF, aMask = 0;
            int offset = headerSize;
            if (compression == BI_BITFIELDS)
            {
                if (headerSize >= 52) { rMask = BitConverter.ToUInt32(dib, 40); gMask = BitConverter.ToUInt32(dib, 44); bMask = BitConverter.ToUInt32(dib, 48); }
                else { rMask = BitConverter.ToUInt32(dib, 40); gMask = BitConverter.ToUInt32(dib, 44); bMask = BitConverter.ToUInt32(dib, 48); offset += 12; }
                if (headerSize >= 56) aMask = BitConverter.ToUInt32(dib, 52);
            }
            else if (headerSize >= 56)
            {
                aMask = BitConverter.ToUInt32(dib, 52);
            }
            int stride = width * 4;
            if ((long)offset + (long)stride * height > dib.Length) return null;
            var pixels = new byte[stride * height];
            bool anyAlpha = false, allOpaque = true;
            for (int y = 0; y < height; y++)
            {
                int src = offset + (topDown ? y : height - 1 - y) * stride;
                for (int x = 0; x < width; x++)
                {
                    uint v = BitConverter.ToUInt32(dib, src + x * 4);
                    byte r = Channel(v, rMask), g = Channel(v, gMask), b = Channel(v, bMask);
                    // BI_RGB has no alpha by the book, but many apps put one there: use it unless it is all zero.
                    byte a = aMask != 0 ? Channel(v, aMask) : (byte)(v >> 24);
                    anyAlpha |= a != 0;
                    allOpaque &= a == 255;
                    int d = y * stride + x * 4;
                    pixels[d] = b; pixels[d + 1] = g; pixels[d + 2] = r; pixels[d + 3] = a;
                }
            }
            if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            return Png(source);
        }
        // Other depths: as a BMP file, through WIC.
        int paletteBytes = bits <= 8 ? (BitConverter.ToInt32(dib, 32) is var used && used > 0 ? used : 1 << bits) * 4 : 0;
        int masks = compression == BI_BITFIELDS && headerSize == 40 ? 12 : 0;
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(14 + headerSize + masks + paletteBytes).CopyTo(file, 10);
        dib.CopyTo(file, 14);
        try
        {
            var decoder = new BmpBitmapDecoder(new MemoryStream(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return Png(decoder.Frames[0]);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static byte Channel(uint v, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        uint c = (v & mask) >> shift;
        return max == 255 ? (byte)c : (byte)Math.Round(c * 255.0 / max);
    }

    private static byte[] Png(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ------------------------------------------------------------------------------------------------- writing

    /// <summary>Premultiplied BGRA (the editor's) to straight BGRA (PNG's and DIBV5's).</summary>
    private static byte[] Straight(RgbaImage image, out int stride)
    {
        stride = image.Width * 4;
        var src = image.Bgra.Span;
        var dst = new byte[stride * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int s = y * image.Stride + x * 4, d = y * stride + x * 4;
                byte a = src[s + 3];
                dst[d + 3] = a;
                if (a == 0) continue;
                for (int c = 0; c < 3; c++) dst[d + c] = a == 255 ? src[s + c] : (byte)Math.Min(255, (src[s + c] * 255 + a / 2) / a);
            }
        }
        return dst;
    }

    private static byte[] EncodePng(RgbaImage image)
    {
        var pixels = Straight(image, out int stride);
        return Png(BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride));
    }

    /// <summary>BITMAPV5HEADER, BI_BITFIELDS with an alpha mask, sRGB, bottom-up rows of straight BGRA.</summary>
    private static byte[] EncodeDibV5(RgbaImage image)
    {
        var pixels = Straight(image, out int stride);
        const int header = 124;
        var dib = new byte[header + pixels.Length];
        void Int(int at, int v) => BitConverter.GetBytes(v).CopyTo(dib, at);
        void UInt(int at, uint v) => BitConverter.GetBytes(v).CopyTo(dib, at);
        Int(0, header);
        Int(4, image.Width);
        Int(8, image.Height);                 // positive: bottom-up, which every reader understands
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
        BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);
        Int(16, BI_BITFIELDS);
        Int(20, pixels.Length);
        Int(24, 2835);                         // 72 dpi
        Int(28, 2835);
        UInt(40, 0x00FF0000);
        UInt(44, 0x0000FF00);
        UInt(48, 0x000000FF);
        UInt(52, 0xFF000000);
        UInt(56, LCS_sRGB);
        Int(108, 4);                           // LCS_GM_IMAGES
        for (int y = 0; y < image.Height; y++)
            Buffer.BlockCopy(pixels, (image.Height - 1 - y) * stride, dib, header + y * stride, stride);
        return dib;
    }

    private static void Put(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero) throw new OutOfMemoryException();
        var p = GlobalLock(handle);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        GlobalUnlock(handle);
        // The clipboard owns the memory once SetClipboardData succeeds; free it only when it didn't.
        if (SetClipboardData(format, handle) == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            GlobalFree(handle);
            throw new Win32Exception(error);
        }
    }

    /// <summary>Another app may hold the clipboard for a moment (a clipboard manager, Remote Desktop): try for ~half a second.</summary>
    private static ClipboardLease OpenClipboardWithRetry()
    {
        var owner = Application.Current?.MainWindow is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;
        for (int attempt = 0; ; attempt++)
        {
            if (OpenClipboard(owner)) return new ClipboardLease();
            if (attempt >= 25) throw new Win32Exception(Marshal.GetLastWin32Error(), Glimmer.App.Localization.Loc.T("The clipboard is in use by another app."));
            Thread.Sleep(20);
        }
    }

    private readonly struct ClipboardLease : IDisposable
    {
        public void Dispose() => CloseClipboard();
    }

    // ------------------------------------------------------------------------------------------------- Win32

    private const uint CF_DIB = 8, CF_UNICODETEXT = 13, CF_DIBV5 = 17;
    private const int BI_RGB = 0, BI_BITFIELDS = 3;
    private const uint LCS_sRGB = 0x73524742;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr memory);
}
