using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Bounce;

/// <summary>
/// <c>BounceLight</c>, <c>GatherLight</c> and <c>CollectLight</c>
/// (<c>vrad.cpp:1413-1725</c>): light passed from patch to patch along the
/// transfers until a bounce adds less than 1 in every channel.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it leaves behind.</b> Stock's <c>BounceLight</c> first MOVES each
/// patch's direct light out of <c>totallight.light[0]</c> into
/// <c>emitlight</c> and zeroes it, so that when the loop ends
/// <c>totallight</c> holds the BOUNCED light only, per bump normal on a bumped
/// face (<c>vrad.cpp:1662-1668</c>, "only the bounced light is integrated into
/// totallight!"). That is what <c>FinalLightFace</c> blends back in through the
/// radial filter (<c>radial.cpp:332,337</c>), so it is written back into
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
/// </remarks>
public sealed class Radiosity
{
    private readonly BounceContext _context;
    private readonly TransferSet _transfers;
    private readonly Vec3[] _emit;
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
        _add = new BumpLights[count];

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

    /// <summary><c>addlight</c>: what each patch received in the current bounce.</summary>
    public ReadOnlySpan<BumpLights> AddLight => _add;

    /// <summary>
    /// <c>BounceLight</c> (<c>vrad.cpp:1653</c>).
    /// </summary>
    /// <param name="queue">The workers.</param>
    /// <param name="cancellationToken">Cancels the bounce.</param>
    /// <returns>
    /// Each bounce's total added light, in order: the
    /// <c>Bounce #%i added RGB(%.0f, %.0f, %.0f)</c> lines.
    /// </returns>
    /// <remarks>
    /// The loop stops after <c>numbounce</c> bounces or after the first bounce
    /// that adds less than 1 in all three channels (<c>:1717</c>, double
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
    /// The first step of <c>BounceLight</c> (<c>vrad.cpp:1661-1668</c>): every
    /// patch's direct light becomes its first emission and is zeroed in its
    /// total, "to integrate bounces only".
    /// </summary>
    public void MoveDirectLightToEmission()
    {
        Span<Patch> patches = _context.Patches.AsSpan();
        for (int i = 0; i < patches.Length; i++)
        {
            _emit[i] = patches[i].TotalLight.Flat;
            patches[i].TotalLight.Flat = Vec3.Zero;
        }
    }

    /// <summary>
    /// <c>GatherLight</c> for one patch (<c>vrad.cpp:1535</c>): the light it
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
    /// factored into transfer steradian", <c>:1604</c>), then re-applies the
    /// cosine against each of its four normals -- the flat normal and the three
    /// bump basis vectors -- skipping any the source is behind. The flat slot
    /// uses <c>patch-&gt;normal</c>, not the phong normal the basis was built
    /// from (stock's own "FIXME: why does the patch not use the phong
    /// normal?", <c>:1590</c>).
    /// </para>
    /// </remarks>
    public void GatherLight(int j)
    {
        PatchSet patches = _context.Patches;
        ref Patch patch = ref patches.At(j);
        ReadOnlySpan<Transfer> trans = _transfers.For(j);

        if (patch.NeedsBumpmap)
        {
            Vec3 sum0 = Vec3.Zero, sum1 = Vec3.Zero, sum2 = Vec3.Zero, sum3 = Vec3.Zero;
            if (trans.Length > 0)
            {
                ReadOnlySpan<Vec3> normals = _normals.AsSpan(_normalBase[j], BumpBasis.Count + 1);
                bool stock = _context.Settings.StockNormalise;
                for (int k = 0; k < trans.Length; k++)
                {
                    int source = trans[k].Patch;
                    ref Patch patch2 = ref patches.At(source);

                    (Vec3 delta, _) = FormFactors.Normalise(patch2.Origin - patch.Origin, stock);
                    Vec3 e = _emit[source];
                    Vec3 v = new(e.X * patch2.Reflectivity.X, e.Y * patch2.Reflectivity.Y, e.Z * patch2.Reflectivity.Z);

                    float scale = 1.0f / Vec3.Dot(delta, patch.Normal);
                    v *= trans[k].Weight * scale;

                    Accumulate(ref sum0, v, delta, normals[0]);
                    Accumulate(ref sum1, v, delta, normals[1]);
                    Accumulate(ref sum2, v, delta, normals[2]);
                    Accumulate(ref sum3, v, delta, normals[3]);
                }
            }

            _add[j] = new BumpLights { Flat = sum0, Bump1 = sum1, Bump2 = sum2, Bump3 = sum3 };
            return;
        }

        Vec3 sum = Vec3.Zero;
        for (int k = 0; k < trans.Length; k++)
        {
            int source = trans[k].Patch;
            Vec3 e = _emit[source];
            Vec3 r = patches.At(source).Reflectivity;
            Vec3 v = new(e.X * r.X, e.Y * r.Y, e.Z * r.Z);
            v *= trans[k].Weight;
            sum += v;
        }

        _add[j] = new BumpLights { Flat = sum };
    }

