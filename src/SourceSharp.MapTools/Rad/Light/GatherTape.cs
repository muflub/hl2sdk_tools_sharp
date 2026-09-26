//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// The intermediate values of the direct-light gather, written while its rays
/// are EMITTED and read back, in the same order, when their answers are
/// RESOLVED.
/// </summary>
/// <remarks>
/// <para>
/// Stock traces four rays in the middle of the gather and carries on
/// (<c>TestLine</c>). The batch tracer answers only
/// between batches, so the gather is split at every ray: everything computed
/// before a ray is asked for is computed ONCE, while emitting, and whatever the
/// rest of the arithmetic needs from it is written here; the resolve reads it
/// back with the answers and finishes. Nothing is computed twice.
/// </para>
/// <para>
/// One flat, growable <see cref="int"/> stream, floats stored by their bits,
/// owned by one worker (or one <see cref="LightRayLog"/>) and reused for every
/// batch: after the first few batches it never allocates.
/// </para>
/// </remarks>
internal sealed class GatherTape
{
    private int[] _data = new int[64];
    private int _write;
    private int _read;

    // A four-entry memo of DirectLightGatherer.LaneRecurses, keyed by the
    // point's exact bits.
    private readonly Vec3[] _recursionPoints = new Vec3[4];
    private readonly bool[] _recursionAnswers = new bool[4];
    private int _recursionCount;
    private int _recursionNext;

    /// <summary>How many words are written.</summary>
    public int Length => _write;

    /// <summary>True once every written word has been read.</summary>
    public bool FullyRead => _read == _write;

    /// <summary>Forgets everything; keeps the storage.</summary>
    public void Reset()
    {
        _write = 0;
        _read = 0;
        _recursionCount = 0;
        _recursionNext = 0;
    }

    /// <summary>A remembered skybox-recursion answer for a point.</summary>
    /// <param name="point">The point, compared bit for bit.</param>
    /// <param name="recurses">The answer, when remembered.</param>
    /// <returns>True when remembered.</returns>
    public bool TryRecursion(Vec3 point, out bool recurses)
    {
        for (int i = 0; i < _recursionCount; i++)
        {
            Vec3 p = _recursionPoints[i];
            if (BitConverter.SingleToInt32Bits(p.X) == BitConverter.SingleToInt32Bits(point.X)
                && BitConverter.SingleToInt32Bits(p.Y) == BitConverter.SingleToInt32Bits(point.Y)
                && BitConverter.SingleToInt32Bits(p.Z) == BitConverter.SingleToInt32Bits(point.Z))
            {
                recurses = _recursionAnswers[i];
                return true;
            }
        }

        recurses = false;
        return false;
    }

    /// <summary>Remembers a skybox-recursion answer, replacing the oldest.</summary>
    /// <param name="point">The point.</param>
    /// <param name="recurses">The answer.</param>
    public void StoreRecursion(Vec3 point, bool recurses)
    {
        _recursionPoints[_recursionNext] = point;
        _recursionAnswers[_recursionNext] = recurses;
        _recursionNext = (_recursionNext + 1) & 3;
        _recursionCount = Math.Min(_recursionCount + 1, 4);
    }

    /// <summary>Starts reading from the first word again.</summary>
    public void Rewind() => _read = 0;

    /// <summary>Appends an int.</summary>
    /// <param name="value">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Int(int value)
    {
        if (_write == _data.Length)
        {
            Grow();
        }

        _data[_write++] = value;
    }

    /// <summary>Appends a float, bit for bit.</summary>
    /// <param name="value">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Float(float value) => Int(BitConverter.SingleToInt32Bits(value));

    /// <summary>Appends a vector.</summary>
    /// <param name="value">The value.</param>
    public void Vector(Vec3 value)
    {
        Float(value.X);
        Float(value.Y);
        Float(value.Z);
    }

    /// <summary>Reads the next int.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The tape is exhausted.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt()
    {
        if (_read >= _write)
        {
            throw new InvalidOperationException("the resolve read past what the emit wrote");
        }

        return _data[_read++];
    }

    /// <summary>Reads the next float.</summary>
    /// <returns>The value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadInt());

    /// <summary>Reads the next vector.</summary>
    /// <returns>The value.</returns>
    public Vec3 ReadVector()
    {
        float x = ReadFloat();
        float y = ReadFloat();
        float z = ReadFloat();
        return new Vec3(x, y, z);
    }

    private void Grow() => Array.Resize(ref _data, _data.Length * 2);
}
