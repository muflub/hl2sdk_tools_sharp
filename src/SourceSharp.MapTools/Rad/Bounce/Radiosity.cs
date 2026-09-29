//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>
/// <c>BounceLight</c>, <c>GatherLight</c> and <c>CollectLight</c>
/// Light passed from patch to patch along the
/// transfers until a bounce adds less than 1 in every channel.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it leaves behind.</b> Stock's <c>BounceLight</c> first MOVES each
/// patch's direct light out of <c>totallight.light[0]</c> into
/// <c>emitlight</c> and zeroes it, so that when the loop ends
/// <c>totallight</c> holds the BOUNCED light only, per bump normal on a bumped
/// face ("only the bounced light is integrated into
/// totallight!"). That is what <c>FinalLightFace</c> blends back in through the
/// radial filter, so it is written back into
/// <see cref="Patch.TotalLight"/> exactly as stock leaves it.
/// <see cref="Patch.DirectLight"/> is not touched.
/// </para>
/// <para>
/// <b>Determinism.</b> <c>GatherLight</c> runs over patches in parallel, but
/// each patch reads only <c>emitlight</c> (fixed for the whole pass) and writes
/// only its own <c>addlight</c>, summing over its own transfers in their fixed
/// order. <c>CollectLight</c> is one serial walk in reverse patch order, as
/// stock's is, so the per-bounce total is the same float sum at any thread
/// count.
/// </para>
/// <para>
/// <b>Locality.</b> A transfer names its shooter by index, so every transfer
/// of every bounce is a random read of the shooter. Stock reads the
/// shooter's whole <c>CPatch</c> for its reflectivity (and, for a bumped
/// receiver, its origin): a structure of some three hundred bytes, one or
/// two cache lines per transfer out of an array far larger than the cache.
/// Here the two things the gather needs are kept in dense arrays instead --
/// each patch's <c>emit * reflectivity</c>, the product stock forms per
/// transfer, formed once per bounce where the emission is written; and each
/// patch's origin, fixed for the whole bounce -- so a transfer costs one
/// twelve-byte read. The product is the same three float multiplies stock
/// makes, so the sums are the same bits.
/// </para>
/// </remarks>
public sealed class Radiosity
{
    private readonly BounceContext _context;
    private readonly TransferSet _transfers;
    private readonly Vec3[] _emit;
    private readonly Vec3[] _shoot;
    private readonly Vec3[] _origins;
    private readonly BumpLights[] _add;
    private readonly Vec3[] _normals;
    private readonly int[] _normalBase;

    /// <summary>Prepares a bounce.</summary>
    /// <param name="context">The lit world.</param>
    /// <param name="transfers">The transfers from <see cref="VisMatrix"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public Radiosity(BounceContext context, TransferSet transfers)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transfers);

        _context = context;
        _transfers = transfers;
        int count = context.Patches.Count;
        _emit = new Vec3[count];
        _shoot = new Vec3[count];
        _origins = new Vec3[count];
        _add = new BumpLights[count];
        for (int i = 0; i < count; i++)
        {
            _origins[i] = context.Patches.At(i).Origin;
        }

        // Bumped patches with transfers need four normals each; they do not
        // change between bounces, so they are computed once.
        _normalBase = new int[count];
        int bumped = 0;
        for (int i = 0; i < count; i++)
        {
            _normalBase[i] = -1;
            if (context.Patches.At(i).NeedsBumpmap && transfers.CountFor(i) > 0)
            {
                _normalBase[i] = bumped;
                bumped += BumpBasis.Count + 1;
            }
        }

