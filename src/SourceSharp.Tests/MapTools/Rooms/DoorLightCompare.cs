//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// How a lit linked level compares with a reference lighting of the same
/// level, luxel by luxel, the way the rooms design's prototype (9.7) and
/// PR 10's sweep measured it: the relative error of each luxel within one
/// door width of a joint and elsewhere, how many luxels are brighter or
/// darker than the reference by more than 5%, and the energy (the sum of
/// every compared luxel) over the reference's.
/// </summary>
internal static class DoorLightCompare
{
    /// <summary>One jointed socket pair, the light of room <c>A</c> into room <c>B</c>, world coordinates.</summary>
    public sealed record Joint(int A, int B, Vec3 Centre, Vec3 Out, float Width, float Height);

    /// <summary>What a comparison measured.</summary>
    /// <param name="Near">Luxels within a door width of a joint.</param>
    /// <param name="NearP50">Their median relative error.</param>
    /// <param name="NearP95">Their 95th percentile.</param>
    /// <param name="NearP99">Their 99th percentile.</param>
    /// <param name="Far">The other luxels.</param>
    /// <param name="FarP50">Their median.</param>
    /// <param name="FarP95">Their 95th percentile.</param>
    /// <param name="FarP99">Their 99th percentile.</param>
    /// <param name="Brighter">Luxels brighter than the reference by more than 5% (and a hundredth of a unit).</param>
    /// <param name="Darker">Luxels darker by as much.</param>
    /// <param name="Count">Luxels compared.</param>
    /// <param name="Energy">The sum of the level's luxels over the reference's.</param>
    /// <param name="ExcessP99">
    /// The 99th percentile over every luxel of how much brighter than the
    /// reference it is, relative as <see cref="LitCompare.Relative"/> (zero
    /// for a luxel no brighter): how much light the level invents.
    /// </param>
    public sealed record Metric(
        int Near, double NearP50, double NearP95, double NearP99, int Far, double FarP50, double FarP95, double FarP99,
        int Brighter, int Darker, int Count, double Energy, double ExcessP99)
    {
        /// <inheritdoc/>
        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"near {Near} p50 {NearP50:F3} p95 {NearP95:F3} p99 {NearP99:F3}; far {Far} p50 {FarP50:F3} p95 {FarP95:F3} p99 {FarP99:F3};"
            + $" brighter {Brighter} darker {Darker} of {Count}; energy {Energy:F3}; excess p99 {ExcessP99:F3}");
    }

    /// <summary>Every jointed pair of sockets of a linked level, each direction once.</summary>
    public static List<Joint> Joints(LinkedLevel level)
    {
        IReadOnlyList<ResolvedPlacement> placed = level.Plan.Rooms;
        List<Joint> joints = [];
        for (int a = 0; a < placed.Count; a++)
        {
            RoomDefinition da = placed[a].Room.Definition;
            foreach (RoomSocket sa in da.Sockets)
            {
                DoorFrame fa = DoorFrame.Of(da, sa);
                (Vec3 ca, Vec3 oa) = WorldFrame(placed[a], fa);
                for (int b = 0; b < placed.Count; b++)
                {
                    if (b == a)
                    {
                        continue;
                    }

                    RoomDefinition db = placed[b].Room.Definition;
                    foreach (RoomSocket sb in db.Sockets)
                    {
                        (Vec3 cb, Vec3 ob) = WorldFrame(placed[b], DoorFrame.Of(db, sb));
                        if ((ca + (oa * (2 * fa.Depth)) - cb).Length() < 0.01f && Vec3.Dot(oa, ob) < -0.99f)
                        {
                            joints.Add(new Joint(a, b, ca + (oa * fa.Depth), oa, fa.Width, fa.Height));
                        }
                    }
                }
            }
        }

        return joints;

        static (Vec3 Centre, Vec3 Out) WorldFrame(ResolvedPlacement p, DoorFrame f)
        {
            RoomTransform t = new(p.Instance.Placement, p.Room.Definition.CellSize);
            return (t.Apply(f.Centre), RoomTransform.Rotate(f.Out, p.Instance.Placement.NormalizedRotation));
        }
    }

    /// <summary>
    /// The level's style-0 luxels (every style's with <paramref name="allStyles"/>)
    /// against the reference's at the same points of the world. A luxel only
    /// the reference has is skipped for style 0 (a doorway face, which a
    /// linked level does not have: the known difference) and counted as black
    /// for another style; thin faces, whose luxel sits on their edges, are
    /// skipped.
    /// </summary>
    public static Metric Measure(BspData level, BspData reference, List<Joint> joints, bool allStyles = false)
    {
        var a = LitCompare.Lattice(level);
        List<double> near = [], far = [], excess = [];
        int brighter = 0, darker = 0, count = 0;
        double sumA = 0, sumB = 0;
        foreach ((var key, (List<ColorRgbExp32> colours, bool thin)) in LitCompare.Lattice(reference))
        {
            if ((!allStyles && key.Item7 != 0) || thin)
            {
                continue;
            }

            Vec3 y = colours[0].ToLinear();
            Vec3 x = Vec3.Zero;
            if (a.TryGetValue(key, out var other))
            {
                if (other.Thin)
                {
                    continue;
                }

                x = other.Colours[0].ToLinear();
            }
            else if (key.Item7 == 0)
            {
                continue;
            }

            Vec3 p = new(key.Item1 / 100f, key.Item2 / 100f, key.Item3 / 100f);
            (joints.Any(j => NearJoint(j, p)) ? near : far).Add(LitCompare.Relative(x, y));
            excess.Add(LitCompare.Relative(new Vec3(MathF.Max(x.X, y.X), MathF.Max(x.Y, y.Y), MathF.Max(x.Z, y.Z)), y));
            count++;
            float sx = x.X + x.Y + x.Z, sy = y.X + y.Y + y.Z;
            brighter += sx > (sy * 1.05f) + 0.01f ? 1 : 0;
            darker += sx < (sy * 0.95f) - 0.01f ? 1 : 0;
            sumA += sx;
            sumB += sy;
        }

        near.Sort();
        far.Sort();
        excess.Sort();
        return new Metric(
            near.Count, LitCompare.Quantile(near, .5), LitCompare.Quantile(near, .95), LitCompare.Quantile(near, .99),
            far.Count, LitCompare.Quantile(far, .5), LitCompare.Quantile(far, .95), LitCompare.Quantile(far, .99),
            brighter, darker, count, sumB == 0 ? 1 : sumA / sumB, LitCompare.Quantile(excess, .99));
    }

    /// <summary>Within one door width of a joint's opening.</summary>
    private static bool NearJoint(Joint j, Vec3 p)
    {
        Vec3 d = p - j.Centre;
        float along = MathF.Abs(Vec3.Dot(d, j.Out));
        Vec3 across = Vec3.Cross(new Vec3(0, 0, 1), j.Out);
        float side = MathF.Max(0, MathF.Abs(Vec3.Dot(d, across)) - (j.Width / 2));
        float up = MathF.Max(0, MathF.Abs(d.Z) - (j.Height / 2));
        return MathF.Sqrt((along * along) + (side * side) + (up * up)) <= j.Width;
    }
}
