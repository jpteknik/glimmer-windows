// Ported from Packages/GlimmerKit/Sources/GlimmerCore/GlimmerPackage.swift

using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Glimmer.Core;

public enum GlimmerPackageError
{
    NotAPackage,
    MissingManifest,
    UnsupportedFormatVersion,
    CorruptManifest,
    CorruptLayer,
}

/// <summary>Why a <c>.glimmer</c> document couldn't be read (Swift's <c>GlimmerPackageError</c>).</summary>
public sealed class GlimmerPackageException : Exception
{
    private GlimmerPackageException(GlimmerPackageError error, string message, int? formatVersion = null, string? reason = null)
        : base(message)
    {
        Error = error;
        FormatVersion = formatVersion;
        Reason = reason;
    }

    public GlimmerPackageError Error { get; }
    /// <summary>The newer format's number, for <see cref="GlimmerPackageError.UnsupportedFormatVersion"/>.</summary>
    public int? FormatVersion { get; }
    /// <summary>What is damaged, for <see cref="GlimmerPackageError.CorruptManifest"/> and <see cref="GlimmerPackageError.CorruptLayer"/>.</summary>
    public string? Reason { get; }

    public static GlimmerPackageException NotAPackage() =>
        new(GlimmerPackageError.NotAPackage, "The file is not a Glimmer document package.");

    public static GlimmerPackageException MissingManifest() =>
        new(GlimmerPackageError.MissingManifest, "The document is missing its manifest.");

    public static GlimmerPackageException UnsupportedFormatVersion(int version) =>
        new(GlimmerPackageError.UnsupportedFormatVersion, $"The document was saved by a newer version of Glimmer (format {version}).", formatVersion: version);

    public static GlimmerPackageException CorruptManifest(string reason) =>
        new(GlimmerPackageError.CorruptManifest, $"The document manifest is damaged: {reason}", reason: reason);

    public static GlimmerPackageException CorruptLayer(string reason) =>
        new(GlimmerPackageError.CorruptLayer, $"A layer in the document is damaged: {reason}", reason: reason);

    /// <summary>The same error (kind and particulars), as Swift's <c>Equatable</c> compares them.</summary>
    public bool SameError(GlimmerPackageException other) =>
        Error == other.Error && FormatVersion == other.FormatVersion && Reason == other.Reason;
}

/// <summary>
/// Reads and writes the <c>.glimmer</c> package layout:
/// <code>
/// Name.glimmer
///   manifest.json
///   layers/&lt;uuid&gt;.tiles
///   QuickLook/Thumbnail.png, Preview.png
/// </code>
/// On disk it is a ZIP of these files here (see <see cref="PackageContents"/>) and a package folder on the Mac.
/// </summary>
public static class GlimmerPackage
{
    public const string ManifestFilename = "manifest.json";
    public const string LayersDirectory = "layers";
    /// <summary>Finder/QuickLook previews (PNG), read by the Mac's QuickLook extensions.</summary>
    public const string QuickLookDirectory = "QuickLook";
    public const string ThumbnailFilename = "Thumbnail.png";
    public const string PreviewFilename = "Preview.png";

    // MARK: Write

