// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/TextTransformTests.swift
using System.Text.Json;
using System.Text.Json.Nodes;
using Glimmer.Core;

namespace Glimmer.Render.Tests;

/// <summary>Free Transform keeps text editable: the transform rides on the text layer.</summary>
public class TextTransformTests
{
    private static TextLayer Text() => new("Glimmer", new Vec2(100, 100), size: 40, color: SRGBColor.Black);

    [Fact]
    public void AQuarterTurnSwapsTheBounds()
    {
        var t = Text();
        var plain = t.Bounds;
        var c = new Vec2(plain.X + plain.Width / 2.0, plain.Y + plain.Height / 2.0);
        // Rotate 90° about the text's centre.
        t = t with
        {
            Transform = new AffineTransform(tx: c.X, ty: c.Y)
                .Concatenating(new AffineTransform(a: 0, b: 1, c: -1, d: 0))
                .Concatenating(new AffineTransform(tx: -c.X, ty: -c.Y)),
        };
        var turned = t.Bounds;
        Assert.True(Math.Abs(turned.Width - plain.Height) <= 2 && Math.Abs(turned.Height - plain.Width) <= 2, $"{plain} → {turned}");
        var image = t.Render();
        Assert.NotNull(image);
        Assert.True(image.Width == turned.Width && image.Height == turned.Height);
        Assert.Equal(plain, t.LayoutBounds);  // the layout itself is unchanged, so the text stays editable
    }

    [Fact]
    public void MovingATransformedTextMovesItsTransform()
    {
        var t = Text() with { Transform = new AffineTransform(a: 2, d: 2) };  // scaled ×2 about the origin
        var before = t.Bounds;
        var layer = new Layer("T", new TextContent(t));
        var moved = TileOperations.Translate(layer, (30, -10));
        var m = Assert.IsType<TextContent>(moved.Content).Text;
        Assert.Equal(t.Origin, m.Origin);  // the layout stays put
        Assert.True(m.Bounds.X == before.X + 30 && m.Bounds.Y == before.Y - 10);
    }

    [Fact]
    public void TheTransformIsSaved()
    {
        var t = Text() with { Transform = new AffineTransform(0.9, 0.3, -0.3, 0.9, 12, -4) };
        var back = JsonSerializer.Deserialize<TextLayer>(JsonSerializer.Serialize(t));
        Assert.Equal(t, back);
        // Documents saved before the property existed still open.
        var old = JsonNode.Parse(JsonSerializer.Serialize(Text()))!.AsObject();
        old.Remove("transform");
        var decoded = JsonSerializer.Deserialize<TextLayer>(old.ToJsonString())!;
        Assert.Null(decoded.Transform);
    }
}
