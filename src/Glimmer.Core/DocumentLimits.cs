// Ported from Packages/GlimmerKit/Sources/GlimmerCore/DocumentLimits.swift

using System.Globalization;

namespace Glimmer.Core;

/// <summary>
/// How large a document this computer can hold, and when to say that one is getting too big.
/// </summary>
/// <remarks>
/// Pixels are kept as 16-bit floating point RGBA (8 bytes a pixel) in memory. Measured on the Mac on a 6000 × 4000
/// document with 60 layers (3 full layers, 30 photo layers, 12 smart objects, effects): about 4,100 layer tiles of
/// 512 kB and a footprint of 5.5 GB, so a document needs roughly two and a half times its own tiles (caches for smart
/// objects, effects and the flattened image) plus a base.
///
/// The Mac's texts say "this Mac"; here they say "this computer" (the only change to their wording).
/// </remarks>
public static class DocumentLimits
{
    public const int BytesPerPixel = 8;

    /// <summary>The memory the process can use: the machine's, or less when a job object or container limits it.</summary>
    public static long PhysicalMemory => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>The most pixels a canvas may have: one full layer may take two fifths of the memory.</summary>
    public static long MaxCanvasPixels(long? memory = null) => (memory ?? PhysicalMemory) / 5 * 2 / BytesPerPixel;

    private static string Gigabytes(long bytes)
    {
        double gb = bytes / 1_073_741_824.0;
        return gb >= 10 ? gb.ToString("F0", CultureInfo.InvariantCulture) + " GB" : gb.ToString("F1", CultureInfo.InvariantCulture) + " GB";
    }

    /// <summary>
    /// Why a canvas of this size can't be made on this computer, or null when it can. The sides' own limit
    /// (<see cref="DocumentManifest.MaxDimension"/>) is checked by the caller.
    /// </summary>
    public static string? Refusal(long width, long height, long? memory = null)
    {
        long mem = memory ?? PhysicalMemory;
        if (width <= 0 || height <= 0) return null;
        bool overflow = false;
        long pixels;
        try { pixels = checked(width * height); }
        catch (OverflowException) { pixels = 0; overflow = true; }
        long limit = MaxCanvasPixels(mem);
        if (!overflow && pixels <= limit) return null;
        string need = overflow ? "more memory than any computer has" : Gigabytes(pixels * BytesPerPixel);
        long side = (long)(Math.Sqrt(limit) / 1000) * 1000;
        return $"A canvas of {width} × {height} pixels needs {need} for a single layer, and this computer has {Gigabytes(mem)} of memory. "
               + $"The largest canvas here is {limit / 1_000_000} megapixels, for example {side} × {side}.";
    }

    /// <summary>
    /// About how much memory a document with this many pixel tiles takes while it is open. <paramref name="keptBytes"/>:
    /// files a document keeps whole beside its pixels (the RAW files of its RAW layers).
    /// </summary>
    public static long EstimatedBytes(long textureTiles, long keptBytes = 0) =>
        textureTiles * TileGeometry.ByteCount / 2 * 5 + 400L * 1_048_576 + keptBytes;

    /// <summary>
    /// What to tell the person once when a document outgrows this computer, or null while it fits comfortably (under
    /// three fifths of the memory).
    /// </summary>
    public static string? ComfortNotice(long textureTiles, long keptBytes = 0, long? memory = null)
    {
        long mem = memory ?? PhysicalMemory;
        long need = EstimatedBytes(textureTiles, keptBytes);
        if (need <= mem / 5 * 3) return null;
        return $"This document needs about {Gigabytes(need)} of memory, and this computer has {Gigabytes(mem)}. Glimmer keeps working, but it may get slow. "
               + "Merging layers you no longer need apart, or making the image smaller with Image › Image Size, helps.";
    }
}
