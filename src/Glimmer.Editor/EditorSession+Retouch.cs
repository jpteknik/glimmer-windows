// Ported from App/EditorSession+Retouch.swift
using System.Numerics;
using Glimmer.Core;
using Glimmer.Render;
using Glimmer.Tools;

namespace Glimmer.Editor;

public sealed partial class EditorSession
{
    // MARK: Retouch tools

    public static readonly IReadOnlySet<ToolID> RetouchTools = new HashSet<ToolID>
    {
        ToolID.CloneStamp, ToolID.HealingBrush, ToolID.Dodge, ToolID.Burn, ToolID.Blur, ToolID.Sharpen, ToolID.Smudge, ToolID.PatternStamp,
    };

    public void SetCloneSource(Vec2 point)
    {
        CloneSource = point;
        cloneOffset = null;
    }

    /// <summary>Starts a clone / heal / dodge / burn / blur / sharpen / smudge stroke.</summary>
    public void BeginRetouch(Vec2 point, double pressure)
    {
        CommitLiveEdit();
        if (FilterEngine is not { } engine || ActiveLayer is not { } layer) return;
        if (!layer.IsRaster)
        {
            ErrorMessage = "Retouch tools work on pixel layers.";
            return;
        }
        var frame = State.Bounds;
        try
        {
            var sampler = new StrokeSampler(brush);
            var dabs = sampler.Begin(new StrokeSampler.Sample(point, pressure));
            if (tool == ToolID.Smudge)
            {
                var smudger = new SmudgeEngine(layer.Tiles, frame, State.Selection);
                smudge = (smudger, sampler, layer.Id, point);
                Overrides = new LayerOverrides(pixels: [new(layer.Id, smudger.Result)]);
                return;
            }
            PixelOps.Mode mode;
            TileGrid? effect = null;
            var parameters = Vector4.Zero;
            switch (tool)
            {
                case ToolID.CloneStamp:
                case ToolID.HealingBrush:
                    {
                        if (cloneSource is not { } source)
                        {
                            // The Mac says "⌥-click"; the key is Alt on Windows.
                            ErrorMessage = new UiText("Alt-click to set the {0} source first.", tool == ToolID.CloneStamp ? "clone" : "healing");
                            return;
                        }
                        var offset = cloneAligned && cloneOffset is { } o ? o : source - point;
                        cloneOffset = offset;
                        var shift = ((int)(-offset.X).Rounded(), (int)(-offset.Y).Rounded());
                        effect = tool == ToolID.CloneStamp
                            ? engine.Shifted(layer.Tiles, shift, frame)
                            : engine.Healed(layer.Tiles, shift, frame, (float)Math.Max(brush.Size / 4, 3));
                        mode = PixelOps.Mode.TowardEffect;
                        break;
                    }
                case ToolID.Blur:
                    effect = engine.Apply(new FilterSettings(FilterKind.GaussianBlur, [new("radius", 2 + 10 * retouchStrength)]), layer.Tiles, frame, null);
                    mode = PixelOps.Mode.TowardEffect;
                    break;
                case ToolID.Sharpen:
                    effect = engine.Apply(new FilterSettings(FilterKind.UnsharpMask, [new("amount", 0.5 + 2 * retouchStrength), new("radius", 1.5)]),
                                          layer.Tiles, frame, null);
                    mode = PixelOps.Mode.TowardEffect;
                    break;
                case ToolID.PatternStamp:
                    {
                        var pattern = new Layer(Environment.LayerName("Pattern"), new FillContent(new Fill.Pattern(new PatternFill
                        {
                            Kind = patternKind, Scale = patternScale, Foreground = foreground, Background = background,
                        })));
                        effect = DocumentCompositor.Rasterize(pattern, frame, State.BlendSpace);
                        mode = PixelOps.Mode.TowardEffect;
                        break;
                    }
                case ToolID.Dodge:
                case ToolID.Burn:
                    mode = tool == ToolID.Dodge ? PixelOps.Mode.Dodge : PixelOps.Mode.Burn;
                    parameters = new Vector4((float)dodgeExposure, dodgeRange, 0, 0);
                    break;
                default:
                    return;
            }
            var strokeEngine = new StrokeEngine(layer.Tiles, mode, effect, parameters, (float)brush.Opacity, State.Selection);
            strokeEngine.Add(dabs);
            stroke = (strokeEngine, sampler, layer.Id, false);
            Overrides = new LayerOverrides(pixels: [new(layer.Id, strokeEngine.Result)]);
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    public void ContinueSmudge(Vec2 point, double pressure)
    {
        if (smudge is not { } s) return;
        var dabs = s.Sampler.Move(new StrokeSampler.Sample(point, pressure));
        if (dabs.Count == 0) return;
        var last = s.Last;
        var smudgeDabs = new List<SmudgeDab>(dabs.Count);
        foreach (var d in dabs)
        {
            var c = new Vec2(d.Center.X, d.Center.Y);
            smudgeDabs.Add(new SmudgeDab(d.Center, new Vector2((float)(c.X - last.X), (float)(c.Y - last.Y)), d.Radius, d.Hardness,
                                         (float)retouchStrength * d.Flow / Math.Max((float)brush.Flow, 0.01f)));
            last = c;
        }
        smudge = s with { Last = last };
        s.Engine.Add(smudgeDabs);
        Overrides = new LayerOverrides(pixels: [new(s.Layer, s.Engine.Result)]);
    }

    public void EndSmudge()
    {
        if (smudge is not { } s) return;
        smudge = null;
        s.Engine.Finish();
        var result = s.Engine.Result;
        Edit("Smudge", state => state.UpdateLayer(s.Layer, l => l with { Tiles = result }));
        Overrides = LayerOverrides.None;
    }

    public UiText RetouchActionName => tool switch
    {
        ToolID.CloneStamp => "Clone Stamp",
        ToolID.HealingBrush => "Healing Brush",
        ToolID.Dodge => "Dodge",
        ToolID.Burn => "Burn",
        ToolID.Blur => "Blur Tool",
        ToolID.Sharpen => "Sharpen Tool",
        ToolID.Smudge => "Smudge",
        ToolID.PatternStamp => "Pattern Stamp",
        _ => "Retouch",
    };

    /// <summary>Where the clone source sits for a brush at <paramref name="point"/> (document point), for the canvas marker.</summary>
    /// <remarks>The Mac passes the hover point in view points here and adds a document offset to it, which puts the marker
    /// in the wrong place whenever the view isn't at 100 % and unpanned; this takes a document point.</remarks>
    public Vec2? CloneMarker(Vec2? point)
    {
        if (tool != ToolID.CloneStamp && tool != ToolID.HealingBrush) return null;
        if (point is { } p && cloneOffset is { } offset && (cloneAligned || stroke is not null)) return p + offset;
        return cloneSource;
    }
}