    /// <summary>
    /// <c>CollectLight</c> (<c>vrad.cpp:1413</c>): add what every leaf patch
    /// received to its total, pull it up the patch tree, make it next
    /// bounce's emission, and clear <see cref="AddLight"/>.
    /// </summary>
    /// <returns>The sum of every leaf patch's new emission: the bounce's total.</returns>
    /// <remarks>
    /// Reverse patch order, so that children -- always created after their
    /// parents -- are done first. A sky patch emits nothing. A parent's light
    /// is its children's, weighted by area; stock's
    /// <c>(int)patch-&gt;area != (int)(child1-&gt;area + child2-&gt;area)</c>
    /// test (<c>:1450</c>) assigns a variable that is overwritten on the next
    /// line, so it has no effect and is not ported.
    /// </remarks>
    public Vec3 CollectLight()
    {
        Span<Patch> patches = _context.Patches.AsSpan();
        Vec3 total = Vec3.Zero;

        for (int i = patches.Length - 1; i >= 0; i--)
        {
            ref Patch patch = ref patches[i];
            int normalCount = patch.NeedsBumpmap ? BumpBasis.Count + 1 : 1;

            if (patch.Sky)
            {
                _emit[i] = Vec3.Zero;
            }
            else if (patch.Child1 == Patch.Invalid)
            {
                for (int j = 0; j < normalCount; j++)
                {
                    patch.TotalLight[j] = patch.TotalLight[j] + _add[i][j];
                }

                _emit[i] = _add[i].Flat;
                total += _emit[i];
            }
            else
            {
                ref Patch child1 = ref patches[patch.Child1];
                ref Patch child2 = ref patches[patch.Child2];
                float s1 = child1.Area / (child1.Area + child2.Area);
                float s2 = child2.Area / (child1.Area + child2.Area);

                for (int j = 0; j < normalCount; j++)
                {
                    // VectorScale then VectorMA: c1 * s1, then + s2 * c2.
                    Vec3 light = child1.TotalLight[j] * s1;
                    patch.TotalLight[j] = light + (child2.TotalLight[j] * s2);
                }

                _emit[i] = (_emit[patch.Child1] * s1) + (_emit[patch.Child2] * s2);
            }

            _add[i] = default;
        }

        return total;
    }

    /// <summary>
    /// The four normals <c>GatherLight</c> builds for a bumped patch
    /// (<c>vrad.cpp:1558-1587</c>).
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
            // :1560-1569. A displacement's basis comes from its texture axes
            // mapped into lightmap space.
            Vec3 normal = patch.Normal;
            (Vec3 u, Vec3 v) = PreGetBumpNormalsForDisp(in tex, ref normal, stock);
            normals[0] = normal;
            BumpBasis.Build(u, v, normals[0], normals[0], normals[1..], stock);
        }
        else
        {
            // :1571-1578. The phong normal at the patch origin, as
            // CreateChildPatch computes it: in offset space.
            Vec3 phong = PhongNormals.Compute(
                geometry, _context.Neighbours, _context.Patches.Centroids,
                patch.FaceNumber, patch.Origin, _context.Settings.SmoothingThreshold);
            (Vec3 s, Vec3 t) = FaceLightJob.TextureAxes(in tex);
            BumpBasis.Build(s, t, patch.Normal, phong, normals[1..], stock);
        }

        // :1582. "force the base lightmap to use the flat normal".
        normals[0] = patch.Normal;
    }

    /// <summary>
    /// <c>PreGetBumpNormalsForDisp</c> (<c>vrad.cpp:1494</c>): a displacement's
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
    /// (<c>mathlib_base.cpp:658</c>), so each element is
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

    private static void Accumulate(ref Vec3 sum, Vec3 v, Vec3 delta, Vec3 normal)
    {
        float dot = Vec3.Dot(delta, normal);
        if (dot <= 0)
        {
            return;
        }

        sum += v * dot;
    }
}