    /// <summary>The package's files for <paramref name="state"/> (the Mac's <c>fileWrapper(for:cache:quickLook:)</c>).</summary>
    /// <param name="cache">Reuses encoded tile files whose tiles haven't changed.</param>
    /// <param name="quickLook">PNG thumbnail and preview, kept for the Mac's Finder.</param>
    public static PackageContents Contents(DocumentState state, EncodedLayerCache? cache = null, (byte[] Thumbnail, byte[] Preview)? quickLook = null)
    {
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        void AddTiles(TileGrid grid, string name) => files[$"{LayersDirectory}/{name}"] = cache?.Data(grid) ?? TileCodec.Encode(grid);

        LayerRecord Record(Layer layer)
        {
            var id = SwiftJson.UuidString(layer.Id);
            LayerRecord.RawRecord? raw = null;
            if (layer.Raw is { } source && source.Data.Length <= RawSource.MaxBytes)
            {
                bool changed = false;
                if (layer.Content is RasterContent rc) changed = source.DevelopedSignature != rc.Tiles.ContentSignature;
                raw = new LayerRecord.RawRecord(source.FileName, source.Settings, source.Data.Length, changed ? true : null);
                files[$"{LayersDirectory}/{id}.raw"] = source.Data;
            }
            LayerRecord.MaskRecord? mask = null;
            if (layer.Mask is { } m)
            {
                mask = new LayerRecord.MaskRecord(m.DefaultValue, m.IsEnabled, m.Density, m.Feather);
                AddTiles(m.Tiles, $"{id}.mask.tiles");
            }
            var r = new LayerRecord
            {
                Id = layer.Id,
                Name = layer.Name,
                Kind = LayerRecordKind.Raster,
                Visible = layer.IsVisible,
                Opacity = layer.Opacity,
                BlendMode = layer.BlendMode,
                Clipped = layer.IsClipped ? true : null,
                Effects = layer.Effects.HasEnabled || layer.Effects != LayerEffects.None ? layer.Effects : null,
                VectorMask = layer.VectorMask,
                FormerText = layer.FormerText,
                Raw = raw,
                Mask = mask,
            };
            switch (layer.Content)
            {
                case RasterContent raster:
                    AddTiles(raster.Tiles, $"{id}.tiles");
                    return r with { Kind = LayerRecordKind.Raster };
                case AdjustmentContent adjustment:
                    return r with { Kind = LayerRecordKind.Adjustment, Adjustment = adjustment.Adjustment };
                case FillContent fill:
                    return r with { Kind = LayerRecordKind.Fill, Fill = fill.Fill };
                case SmartContent smart:
                    AddTiles(smart.Smart.Source, $"{id}.smart.tiles");
                    return r with
                    {
                        Kind = LayerRecordKind.Smart,
                        Smart = new LayerRecord.SmartRecord(smart.Smart.Width, smart.Smart.Height, smart.Smart.Transform,
                                                            smart.Smart.Filters.IsEmpty ? null : smart.Smart.Filters),
                    };
                case TextContent text:
                    return r with { Kind = LayerRecordKind.Text, Text = text.Text };
                case GroupContent group:
                    return r with { Kind = LayerRecordKind.Group, Expanded = group.Group.IsExpanded, Children = [.. group.Group.Children.Select(Record)] };
                default:
                    throw new InvalidOperationException("Unknown layer content.");
            }
        }

        var manifest = new DocumentManifest(state.Width, state.Height, state.ColorSpace, state.BlendSpace,
                                            state.Layers.Select(Record).ToList(), state.ActiveLayerID)
        {
            Paths = state.Paths.IsDefaultOrEmpty ? null : state.Paths,
            Aids = state.Aids == new ViewAids() ? null : state.Aids,
            Link = state.Link,
            ImportReport = state.ImportReport,
        };
        files[ManifestFilename] = JsonSerializer.SerializeToUtf8Bytes(manifest, SwiftJson.Pretty);
        if (quickLook is { } ql)
        {
            files[$"{QuickLookDirectory}/{ThumbnailFilename}"] = ql.Thumbnail;
            files[$"{QuickLookDirectory}/{PreviewFilename}"] = ql.Preview;
        }
        return new PackageContents(files, [LayersDirectory]);
    }

    /// <summary>
    /// Saves <paramref name="state"/> as a <c>.glimmer</c> file (a ZIP), atomically: see <see cref="DocumentSaver"/>.
    /// </summary>
    public static void Save(DocumentState state, string path, EncodedLayerCache? cache = null, (byte[] Thumbnail, byte[] Preview)? quickLook = null) =>
        DocumentSaver.Write(Contents(state, cache, quickLook), path);

    // MARK: Read

    /// <summary>Opens a <c>.glimmer</c> document: a ZIP made here, a package folder from a Mac, or a Finder-compressed package.</summary>
    public static DocumentState Load(string path) => State(PackageContents.Read(path));

