//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Bounce;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// The bounce half of <c>RadWorld_Go</c>: lane 4d's
/// additions to the hand-off object, kept in their own file.
/// </summary>
/// <remarks>
/// <para>
/// <b>What 4f reads after <see cref="BounceAsync"/>.</b> The bounced light is
/// in the PATCHES, not in the facelights, exactly as stock leaves it for
/// <c>FinalLightFace</c>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Patch.TotalLight"/> of every patch holds the BOUNCED light only
/// -- the direct light was moved out before the first bounce
/// <c>Flat</c> for every patch; <c>Bump1..3</c>
/// too on a <c>SURF_BUMPLIGHT</c> face. It is in the same units as the
/// facelight values (pre-encode linear light, style 0).
/// </description></item>
/// <item><description>
/// The reference implementation's <c>BuildPatchRadial</c> reads it for every LEAF patch
/// (<see cref="Patch.Child1"/> == -1) of the face and its neighbours, at
/// <see cref="Patch.Origin"/> (or the winding centre on a displacement), and
/// blends it into style 0 only. <see cref="Patch.DirectLight"/> is not
/// changed by the bounce.
/// </description></item>
/// <item><description>
/// With <see cref="DirectLightingSettings.Bounces"/> 0 (<c>-bounce 0</c>, or a
/// map with no vis) nothing here runs, <see cref="IsBounced"/> stays false,
/// and <c>FinalLightFace</c> adds no patch light at all
/// (<c>numbounce &gt; 0 &amp;&amp; k == 0</c>).
/// </description></item>
/// <item><description>
/// Static-prop lighting (4g) reads the same product: with
/// <c>numbounce &gt;= 1</c> its indirect term traces into the lit world
/// </description></item>
/// </list>
/// <para>
/// <see cref="Transfers"/> and <see cref="BounceEnergies"/> are kept for the
/// gates and the log lines; nothing after the bounce needs them.
/// </para>
/// </remarks>
public sealed partial class RadWorld
{
    /// <summary>
    /// The transfers, after <see cref="BounceAsync"/>; null before, with no
    /// bounce, and once the compile has let them go (<see cref="ReleaseTransfers"/>).
    /// </summary>
    public TransferSet? Transfers { get; private set; }

    /// <summary>
    /// Lets go of <see cref="Transfers"/>: nothing after the bounce reads
    /// them except the next pass of <c>-both</c> (<see cref="ShareTransfers"/>),
    /// so the driver calls this on a compile's last pass only. The counts in
    /// <see cref="VisMatrixStatistics"/> and the bounce's energies are kept.
    /// </summary>
    internal void ReleaseTransfers() => Transfers = null;

    /// <summary>
    /// Each bounce's added light, in order: stock's
    /// <c>Bounce #%i added RGB(%.0f, %.0f, %.0f)</c> lines.
    /// </summary>
    public IReadOnlyList<Vec3> BounceEnergies { get; private set; } = [];

    /// <summary>Counts from the transfer build, after <see cref="BounceAsync"/>.</summary>
    public VisMatrixStatistics? VisMatrixStatistics { get; private set; }

    /// <summary>
    /// True once <see cref="BounceAsync"/> has run the bounce, so that
    /// <see cref="Patch.TotalLight"/> holds bounced light only.
    /// </summary>
    public bool IsBounced { get; private set; }

    /// <summary>
    /// Transfers another pass built over the same patch tree (<c>-both</c>,
    /// plan 4p): <see cref="BounceAsync"/> uses them instead of building its
    /// own when the tree digest, the patch count and the switches the
    /// transfer build reads all match; otherwise it builds as usual.
    /// </summary>
    internal SharedTransfers? ReuseTransfers { get; set; }

    /// <summary>
    /// The cross-compile transfer cache, or null. Used only together with
    /// <see cref="TransferTracerDigest"/>: without a digest of what the
    /// tracer traces against, no key can be formed and nothing is cached.
    /// </summary>
    internal ITransferCache? TransferCache { get; set; }

    /// <summary>
    /// A digest of the tracer's scene (the shadow casters and which tracer
    /// traces them), or null when the tracer came from the host and its
    /// scene is unknown.
    /// </summary>
    internal string? TransferTracerDigest { get; set; }

    /// <summary>True when <see cref="BounceAsync"/> replayed its transfers from <see cref="TransferCache"/>.</summary>
    internal bool TransfersWereCached { get; private set; }

