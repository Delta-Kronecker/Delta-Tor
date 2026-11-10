using System.Buffers.Binary;

namespace DeltaTor.Core.Sstp;

/// <summary>
/// A faithful, minimal port of the slice of <c>java.nio.ByteBuffer</c> the
/// vendored kittoku SSTP core relies on: a big-endian read/write cursor over a
/// backing <c>byte[]</c> with <c>position</c>/<c>limit</c>/<c>capacity</c>
/// semantics. The Kotlin sources drive every packet layout through this type,
/// so reproducing it verbatim keeps the byte-for-byte behaviour identical (the
/// alternative — rewriting each reader/writer against spans — would be a place
/// for a parsing bug to hide).
///
/// Only the operations the core actually uses are implemented. Reads past the
/// limit throw <see cref="ParsingDataUnitException"/>, matching Java's
/// <c>BufferUnderflowException</c> closely enough for the caller's
/// tryReadDataUnit() recovery path.
/// </summary>
public sealed class ByteBuffer
{
    private readonly byte[] _array;
    private int _position;
    private int _limit;

    public ByteBuffer(int capacity)
    {
        _array = new byte[capacity];
        _position = 0;
        _limit = capacity;
    }

    private ByteBuffer(byte[] array)
    {
        _array = array;
        _position = 0;
        _limit = array.Length;
    }

    /// <summary>Wrap an existing array the way ByteBuffer.wrap() does.</summary>
    public static ByteBuffer Wrap(byte[] array) => new(array);

    public static ByteBuffer Allocate(int capacity) => new(capacity);

    public byte[] Array => _array;

    public int Capacity => _array.Length;

    public int Position
    {
        get => _position;
        set => _position = value;
    }

    public int Limit
    {
        get => _limit;
        set => _limit = value;
    }

    public int Remaining => _limit - _position;

    public bool HasRemaining => _position < _limit;

    public ByteBuffer Clear()
    {
        _position = 0;
        _limit = _array.Length;
        return this;
    }

    public ByteBuffer Flip()
    {
        _limit = _position;
        _position = 0;
        return this;
    }

    private void CheckRead(int count)
    {
        if (_position + count > _limit)
        {
            throw new ParsingDataUnitException();
        }
    }

    private void CheckWrite(int count)
    {
        if (_position + count > _array.Length)
        {
            throw new ParsingDataUnitException();
        }
    }

    public byte Get()
    {
        CheckRead(1);
        return _array[_position++];
    }

    public byte Get(int index) => _array[index];

    public void Get(Span<byte> destination)
    {
        CheckRead(destination.Length);
        _array.AsSpan(_position, destination.Length).CopyTo(destination);
        _position += destination.Length;
    }

    public void Get(byte[] destination) => Get(destination.AsSpan());

    public void Get(byte[] destination, int offset, int length)
    {
        CheckRead(length);
        System.Array.Copy(_array, _position, destination, offset, length);
        _position += length;
    }

    public short GetShort()
    {
        CheckRead(2);
        var value = BinaryPrimitives.ReadInt16BigEndian(_array.AsSpan(_position, 2));
        _position += 2;
        return value;
    }

    public short GetShort(int index) =>
        BinaryPrimitives.ReadInt16BigEndian(_array.AsSpan(index, 2));

    public int GetInt()
    {
        CheckRead(4);
        var value = BinaryPrimitives.ReadInt32BigEndian(_array.AsSpan(_position, 4));
        _position += 4;
        return value;
    }

    public int GetInt(int index) =>
        BinaryPrimitives.ReadInt32BigEndian(_array.AsSpan(index, 4));

    public long GetLong()
    {
        CheckRead(8);
        var value = BinaryPrimitives.ReadInt64BigEndian(_array.AsSpan(_position, 8));
        _position += 8;
        return value;
    }

    public long GetLong(int index) =>
        BinaryPrimitives.ReadInt64BigEndian(_array.AsSpan(index, 8));

    public ByteBuffer Put(byte value)
    {
        CheckWrite(1);
        _array[_position++] = value;
        return this;
    }

    public ByteBuffer Put(int value) => Put((byte)value);

    public ByteBuffer Put(byte[] source) => Put(source.AsSpan());

    public ByteBuffer Put(ReadOnlySpan<byte> source)
    {
        CheckWrite(source.Length);
        source.CopyTo(_array.AsSpan(_position, source.Length));
        _position += source.Length;
        return this;
    }

    /// <summary>ByteBuffer.put(ByteBuffer): copies the source's remaining bytes.</summary>
    public ByteBuffer Put(ByteBuffer source)
    {
        var remaining = source.Remaining;
        CheckWrite(remaining);
        System.Array.Copy(source._array, source._position, _array, _position, remaining);
        _position += remaining;
        source._position += remaining;
        return this;
    }

    public ByteBuffer PutShort(short value)
    {
        CheckWrite(2);
        BinaryPrimitives.WriteInt16BigEndian(_array.AsSpan(_position, 2), value);
        _position += 2;
        return this;
    }

    public ByteBuffer PutInt(int value)
    {
        CheckWrite(4);
        BinaryPrimitives.WriteInt32BigEndian(_array.AsSpan(_position, 4), value);
        _position += 4;
        return this;
    }

    public ByteBuffer PutLong(long value)
    {
        CheckWrite(8);
        BinaryPrimitives.WriteInt64BigEndian(_array.AsSpan(_position, 8), value);
        _position += 8;
        return this;
    }
}

/// <summary>The Kotlin extension helpers from kittoku/osc/extension/ByteBuffer.kt.</summary>
internal static class ByteBufferExtensions
{
    public static void Move(this ByteBuffer buffer, int diff) =>
        buffer.Position += diff;

    public static void PadZeroByte(this ByteBuffer buffer, int size)
    {
        for (var i = 0; i < size; i++)
        {
            buffer.Put(0);
        }
    }

    public static byte ProbeByte(this ByteBuffer buffer, int diff) =>
        buffer.Get(buffer.Position + diff);

    public static short ProbeShort(this ByteBuffer buffer, int diff) =>
        buffer.GetShort(buffer.Position + diff);

    public static int CapacityAfterLimit(this ByteBuffer buffer) =>
        buffer.Capacity - buffer.Limit;

    /// <summary>
    /// Drop the already-consumed prefix: copy [position, limit) to index 0 and
    /// reset the cursor so the remainder is at the front of the array.
    /// </summary>
    public static void Slide(this ByteBuffer buffer)
    {
        var remaining = buffer.Remaining;

        var array = buffer.Array;
        System.Array.Copy(array, buffer.Position, array, 0, remaining);

        buffer.Position = 0;
        buffer.Limit = remaining;
    }

    public static int ToIntAsUByte(this byte value) => value & 0xFF;

    public static int ToIntAsUShort(this short value) => value & 0xFFFF;
}