    /// <exception cref="GlimmerPackageException">The package can't be read.</exception>
    public static DocumentState State(PackageContents contents)
    {
        if (contents[ManifestFilename] is not { } data) throw GlimmerPackageException.MissingManifest();
        var manifest = DecodeManifest(data.Span);
        var files = contents.FilesIn(LayersDirectory);

        TileGrid Tiles(string name, LayerRecord record)
        {
            if (!files.TryGetValue(name, out var bytes)) throw GlimmerPackageException.CorruptLayer($"missing pixels for “{record.Name}”");
            return TileCodec.Decode(bytes.Span);
        }

        Layer MakeLayer(LayerRecord r)
        {
            var id = SwiftJson.UuidString(r.Id);
            LayerContent content = r.Kind switch
            {
                LayerRecordKind.Raster => new RasterContent(Tiles($"{id}.tiles", r)),
                LayerRecordKind.Adjustment => new AdjustmentContent(r.Adjustment ?? throw GlimmerPackageException.CorruptLayer($"adjustment “{r.Name}”")),
                LayerRecordKind.Fill => new FillContent(r.Fill ?? throw GlimmerPackageException.CorruptLayer($"fill “{r.Name}”")),
                LayerRecordKind.Smart => r.Smart is { } s
                    ? new SmartContent(new SmartObject(Tiles($"{id}.smart.tiles", r), s.Width, s.Height, s.Transform, s.Filters))
                    : throw GlimmerPackageException.CorruptLayer($"smart object “{r.Name}”"),
                LayerRecordKind.Text => new TextContent(r.Text ?? throw GlimmerPackageException.CorruptLayer($"text “{r.Name}”")),
                LayerRecordKind.Group => new GroupContent(new LayerGroup((r.Children ?? []).Select(MakeLayer), r.Expanded ?? true)),
                _ => throw GlimmerPackageException.CorruptLayer($"layer kind of “{r.Name}”"),
            };
            LayerMask? mask = null;
            if (r.Mask is { } m)
            {
                mask = new LayerMask(Tiles($"{id}.mask.tiles", r), m.DefaultValue, m.Enabled, Math.Min(Math.Max(m.Density, 0), 1), Math.Max(0, m.Feather));
            }
            var made = new Layer(r.Name, content, r.Id, r.Visible, Math.Min(Math.Max(r.Opacity, 0), 1), r.BlendMode, mask, r.VectorMask,
                                 r.Clipped ?? false, r.Effects)
            {
                FormerText = r.FormerText is { } text ? PrefixCharacters(text, 10_000) : null,
            };
            // The RAW file is kept only when it is there whole: the layer's pixels stand without it.
            if (r.Raw is { } raw && content is RasterContent grid && files.TryGetValue($"{id}.raw", out var rawBytes)
                && rawBytes.Length == raw.ByteCount && rawBytes.Length <= RawSource.MaxBytes)
            {
                made = made with
                {
                    Raw = new RawSource(rawBytes.ToArray(), PrefixCharacters(raw.FileName, 255), raw.Settings,
                                        raw.PixelsChanged == true ? null : grid.Tiles.ContentSignature),
                };
            }
            return made;
        }

        var layers = manifest.Layers.Select(MakeLayer).ToList();
        var state = new DocumentState(manifest.Width, manifest.Height, manifest.ColorSpace, manifest.BlendSpace, layers);
        if (state.PathOf(manifest.ActiveLayerID) is not null) state = state with { ActiveLayerID = manifest.ActiveLayerID };
        return state with
        {
            Paths = manifest.Paths ?? [],
            Aids = manifest.Aids ?? new ViewAids(),
            Link = manifest.Link,
            ImportReport = manifest.ImportReport,
        };
    }

    /// <summary>Swift's <c>String.prefix(n)</c>: the first n characters (grapheme clusters).</summary>
    private static string PrefixCharacters(string s, int n)
    {
        if (s.Length <= n) return s;
        var starts = StringInfo.ParseCombiningCharacters(s);
        return starts.Length <= n ? s : s[..starts[n]];
    }