    /// <summary>
    /// The transfer cache key: everything the transfer build reads. The
    /// patch tree (<see cref="WalkPatchTree"/>), the faces' sky and
    /// displacement flags, the leaves' clusters and face lists, every
    /// cluster's visibility row, the switches, and the tracer's scene.
    /// No light, so a light edit keeps the key.
    /// </summary>
    /// <param name="tracerDigest">The tracer scene's digest.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    internal string TransferKey(string tracerDigest)
    {
        using System.Security.Cryptography.IncrementalHash sha =
            System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        // Values are batched: one hash call per 64 KB, not per int.
        byte[] buffer = new byte[1 << 16];
        int used = 0;
        void Flush()
        {
            sha.AppendData(buffer.AsSpan(0, used));
            used = 0;
        }

        void Int(int v)
        {
            if (used + 4 > buffer.Length)
            {
                Flush();
            }

            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(used), v);
            used += 4;
        }

        void Str(string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            Int(bytes.Length);
            Flush();
            sha.AppendData(bytes);
        }

        Str("vrad.transfers/1");
        Str(tracerDigest);
        Str(Compile.Cache.OptionsDigest.Of(Settings.Compliance));
        Int(Settings.StockNormalise ? 1 : 0);

        WalkPatchTree(Int);

        Int(Geometry.Faces.Length);
        for (int f = 0; f < Geometry.Faces.Length; f++)
        {
            Int(Geometry.Faces[f].DispInfo == -1 ? 0 : 1);
            Int(IsSkyFace(f) ? 1 : 0);
        }

        Int(Geometry.Leaves.Length);
        foreach (LeafInfo leaf in Geometry.Leaves)
        {
            Int(leaf.Cluster);
            Int(leaf.FirstLeafFace);
            Int(leaf.NumLeafFaces);
        }

        Int(Geometry.LeafFaces.Length);
        foreach (ushort face in Geometry.LeafFaces)
        {
            Int(face);
        }

        int clusters = Visibility.ClusterCount;
        Int(clusters);
        Int(Visibility.RowBytes);
        byte[] row = new byte[Math.Max(Visibility.RowBytes, 1)];
        for (int c = 0; c < clusters; c++)
        {
            Array.Clear(row);
            Visibility.GetVisCache(c, row);
            Flush();
            sha.AppendData(row);
        }

        Flush();
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    // IsSky indexes the texinfo table; a face whose texinfo is out of range is folded as its own value.
    private bool IsSkyFace(int face)
    {
        int texInfo = Geometry.Faces[face].TexInfo;
        return (uint)texInfo < (uint)Geometry.TexInfos.Length && Geometry.IsSky(face);
    }

    /// <summary>True when <see cref="BounceAsync"/> used <see cref="ReuseTransfers"/>.</summary>
    internal bool TransfersWereShared { get; private set; }

    /// <summary>This pass's transfers, for the next pass over the same map.</summary>
    /// <returns>The transfers and what they were built from; null before the bounce.</returns>
    internal SharedTransfers? ShareTransfers() =>
        Transfers is null
            ? null
            : new SharedTransfers(Transfers, VisMatrixStatistics, PatchTreeDigest(), Settings.StockNormalise, Settings.Compliance);

    /// <summary>
    /// A digest of everything about the patch tree that is geometry: every
    /// patch's winding, bounds, plane, origin, normal, area, sky flag, chop,
    /// scales, face, cluster and links, and the face, parent and cluster
    /// lists -- everything but its light, which is the only thing the two
    /// passes of <c>-both</c> are expected to differ in.
    /// </summary>
    /// <returns>A 64-bit FNV-1a digest.</returns>
    internal ulong PatchTreeDigest()
    {
        ulong h = 14695981039346656037UL;
        WalkPatchTree(v =>
        {
            for (int i = 0; i < 4; i++)
            {
                h = (h ^ (byte)(v >> (8 * i))) * 1099511628211UL;
            }
        });

        return h;
    }

