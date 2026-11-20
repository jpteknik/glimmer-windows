// Tests for the port of App/EditorSession+Text.swift: the Type tool's click, committing what the text editor holds,
// styling, text on a path and warps. The original has no tests at this level.
using Glimmer.Core;
using Glimmer.Tools;

namespace Glimmer.Editor.Tests;

public class TextTests
{
    private static string[] Steps(EditorSession s) => [.. s.Document.History.Select(t => t.ToString())];

    private static TextLayer TextOf(EditorSession s, Guid id) => ((TextContent)s.State.LayerById(id)!.Content).Text;

    [Fact]
    public void TypeClickStartsANewLayerAndCommitStoresTheText()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Tool = ToolID.Type;
        new CanvasInput(s, h.Scheduler).Click(new Vec2(50, 100));
        var id = Assert.NotNull(s.EditingText);
        Assert.Equal(["New Text Layer"], Steps(s));
        Assert.Equal("Text", s.ActiveLayer!.Name);
        // While the editor is open, the layer's own pixels are left out (the editor shows the text).
        Assert.True(s.Overrides.Pixels[id].IsEmpty);
        var style = new TextStyle(s.TextDefaults.FontName, 48, new SRGBColor(1, 0, 0));
        // The editor closes: what it holds is committed.
        s.EditingText = null;
        Assert.True(s.Overrides.Pixels.IsEmpty);
        s.CommitText("Hello\nworld", [(new TextRange(0, 5), style)], id);
        h.Scheduler.RunPosted();
        Assert.Equal(["New Text Layer", "Edit Text"], Steps(s));
        // The layer takes the first line as its name.
        Assert.Equal("Hello", s.ActiveLayer!.Name);
        Assert.Equal("Hello\nworld", TextOf(s, id).String);
        // Committing the same text again changes nothing.
        s.CommitText("Hello\nworld", [(new TextRange(0, 5), style)], id);
        Assert.Equal(2, s.Document.History.Count);
        // Only the style changed: a style step.
        s.CommitText("Hello\nworld", [(new TextRange(0, 5), style with { Size = 60 })], id);
        Assert.Equal("Style Text", Steps(s)[^1]);
        Assert.NotNull(s.TextLayerAt(new Vec2(55, 95)));
    }

    [Fact]
    public void AnEmptyTextLayerGoesAway()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.TypeClick(new Vec2(50, 100));
        var id = s.EditingText!.Value;
        s.CommitText("   ", [], id);
        Assert.Null(s.State.LayerById(id));
        Assert.Equal("Delete Empty Text", Steps(s)[^1]);
    }

    [Fact]
    public void LongFirstLinesNameTheLayerWithThirtyTwoCharacters()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.TypeClick(new Vec2(50, 100));
        var id = s.EditingText!.Value;
        s.CommitText("\nA very long first line that keeps going and going", [], id);
        Assert.Equal("A very long first line that keep", s.State.LayerById(id)!.Name);
    }

    [Fact]
    public void ClickingTextEditsItInsteadOfMakingAnother()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.TypeClick(new Vec2(50, 100));
        var id = s.EditingText!.Value;
        s.CommitText("Glimmer", [], id);
        s.EditingText = null;
        s.SetActiveLayer(s.State.Layers[0].Id);
        h.Scheduler.RunPosted();
        var inside = TextOf(s, id).Bounds;
        s.TypeClick(new Vec2(inside.X + 2, inside.Y + 2));
        Assert.Equal(id, s.EditingText);
        Assert.Equal(id, s.State.ActiveLayerID);
        Assert.Equal(2, s.Document.History.Count);
        // A double click with the Move tool does the same.
        s.EditingText = null;
        s.Tool = ToolID.Move;
        new CanvasInput(s, h.Scheduler).Click(new Vec2(inside.X + 2, inside.Y + 2), clickCount: 2);
        Assert.Equal(ToolID.Type, s.Tool);
        Assert.Equal(id, s.EditingText);
    }

    [Fact]
    public void TheOptionsBarStylesTheLayerOrTheSelectedWords()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.TypeClick(new Vec2(50, 100));
        var id = s.EditingText!.Value;
        s.CommitText("Glimmer", [], id);
        h.Scheduler.RunPosted();
        // Not editing: the whole layer, as one step once the control goes quiet.
        s.UpdateText(t => t with { Size = 80 });
        s.UpdateText(t => t with { Size = 90 });
        h.Scheduler.Settle();
        Assert.Equal(90, TextOf(s, id).Size);
        Assert.Equal("Text Style", Steps(s)[^1]);
        Assert.Equal(90, s.TextDefaults.Size);
        // Editing with words selected: the editor styles them.
        var styler = new RecordingStyler();
        s.TextStyler = styler;
        s.TextSelectionIsRange = true;
        s.TextSelectionStyle = new TextStyle(TextOf(s, id).FontName, 90, TextOf(s, id).Color);
        s.UpdateText(t => t with { Color = new SRGBColor(0, 0, 1) });
        Assert.Equal((true, (string?)null, (double?)null, (SRGBColor?)new SRGBColor(0, 0, 1)), Assert.Single(styler.Calls));
    }

    private sealed class RecordingStyler : ITextStyler
    {
        public List<(bool, string?, double?, SRGBColor?)> Calls { get; } = [];
        public void Style(bool selectionOnly, string? fontName, double? size, SRGBColor? color) => Calls.Add((selectionOnly, fontName, size, color));
    }

    [Fact]
    public void TextFollowsAPathAndComesOffIt()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Tool = ToolID.Pen;
        var input = new CanvasInput(s, h.Scheduler);
        input.Click(new Vec2(50, 200));
        input.Click(new Vec2(350, 200));
        s.FinishPathSegment();
        var pathId = s.ActivePathID!.Value;
        // Clicking on the active path with the Type tool starts text along it.
        s.Tool = ToolID.Type;
        input.Click(new Vec2(200, 201));
        var id = s.EditingText!.Value;
        Assert.Equal(pathId, TextOf(s, id).OnPathID);
        s.CommitText("Along the line", [], id);
        s.SetTextPath(null);
        Assert.Null(TextOf(s, id).OnPath);
        Assert.Equal("Remove Text from Path", Steps(s)[^1]);
        s.SetTextPath(pathId);
        Assert.Equal("Text on Path", Steps(s)[^1]);
        // Warp: live, one step.
        s.UpdateTextWarp(new TextWarp(TextWarpStyle.Arc, 0.5));
        h.Scheduler.Settle();
        Assert.Equal("Warp Text", Steps(s)[^1]);
        Assert.NotNull(TextOf(s, id).Warp);
    }
}