    /// <exception cref="GlimmerPackageException">No format version, a newer one, damage, or an impossible size.</exception>
    public static DocumentManifest DecodeManifest(ReadOnlySpan<byte> data)
    {
        long version;
        try
        {
            using var probe = JsonDocument.Parse(data.ToArray());
            if (probe.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("not an object");
            version = probe.RootElement.ReqInt("formatVersion");
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            throw GlimmerPackageException.CorruptManifest("no format version");
        }
        if (version > DocumentManifest.CurrentFormatVersion) throw GlimmerPackageException.UnsupportedFormatVersion((int)Math.Min(version, int.MaxValue));

        DocumentManifest manifest;
        try
        {
            manifest = SwiftJson.Deserialize<DocumentManifest>(data);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NotSupportedException)
        {
            throw GlimmerPackageException.CorruptManifest(e.Message);
        }
        if (manifest.Width is < 1 or > DocumentManifest.MaxDimension || manifest.Height is < 1 or > DocumentManifest.MaxDimension)
            throw GlimmerPackageException.CorruptManifest($"invalid size {manifest.Width}×{manifest.Height}");
        return manifest;
    }
}

/// <summary>
/// Remembers encoded layer files by tile identity so autosave only re-encodes layers that changed. Safe to use from
/// the background writer.
/// </summary>
public sealed class EncodedLayerCache
{
    private readonly Dictionary<long, byte[]> entries = [];
    private readonly Lock gate = new();
    private readonly int limit;
    private readonly long byteLimit;

    /// <param name="byteLimit">
    /// The most encoded bytes kept (a full 96-megapixel layer of detailed pixels encodes to several hundred megabytes;
    /// counting entries alone let the cache hold gigabytes).
    /// </param>
    public EncodedLayerCache(int limit = 64, long byteLimit = 384L * 1_048_576)
    {
        this.limit = limit;
        this.byteLimit = byteLimit;
    }

    /// <summary>Encoded bytes held right now.</summary>
    public long ByteCount
    {
        get { lock (gate) return entries.Values.Sum(d => (long)d.Length); }
    }

    public byte[] Data(TileGrid tiles)
    {
        long key = tiles.ContentSignature;
        lock (gate)
        {
            if (entries.TryGetValue(key, out var hit)) return hit;
        }
        var data = TileCodec.Encode(tiles);
        lock (gate)
        {
            if (entries.Count >= limit || entries.Values.Sum(d => (long)d.Length) + data.Length > byteLimit) entries.Clear();
            // A layer bigger than the whole budget isn't kept at all.
            if (data.Length <= byteLimit) entries[key] = data;
        }
        return data;
    }
}

/// <summary>
/// Binary layer pixel format (little-endian), byte for byte the Mac's:
/// <code>
/// "GLTL" | version u32 | tileSize u32 | count u32
/// per tile: x i32 | y i32 | kind u8
///   kind 0 (solid): r g b a f32
///   kind 1 (lzfse):  byteCount u32 | LZFSE(Half RGBA, 256×256, row-major)
/// </code>
/// </summary>
public static class TileCodec
{
    internal static ReadOnlySpan<byte> Magic => "GLTL"u8;
    internal const uint Version = 1;

