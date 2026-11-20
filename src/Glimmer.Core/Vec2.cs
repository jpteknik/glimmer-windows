// Ported from the simd types the original uses throughout Packages/GlimmerKit/Sources/GlimmerCore
// (SIMD2<Double>, SIMD3<Double>, simd_double2x2, simd_double3x3). No Swift file of its own.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

/// <summary>
/// A point or vector in double precision: Swift's <c>SIMD2&lt;Double&gt;</c>. Arithmetic is component-wise,
/// as with SIMD. Encoded in JSON as <c>[x, y]</c>, the way Swift's <c>Codable</c> writes a SIMD2.
/// </summary>
[JsonConverter(typeof(Vec2JsonConverter))]
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 Zero => default;
    public static Vec2 One => new(1, 1);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, Vec2 b) => new(a.X * b.X, a.Y * b.Y);
    public static Vec2 operator /(Vec2 a, Vec2 b) => new(a.X / b.X, a.Y / b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    /// <summary><c>simd_length</c>.</summary>
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;

    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    /// <summary><c>simd_distance</c>.</summary>
    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;
    /// <summary><c>simd_min</c>: the component-wise minimum.</summary>
    public static Vec2 Min(Vec2 a, Vec2 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
    /// <summary><c>simd_max</c>: the component-wise maximum.</summary>
    public static Vec2 Max(Vec2 a, Vec2 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X}, {Y})");
}

/// <summary>Swift's <c>SIMD3&lt;Double&gt;</c>, for homogeneous 2D coordinates.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public Vec3(Vec2 xy, double z) : this(xy.X, xy.Y, z) { }

    public Vec2 XY => new(X, Y);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}

/// <summary>
/// Swift's <c>simd_double2x2</c>: column-major, <c>m * v</c> multiplies a column vector.
/// </summary>
public readonly record struct Double2x2(Vec2 Column0, Vec2 Column1)
{
    public Double2x2 Transpose => new(new Vec2(Column0.X, Column1.X), new Vec2(Column0.Y, Column1.Y));

    public static Vec2 operator *(Double2x2 m, Vec2 v) => m.Column0 * v.X + m.Column1 * v.Y;
    public static Double2x2 operator *(Double2x2 m, double s) => new(m.Column0 * s, m.Column1 * s);
    public static Double2x2 operator *(Double2x2 a, Double2x2 b) => new(a * b.Column0, a * b.Column1);
}

/// <summary>
/// Swift's <c>simd_double3x3</c>: column-major, <c>m * v</c> multiplies a column vector. Used for affine and
/// projective 2D transforms (the third row holds the projective terms).
/// </summary>
public readonly record struct Double3x3(Vec3 Column0, Vec3 Column1, Vec3 Column2)
{
    public static Double3x3 Identity => new(new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));

    public double Determinant =>
        Column0.X * (Column1.Y * Column2.Z - Column2.Y * Column1.Z)
        - Column1.X * (Column0.Y * Column2.Z - Column2.Y * Column0.Z)
        + Column2.X * (Column0.Y * Column1.Z - Column1.Y * Column0.Z);

    public Double3x3 Transpose => new(
        new Vec3(Column0.X, Column1.X, Column2.X),
        new Vec3(Column0.Y, Column1.Y, Column2.Y),
        new Vec3(Column0.Z, Column1.Z, Column2.Z));

    /// <summary>The inverse (callers check <see cref="Determinant"/> first, as <c>simd</c> callers do).</summary>
    public Double3x3 Inverse
    {
        get
        {
            // Rows of the matrix are the columns of the transpose; the inverse is adj(M) / det(M).
            double a = Column0.X, b = Column1.X, c = Column2.X;
            double d = Column0.Y, e = Column1.Y, f = Column2.Y;
            double g = Column0.Z, h = Column1.Z, i = Column2.Z;
            double det = Determinant;
            double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            double D = -(b * i - c * h), E = a * i - c * g, F = -(a * h - b * g);
            double G = b * f - c * e, H = -(a * f - c * d), I = a * e - b * d;
            // Inverse rows: (A D G), (B E H), (C F I) divided by det; stored by column.
            return new Double3x3(
                new Vec3(A / det, B / det, C / det),
                new Vec3(D / det, E / det, F / det),
                new Vec3(G / det, H / det, I / det));
        }
    }

    public static Vec3 operator *(Double3x3 m, Vec3 v) => m.Column0 * v.X + m.Column1 * v.Y + m.Column2 * v.Z;
    public static Double3x3 operator *(Double3x3 a, Double3x3 b) => new(a * b.Column0, a * b.Column1, a * b.Column2);
}

/// <summary>Writes a <see cref="Vec2"/> as <c>[x, y]</c> and reads exactly two numbers, as Swift's SIMD2 does.</summary>
public sealed class Vec2JsonConverter : JsonConverter<Vec2>
{
    public override Vec2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected to decode a two-element array for a point.");
        Span<double> v = stackalloc double[2];
        int n = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.Number) throw new JsonException("Expected a number in a point.");
            if (n < 2) v[n] = reader.GetDouble();
            n++;
        }
        if (n != 2) throw new JsonException($"Expected 2 values for a point but found {n}.");
        return new Vec2(v[0], v[1]);
    }

    public override void Write(Utf8JsonWriter writer, Vec2 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        SwiftJson.WriteDouble(writer, value.X);
        SwiftJson.WriteDouble(writer, value.Y);
        writer.WriteEndArray();
    }
}
