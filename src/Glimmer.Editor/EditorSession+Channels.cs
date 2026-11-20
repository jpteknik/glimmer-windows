// Ported from App/EditorSession+Channels.swift
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;

namespace Glimmer.Editor;

public sealed partial class EditorSession
{
    // MARK: Channels

    /// <summary>Loads a channel (0 = RGB luminosity, 1-3 = R, G, B, 4 = alpha) of the composite as a selection.</summary>
    public void LoadChannelAsSelection(int channel)
    {
        CommitLiveEdit();
        try
        {
            using var image = ImageExporter.FlattenedSRGB(State);
            var rgba = RGBAImage.FromSKBitmap(image);
            var mask = new float[rgba.Width * rgba.Height];
            var px = rgba.Pixels;
            for (int i = 0; i < mask.Length; i++)
            {
                // RGBAImage holds 0...255; a selection 0...1.
                float r = px[i * 4] / 255f, g = px[i * 4 + 1] / 255f, b = px[i * 4 + 2] / 255f, a = px[i * 4 + 3] / 255f;
                float v = channel switch
                {
                    1 => r,
                    2 => g,
                    3 => b,
                    _ => 0.299f * r + 0.587f * g + 0.114f * b,
                };
                mask[i] = channel == 4 ? a : v * a;
            }
            var selection = Selection.From(mask, new PixelRect(0, 0, rgba.Width, rgba.Height));
            Edit("Load Channel", s => s with { Selection = selection });
        }
        catch (Exception error)
        {
            Report(error);
        }
    }
}