        _normals = new Vec3[bumped];
    }

    /// <summary><c>emitlight</c>: what each patch sends in the current bounce.</summary>
    public ReadOnlySpan<Vec3> EmitLight => _emit;

    /// <summary>
    /// Gather every bumped patch's transfers one at a time, never four at
    /// once -- for the facts that hold the two paths to the same bits.
    /// </summary>
    internal bool ScalarBumpedGather { get; init; }

    /// <summary><c>addlight</c>: what each patch received in the current bounce.</summary>
    public ReadOnlySpan<BumpLights> AddLight => _add;

    /// <summary>
    /// <c>BounceLight</c>.
    /// </summary>
    /// <param name="queue">The workers.</param>
    /// <param name="cancellationToken">Cancels the bounce.</param>
    /// <returns>
    /// Each bounce's total added light, in order: the
    /// <c>Bounce #%i added RGB(%.0f, %.0f, %.0f)</c> lines.
    /// </returns>
    /// <remarks>
    /// The loop stops after <c>numbounce</c> bounces or after the first bounce
    /// that adds less than 1 in all three channels (double
    /// compares) -- so it always runs at least once when it runs at all.
    /// </remarks>
    public async Task<IReadOnlyList<Vec3>> BounceAsync(WorkQueue queue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queue);
        cancellationToken.ThrowIfCancellationRequested();

        List<Vec3> added = [];
        int bounces = _context.Settings.Bounces;
        if (bounces <= 0)
        {
            return added;
        }

        await queue.RunAsync(
            _context.Patches.Count,
            (i, _) => PrepareNormals(i),
            new WorkQueueOptions { Stage = "GatherLight" },
            cancellationToken).ConfigureAwait(false);

        MoveDirectLightToEmission();

        for (int i = 0; ; i++)
        {
            await queue.RunAsync(
                _context.Patches.Count,
                (j, _) => GatherLight(j),
                new WorkQueueOptions { Stage = "GatherLight" },
                cancellationToken).ConfigureAwait(false);

            Vec3 total = CollectLight();
            added.Add(total);

            if (i + 1 == bounces || (total.X < 1.0 && total.Y < 1.0 && total.Z < 1.0))
            {
                break;
            }
        }

        return added;
    }

    /// <summary>
    /// The first step of <c>BounceLight</c>: every
    /// patch's direct light becomes its first emission and is zeroed in its
    /// total, "to integrate bounces only".
    /// </summary>
    public void MoveDirectLightToEmission()
    {
        PatchSet patches = _context.Patches;
        for (int i = 0; i < patches.Count; i++)
        {
            ref Patch patch = ref patches.At(i);
            _emit[i] = patch.TotalLight.Flat;
            _shoot[i] = Shoot(_emit[i], patch.Reflectivity);
            patch.TotalLight.Flat = Vec3.Zero;
        }
    }

    /// <summary>
    /// What a patch sends down each of its transfers before the transfer's
    /// weight: its emission times its reflectivity, per channel, as stock
    /// forms it inside <c>GatherLight</c>.
    /// </summary>
    private static Vec3 Shoot(Vec3 e, Vec3 r) => new(e.X * r.X, e.Y * r.Y, e.Z * r.Z);

    /// <summary>
    /// <c>GatherLight</c> for one patch: the light it
    /// receives this bounce, into <see cref="AddLight"/>.
    /// </summary>
    /// <param name="j">The receiving patch.</param>
    /// <remarks>
    /// <para>
    /// A flat patch sums <c>emit * reflectivity * transfer</c> over its list.
    /// </para>
    /// <para>
    /// A BUMPED one first divides out the receiver's cosine, which the
    /// transfer's form factor already contains ("remove normal already
    /// factored into transfer steradian",), then re-applies the
    /// cosine against each of its four normals -- the flat normal and the three
    /// bump basis vectors -- skipping any the source is behind. The flat slot
    /// uses <c>patch-&gt;normal</c>, not the phong normal the basis was built
    /// from (stock's own "FIXME: why does the patch not use the phong
    /// Normal?",).
    /// </para>
    /// </remarks>
    public void GatherLight(int j)
    {
        PatchSet patches = _context.Patches;
        ref Patch patch = ref patches.At(j);
        ReadOnlySpan<Transfer> trans = _transfers.For(j);

        if (patch.NeedsBumpmap)
        {
            BumpLights bumped = default;
            if (trans.Length > 0)
            {
                ReadOnlySpan<Vec3> normals = _normals.AsSpan(_normalBase[j], BumpBasis.Count + 1);
                bool stock = _context.Settings.StockNormalise;
                if (stock || ScalarBumpedGather)
                {
                    GatherBumpedScalar(trans, patch.Origin, patch.Normal, normals, stock, ref bumped);
                }
                else
                {
                    GatherBumpedFour(trans, patch.Origin, patch.Normal, normals, ref bumped);
                }
            }

            _add[j] = bumped;
            return;
        }

        Vec3 sum = Vec3.Zero;
        for (int k = 0; k < trans.Length; k++)
        {
            Vec3 v = _shoot[trans[k].Patch];
            v *= trans[k].Weight;
            sum += v;
        }

        _add[j] = new BumpLights { Flat = sum };
    }

    /// <summary>
    /// <c>CollectLight</c>: add what every leaf patch
    /// received to its total, pull it up the patch tree, make it next
    /// bounce's emission, and clear <see cref="AddLight"/>.
    /// </summary>
    /// <returns>The sum of every leaf patch's new emission: the bounce's total.</returns>
    /// <remarks>
    /// Reverse patch order, so that children -- always created after their
    /// parents -- are done first. A sky patch emits nothing. A parent's light
    /// is its children's, weighted by area; stock's
    /// <c>(int)patch-&gt;area != (int)(child1-&gt;area + child2-&gt;area)</c>
    /// test assigns a variable that is overwritten on the next
    /// line, so it has no effect and is not ported.
    /// </remarks>
    public Vec3 CollectLight()
    {
        PatchSet patches = _context.Patches;
        Vec3 total = Vec3.Zero;

        for (int i = patches.Count - 1; i >= 0; i--)
        {
            ref Patch patch = ref patches.At(i);
            int normalCount = patch.NeedsBumpmap ? BumpBasis.Count + 1 : 1;

            if (patch.Sky)
            {
                _emit[i] = Vec3.Zero;
                _shoot[i] = Shoot(_emit[i], patch.Reflectivity);
            }
            else if (patch.Child1 == Patch.Invalid)
            {
                for (int j = 0; j < normalCount; j++)
                {
                    patch.TotalLight[j] = patch.TotalLight[j] + _add[i][j];
                }

                _emit[i] = _add[i].Flat;
                _shoot[i] = Shoot(_emit[i], patch.Reflectivity);
                total += _emit[i];
            }
            else
            {
                ref Patch child1 = ref patches.At(patch.Child1);
                ref Patch child2 = ref patches.At(patch.Child2);
                float s1 = child1.Area / (child1.Area + child2.Area);
                float s2 = child2.Area / (child1.Area + child2.Area);

                for (int j = 0; j < normalCount; j++)
                {
                    // VectorScale then VectorMA: c1 * s1, then + s2 * c2.
                    Vec3 light = child1.TotalLight[j] * s1;
                    patch.TotalLight[j] = light + (child2.TotalLight[j] * s2);
                }

                _emit[i] = (_emit[patch.Child1] * s1) + (_emit[patch.Child2] * s2);
                _shoot[i] = Shoot(_emit[i], patch.Reflectivity);
            }

            _add[i] = default;
        }

        return total;
    }

    /// <summary>
    /// The four normals <c>GatherLight</c> builds for a bumped patch
    /// </summary>
    /// <param name="i">The patch.</param>
    public void PrepareNormals(int i)
    {
        if (_normalBase[i] < 0)
        {
            return;
        }

        LightGeometry geometry = _context.Geometry;
        ref Patch patch = ref _context.Patches.At(i);
        Span<Vec3> normals = _normals.AsSpan(_normalBase[i], BumpBasis.Count + 1);
        ref readonly TexInfo tex = ref geometry.TexInfos[geometry.Faces[patch.FaceNumber].TexInfo];
        bool stock = _context.Settings.StockNormalise;

        if (geometry.Faces[patch.FaceNumber].DispInfo != -1)
        {
            // A displacement's basis comes from its texture axes
            // mapped into lightmap space.
            Vec3 normal = patch.Normal;
            (Vec3 u, Vec3 v) = PreGetBumpNormalsForDisp(in tex, ref normal, stock);
            normals[0] = normal;
            BumpBasis.Build(u, v, normals[0], normals[0], normals[1..], stock);
        }
        else
        {
            // The phong normal at the patch origin, as
            // CreateChildPatch computes it: in offset space.
            Vec3 phong = PhongNormals.Compute(
                geometry, _context.Neighbours, _context.Patches.Centroids,
                patch.FaceNumber, patch.Origin, _context.Settings.SmoothingThreshold);
            (Vec3 s, Vec3 t) = FaceLightJob.TextureAxes(in tex);
            BumpBasis.Build(s, t, patch.Normal, phong, normals[1..], stock);
        }

        // 1582. "force the base lightmap to use the flat normal".
        normals[0] = patch.Normal;
    }

    /// <summary>
    /// <c>PreGetBumpNormalsForDisp</c>: a displacement's
    /// texture axes, re-expressed in lightmap space when the two disagree.
    /// </summary>
    /// <param name="tex">The face's texinfo.</param>
    /// <param name="normal">The normal; replaced when a conversion happens.</param>
    /// <param name="stockNormalise">The normalise fork.</param>
    /// <returns>The U and V axes the bump basis is built from.</returns>
    /// <remarks>
    /// When either texture axis is more than about 2.6 degrees off its lightmap
    /// axis (<c>|dot| &lt; 0.999f</c>) the result is the columns of
    /// <c>ConcatTransforms(light, tex)</c> with each matrix's columns the
    /// (U, V, normal) axes; <c>ConcatTransforms</c> is the SSE form
    /// So each element is
    /// <c>a0*b0 + (a1*b1 + a2*b2)</c>.
    /// </remarks>
    public static (Vec3 U, Vec3 V) PreGetBumpNormalsForDisp(in TexInfo tex, ref Vec3 normal, bool stockNormalise)
    {
        FloatArray8 t = tex.TextureVecsTexelsPerWorldUnits;
        FloatArray8 l = tex.LightmapVecsLuxelsPerWorldUnits;
        Vec3 texU = BumpBasis.Normalise(new Vec3(t[0], t[1], t[2]), stockNormalise);
        Vec3 texV = BumpBasis.Normalise(new Vec3(t[4], t[5], t[6]), stockNormalise);
        Vec3 lightU = BumpBasis.Normalise(new Vec3(l[0], l[1], l[2]), stockNormalise);
        Vec3 lightV = BumpBasis.Normalise(new Vec3(l[4], l[5], l[6]), stockNormalise);

        bool convert = MathF.Abs(Vec3.Dot(texU, lightU)) < 0.999f || MathF.Abs(Vec3.Dot(texV, lightV)) < 0.999f;
        if (!convert)
        {
            return (texU, texV);
        }

        Vec3 n = normal;
        Vec3 Column(Vec3 b) => new(
            (lightU.X * b.X) + ((lightV.X * b.Y) + (n.X * b.Z)),
            (lightU.Y * b.X) + ((lightV.Y * b.Y) + (n.Y * b.Z)),
            (lightU.Z * b.X) + ((lightV.Z * b.Y) + (n.Z * b.Z)));

        Vec3 u = Column(texU);
        Vec3 v = Column(texV);
        normal = Column(n);
        return (u, v);
    }

    /// <summary>
    /// A bumped receiver's gather, one transfer at a time: stock's loop, on
    /// either side of the normalise fork.
    /// </summary>
    private void GatherBumpedScalar(
        ReadOnlySpan<Transfer> trans, Vec3 origin, Vec3 normal, ReadOnlySpan<Vec3> normals, bool stock, ref BumpLights sums)
    {
        for (int k = 0; k < trans.Length; k++)
        {
            int source = trans[k].Patch;
            (Vec3 delta, _) = FormFactors.Normalise(_origins[source] - origin, stock);
            float scale = 1.0f / Vec3.Dot(delta, normal);
            Vec3 v = _shoot[source] * (trans[k].Weight * scale);

            Accumulate(ref sums.Flat, v, Vec3.Dot(delta, normals[0]));
            Accumulate(ref sums.Bump1, v, Vec3.Dot(delta, normals[1]));
            Accumulate(ref sums.Bump2, v, Vec3.Dot(delta, normals[2]));
            Accumulate(ref sums.Bump3, v, Vec3.Dot(delta, normals[3]));
        }
    }

    /// <summary>
    /// <see cref="GatherBumpedScalar"/> on the exact side, with the geometry of
    /// four transfers at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why it exists. Per transfer, a bumped receiver normalises the direction
    /// to its shooter -- a square root and three divides -- divides once more
    /// by the cosine it removes, and takes four dot products, all before any
    /// light is summed. None of that depends on the bounce's light, and on the
    /// exact side all of it is lane-wise IEEE arithmetic, so it is done here
    /// four transfers to a vector: one square root and four divides per four
    /// transfers instead of per one.
    /// </para>
    /// <para>
    /// Why it gives the same bits. Each lane makes the scalar path's
    /// operations in the scalar path's order: the same left-to-right sum of
    /// squares, an exact square root, three true divides (not a reciprocal
    /// and three multiplies), a zero length giving the zero vector as
    /// <see cref="Vec3.Normalise"/> does, the same dot products, and
    /// <c>weight * scale</c> formed before it scales the light. The
    /// accumulation -- the only part where order matters, because float
    /// addition is not associative -- stays scalar, transfer after transfer in
    /// list order, with the scalar path's test (<c>!(dot &lt;= 0)</c>, so a
    /// NaN cosine is summed as the scalar path sums it). A short last block
    /// repeats its final transfer in the unused lanes, whose answers are never
    /// read.
    /// </para>
    /// </remarks>
    [SkipLocalsInit]
    private void GatherBumpedFour(
        ReadOnlySpan<Transfer> trans, Vec3 origin, Vec3 normal, ReadOnlySpan<Vec3> normals, ref BumpLights sums)
    {
        const int L = 4;
        Span<float> weight = stackalloc float[L];
        Span<float> d0 = stackalloc float[L];
        Span<float> d1 = stackalloc float[L];
        Span<float> d2 = stackalloc float[L];
        Span<float> d3 = stackalloc float[L];
        Span<int> source = stackalloc int[L];

        Vector128<float> ox = Vector128.Create(origin.X);
        Vector128<float> oy = Vector128.Create(origin.Y);
        Vector128<float> oz = Vector128.Create(origin.Z);
        Vector128<float> zero = Vector128<float>.Zero;
        Vector128<float> one = Vector128.Create(1.0f);

        for (int k = 0; k < trans.Length; k += L)
        {
            int live = Math.Min(L, trans.Length - k);
            for (int lane = 0; lane < L; lane++)
            {
                source[lane] = trans[k + Math.Min(lane, live - 1)].Patch;
            }

            Vec3 a = _origins[source[0]], b = _origins[source[1]], c = _origins[source[2]], d = _origins[source[3]];
            Vector128<float> dx = Vector128.Create(a.X, b.X, c.X, d.X) - ox;
            Vector128<float> dy = Vector128.Create(a.Y, b.Y, c.Y, d.Y) - oy;
            Vector128<float> dz = Vector128.Create(a.Z, b.Z, c.Z, d.Z) - oz;

            // Vec3.Normalise: an exact length, three true divides, and the
            // zero vector for a zero length.
            Vector128<float> length = Vector128.Sqrt(((dx * dx) + (dy * dy)) + (dz * dz));
            Vector128<float> nonzero = ~Vector128.Equals(length, zero);
            dx = Vector128.ConditionalSelect(nonzero, dx / length, zero);
            dy = Vector128.ConditionalSelect(nonzero, dy / length, zero);
            dz = Vector128.ConditionalSelect(nonzero, dz / length, zero);

            Vector128<float> scale = one / Dot(dx, dy, dz, normal);
            Vector128<float> w = Vector128.Create(
                trans[k].Weight,
                trans[k + Math.Min(1, live - 1)].Weight,
                trans[k + Math.Min(2, live - 1)].Weight,
                trans[k + Math.Min(3, live - 1)].Weight);
            (w * scale).CopyTo(weight);
            Dot(dx, dy, dz, normals[0]).CopyTo(d0);
            Dot(dx, dy, dz, normals[1]).CopyTo(d1);
            Dot(dx, dy, dz, normals[2]).CopyTo(d2);
            Dot(dx, dy, dz, normals[3]).CopyTo(d3);

            for (int lane = 0; lane < live; lane++)
            {
                Vec3 v = _shoot[source[lane]] * weight[lane];
                Accumulate(ref sums.Flat, v, d0[lane]);
                Accumulate(ref sums.Bump1, v, d1[lane]);
                Accumulate(ref sums.Bump2, v, d2[lane]);
                Accumulate(ref sums.Bump3, v, d3[lane]);
            }
        }
    }

    /// <summary><see cref="Vec3.Dot"/>'s order, four lanes against one vector.</summary>
    private static Vector128<float> Dot(Vector128<float> x, Vector128<float> y, Vector128<float> z, Vec3 n) =>
        ((x * Vector128.Create(n.X)) + (y * Vector128.Create(n.Y))) + (z * Vector128.Create(n.Z));

    /// <summary>
    /// Adds one transfer's light along one normal, unless the shooter is
    /// behind it. The test is stock's <c>dot &lt;= 0</c> skip, so a NaN
    /// cosine is summed rather than skipped.
    /// </summary>
    private static void Accumulate(ref Vec3 sum, Vec3 v, float dot)
    {
        if (dot <= 0)
        {
            return;
        }

        sum += v * dot;
    }
}
