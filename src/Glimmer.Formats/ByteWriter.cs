// New file, no Swift original: the writers (PSDWriter, TiffWriter) append to a growing buffer, as Swift appends to
// Data. Lengths that come before their contents are reserved and filled in afterwards.
using System.Buffers.Binary;
using System.Text;

namespace Glimmer.Formats;

internal sealed class ByteWriter
{
    private byte[] buffer;
    private int length;

    public ByteWriter(int capacity = 256) { buffer = new byte[Math.Max(16, capacity)]; }

    public int Length => length;
    public ReadOnlySpan<byte> Written => buffer.AsSpan(0, length);

    private void Ensure(int more)
    {
        long needed = (long)length + more;
        if (needed <= buffer.Length) return;
        if (needed > Array.MaxLength) throw new InvalidOperationException("A buffer grew past the largest array .NET can make.");
        long next = Math.Max(needed, Math.Min((long)buffer.Length * 2, Array.MaxLength));
        Array.Resize(ref buffer, (int)next);
    }

    public void U8(byte value)
    {
        Ensure(1);
        buffer[length++] = value;
    }

    public void BE16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(length), value);
        length += 2;
    }

    public void BE32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(length), value);
        length += 4;
    }

    public void LE16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(length), value);
        length += 2;
    }

    public void LE32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(length), value);
        length += 4;
    }

    public void LE64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(length), value);
        length += 8;
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(buffer.AsSpan(length));
        length += bytes.Length;
    }

    /// <summary>The bytes of an ASCII key such as "8BIM".</summary>
    public void Ascii(string text)
    {
        int count = Encoding.UTF8.GetByteCount(text);
        Ensure(count);
        length += Encoding.UTF8.GetBytes(text, buffer.AsSpan(length));
    }

    public void Zeros(int count)
    {
        Ensure(count);
        buffer.AsSpan(length, count).Clear();
        length += count;
    }

    /// <summary>Room for <paramref name="count"/> bytes, zeroed, to be filled in later; returns where it starts.</summary>
    public int Reserve(int count)
    {
        int at = length;
        Zeros(count);
        return at;
    }

    public void PatchBE16(int at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(at, 2), value);

    public void CopyTo(Stream destination) => destination.Write(buffer, 0, length);

    public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();
}
