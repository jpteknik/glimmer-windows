// Ported from Packages/GlimmerKit/Sources/GlimmerCore/Histogram.swift

using SkiaSharp;

namespace Glimmer.Core;

/// <summary>256-bin histograms of an sRGB image (red, green, blue and luminance), counting only visible pixels.</summary>
public sealed record Histogram
{
    public Histogram(uint[] red, uint[] green, uint[] blue, uint[] luminance)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Luminance = luminance;
    }

    public uint[] Red { get; init; }
    public uint[] Green { get; init; }
    public uint[] Blue { get; init; }
    public uint[] Luminance { get; init; }

    /// <summary>Straight-alpha RGBA8 pixels; pixels with alpha below 1/255 are skipped.</summary>
    public Histogram(ReadOnlySpan<byte> rgba) : this(new uint[256], new uint[256], new uint[256], new uint[256])
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] == 0) continue;
            int pr = rgba[i], pg = rgba[i + 1], pb = rgba[i + 2];
            Red[pr]++;
            Green[pg]++;
            Blue[pb]++;
            // Rec. 709 luma on the encoded values, like Photoshop's RGB histogram.
            Luminance[Math.Min((pr * 2126 + pg * 7152 + pb * 722 + 5000) / 10000, 255)]++;
        }
    }

    /// <summary>
    /// Histogram of a premultiplied sRGB RGBA8 picture (what an image drawn into an 8-bit sRGB context holds): soft
    /// edges are unpremultiplied first so their colours land in the right bins.
    /// </summary>
    public static Histogram FromPremultiplied(ReadOnlySpan<byte> premultipliedRgba)
    {
        var px = premultipliedRgba.ToArray();
        Unpremultiply(px);
        return new Histogram(px);
    }

    /// <summary>
    /// Histogram of an image of <paramref name="width"/> × <paramref name="height"/> premultiplied sRGB RGBA8 pixels,
    /// scaled down first to at most <paramref name="maxSide"/> pixels a side (the Mac's <c>Histogram(image:maxSide:)</c>
    /// draws the CGImage into such a buffer). Null when the picture can't be scaled.
    /// </summary>
    public static unsafe Histogram? FromImage(ReadOnlySpan<byte> premultipliedRgba, int width, int height, int maxSide = 512)
    {
        if (width <= 0 || height <= 0 || premultipliedRgba.Length < (long)width * height * 4) return null;
        double scale = Math.Min(1, (double)maxSide / Math.Max(width, height));
        int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
        if (w == width && h == height) return FromPremultiplied(premultipliedRgba[..(width * height * 4)]);
        var srcInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var dstInfo = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var px = new byte[w * h * 4];
        fixed (byte* src = premultipliedRgba)
        fixed (byte* dst = px)
        {
            using var source = new SKPixmap(srcInfo, (IntPtr)src, width * 4);
            using var target = new SKPixmap(dstInfo, (IntPtr)dst, w * 4);
            // Medium quality, as the Mac's context draws it.
            if (!source.ScalePixels(target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))) return null;
        }
        Unpremultiply(px);
        return new Histogram(px);
    }

    /// <summary>Un-premultiply so colours of soft edges land in the right bins.</summary>
    private static void Unpremultiply(byte[] px)
    {
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            byte a8 = px[i + 3];
            if (a8 == 0 || a8 == 255) continue;
            double a = a8 / 255.0;
            for (int c = 0; c < 3; c++) px[i + c] = (byte)Math.Round(Math.Min(px[i + c] / a, 255), MidpointRounding.AwayFromZero);
        }
    }

    public uint[] Bins(CurveChannel channel) => channel switch
    {
        CurveChannel.Rgb => Luminance,
        CurveChannel.Red => Red,
        CurveChannel.Green => Green,
        _ => Blue,
    };

    public bool Equals(Histogram? other) =>
        other is not null && Red.AsSpan().SequenceEqual(other.Red) && Green.AsSpan().SequenceEqual(other.Green)
        && Blue.AsSpan().SequenceEqual(other.Blue) && Luminance.AsSpan().SequenceEqual(other.Luminance);

    public override int GetHashCode() => HashCode.Combine(Red.Length, Red.Length > 128 ? Red[128] : 0, Luminance.Length > 128 ? Luminance[128] : 0);
}

public sealed partial record DocumentState
{
    /// <summary>
    /// The document as a layer receives it: the layer itself and everything stacked above it (later in
    /// <see cref="AllLayers"/>) hidden.
    /// </summary>
    public DocumentState Below(Guid id)
    {
        var order = AllLayers.Select(l => l.Id).ToList();
        int index = order.IndexOf(id);
        if (index < 0) return this;
        var copy = this;
        foreach (var hidden in order.Skip(index)) copy = copy.UpdateLayer(hidden, l => l with { IsVisible = false });
        return copy with { Selection = null };
    }

    /// <summary>
    /// Ids of every pixel tile the document references (layers, masks, smart object sources and the selection). Solid
    /// tiles cost no pixel memory and are left out.
    /// </summary>
    public HashSet<long> TextureTileIDs
    {
        get
        {
            var ids = new HashSet<long>();
            void Add(TileGrid grid)
            {
                foreach (var tile in grid.Tiles.Values)
                    if (!tile.IsSolid) ids.Add(tile.Id);
            }
            foreach (var layer in AllLayers)
            {
                switch (layer.Content)
                {
                    case RasterContent r: Add(r.Tiles); break;
                    case SmartContent s: Add(s.Smart.Source); break;
                }
                if (layer.Mask is { } mask) Add(mask.Tiles);
            }
            if (Selection is { } selection) Add(selection.Tiles);
            return ids;
        }
    }
}
