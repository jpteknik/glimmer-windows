// Ported from Packages/GlimmerKit/Sources/GlimmerCore/PSD/PSDDescriptor.swift
#if GLIMMER_PSD   // Directory.Build.props switches this (Config/Features.xcconfig on the Mac)
using System.Collections.Immutable;
using Glimmer.Core;

namespace Glimmer.Formats;

/// <summary>
/// The "descriptor" structure Photoshop files use for settings (fill colours, text, smart
/// objects), read as the specification lays it out in its Actions section. Untrusted like
/// the rest of the file: depth, item counts and every length are bounded.
/// </summary>
internal sealed record PSDItem(string Key, PSDValue Value);

/// <summary>One value in a descriptor. Swift's <c>indirect enum PSDValue</c>; each case is a nested record.</summary>
/// <remarks>The case names shadow <c>System.String</c>, <c>System.Double</c> and so on inside this type: code in here
/// uses the keywords (<c>string</c>, <c>double</c>) for those.</remarks>
internal abstract record PSDValue
{
    public sealed record Object : PSDValue
    {
        public string ClassID { get; }
        public override ImmutableArray<PSDItem> Items { get; }
        public Object(string classID, ImmutableArray<PSDItem> items) { ClassID = classID; Items = items.IsDefault ? [] : items; }
        public bool Equals(Object? other) => other is not null && ClassID == other.ClassID && Items.SequenceEqual(other.Items);
        public override int GetHashCode() => HashCode.Combine(ClassID, Items.Length);
    }

    public sealed record List : PSDValue
    {
        public ImmutableArray<PSDValue> Values { get; }
        public List(ImmutableArray<PSDValue> values) { Values = values.IsDefault ? [] : values; }
        public bool Equals(List? other) => other is not null && Values.SequenceEqual(other.Values);
        public override int GetHashCode() => Values.Length;
    }

    public sealed record Double(double Value) : PSDValue;
    public sealed record UnitFloat(string Unit, double Value) : PSDValue;
    public sealed record String(string Value) : PSDValue;
    public sealed record Enumerated(string Type, string Value) : PSDValue;
    /// <summary>Swift's <c>Int</c>: 64 bits (a "comp" value is one).</summary>
    public sealed record Integer(long Value) : PSDValue;
    public sealed record Boolean(bool Value) : PSDValue;

    public sealed record Raw : PSDValue
    {
        public ReadOnlyMemory<byte> Data { get; }
        public Raw(ReadOnlyMemory<byte> data) { Data = data; }
        public bool Equals(Raw? other) => other is not null && Data.Span.SequenceEqual(other.Data.Span);
        public override int GetHashCode() => Data.Length;
    }

    /// <summary>A type that is skipped whole: reference, class, alias.</summary>
    public sealed record Other(string Type) : PSDValue;

    public virtual ImmutableArray<PSDItem> Items => [];

    public PSDValue? this[string key]
    {
        get
        {
            foreach (var item in Items) if (item.Key == key) return item.Value;
            return null;
        }
    }

    public double? Number => this switch
    {
        Double d => d.Value,
        UnitFloat u => u.Value,
        Integer i => i.Value,
        _ => null,
    };

    /// <summary>Every value in the tree, this one first, depth first.</summary>
    public ImmutableArray<PSDValue> All
    {
        get
        {
            var all = ImmutableArray.CreateBuilder<PSDValue>();
            Collect(this, all);
            return all.ToImmutable();
        }
    }

    private static void Collect(PSDValue value, ImmutableArray<PSDValue>.Builder into)
    {
        into.Add(value);
        switch (value)
        {
            case Object o: foreach (var item in o.Items) Collect(item.Value, into); break;
            case List l: foreach (var v in l.Values) Collect(v, into); break;
        }
    }
}

internal static class PSDDescriptor
{
    public const int MaxDepth = 24;
    public const int MaxItems = 20_000;

    private static PSDFile.Problem Damaged() => new("This Photoshop file's settings are damaged.");
    private static PSDFile.Problem UnknownForm() => new("This Photoshop file has settings in a form Glimmer doesn't read.");

    /// <summary>A descriptor at the cursor: the name, the class and its items.</summary>
    public static PSDValue Read(ref PSDBytes r, ref int budget, int depth = 0)
    {
        if (depth > MaxDepth) throw new PSDFile.Problem("This Photoshop file nests its settings deeper than Glimmer reads.");
        _ = Unicode(ref r);
        string classID = Id(ref r);
        long count = r.U32();
        if (count > budget) throw Damaged();
        budget -= (int)count;
        var items = ImmutableArray.CreateBuilder<PSDItem>((int)count);
        for (long i = 0; i < count; i++)
        {
            string key = Id(ref r);
            string type = r.Ascii(4);
            items.Add(new PSDItem(key, Value(ref r, type, depth + 1, ref budget)));
        }
        return new PSDValue.Object(classID, items.MoveToImmutable());
    }

    public static PSDValue Read(ReadOnlyMemory<byte> data)
    {
        var r = new PSDBytes(data);
        int budget = MaxItems;
        return Read(ref r, ref budget);
    }

    /// <summary>"4 bytes (length), followed either by string or (if length is zero) 4-byte ID".</summary>
    public static string Id(ref PSDBytes r)
    {
        long length = r.U32();
        return r.Ascii(length == 0 ? 4 : length);
    }

