using System.Buffers.Binary;
using System.Globalization;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// Compares two <c>VPHY</c> blobs the way plan ruling Q18 grades general brushes: every
/// structural field exactly (header, counts, byte sizes, offsets, triangle and edge words, tree
/// shape, box bytes), every float within a few ULP or relative 1e-6 with a 1e-9 absolute floor.
/// </summary>
/// <remarks>
/// The walk follows the layout the managed cooker writes (<c>IvpCompactLedge</c>,
/// <c>IvpLedgeSoup</c>): a 28-byte VPHY header, a 48-byte compact surface header, the ledges, then
/// 28-byte tree nodes reached from <c>offset_ledgetree_root</c>.
/// </remarks>
internal static class VphyCompare
{
    /// <summary>Maximum ULP distance accepted for a float field.</summary>
    public const int Ulps = 4;

    /// <summary>Relative tolerance.</summary>
    public const double Relative = 1e-6;

    /// <summary>Absolute floor, metres.</summary>
    public const double Floor = 1e-9;

    /// <summary>The outcome of one comparison.</summary>
    /// <param name="Identical">Byte-for-byte equal.</param>
    /// <param name="StructureEqual">Every non-float field equal.</param>
    /// <param name="FloatsWithin">Every float field within tolerance.</param>
    /// <param name="WorstRelative">Largest relative error seen on a float field.</param>
    /// <param name="Detail">The first difference, for a failure message.</param>
    public readonly record struct Result(bool Identical, bool StructureEqual, bool FloatsWithin, double WorstRelative, string Detail);

    /// <summary>Compares a managed blob against a reference one.</summary>
    /// <param name="native">The reference blob.</param>
    /// <param name="managed">The managed blob.</param>
    /// <returns>The grading.</returns>
    public static Result Compare(byte[] native, byte[] managed)
    {
        if (native.AsSpan().SequenceEqual(managed))
        {
            return new Result(true, true, true, 0, string.Empty);
        }

        if (native.Length != managed.Length)
        {
            return new Result(false, false, false, double.PositiveInfinity, $"length {native.Length} vs {managed.Length}");
        }

        var state = new State(native, managed);
        state.Exact(0, 12, "VPHY header");
        state.Float(12, "drag.x");
        state.Float(16, "drag.y");
        state.Float(20, "drag.z");
        state.Exact(24, 4, "axisMapSize");

        const int S = 28;
        for (int k = 0; k < 7; k++)
        {
            state.Float(S + (4 * k), k switch
            {
                < 3 => "mass_center",
                < 6 => "rotation_inertia",
                _ => "upper_limit_radius",
            });
        }

        // max_factor byte is derived from floats; byte_size (3 bytes), root offset, dummies exact.
        state.Exact(S + 29, 3, "byte_size");
        state.Exact(S + 32, 16, "ledgetree_root / dummy");
        state.Derived(S + 28, "max_factor_surface_deviation");
        int root = BinaryPrimitives.ReadInt32LittleEndian(native.AsSpan(S + 32));
        state.Node(S + root);
        return state.Finish();
    }

    private sealed class State(byte[] a, byte[] b)
    {
        private readonly HashSet<int> _visitedLedges = [];
        private bool _structure = true;
        private bool _floats = true;
        private double _worst;
        private string _detail = string.Empty;

        public void Exact(int offset, int length, string what)
        {
            if (!a.AsSpan(offset, length).SequenceEqual(b.AsSpan(offset, length)))
            {
                Fail(ref _structure, $"{what} @{offset}");
            }
        }

        public void Derived(int offset, string what)
        {
            if (a[offset] != b[offset] && Math.Abs(a[offset] - b[offset]) > 1)
            {
                Fail(ref _structure, $"{what} @{offset}: {a[offset]} vs {b[offset]}");
            }
        }

        public void Float(int offset, string what)
        {
            float x = BinaryPrimitives.ReadSingleLittleEndian(a.AsSpan(offset));
            float y = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset));
            if (BitConverter.SingleToInt32Bits(x) == BitConverter.SingleToInt32Bits(y) || x == y)
            {
                return;
            }

            double diff = Math.Abs((double)x - y);
            double scale = Math.Max(Math.Abs((double)x), Math.Abs((double)y));
            double rel = scale == 0 ? 0 : diff / scale;
            _worst = Math.Max(_worst, rel);
            if (UlpDistance(x, y) <= Ulps || diff <= Relative * scale || diff <= Floor)
            {
                return;
            }

            Fail(ref _floats, string.Create(CultureInfo.InvariantCulture, $"{what} @{offset}: {x:R} vs {y:R} (rel {rel:E2})"));
        }

        public void Node(int at)
        {
            Exact(at, 8, "node offsets");
            for (int k = 0; k < 4; k++)
            {
                Float(at + 8 + (4 * k), k < 3 ? "node center" : "node radius");
            }

            Derived(at + 24, "node box size x");
            Derived(at + 25, "node box size y");
            Derived(at + 26, "node box size z");
            Exact(at + 27, 1, "node free_0");
            int right = BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(at));
            int ledge = BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(at + 4));
            if (ledge != 0)
            {
                Ledge(at + ledge);
            }

            if (right != 0)
            {
                Node(at + 28);
                Node(at + right);
            }
        }

        private void Ledge(int at)
        {
            if (!_visitedLedges.Add(at))
            {
                return;
            }

            Exact(at, 16, "ledge header");
            int points = BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(at));
            int size = (int)(BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(at + 8)) >> 8) * 16;
            Exact(at + 16, points - 16, "triangles");
            for (int p = at + points; p < at + size; p += 16)
            {
                Float(p, "point.x");
                Float(p + 4, "point.y");
                Float(p + 8, "point.z");
                Exact(p + 12, 4, "point.w");
            }
        }

        private void Fail(ref bool flag, string what)
        {
            if (flag && _structure && _floats)
            {
                _detail = what;
            }

            flag = false;
        }

        public Result Finish() => new(false, _structure, _floats, _worst, _detail);
    }

    /// <summary>ULP distance between two finite floats of the same sign.</summary>
    /// <param name="x">First.</param>
    /// <param name="y">Second.</param>
    /// <returns>The distance, or int.MaxValue across a sign change or for non-finite values.</returns>
    public static long UlpDistance(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            return int.MaxValue;
        }

        int ix = BitConverter.SingleToInt32Bits(x), iy = BitConverter.SingleToInt32Bits(y);
        if ((ix < 0) != (iy < 0))
        {
            return (x == 0 && y == 0) ? 0 : int.MaxValue;
        }

        return Math.Abs((long)ix - iy);
    }
}
