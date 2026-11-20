// New file, no Swift original. The parts of the Windows Imaging Component (WIC) that WPF's BitmapDecoder and BitmapEncoder
// don't reach: an encoder's options and colour profile (HEIC's quality, WicHeifEncoder) and a RAW codec's development
// interface (IWICDevelopRaw: the camera's white balance, WicImageDecoder). COM finds a method by its place in the
// interface, so every interface below declares its methods in wincodec.h's order, the inherited ones first, up to the last
// one Glimmer calls (checked against the Windows SDK header as TerraFX.Interop.Windows publishes it, and against Wine's
// wincodec.idl). Methods Glimmer never calls keep their place with simplified parameters.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Glimmer.App.Platform;

internal static class Wic
{
    public static readonly Guid ContainerFormatHeif = new("e1e62521-6787-405b-a339-500715b5763f");
    public static readonly Guid PixelFormat32bppRGBA = new("f5c7ad2d-6a8d-43dd-a7a8-a29935261ae9");
    public static readonly Guid PixelFormat32bppBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    /// <summary>The pixel formats with an alpha channel an encoder may answer SetPixelFormat with (wincodec.h).</summary>
    private static readonly HashSet<Guid> WithAlpha =
    [
        PixelFormat32bppRGBA, PixelFormat32bppBGRA,
        new("6fddc324-4e03-4bfe-b185-3d77768dc910"),   // 32bppPBGRA
        new("3cc4a650-a527-4d37-a916-3142c7ebedba"),   // 32bppPRGBA
        new("6fddc324-4e03-4bfe-b185-3d77768dc916"),   // 64bppRGBA
        new("1562ff7c-d352-46f9-979e-42976b792246"),   // 64bppBGRA
        new("6fddc324-4e03-4bfe-b185-3d77768dc917"),   // 64bppPRGBA
        new("8c518e8e-a4ec-468b-ae70-c9a35a9c5530"),   // 64bppPBGRA
        new("6fddc324-4e03-4bfe-b185-3d77768dc93a"),   // 64bppRGBAHalf
        new("58ad26c2-c623-4d9d-b320-387e49f8c442"),   // 64bppPRGBAHalf
        new("6fddc324-4e03-4bfe-b185-3d77768dc919"),   // 128bppRGBAFloat
        new("6fddc324-4e03-4bfe-b185-3d77768dc91a"),   // 128bppPRGBAFloat
    ];

    public static bool HasAlpha(Guid pixelFormat) => WithAlpha.Contains(pixelFormat);

    public const int DecodeMetadataCacheOnDemand = 0, EncoderNoCache = 2, DitherTypeNone = 0, PaletteTypeCustom = 0;
    public const int AsShotParameterSet = 1;

    private static readonly Guid FactoryClass = new("cacaf262-9370-4615-a13b-9f5539da4c0a");   // CLSID_WICImagingFactory

    public static IWICImagingFactory Factory() =>
        (IWICImagingFactory)(Activator.CreateInstance(Type.GetTypeFromCLSID(FactoryClass, throwOnError: true)!)
                             ?? throw new COMException("Windows has no imaging component."));