    /// <summary>A count of UTF-16 code units, then the units. A trailing null, when the count includes one, is dropped.</summary>
    public static string Unicode(ref PSDBytes r)
    {
        long count = r.U32();
        if (count > r.Remaining / 2) throw PSDBytes.CutShort();
        // Big-endian UTF-16. Ill-formed text (a lone surrogate) becomes U+FFFD, as Swift's String(decoding:as:) makes it;
        // trailing nulls are dropped after decoding rather than before, which comes to the same.
        return System.Text.Encoding.BigEndianUnicode.GetString(r.Slice(count * 2).Rest.Span).TrimEnd('\0');
    }

    public static double Double(ref PSDBytes r)
    {
        ulong bits = (ulong)r.U32() << 32 | r.U32();
        return BitConverter.UInt64BitsToDouble(bits);
    }

    private static PSDValue Value(ref PSDBytes r, string type, int depth, ref int budget)
    {
        switch (type)
        {
            case "Objc":
            case "GlbO":
                return Read(ref r, ref budget, depth);
            case "VlLs":
            {
                long count = r.U32();
                if (count > budget || depth >= MaxDepth) throw Damaged();
                budget -= (int)count;
                var values = ImmutableArray.CreateBuilder<PSDValue>((int)count);
                for (long i = 0; i < count; i++)
                {
                    string itemType = r.Ascii(4);
                    values.Add(Value(ref r, itemType, depth + 1, ref budget));
                }
                return new PSDValue.List(values.MoveToImmutable());
            }
            case "doub":
                return new PSDValue.Double(Double(ref r));
            case "UntF":
            {
                string unit = r.Ascii(4);
                return new PSDValue.UnitFloat(unit, Double(ref r));
            }
            case "TEXT":
                return new PSDValue.String(Unicode(ref r));
            case "enum":
            {
                string enumType = Id(ref r);
                return new PSDValue.Enumerated(enumType, Id(ref r));
            }
            case "long":
                return new PSDValue.Integer(r.I32());
            case "comp":
            {
                ulong high = r.U32(), low = r.U32();
                return new PSDValue.Integer((long)(high << 32 | low));
            }
            case "bool":
                return new PSDValue.Boolean(r.U8() != 0);
            case "tdta":
                return new PSDValue.Raw(r.Slice(r.U32()).Rest);
            case "type":
            case "GlbC":
                _ = Unicode(ref r); _ = Id(ref r);
                return new PSDValue.Other(type);
            case "alis":
                r.Skip(r.U32());
                return new PSDValue.Other(type);
            case "obj ":
            {
                // A reference: its items are read only to get past them.
                long count = r.U32();
                if (count > budget) throw Damaged();
                budget -= (int)count;
                for (long i = 0; i < count; i++)
                {
                    switch (r.Ascii(4))
                    {
                        case "prop": _ = Unicode(ref r); _ = Id(ref r); _ = Id(ref r); break;
                        case "Clss": _ = Unicode(ref r); _ = Id(ref r); break;
                        case "Enmr": _ = Unicode(ref r); _ = Id(ref r); _ = Id(ref r); _ = Id(ref r); break;
                        case "rele": _ = Unicode(ref r); _ = Id(ref r); r.Skip(4); break;
                        default: throw UnknownForm();   // identifier, index, name: the specification gives no layout
                    }
                }
                return new PSDValue.Other(type);
            }
            default:
                throw UnknownForm();
        }
    }
}

/// <summary>
/// The path records of a vector mask ("Path resource format" in the specification): 26 bytes
/// each, a selector, then for a knot three points (the control point before it, the anchor,
/// the control point after it), each vertical then horizontal, as 8.24 fixed-point fractions
/// of the document's height and width.
/// </summary>
internal static class PSDPath
{
    /// <summary>The subpaths in a vector mask's records, in document pixels.</summary>
    public static ImmutableArray<Subpath> Subpaths(ReadOnlyMemory<byte> data, int width, int height)
    {
        var r = new PSDBytes(data);
        var result = new List<(bool IsClosed, List<PathAnchor> Anchors)>();
        int expecting = 0;
        bool Point(ref PSDBytes record, out Vec2 point)
        {
            point = default;
            if (!record.TryI32(out int yFixed) || !record.TryI32(out int xFixed)) return false;
            double y = yFixed / 16_777_216.0, x = xFixed / 16_777_216.0;
            point = new Vec2(x * width, y * height);
            return true;
        }
        while (r.Remaining >= 26 && result.Count < 10_000)
        {
            if (!r.TrySlice(26, out var record) || !record.TryU16(out ushort selector)) break;
            switch (selector)
            {
                case 0:
                case 3:
                    expecting = record.TryU16(out ushort count) ? count : 0;
                    result.Add((selector == 0, []));
                    break;
                case 1:
                case 2:
                case 4:
                case 5:
                    if (expecting <= 0 || result.Count == 0 || !Point(ref record, out var before) || !Point(ref record, out var anchor)
                        || !Point(ref record, out var after)) continue;
                    if (!new[] { before, anchor, after }.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y))) continue;
                    expecting -= 1;
                    result[^1].Anchors.Add(new PathAnchor(anchor, handleIn: before, handleOut: after));
                    break;
                default:
                    continue;   // fill rule, clipboard and initial fill records carry no points
            }
        }
        return [.. result.Where(s => s.Anchors.Count >= 2).Select(s => new Subpath(s.Anchors, isClosed: s.IsClosed))];
    }
}
#endif
