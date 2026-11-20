// Ported from App/EditorSession+AddImage.swift
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;
using Glimmer.Render;

namespace Glimmer.Editor;

public sealed partial class EditorSession
{
    /// <summary>Adds <paramref name="image"/> as a new layer above the active one, fitted to the canvas (an agent's
    /// add_image). One undo step of its own. Returns the new layer.</summary>
    public Layer AddImageLayer(DecodedImage image, string name, CanvasSteps.Fit fit, double? x, double? y, bool smartObject)
    {
        CommitLiveEdit();
        var p = CanvasSteps.Placement((image.Width, image.Height), (State.Width, State.Height), fit, x, y);
        double scale = p.Width / image.Width;
        LayerContent content;
        if (smartObject)
        {
            var source = ImageImporter.Tiles(image, (0, 0));
            content = new SmartContent(new SmartObject(source, image.Width, image.Height, new AffineTransform(a: scale, d: scale, tx: p.X, ty: p.Y)));
        }
        else if (Math.Abs(scale - 1) < 1e-9)
        {
            content = new RasterContent(ImageImporter.Tiles(image, ((int)p.X.Rounded(), (int)p.Y.Rounded())));
        }
        else
        {
            var source = ImageImporter.Tiles(image, (0, 0));
            var smart = new SmartObject(source, image.Width, image.Height, new AffineTransform(a: scale, d: scale, tx: p.X, ty: p.Y));
            content = new RasterContent(new SmartObjectRenderer().Render(smart, smart.Bounds));
        }
        var layer = new Layer(name, content);
        Document.Edit("Add Image", s => s.InsertLayerAboveActive(layer), separateStep: true);
        return layer;
    }
}
