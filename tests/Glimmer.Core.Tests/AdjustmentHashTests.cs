// New for the Windows port: the renderer keys its caches on adjustments, so their hash must follow the curve points.
using System.Collections.Immutable;

namespace Glimmer.Core.Tests;

public class AdjustmentHashTests
{
    static Adjustment CurvesWith(double midY) =>
        new Adjustment(AdjustmentKind.Curves) with
        {
            Curves = ImmutableDictionary<CurveChannel, ImmutableArray<CurvePoint>>.Empty
                .Add(CurveChannel.Rgb, [new CurvePoint(0, 0), new CurvePoint(0.5, midY), new CurvePoint(1, 1)]),
        };

    [Fact]
    public void EqualCurvesHashEqual()
    {
        var a = CurvesWith(0.6);
        var b = CurvesWith(0.6);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ACurvePointChangesTheHash()
    {
        Assert.NotEqual(CurvesWith(0.6), CurvesWith(0.7));
        Assert.NotEqual(CurvesWith(0.6).GetHashCode(), CurvesWith(0.7).GetHashCode());
    }
}
