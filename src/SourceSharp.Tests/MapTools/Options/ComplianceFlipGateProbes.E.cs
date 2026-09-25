using System.Collections.Immutable;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Cubemaps;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;

namespace SourceSharp.Tests.MapTools.Options;

/// <summary>
/// The gate's dedicated GAP-cell fixture. This is the one row §11a pays for
/// with a deliberate fixture build rather than a reused effect fact:
/// <see cref="StockQuirk.CubemapDistanceNormalise"/> decides the
/// <c>dot &gt;= 0</c> gate of <c>Cubemap_FindClosestCubemap</c>
/// (<c>utils/vbsp/cubemap.cpp:863</c>) between two cubemap samples whose
/// tilted sample deltas put that dot within the reciprocal-square-root
/// estimate's error of zero, so the winner changes with the policy.
/// </summary>
/// <remarks>
/// <para>
/// The matrix's one GAP cell said no existing cubemap fixture showed a
/// difference: correct, because every fixture's samples sit far from the
/// perpendicular. This probe searches none at run time — the straddling
/// geometry is computed offline and pinned here:
/// </para>
/// <list type="bullet">
/// <item>the side's plane normal is <c>(1,1,1)</c> (a slanted specular face —
/// the plane table's normalise rescales it, which leaves the sign, the only
/// thing the gate reads);</item>
/// <item>sample A at <c>(-3,4,-1)</c>, the nearer: its dot is
/// <c>+1.49e-8</c> on the exact path (admits, <c>dot &gt;= 0</c>) and
/// <c>-2.98e-8</c> on stock's estimate (rejects) — measured on this box;</item>
/// <item>sample B at <c>(6,5,5)</c>, the farther, whose dot is a clear
/// <c>+1.72</c> on both paths.</item>
/// </list>
/// <para>
/// So correct picks the nearer A and stock — A rejected, B the only admitted
/// sample — picks B: a different winner index, and because A was admitted the
/// fallback nearest-sample loop never runs under either policy. The side's
/// winding is four points symmetric about the origin so its centre is exactly
/// <c>(0,0,0)</c> (each <c>p + (-p)</c> cancels exactly in float); sample
/// origins are integers, which survive <see cref="CubemapFixups.SampleOrigin"/>'s
/// truncation untouched.
/// </para>
/// </remarks>
internal static partial class ComplianceFlipGateProbes
{
    /// <summary>The slanted specular plane's normal (<c>cubemap.cpp:863</c> reads only its sign).</summary>
    private static readonly Vec3 StraddleNormal = new(1f, 1f, 1f);

    /// <summary>The nearer sample: exact dot +1.49e-8 (admits), stock's estimate dot -2.98e-8 (rejects).</summary>
    private static readonly Vec3 StraddlingSample = new(-3f, 4f, -1f);

    /// <summary>The farther sample: admitted on both paths.</summary>
    private static readonly Vec3 AlwaysAdmittedSample = new(6f, 5f, 5f);

    private static partial Dictionary<StockQuirk, FlipGateProbe> ProbesE()
    {
        return new Dictionary<StockQuirk, FlipGateProbe>
        {
            [StockQuirk.CubemapDistanceNormalise] = new FlipGateProbe(
                StockQuirk.CubemapDistanceNormalise,
                "ComplianceFlipGateProbes.E: hand-built side on a (1,1,1) plane with samples (-3,4,-1) and (6,5,5) straddling the FindClosestCubemap dot>=0 gate",
                ["cubemap_findclosest_winner"],
                WholeStage: false)
            {
                Fingerprint = async policy =>
                {
                    int winner = await FindClosest(policy).ConfigureAwait(false);
                    return Fp.Join(Fp.Int(winner));
                },
            },
        };
    }

    /// <summary>
    /// The witness call: <see cref="CubemapFixups.FindClosestCubemap"/> over
    /// the two pinned samples, as a function of the policy.
    /// </summary>
    /// <param name="policy">The compliance policy to decide under.</param>
    /// <returns>The winning sample index (0 = the near straddler, 1 = the far admitted).</returns>
    private static async ValueTask<int> FindClosest(ComplianceOptions policy)
    {
        await using ContentFileSystem content = new([]);
        VbspContext context = new(new VbspOptions { Compliance = policy }, content);

        MapFile map = new(new WindingArena());
        int plane = map.Planes.Create(StraddleNormal, 0f);

        // Four points symmetric about the origin (w, -w, z, -z): the centre
        // sums to exactly (0,0,0), so the sample deltas are the sample origins.
        // u = (1,-1,0)/sqrt(2) and v = (1,1,-2)/sqrt(6) span the plane
        // x + y + z = 0; 64-unit square, a plausible specular face.
        const float U = 0.7071067811865476f * 64f;
        const float Vx = 0.4082482904638631f * 64f;
        const float Vz = -0.8164965809277261f * 64f;
        Vec3 w = new(U + Vx, -U + Vx, Vz);
        Vec3 z = new(Vx - U, Vx + U, Vz);
        MapBrushSide side = new()
        {
            PlaneNumber = plane,
            Winding = map.Windings.Create([w, -w, z, -z]),
        };
        // The side needs no brush: FindClosestCubemap reads only the winding,
        // the plane table, and the context's samples.
        map.AddBrushSide(side, new BrushTexture());

        context.CubemapSamples.Add(new CubemapSample(StraddlingSample, 8, "*"));
        context.CubemapSamples.Add(new CubemapSample(AlwaysAdmittedSample, 8, "*"));

        CubemapFixups fixups = new(context, map, new MaterialPatcher(content, new MapPakFile(), policy));
        return fixups.FindClosestCubemap(Vec3.Zero, side);
    }
}