    public static byte[] Encode(TileGrid grid)
    {
        var coords = grid.Coords.OrderBy(c => c).ToArray();
        var tiles = coords.Select(c => grid[c]!).ToArray();
        // Compressing is the slow part of saving: the pixel tiles are compressed on all cores.
        var packed = new byte[]?[coords.Length];
        Parallel.For(0, coords.Length, i =>
        {
            if (tiles[i].IsSolid) return;
            packed[i] = Glimmer.Lzfse.Lzfse.Encode(MemoryMarshal.AsBytes(tiles[i].PixelSpan));
        });
        long size = 16 + coords.Length * 9L;
        for (int i = 0; i < coords.Length; i++) size += tiles[i].IsSolid ? 16 : 4 + packed[i]!.Length;
        var output = new byte[size];
        var span = output.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], TileGeometry.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)grid.Count);
        int at = 16;
        for (int i = 0; i < coords.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[at..], coords[i].X);
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 4)..], coords[i].Y);
            at += 8;
            if (tiles[i].IsSolid)
            {
                span[at++] = 0;
                var c = tiles[i].SolidColor;
                BinaryPrimitives.WriteSingleLittleEndian(span[at..], c.X);
                BinaryPrimitives.WriteSingleLittleEndian(span[(at + 4)..], c.Y);
                BinaryPrimitives.WriteSingleLittleEndian(span[(at + 8)..], c.Z);
                BinaryPrimitives.WriteSingleLittleEndian(span[(at + 12)..], c.W);
                at += 16;
            }
            else
            {
                span[at++] = 1;
                var data = packed[i]!;
                BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)data.Length);
                at += 4;
                data.CopyTo(span[at..]);
                at += data.Length;
            }
        }
        return output;
    }

    /// <exception cref="GlimmerPackageException">The data is damaged (<see cref="GlimmerPackageError.CorruptLayer"/>).</exception>
    public static TileGrid Decode(ReadOnlySpan<byte> data)
    {
        var reader = new ByteReader(data);
        if (!reader.Bytes(4).SequenceEqual(Magic)) throw GlimmerPackageException.CorruptLayer("bad magic");
        uint version = reader.UInt32();
        if (version > Version) throw GlimmerPackageException.CorruptLayer($"tile format {version}");
        uint size = reader.UInt32();
        if (size != TileGeometry.Size) throw GlimmerPackageException.CorruptLayer($"tile size {size}");
        uint count = reader.UInt32();

        var builder = new TileGrid.Builder();
        // Read the tile list first, then decompress the pixel tiles on all cores.
        var pending = new List<(TileCoord Coord, byte[] Data)>();
        for (uint n = 0; n < count; n++)
        {
            int x = reader.Int32(), y = reader.Int32();
            byte kind = reader.Byte();
            var coord = new TileCoord(x, y);
            switch (kind)
            {
                case 0:
                    float r = reader.Single(), g = reader.Single(), b = reader.Single(), a = reader.Single();
                    builder[coord] = Tile.Solid(new Vector4(r, g, b, a));
                    break;
                case 1:
                    uint length = reader.UInt32();
                    pending.Add((coord, reader.Bytes(checked((int)Math.Min(length, int.MaxValue))).ToArray()));
                    break;
                default:
                    throw GlimmerPackageException.CorruptLayer($"unknown tile kind {kind}");
            }
        }
        var decoded = new Tile[pending.Count];
        GlimmerPackageException? failure = null;
        Parallel.For(0, pending.Count, (i, loop) =>
        {
            var (coord, compressed) = pending[i];
            byte[] raw;
            try
            {
                raw = Glimmer.Lzfse.Lzfse.Decode(compressed);
            }
            catch (Exception)
            {
                Interlocked.CompareExchange(ref failure, GlimmerPackageException.CorruptLayer($"tile {coord.X},{coord.Y} does not decompress"), null);
                loop.Stop();
                return;
            }
            if (raw.Length != TileGeometry.ByteCount)
            {
                Interlocked.CompareExchange(ref failure, GlimmerPackageException.CorruptLayer($"tile {coord.X},{coord.Y} has {raw.Length} bytes"), null);
                loop.Stop();
                return;
            }
            var pixels = new Half[TileGeometry.FloatCount];
            raw.AsSpan().CopyTo(MemoryMarshal.AsBytes(pixels.AsSpan()));
            decoded[i] = Tile.FromHalf(pixels);
        });
        if (failure is not null) throw failure;
        for (int i = 0; i < pending.Count; i++) builder[pending[i].Coord] = decoded[i];
        return builder.ToImmutable();
    }

    private ref struct ByteReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int offset;

        public ReadOnlySpan<byte> Bytes(int n)
        {
            if (n < 0 || offset + (long)n > data.Length) throw GlimmerPackageException.CorruptLayer("truncated");
            var result = data.Slice(offset, n);
            offset += n;
            return result;
        }

        public byte Byte() => Bytes(1)[0];
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));
        public float Single() => BinaryPrimitives.ReadSingleLittleEndian(Bytes(4));
    }
}