    /// <summary>
    /// Feeds every geometry value of the patch tree, in a fixed order, to
    /// <paramref name="put"/>: the one walk both <see cref="PatchTreeDigest"/>
    /// and the transfer cache key read.
    /// </summary>
    /// <param name="put">Receives each value (floats as their bits).</param>
    internal void WalkPatchTree(Action<int> put)
    {
        void Int(int v) => put(v);
        void Float(float v) => Int(BitConverter.SingleToInt32Bits(v));
        void Vector(Vec3 v)
        {
            Float(v.X);
            Float(v.Y);
            Float(v.Z);
        }

        PatchSet patches = Patches;
        Int(patches.Count);
        for (int i = 0; i < patches.Count; i++)
        {
            ref Patch p = ref patches.At(i);
            foreach (Vec3 v in patches.Arena.Points(p.Winding))
            {
                Vector(v);
            }

            Vector(p.Mins);
            Vector(p.Maxs);
            Vector(p.FaceMins);
            Vector(p.FaceMaxs);
            Vector(p.Origin);
            Vector(p.PlaneNormal);
            Float(p.PlaneDist);
            Float(p.CachedPlaneDist);
            Vector(p.Normal);
            Int(p.Sky ? 1 : 0);
            Int(p.NeedsBumpmap ? 1 : 0);
            Float(p.Chop);
            Float(p.LuxScale);
            Float(p.ScaleS);
            Float(p.ScaleT);
            Float(p.Area);
            Float(p.BaseArea);
            Int(p.FaceNumber);
            Int(p.ClusterNumber);
            Int(p.Parent);
            Int(p.Child1);
            Int(p.Child2);
            Int(p.Next);
            Int(p.NextParent);
            Int(p.NextClusterChild);
            Int(p.Index0);
            Int(p.Index1);
            Int(p.Index2);
        }

        foreach (int[] list in (int[][])[patches.FacePatches, patches.FaceParents, patches.ClusterChildren])
        {
            Int(list.Length);
            foreach (int v in list)
            {
                Int(v);
            }
        }
    }

    /// <summary>The bounce's view of this world.</summary>
    /// <returns>A context over the same patches, geometry and settings.</returns>
    public BounceContext BounceContext() => new(Geometry, Neighbours, Patches, Visibility, Settings);

    /// <summary>
    /// <c>MakeAllScales</c> then <c>BounceLight</c>,
    /// when the settings ask for bounces.
    /// </summary>
    /// <param name="tracer">The tracer the transfer rays go to.</param>
    /// <param name="parallelism">How many workers.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>A task that completes when the bounce is done.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="LightFacesAsync"/> has not run, or this has already run: the
    /// bounce moves the direct light out of the patches, so a second run would
    /// bounce nothing.
    /// </exception>
    public async Task BounceAsync(
        IRayTracer tracer,
        CompileParallelism parallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tracer);
        ArgumentNullException.ThrowIfNull(parallelism);
        cancellationToken.ThrowIfCancellationRequested();

        if (Layout is null)
        {
            throw new InvalidOperationException(
                "BounceAsync needs the patches' direct light: run LightFacesAsync first");
        }

        if (IsBounced)
        {
            throw new InvalidOperationException("the bounce has already run on this world");
        }

        if (Settings.Bounces <= 0)
        {
            return;
        }

        using WorkQueue queue = new(parallelism);
        BounceContext context = BounceContext();

        TransferSet transfers;
        VisMatrixStatistics? statistics;
        if (ReuseTransfers is { } shared
            && shared.Transfers.PatchCount == Patches.Count
            && shared.StockNormalise == Settings.StockNormalise
            && Equals(shared.Compliance, Settings.Compliance)
            && shared.TreeDigest == PatchTreeDigest())
        {
            // -both (plan 4p): the other range built these over the same tree;
            // the transfer build reads geometry, vis and these switches only.
            transfers = shared.Transfers;
            statistics = shared.Statistics;
            TransfersWereShared = true;
        }
        else
        {
            // Across compiles: the key reads no light, so a light-only edit hits.
            string? key = TransferCache is not null && TransferTracerDigest is { } scene ? TransferKey(scene) : null;
            TransferSet? cached = key is null
                ? null
                : await TransferCache!.TryGetAsync(key, Patches.Count, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                transfers = cached;
                statistics = null;
                TransfersWereCached = true;
            }
            else
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                VisMatrix matrix = new(context) { ScratchPool = ScratchPool };
                transfers = await matrix.BuildAsync(tracer, queue, cancellationToken).ConfigureAwait(false);
                statistics = matrix.Statistics;
                if (key is not null)
                {
                    long costMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    await TransferCache!.StoreAsync(key, transfers, costMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        Radiosity radiosity = new(context, transfers);
        IReadOnlyList<Vec3> energies = await radiosity.BounceAsync(queue, cancellationToken).ConfigureAwait(false);

        Transfers = transfers;
        VisMatrixStatistics = statistics;
        BounceEnergies = energies;
        IsBounced = true;
    }
}

/// <summary>One pass's transfers and what they were built from, for the next pass of <c>-both</c>.</summary>
/// <param name="Transfers">The transfers.</param>
/// <param name="Statistics">The build's counts.</param>
/// <param name="TreeDigest"><see cref="RadWorld.PatchTreeDigest"/> of the tree they were built over.</param>
/// <param name="StockNormalise">The normalise switch the build read.</param>
/// <param name="Compliance">The compliance the build read.</param>
internal sealed record SharedTransfers(
    TransferSet Transfers,
    VisMatrixStatistics? Statistics,
    ulong TreeDigest,
    bool StockNormalise,
    Options.ComplianceOptions Compliance);
