// New file, no Swift original. Makes Glimmer for Windows' icons from the original's artwork (glimmer-fullbleed-1024.png,
// copied from glimmer-native's design/icon, MIT): the app's (src/Glimmer.App/Glimmer.ico, also the console companion's)
// and the installer's (src/Glimmer.Setup/GlimmerSetup.ico, the same with a download badge). Windows icons fill more of
// their square than the Mac's, so the artwork is drawn into a rounded tile 92 % of the icon; at the smallest sizes the
// tile fills the icon and the glyph is shown larger, so the sparkle and the picture stay readable. Every size is drawn
// from the 1024 px artwork (no size is scaled from another) and stored as PNG in the .ico.
//
//   dotnet run design/icon/make_icons.cs -- <repo root> [<folder for preview PNGs>]
// (Package versions come from the repository's Directory.Packages.props.)
#:package SkiaSharp
#:package SkiaSharp.NativeAssets.Linux.NoDependencies
#:property PublishAot=false
using SkiaSharp;

var root = args.Length > 0 ? args[0] : ".";
var previews = args.Length > 1 ? args[1] : null;
using var art = SKBitmap.Decode(Path.Combine(root, "design", "icon", "glimmer-fullbleed-1024.png")) ?? throw new FileNotFoundException("glimmer-fullbleed-1024.png");
Console.WriteLine($"artwork {art.Width}×{art.Height}, corner alpha {art.GetPixel(2, 2).Alpha}");
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

byte[] Draw(int size, bool badge)
{
    var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    float s = size;
    bool small = size <= 24;
    // The tile: 92 % of the icon with Windows' gentle rounding; the whole icon at the smallest sizes.
    float inset = small ? 0 : s * 0.04f;
    var tile = new SKRect(inset, inset, s - inset, s - inset);
    float radius = tile.Width * (small ? 0.18f : 0.22f);
    using (var clip = new SKPath())
    {
        clip.AddRoundRect(tile, radius, radius);
        canvas.Save();
        canvas.ClipPath(clip, antialias: true);
        // The glyph sits in the middle of the artwork: crop towards it as the icon gets smaller.
        float crop = size <= 24 ? 0.62f : size <= 48 ? 0.78f : 0.9f;
        float side = art.Width * crop, offset = (art.Width - side) / 2;
        using var image = SKImage.FromBitmap(art);
        canvas.DrawImage(image, new SKRect(offset, offset, offset + side, offset + side), tile,
                         new SKSamplingOptions(SKCubicResampler.Mitchell));
        canvas.Restore();
        // A faint edge, so the dark tile holds its shape on a dark taskbar.
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, s / 128), Color = new SKColor(255, 255, 255, 40) };
        var inner = tile;
        inner.Inflate(-edge.StrokeWidth / 2, -edge.StrokeWidth / 2);
        canvas.DrawRoundRect(inner, radius, radius, edge);
    }
    if (badge)
    {
        // A download badge in the corner: the artwork's neon purple, a dark ring to part it from the tile, and a white
        // arrow over a tray.
        float d = s * (small ? 0.62f : 0.5f);
        var centre = new SKPoint(s - d / 2 - s * 0.01f, s - d / 2 - s * 0.01f);
        using var ring = new SKPaint { IsAntialias = true, Color = new SKColor(10, 6, 20) };
        canvas.DrawCircle(centre, d / 2, ring);
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(centre.X, centre.Y - d / 2), new SKPoint(centre.X, centre.Y + d / 2),
                                                    [new SKColor(0xE0, 0x8C, 0xFF), new SKColor(0x9B, 0x2C, 0xF0)], SKShaderTileMode.Clamp),
        };
        canvas.DrawCircle(centre, d / 2 - Math.Max(1, d * 0.07f), fill);
        using var arrow = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, Color = SKColors.White, StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round, StrokeWidth = Math.Max(1.2f, d * 0.11f),
        };
        float u = d * 0.2f;
        using var path = new SKPath();
        path.MoveTo(centre.X, centre.Y - u * 1.15f);
        path.LineTo(centre.X, centre.Y + u * 0.45f);
        path.MoveTo(centre.X - u * 0.75f, centre.Y - u * 0.25f);
        path.LineTo(centre.X, centre.Y + u * 0.5f);
        path.LineTo(centre.X + u * 0.75f, centre.Y - u * 0.25f);
        if (!small)
        {
            path.MoveTo(centre.X - u * 1.1f, centre.Y + u * 1.2f);
            path.LineTo(centre.X + u * 1.1f, centre.Y + u * 1.2f);
        }
        canvas.DrawPath(path, arrow);
    }
    using var snapshot = surface.Snapshot();
    using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
    return png.ToArray();
}

void WriteIcon(string path, bool badge)
{
    var images = sizes.Select(size => (Size: size, Png: Draw(size, badge))).ToList();
    using var file = new BinaryWriter(File.Create(path));
    file.Write((ushort)0); file.Write((ushort)1); file.Write((ushort)images.Count);   // ICONDIR
    int offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        file.Write((byte)(size >= 256 ? 0 : size)); file.Write((byte)(size >= 256 ? 0 : size));
        file.Write((byte)0); file.Write((byte)0);              // no palette
        file.Write((ushort)1); file.Write((ushort)32);         // planes, bits per pixel
        file.Write(png.Length); file.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images) file.Write(png);
    Console.WriteLine($"{path}: {images.Count} sizes, {offset} bytes");
    if (previews is not null)
    {
        Directory.CreateDirectory(previews);
        var name = Path.GetFileNameWithoutExtension(path);
        foreach (var (size, png) in images) File.WriteAllBytes(Path.Combine(previews, $"{name}-{size}.png"), png);
    }
}

WriteIcon(Path.Combine(root, "src", "Glimmer.App", "Glimmer.ico"), badge: false);
WriteIcon(Path.Combine(root, "src", "Glimmer.Setup", "GlimmerSetup.ico"), badge: true);