    /// <summary>A COM stream holding a copy of <paramref name="data"/>, for a decoder.</summary>
    public static unsafe IStream StreamOver(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            return SHCreateMemStream(p, (uint)data.Length) ?? throw new OutOfMemoryException("No memory for the picture's stream.");
        }
    }

    /// <summary>An empty COM stream in memory that grows as an encoder writes.</summary>
    public static IStream NewStream()
    {
        Check(CreateStreamOnHGlobal(IntPtr.Zero, deleteOnRelease: true, out var stream), "a stream in memory");
        return stream;
    }

    /// <summary>Everything written to <paramref name="stream"/>.</summary>
    public static unsafe byte[] Bytes(IStream stream)
    {
        stream.Stat(out var stat, 1 /* STATFLAG_NONAME */);
        if (stat.cbSize is < 0 or > int.MaxValue) throw new InvalidOperationException("The encoded picture is larger than Glimmer can keep.");
        var bytes = new byte[stat.cbSize];
        stream.Seek(0, 0 /* STREAM_SEEK_SET */, IntPtr.Zero);
        var chunk = new byte[Math.Min(bytes.Length, 1 << 20)];
        int done = 0, read = 0;
        while (done < bytes.Length)
        {
            stream.Read(chunk, Math.Min(chunk.Length, bytes.Length - done), (IntPtr)(&read));
            if (read <= 0) throw new InvalidOperationException("The encoded picture came back short.");
            chunk.AsSpan(0, read).CopyTo(bytes.AsSpan(done));
            done += read;
        }
        return bytes;
    }

    /// <summary>Throws with the failing step and Windows' code when <paramref name="hr"/> is a failure.</summary>
    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"Windows' imaging component failed at {what} (0x{hr:X8}).", hr);
    }

    /// <summary>Lets go of a COM object now rather than when the garbage collector gets to it (a decoder holds a copy of
    /// the whole file, an encoder its picture).</summary>
    public static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    /// <summary>Runs <paramref name="work"/> in the multithreaded apartment: on the calling thread when it is in it, else
    /// on the thread pool, waiting for it. Codecs that use Media Foundation (HEIC's) are made for it, and a UI thread's
    /// apartment would otherwise be the codec's too.</summary>
    public static T InMultithreadedApartment<T>(Func<T> work) =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.STA ? Task.Run(work).GetAwaiter().GetResult() : work();

    [DllImport("shlwapi.dll", EntryPoint = "SHCreateMemStream")]
    [return: MarshalAs(UnmanagedType.Interface)]
    private static extern unsafe IStream? SHCreateMemStream(byte* init, uint size);

    [DllImport("ole32.dll")]
    private static extern int CreateStreamOnHGlobal(IntPtr hGlobal, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IStream stream);
}

[ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICImagingFactory
{
    [PreserveSig] int CreateDecoderFromFilename(IntPtr filename, IntPtr vendor, uint desiredAccess, int metadataOptions, out IntPtr decoder);
    [PreserveSig] int CreateDecoderFromStream(IStream stream, IntPtr vendor, int metadataOptions, out IWICBitmapDecoder decoder);
    [PreserveSig] int CreateDecoderFromFileHandle(IntPtr file, IntPtr vendor, int metadataOptions, out IntPtr decoder);
    [PreserveSig] int CreateComponentInfo(ref Guid component, out IntPtr info);
    [PreserveSig] int CreateDecoder(ref Guid containerFormat, IntPtr vendor, out IntPtr decoder);
    [PreserveSig] int CreateEncoder(ref Guid containerFormat, IntPtr vendor, out IWICBitmapEncoder encoder);
    [PreserveSig] int CreatePalette(out IntPtr palette);
    [PreserveSig] int CreateFormatConverter(out IWICFormatConverter converter);
    [PreserveSig] int CreateBitmapScaler(out IntPtr scaler);
    [PreserveSig] int CreateBitmapClipper(out IntPtr clipper);
    [PreserveSig] int CreateBitmapFlipRotator(out IntPtr rotator);
    [PreserveSig] int CreateStream(out IntPtr stream);
    [PreserveSig] int CreateColorContext(out IWICColorContext context);
    [PreserveSig] int CreateColorTransformer(out IntPtr transformer);
    [PreserveSig] int CreateBitmap(uint width, uint height, ref Guid pixelFormat, int option, out IntPtr bitmap);
    [PreserveSig] int CreateBitmapFromSource(IntPtr source, int option, out IntPtr bitmap);
    [PreserveSig] int CreateBitmapFromSourceRect(IntPtr source, uint x, uint y, uint width, uint height, out IntPtr bitmap);
    [PreserveSig] int CreateBitmapFromMemory(uint width, uint height, ref Guid pixelFormat, uint stride, uint bufferSize, IntPtr buffer, out IWICBitmapSource bitmap);
}

[ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapDecoder
{
    [PreserveSig] int QueryCapability(IStream stream, out uint capability);
    [PreserveSig] int Initialize(IStream stream, int cacheOptions);
    [PreserveSig] int GetContainerFormat(out Guid containerFormat);
    [PreserveSig] int GetDecoderInfo(out IntPtr info);
    [PreserveSig] int CopyPalette(IntPtr palette);
    [PreserveSig] int GetMetadataQueryReader(out IntPtr reader);
    [PreserveSig] int GetPreview(out IntPtr preview);
    [PreserveSig] int GetColorContexts(uint count, IntPtr contexts, out uint actual);
    [PreserveSig] int GetThumbnail(out IntPtr thumbnail);
    [PreserveSig] int GetFrameCount(out uint count);
    [PreserveSig] int GetFrame(uint index, out IWICBitmapFrameDecode frame);
}

[ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapSource
{
    [PreserveSig] int GetSize(out uint width, out uint height);
    [PreserveSig] int GetPixelFormat(out Guid pixelFormat);
    [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
    [PreserveSig] int CopyPalette(IntPtr palette);
    [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
}

[ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapFrameDecode
{
    // IWICBitmapSource
    [PreserveSig] int GetSize(out uint width, out uint height);
    [PreserveSig] int GetPixelFormat(out Guid pixelFormat);
    [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
    [PreserveSig] int CopyPalette(IntPtr palette);
    [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
    // IWICBitmapFrameDecode
    [PreserveSig] int GetMetadataQueryReader(out IntPtr reader);
    [PreserveSig] int GetColorContexts(uint count, IntPtr contexts, out uint actual);
    [PreserveSig] int GetThumbnail(out IntPtr thumbnail);
}

/// <summary>A RAW codec's frame, when the codec offers development (slots 11–32 of 43; the rest aren't called).</summary>
[ComImport, Guid("fbec5e44-f7be-4b65-b7f8-c0c81fef026d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICDevelopRaw
{
    // IWICBitmapSource (3–7)
    [PreserveSig] int GetSize(out uint width, out uint height);
    [PreserveSig] int GetPixelFormat(out Guid pixelFormat);
    [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
    [PreserveSig] int CopyPalette(IntPtr palette);
    [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
    // IWICBitmapFrameDecode (8–10)
    [PreserveSig] int GetMetadataQueryReader(out IntPtr reader);
    [PreserveSig] int GetColorContexts(uint count, IntPtr contexts, out uint actual);
    [PreserveSig] int GetThumbnail(out IntPtr thumbnail);
    // IWICDevelopRaw (11–32)
    [PreserveSig] int QueryRawCapabilitiesInfo(ref WICRawCapabilitiesInfo info);
    [PreserveSig] int LoadParameterSet(int parameterSet);
    [PreserveSig] int GetCurrentParameterSet(out IntPtr parameterSet);
    [PreserveSig] int SetExposureCompensation(double ev);
    [PreserveSig] int GetExposureCompensation(out double ev);
    [PreserveSig] int SetWhitePointRGB(uint red, uint green, uint blue);
    [PreserveSig] int GetWhitePointRGB(out uint red, out uint green, out uint blue);
    [PreserveSig] int SetNamedWhitePoint(int whitePoint);
    [PreserveSig] int GetNamedWhitePoint(out int whitePoint);
    [PreserveSig] int SetWhitePointKelvin(uint kelvin);
    [PreserveSig] int GetWhitePointKelvin(out uint kelvin);
    [PreserveSig] int GetKelvinRangeInfo(out uint minimum, out uint maximum, out uint step);
    [PreserveSig] int SetContrast(double contrast);
    [PreserveSig] int GetContrast(out double contrast);
    [PreserveSig] int SetGamma(double gamma);
    [PreserveSig] int GetGamma(out double gamma);
    [PreserveSig] int SetSharpness(double sharpness);
    [PreserveSig] int GetSharpness(out double sharpness);
    [PreserveSig] int SetSaturation(double saturation);
    [PreserveSig] int GetSaturation(out double saturation);
    [PreserveSig] int SetTint(double tint);
    [PreserveSig] int GetTint(out double tint);
}

/// <summary>WICRawCapabilitiesInfo: 18 four-byte fields. A WICRawCapabilities value is 0 (not supported), 1 (get) or 2
/// (get and set).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WICRawCapabilitiesInfo
{
    public uint Size;
    public uint CodecMajorVersion;
    public uint CodecMinorVersion;
    public int ExposureCompensationSupport;
    public int ContrastSupport;
    public int RGBWhitePointSupport;
    public int NamedWhitePointSupport;
    public uint NamedWhitePointSupportMask;
    public int KelvinWhitePointSupport;
    public int GammaSupport;
    public int TintSupport;
    public int SaturationSupport;
    public int SharpnessSupport;
    public int NoiseReductionSupport;
    public int DestinationColorProfileSupport;
    public int ToneCurveSupport;
    public int RotationSupport;
    public int RenderModeSupport;
}

[ComImport, Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapEncoder
{
    [PreserveSig] int Initialize(IStream stream, int cacheOption);
    [PreserveSig] int GetContainerFormat(out Guid containerFormat);
    [PreserveSig] int GetEncoderInfo(out IntPtr info);
    [PreserveSig] int SetColorContexts(uint count, IntPtr contexts);
    [PreserveSig] int SetPalette(IntPtr palette);
    [PreserveSig] int SetThumbnail(IntPtr thumbnail);
    [PreserveSig] int SetPreview(IntPtr preview);
    /// <remarks><paramref name="options"/> is [in, out]: pass null in, get the frame's options back.</remarks>
    [PreserveSig] int CreateNewFrame(out IWICBitmapFrameEncode frame, ref IPropertyBag2? options);
    [PreserveSig] int Commit();
    [PreserveSig] int GetMetadataQueryWriter(out IntPtr writer);
}

[ComImport, Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICBitmapFrameEncode
{
    [PreserveSig] int Initialize(IPropertyBag2? options);
    [PreserveSig] int SetSize(uint width, uint height);
    [PreserveSig] int SetResolution(double dpiX, double dpiY);
    [PreserveSig] int SetPixelFormat(ref Guid pixelFormat);
    [PreserveSig] int SetColorContexts(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IWICColorContext[] contexts);
    [PreserveSig] int SetPalette(IntPtr palette);
    [PreserveSig] int SetThumbnail(IntPtr thumbnail);
    [PreserveSig] int WritePixels(uint lineCount, uint stride, uint bufferSize, IntPtr pixels);
    [PreserveSig] int WriteSource(IWICBitmapSource source, IntPtr rect);
    [PreserveSig] int Commit();
    [PreserveSig] int GetMetadataQueryWriter(out IntPtr writer);
}

[ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICFormatConverter
{
    // IWICBitmapSource
    [PreserveSig] int GetSize(out uint width, out uint height);
    [PreserveSig] int GetPixelFormat(out Guid pixelFormat);
    [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
    [PreserveSig] int CopyPalette(IntPtr palette);
    [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
    // IWICFormatConverter
    [PreserveSig] int Initialize(IWICBitmapSource source, ref Guid destinationFormat, int dither, IntPtr palette, double alphaThresholdPercent, int paletteTranslate);
    [PreserveSig] int CanConvert(ref Guid sourceFormat, ref Guid destinationFormat, out int canConvert);
}

[ComImport, Guid("3c613a02-34b2-44ea-9a7c-45aea9c6fd6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWICColorContext
{
    [PreserveSig] int InitializeFromFilename([MarshalAs(UnmanagedType.LPWStr)] string filename);
    [PreserveSig] int InitializeFromMemory([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] buffer, uint size);
}

/// <summary>An encoder's options (ocidl.h): Read, then Write, the only one called.</summary>
[ComImport, Guid("22f55882-280b-11d0-a8a9-00a0c90c2004"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyBag2
{
    [PreserveSig] int Read(uint count, IntPtr propertyBag, IntPtr errorLog, IntPtr values, IntPtr errors);
    [PreserveSig] int Write(uint count, ref PROPBAG2 propertyBag, [MarshalAs(UnmanagedType.Struct)] ref object value);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PROPBAG2
{
    public uint Type;
    public ushort VariantType;
    public ushort ClipboardFormat;
    public uint Hint;
    [MarshalAs(UnmanagedType.LPWStr)] public string Name;
    public Guid Class;
}
