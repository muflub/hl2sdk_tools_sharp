//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Reflection;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Gpu;
using SourceSharp.MapTools.Gpu.Interop;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

using Xunit;

namespace SourceSharp.Tests.MapTools.Tracing;

/// <summary>
/// The §10c/§10d gates for <see cref="VulkanRayTracer"/>, measured against the
/// CPU <see cref="KdRayTracer"/> on the same triangles.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gating.</b> The device facts skip — with the probe's own words in the
/// skip reason, never silently — when no ray-query-capable Vulkan device
/// exists, and MUST run wherever one does; this box has radv and they run
/// here. The package-absent facts run everywhere: they are the standing
/// condition of the slnx grant and hold on GPU-less CI too.
/// </para>
/// <para>
/// <b>The contract</b>: any-hit hit-bit disagreement
/// between the GPU and the CPU KD tracer is 0 for rays aimed off-plane (the
/// spike proved 0/1,048,576 on radv with the spec-correct kernel); closest-hit
/// hit/miss agreement is exact, and where both hit, the surface ids and the
/// fractions match to well inside the 1e-3 self-intersection band — a
/// coplanar-face tie may move within the band and is counted, never hidden.
/// Rays are aimed at face interiors with a ≥5% margin off every edge, because
/// on-plane endpoints are the boundary every pair of float implementations
/// legitimately disagrees on (the spike's 19-vs-1,769-bytes lesson).
/// </para>
/// </remarks>
public sealed class VulkanRayTracerFacts
{
    // ------------------------------------------------------------------
    // Parity: GPU vs CPU KD tracer
    // ------------------------------------------------------------------

    /// <summary>
    /// Gate (a) on the corpus tier's real geometry: brush and displacement
    /// shadow casters from <see cref="ShadowCasterLoader"/> (props on the
    /// AABB branch), rays fired at face interiors so every ray's answer is
    /// known-shape, GPU bits/hits against the CPU KD tracer's.
    /// </summary>
    [InlineData("l1_sealed_room")]
    [InlineData("l1_open_arena")]
    [InlineData("l1_two_rooms_and_a_door")]
    [InlineData("l3_arena_144_pillars")]
    [CorpusGpuTheory]
    public async Task ClosestHitAndAnyHitMatchCpuTracerOnCorpusMaps(string map)
    {
        string dir = Environment.GetEnvironmentVariable("VVIS_STOCK_DIR") ?? "";
        string bspPath = Path.Combine(dir, map + ".bsp");
        Assert.True(File.Exists(bspPath), $"{map}.bsp vanished from VVIS_STOCK_DIR");

        await using FileStream stream = File.OpenRead(bspPath);
        BspData bsp = await BspFile.LoadAsync(stream, CancellationToken.None);
        ShadowCasterLoadReport loaded = await ShadowCasterLoader.LoadAsync(
            bsp,
            VradOptions.Default,
            new ContentFileSystem([]),
            NullPropCollisionSource.Instance);

        TracedTriangle[] tris = [.. loaded.Set.Triangles];
        // These corpus maps are small brush worlds (72..1800 casters; props
        // mount adds none — measured). Thin geometry is the tier: the gate is
        // that every ray's answer is oracle-arbitrated, not triangle bulk.
        Assert.True(tris.Length > 50,
            $"{map}: {tris.Length} cast triangles is too thin to prove parity at all");

        Ray[] rays = FaceInteriorRays(tris, rays: 65_536, seed: unchecked((int)0xC0FFEE1));
        RunParity(tris, rays, map);
    }

    /// <summary>
    /// Gate (a) on the spike's lattice — the synthetic scene whose answer is
    /// known by construction, kept as the no-corpus arm of the same contract.
    /// </summary>
    [HwGpuFact]
    public void ClosestHitAndAnyHitMatchCpuTracerOnLattice()
    {
        (TracedTriangle[] tris, Ray[] rays) = Lattice(rays: 65_536, planes: 16, seed: 7);
        RunParity(tris, rays, "lattice");
    }

    /// <summary>
    /// I4's GPU arm: each dispatch's output is a pure function of its rays,
    /// so the same rays answered as one wide slab and as 64-ray slabs give
    /// byte-identical words and identical ids — which is what lets any thread
    /// split of the same rays (the vrad feed, 1 thread vs 32) land identically.
    /// </summary>
    [HwGpuFact]
    public void SlabSizeDoesNotChangeAnyAnswer()
    {
        (TracedTriangle[] tris, Ray[] rays) = Lattice(rays: 131_072, planes: 16, seed: 11);

        using VulkanRayTracer wide = RequireHardware(tris, MaxRaysPerSlab: null);
        using VulkanRayTracer narrow = RequireHardware(tris, MaxRaysPerSlab: 64);
        Assert.True(wide.MaxRaysPerSlab > 4096,
            $"the wide arm got {wide.MaxRaysPerSlab}-ray slabs; the box cannot answer the wide case");
        Assert.True(narrow.MaxRaysPerSlab <= 128,
            $"the narrow arm ignored its cap (MaxRaysPerSlab={narrow.MaxRaysPerSlab})");

        // Both arms pipeline their slabs: the default keeps three in flight.
        Assert.Equal(3, wide.SlabsInFlight);
        Assert.Equal(3, narrow.SlabsInFlight);

        int words = (rays.Length + 63) / 64;
        ulong[] bitsA = new ulong[words];
        ulong[] bitsB = new ulong[words];
        HitId[] hitsA = new HitId[rays.Length];
        HitId[] hitsB = new HitId[rays.Length];

        wide.TraceVisibilityAsync(rays, bitsA, RayTraceOptions.SelfIntersectionSafe)
            .AsTask().GetAwaiter().GetResult();
        narrow.TraceVisibilityAsync(rays, bitsB, RayTraceOptions.SelfIntersectionSafe)
            .AsTask().GetAwaiter().GetResult();
        wide.TraceClosestAsync(rays, hitsA, RayTraceOptions.SelfIntersectionSafe)
            .AsTask().GetAwaiter().GetResult();
        narrow.TraceClosestAsync(rays, hitsB, RayTraceOptions.SelfIntersectionSafe)
            .AsTask().GetAwaiter().GetResult();

        Assert.Equal(bitsA, bitsB);
        Assert.Equal(hitsA, hitsB);
        // Not vacuously green: the rays really were answered.
        Assert.Contains(bitsA, w => w != 0);
        Assert.Contains(hitsA, h => h.Surface >= 0);
    }

    /// <summary>
    /// Split-call determinism: the same rays traced whole, then traced as 32
    /// independently-scheduled chunk batches through the one instance, give
    /// identical results — the property the parallel vrad feed leans on.
    /// </summary>
    [HwGpuFact]
    public void ChunkedConcurrentCallsMatchWholeBatch()
    {
        (TracedTriangle[] tris, Ray[] rays) = Lattice(rays: 98_304, planes: 16, seed: 13);
        using VulkanRayTracer gpu = RequireHardware(tris, MaxRaysPerSlab: null);

        int words = (rays.Length + 63) / 64;
        ulong[] wholeBits = new ulong[words];
        ulong[] chunkBits = new ulong[words];
        HitId[] wholeHits = new HitId[rays.Length];
        HitId[] chunkHits = new HitId[rays.Length];

        gpu.TraceVisibilityAsync(rays, wholeBits, RayTraceOptions.SelfIntersectionSafe)
            .AsTask().GetAwaiter().GetResult();
        gpu.TraceClosestAsync(rays, wholeHits, RayTraceOptions.SelfIntersectionSafe)
            .AsTask().GetAwaiter().GetResult();

        System.Threading.Tasks.Parallel.For(0, 32, chunk =>
        {
            int lo = chunk * rays.Length / 32;
            int hi = (chunk + 1) * rays.Length / 32;
            Ray[] sub = rays[lo..hi];
            ulong[] subBits = new ulong[(sub.Length + 63) / 64];
            HitId[] subHits = new HitId[sub.Length];
            gpu.TraceVisibilityAsync(sub, subBits, RayTraceOptions.SelfIntersectionSafe)
                .AsTask().GetAwaiter().GetResult();
            gpu.TraceClosestAsync(sub, subHits, RayTraceOptions.SelfIntersectionSafe)
                .AsTask().GetAwaiter().GetResult();
            for (int i = 0; i < sub.Length; i++)
            {
                if ((subBits[i >> 6] & (1UL << (i & 63))) != 0)
                {
                    chunkBits[(lo + i) >> 6] |= 1UL << ((lo + i) & 63);
                }

                chunkHits[lo + i] = subHits[i];
            }
        });

        Assert.Equal(wholeBits, chunkBits);
        Assert.Equal(wholeHits, chunkHits);
    }

    // ------------------------------------------------------------------
    // Capability self-test: accept radv, reject the two broken arms
    // ------------------------------------------------------------------

    /// <summary>
    /// §10d's gate, good side: the hardware device passes the known-hit
    /// self-test, and its name is quoted so the log says what traced.
    /// </summary>
    [HwGpuFact]
    public void SelfTestAcceptsRadv()
    {
        VulkanTracerAttempt a = VulkanRayTracer.TryCreate(
            TwoTriangles(), new VulkanRayTracerOptions(DeviceMatch: "radv"));
        Assert.True(a.Success, "radv pin rejected: " + (a.Report.Selected?.Reason ?? a.Report.Failure));
        using VulkanRayTracer t = a.Tracer!;
        SelfTestRecord rec = t.SelfTest;
        Assert.True(rec.Passed);
        Assert.True(rec.ReadbackOk, "known-answer write/readback leg failed");
        Assert.True(rec.AnyHitOk, "any-hit leg failed");
        Assert.True(rec.ClosestOk, "closest-hit leg failed");
        // Telemetry is attribution-only: radv's rayQueryProceed runs the
        // whole traversal inside its first call and returns false (the spec
        // allows a driver to commit-everything-and-stop), so a healthy radv
        // reports iters=0 here while modes 0/1 answer correctly. The real
        // gate is the known-answer hit above; the llvmpipe fact is where the
        // candidate->committed signature is proven to fire.
        Assert.Contains("RADV", t.DeviceName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(rec.DeviceName, t.DeviceName);
    }

    /// <summary>
    /// §10d's gate, Mesa side: the llvmpipe pin MUST be rejected by the
    /// shipped self-test on a box that exposes it, with the root-caused
    /// candidate→committed signature named in the reason.
    /// Skips only where no such device exists; this box has one, so this is
    /// the shipped-code proof the gate closes.
    /// </summary>
    [HwGpuFact(DeviceMatch = "llvmpipe")]
    public void SelfTestRejectsLlvmpeWithMesaSignature()
    {
        VulkanTracerAttempt a = VulkanRayTracer.TryCreate(
            TwoTriangles(), new VulkanRayTracerOptions(DeviceMatch: "llvmpipe"));
        Assert.False(a.Success,
            "llvmpipe passed the self-test — the Mesa candidate->committed bug is gone or the "
            + "gate regressed; both are news that must change this fact");
        Assert.Null(a.Tracer);
        SelfTestRecord rec = a.Report.Selected
            ?? throw new Xunit.Sdk.XunitException(
                $"llvmpipe never opened: failure={a.Report.Failure}");
        Assert.False(rec.Passed);
        Assert.NotNull(rec.Reason);
        // The signature, not just "failed": the readback path worked, the
        // traversal reached candidates, committing is what is broken.
        Assert.True(rec.ReadbackOk, "the readback leg itself failed — a different bug than the Mesa one");
        Assert.True(rec.Candidates > 0,
            $"expected candidates>0 with committed==0 (telemetry iters={rec.Iters}, "
            + $"candidates={rec.Candidates}): {rec.Reason}");
    }

    /// <summary>
    /// §10d's gate, nvidia side: a device whose compute queue never traverses
    /// (the in-box RTX 2070's signature: zero proceed iterations) must be
    /// rejected, not hang. Skips where the inventory names no nvidia device.
    /// this box's loader exposes AMD ICDs only, so it skips here with the
    /// inventory quoted, and the Mesa fact is the arm that runs.
    /// </summary>
    [HwGpuFact(DeviceMatch = "NVIDIA")]
    public void SelfTestRejectsNonTraversingDevice()
    {
        VulkanTracerAttempt a = VulkanRayTracer.TryCreate(
            TwoTriangles(), new VulkanRayTracerOptions(DeviceMatch: "NVIDIA"));
        Assert.False(a.Success, "a never-traversing device passed the gate");
        Assert.Null(a.Tracer);
        SelfTestRecord rec = a.Report.Selected
            ?? throw new Xunit.Sdk.XunitException(
                "a never-traversing device was never opened: failure=" + a.Report.Failure);
        Assert.False(rec.Passed);
        Assert.NotNull(rec.Reason);
    }

    /// <summary>
    /// The device-pin diagnostic: a match nobody has fails with the
    /// inventory named, and never a crash, never a bare null. Runs on every
    /// machine — on a loader-less box the Failure message is the report.
    /// </summary>
    [Fact]
    public void UnmatchedPinFailsCleanlyWithAReason()
    {
        VulkanTracerAttempt a = VulkanRayTracer.TryCreate(
            Array.Empty<TracedTriangle>(), new VulkanRayTracerOptions(DeviceMatch: "no-such-device-xyz"));
        Assert.False(a.Success);
        Assert.Null(a.Tracer);
        string? why = a.Report.Failure ?? a.Report.Selected?.Reason;
        Assert.NotNull(why);
        Assert.True(
            a.Report.Devices.Count > 0 || a.Report.Failure is not null,
            "a pin miss reported neither the inventory nor a loader failure: " + why);
    }

    /// <summary>Construction honours a pre-cancelled token before opening anything.</summary>
    [Fact]
    public async Task TryCreateHonoursPreCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => VulkanRayTracer.TryCreateAsync(
                Array.Empty<TracedTriangle>(), new VulkanRayTracerOptions(), cts.Token));
    }

    // ------------------------------------------------------------------
    // Package absence: the standing condition of the slnx grant
    // ------------------------------------------------------------------

    /// <summary>
    /// The core map-tools assemblies reference neither Silk.NET nor
    /// SourceSharp.MapTools.Gpu: the GPU path is opt-in and lives only in the
    /// Gpu assembly, so the core builds — and this suite's core half runs.
    /// with the package absent (the SQLite store's posture, mirrored).
    /// </summary>
    [Fact]
    public void CoreAssembliesReferenceNeitherGpuNorSilkNet()
    {
        Assembly[] core =
        [
            typeof(BspData).Assembly,        // SourceSharp.MapFormats
            typeof(Ray).Assembly,            // SourceSharp.MapTools (tracing seam + everything vrad)
        ];

        foreach (Assembly a in core)
        {
            string[] refs = [.. a.GetReferencedAssemblies()
                .Select(n => n.Name ?? "?")
                .Where(n => n.Contains("Silk", StringComparison.OrdinalIgnoreCase)
                         || n.Contains("MapTools.Gpu", StringComparison.OrdinalIgnoreCase))];
            Assert.True(refs.Length == 0,
                $"{a.GetName().Name} must reference neither Silk.NET nor the Gpu assembly; "
                + "found: " + string.Join(", ", refs));
        }
    }

    /// <summary>
    /// The seam is package-free and the GPU tracer satisfies it from the
    /// other side: nothing in the device vocabulary leaked into
    /// <see cref="IRayTracer"/> itself.
    /// </summary>
    [Fact]
    public void SeamHasNoDeviceVocabulary()
    {
        Assert.True(typeof(IRayTracer).IsAssignableFrom(typeof(KdRayTracer)));
        Assert.True(typeof(IRayTracer).IsAssignableFrom(typeof(VulkanRayTracer)));
        Assert.DoesNotContain(
            typeof(IRayTracer).GetProperties(),
            p => p.Name.Contains("Device", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The empty batch is a no-op on the GPU path too — vrad's leaf loops
    /// legitimately hand empty batches, and the seam's contract is that they
    /// complete rather than throw.
    /// </summary>
    [HwGpuFact]
    public void EmptyBatchCompletes()
    {
        using VulkanRayTracer t = RequireHardware(Array.Empty<TracedTriangle>(), MaxRaysPerSlab: null);
        t.TraceVisibilityAsync(Array.Empty<Ray>(), default, RayTraceOptions.SelfIntersectionSafe)
            .AsTask().GetAwaiter().GetResult();
        t.TraceClosestAsync(Array.Empty<Ray>(), default, RayTraceOptions.SelfIntersectionSafe)
            .AsTask().GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------
    // Pipelined slabs: slots and in-place buffers change no word
    // ------------------------------------------------------------------

    /// <summary>
    /// The raw kernel words for the lattice rays, through every kernel mode,
    /// are identical whether a device has one slot with both copies (the
    /// shape every slab had before slabs were pipelined), several slots all
    /// in flight at once, or rays read and answers written in place. Runs on
    /// any ray-query device, llvmpipe included: it drives the device below
    /// the self-test gate, and the telemetry mode's per-ray traversal counts
    /// make the comparison meaningful even on a device whose committed hits
    /// are broken.
    /// </summary>
    [HwGpuFact]
    public void SlotsAndInPlaceBuffersGiveTheWordsOfOneStagedSlot()
    {
        (TracedTriangle[] tris, Ray[] rays) = Lattice(rays: 4096, planes: 16, seed: 17);

        // Every third ray turned away from the lattice: its telemetry reads
        // no traversal where its neighbours' reads some, so the words depend
        // on which ray sits in which lane even on llvmpipe, whose telemetry
        // is otherwise the same for every ray that reaches the scene.
        for (int i = 0; i < rays.Length; i += 3)
        {
            Ray r = rays[i];
            rays[i] = new Ray(r.OriginX, r.OriginY, r.OriginZ, -r.DirectionX, r.DirectionY, r.DirectionZ, r.MaxDistance);
        }

        uint[][] reference = RawWords(tris, rays, slots: 1, forceStaged: true, out SlabMemoryLayout layout);
        Assert.Equal(new SlabMemoryLayout(false, false), layout);
        foreach ((int slots, bool staged) in new[] { (3, true), (3, false), (8, false) })
        {
            uint[][] words = RawWords(tris, rays, slots, staged, out _);
            for (int m = 0; m < RawModes.Length; m++)
            {
                Assert.True(reference[m].AsSpan().SequenceEqual(words[m]),
                    $"mode {RawModes[m]} differs with {slots} slots (staged: {staged})");
            }
        }

        // Not vacuous: mode 4 wrote its all-ones, and the telemetry tells
        // the turned rays from the rest.
        Assert.All(reference[0], w => Assert.Equal(0xFFFFFFFFu, w));
        Assert.Contains(reference[1], w => w != 0);
        Assert.Contains(reference[1], w => w == 0);
    }

    /// <summary>
    /// End to end through <see cref="SlabBatcher"/>: many concurrent callers
    /// on a three-slot device with small slabs (so dozens of slabs go out,
    /// several at a time) get the same bits and hits as on a one-slot
    /// device with both copies. Runs on any ray-query device; on llvmpipe,
    /// which commits no hit, both sides are all-miss and the fact proves
    /// only the plumbing, so the raw-words fact above, whose telemetry mode
    /// does vary per ray there, is the one that carries the answers.
    /// </summary>
    [HwGpuFact]
    public async Task BatcherOverSeveralSlotsMatchesOneSlot()
    {
        (TracedTriangle[] tris, Ray[] rays) = Lattice(rays: 16_384, planes: 16, seed: 19);
        (ulong[] Bits, HitId[] Hits) one = await TraceThroughBatcher(tris, rays, slots: 1, forceStaged: true);
        (ulong[] Bits, HitId[] Hits) three = await TraceThroughBatcher(tris, rays, slots: 3, forceStaged: false);

        Assert.Equal(one.Bits, three.Bits);
        Assert.Equal(one.Hits, three.Hits);
    }

    private static readonly int[] RawModes = [4, 5, 0, 1];

    private static float[] Vertices(TracedTriangle[] tris)
    {
        float[] v = new float[tris.Length * 9];
        for (int i = 0; i < tris.Length; i++)
        {
            Vec3[] corners = [tris[i].V0, tris[i].V1, tris[i].V2];
            for (int c = 0; c < 3; c++)
            {
                v[(i * 9) + (c * 3)] = corners[c].X;
                v[(i * 9) + (c * 3) + 1] = corners[c].Y;
                v[(i * 9) + (c * 3) + 2] = corners[c].Z;
            }
        }

        return v;
    }

    private static VulkanDevice OpenRaw(TracedTriangle[] tris, int slots, bool forceStaged)
    {
        // 512 rays a slot: the lattice batches cross many slabs.
        VulkanDevice device = new();
        try
        {
            device.Construct(null, -1, 512 * slots, slots, forceStaged);
            device.LoadScene(Vertices(tris));
            Assert.Equal(512, device.MaxSlabRays);
            Assert.Equal(slots, device.SlotCount);
            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>Every raw mode over the rays, a wave of slots in flight at a time.</summary>
    private static uint[][] RawWords(TracedTriangle[] tris, Ray[] rays, int slots, bool forceStaged, out SlabMemoryLayout layout)
    {
        using VulkanDevice device = OpenRaw(tris, slots, forceStaged);
        layout = device.SlabLayout;
        int slab = device.MaxSlabRays;
        uint[][] result = new uint[RawModes.Length][];
        for (int m = 0; m < RawModes.Length; m++)
        {
            int mode = RawModes[m];
            bool perWorkgroup = mode is 0 or 4;
            int WordsFor(int n) => perWorkgroup ? (n + 63) / 64 * 2 : n * 2;
            uint[] words = new uint[WordsFor(rays.Length)];
            for (int wave = 0; wave < rays.Length; wave += slab * slots)
            {
                List<(int Slot, int Start, int Count)> launched = [];
                for (int s = 0; s < slots && wave + (s * slab) < rays.Length; s++)
                {
                    int start = wave + (s * slab);
                    int count = Math.Min(slab, rays.Length - start);
                    Span<float> staging = device.StageRays(s, count);
                    for (int i = 0; i < count; i++)
                    {
                        Ray r = rays[start + i];
                        staging[i * 8] = r.OriginX;
                        staging[(i * 8) + 1] = r.OriginY;
                        staging[(i * 8) + 2] = r.OriginZ;
                        staging[(i * 8) + 3] = 0f;
                        staging[(i * 8) + 4] = r.DirectionX;
                        staging[(i * 8) + 5] = r.DirectionY;
                        staging[(i * 8) + 6] = r.DirectionZ;
                        staging[(i * 8) + 7] = r.MaxDistance;
                    }

                    device.Submit(s, mode, count, WordsFor(count), SelfIntersectionTminBits, VulkanDevice.TmaxScaleBits);
                    launched.Add((s, start, count));
                }

                // Every slot of the wave is on the device before the first is waited for.
                foreach ((int s, int start, int count) in launched)
                {
                    device.Complete(s, words.AsSpan(WordsFor(start), WordsFor(count)));
                }
            }

            result[m] = words;
        }

        return result;
    }

    private const uint SelfIntersectionTminBits = 0x3A83126Fu; // 1e-3f

    private static async Task<(ulong[] Bits, HitId[] Hits)> TraceThroughBatcher(
        TracedTriangle[] tris, Ray[] rays, int slots, bool forceStaged)
    {
        VulkanDevice device = OpenRaw(tris, slots, forceStaged);
        SlabBatcher batcher = new(device, [.. tris.Select(t => t.Id)], VulkanDevice.TmaxScaleBits);
        try
        {
            ulong[] bits = new ulong[(rays.Length + 63) / 64];
            HitId[] hits = new HitId[rays.Length];

            // 64 callers of uneven sizes, all at once, like vrad's workers.
            List<Task> calls = [];
            int at = 0;
            for (int k = 0; at < rays.Length; k++)
            {
                int n = Math.Min(rays.Length - at, 64 * (1 + (k % 7)));
                calls.Add(batcher.TraceVisibilityAsync(
                    rays.AsMemory(at, n), bits.AsMemory(at / 64), SelfIntersectionTminBits, CancellationToken.None));
                calls.Add(batcher.TraceClosestAsync(
                    rays.AsMemory(at, n), hits.AsMemory(at, n), SelfIntersectionTminBits, CancellationToken.None));
                at += n;
            }

            await Task.WhenAll(calls).WaitAsync(TimeSpan.FromMinutes(5));
            return (bits, hits);
        }
        finally
        {
            batcher.Close(device.Dispose);
        }
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    /// <summary>One shared probe of the Vulkan inventory, feeding every attribute's skip decision.</summary>
    private static readonly Lazy<(string? LoaderFailure, string[] RqDeviceNames)> Inventory = new(() =>
    {
        try
        {
            IReadOnlyList<VulkanDeviceInfo> rows = VulkanRayTracer.ProbeDevices();
            if (rows.Count == 0)
            {
                return ("no Vulkan physical device is exposed on this machine", []);
            }

            string[] rq = [.. rows.Where(r => r.RayQuery).Select(r => r.Name)];
            return (rq.Length == 0
                ? "no Vulkan device exposes VK_KHR_ray_query (found: "
                  + string.Join("; ", rows.Select(r => r.Name)) + ")"
                : null,
                rq);
        }
        catch (Exception e)
        {
            return ($"the Vulkan probe threw {e.GetType().Name}: {e.Message}", []);
        }
    });

    private static string? SkipFor(string? deviceMatch)
    {
        (string? loader, string[] names) = Inventory.Value;
        if (loader is not null)
        {
            return "GPU facts need a ray-query Vulkan device: " + loader;
        }

        if (deviceMatch is not null
            && !names.Any(n => n.Contains(deviceMatch, StringComparison.OrdinalIgnoreCase)))
        {
            return $"no device matching '{deviceMatch}' in the ray-query inventory "
                + "(" + string.Join("; ", names) + ")";
        }

        return null;
    }

    /// <summary>Facts that need any ray-query device; skip with the probe's words otherwise.</summary>
    private sealed class HwGpuFactAttribute : FactAttribute
    {
        public string? DeviceMatch { get; init; }

        public HwGpuFactAttribute()
        {
            string? skip = SkipFor(DeviceMatch);
            if (skip is not null)
            {
                Skip = "skipped: " + skip;
            }
        }
    }

    /// <summary>Corpus-tier GPU theory: needs both the device and <c>VVIS_STOCK_DIR</c>.</summary>
    private sealed class CorpusGpuTheoryAttribute : TheoryAttribute
    {
        public CorpusGpuTheoryAttribute()
        {
            string? skip = SkipFor("RADV");
            skip ??= Environment.GetEnvironmentVariable("VVIS_STOCK_DIR") is { Length: > 0 } d
                && Directory.Exists(d)
                ? null
                : "no VVIS_STOCK_DIR corpus tier mounted";
            if (skip is not null)
            {
                Skip = "skipped: " + skip;
            }
        }
    }

    private static VulkanRayTracer RequireHardware(TracedTriangle[] tris, int? MaxRaysPerSlab)
    {
        VulkanTracerAttempt a = VulkanRayTracer.TryCreate(
            tris,
            new VulkanRayTracerOptions(DeviceMatch: "radv", MaxRaysPerSlab: MaxRaysPerSlab ?? 4_194_304));
        if (!a.Success)
        {
            throw new Xunit.Sdk.XunitException(
                "radv pin rejected: " + (a.Report.Selected?.Reason ?? a.Report.Failure));
        }

        return a.Tracer!;
    }

    /// <summary>
    /// The parity gate, oracle-arbitrated: a double-precision brute force
    /// answers every ray under EACH arm's own near clip, so a cross-arm
    /// difference is only tolerated where the clips legitimately differ —
    /// the CPU KD's leaf test accepts t &gt; 1e-10 (KdRayTracer.cs:918,
    /// stock's FourZeros) while the GPU's ray query culls t &lt; 1e-3, and the
    /// any-hit kernel scales tmax by 1-2^-23. Outside that band every claim
    /// is strict: bits equal, hit/miss equal, ids equal or an oracle-witnessed
    /// tie inside the 1e-3 fraction band, worst fraction gap ≤ 1e-3, and each
    /// arm's committed hit equal to its own policy's oracle answer.
    /// </summary>
    private static void RunParity(TracedTriangle[] tris, Ray[] rays, string scene)
    {
        KdRayTracer cpu = KdRayTracer.Build(tris);
        using VulkanRayTracer gpu = RequireHardware(tris, MaxRaysPerSlab: null);

        int words = (rays.Length + 63) / 64;
        ulong[] cpuBits = new ulong[words];
        ulong[] gpuBits = new ulong[words];
        HitId[] cpuHits = new HitId[rays.Length];
        HitId[] gpuHits = new HitId[rays.Length];

        cpu.TraceVisibility(rays, cpuBits, RayTraceOptions.SelfIntersectionSafe);
        cpu.TraceClosest(rays, cpuHits, RayTraceOptions.SelfIntersectionSafe);
        gpu.TraceVisibilityAsync(rays, gpuBits, RayTraceOptions.SelfIntersectionSafe)
            .AsTask().GetAwaiter().GetResult();
        gpu.TraceClosestAsync(rays, gpuHits, RayTraceOptions.SelfIntersectionSafe)
            .AsTask().GetAwaiter().GetResult();

        int bitDiff = 0;
        int nearBand = 0;
        int hitMissDiff = 0;
        int idDiff = 0;
        int tieBand = 0;
        int gapBad = 0;
        int gpuBitErr = 0;
        int gpuCommitErr = 0;
        int cpuCommitErr = 0;
        double worstCpuErr = 0;
        double worstGpuErr = 0;
        double worstArmGap = 0;
        string? firstBad = null;
        int[] crossId = new int[tris.Length];
        double[] crossT = new double[tris.Length];
        const float FarClip = 1f - 1f / 4_194_304f; // 1 - 2^-22: covers the any-hit kernel's tmax scale with slack

        for (int r = 0; r < rays.Length; r++)
        {
            ref readonly Ray q = ref rays[r];
            double tmax = q.MaxDistance;
            (double tCpu, int idCpu, double tGpu, int idGpu, double tAny,
                bool cpuBitTrue, bool gpuBitTrue, double secondT, int secondId, int nCross) =
                Policies(q, tris, crossId, crossT);

            // The arms' policies legitimately differ when a crossing sits
            // under the GPU's 1e-3 TMIN, or in the any-hit far clip.
            bool band = tAny < 1e-3 * (1 + 1e-6)
                || tAny <= tmax * (1 + 1e-6) && tAny >= tmax * FarClip * (1 - 1e-6);
            bool far = tAny < double.PositiveInfinity
                && tAny >= tmax * FarClip * (1 - 1e-6) && tAny <= tmax * (1 + 1e-6);
            if (tAny < 1e-3 * (1 + 1e-6))
            {
                nearBand++;
            }

            bool c = (cpuBits[r >> 6] & (1UL << (r & 63))) != 0;
            bool g = (gpuBits[r >> 6] & (1UL << (r & 63))) != 0;

            if (!band && !far && g != gpuBitTrue)
            {
                gpuBitErr++;
                firstBad ??= $"gbit ray {r} bit={g} expect={gpuBitTrue} tGpu={tGpu:E4} tmax={tmax:0}";
            }

            if (gpuHits[r].Surface >= 0)
            {
                double eg = Math.Abs(gpuHits[r].Fraction - tGpu / tmax);
                if (eg > worstGpuErr)
                {
                    worstGpuErr = eg;
                }

                bool ok = tGpu < double.PositiveInfinity
                    && gpuHits[r].Surface == idGpu && eg <= 1e-3;
                if (!ok && (!far || !InList(crossId, crossT, nCross, gpuHits[r].Surface, gpuHits[r].Fraction, tmax)))
                {
                    gpuCommitErr++;
                    firstBad ??= $"gcommit ray {r} gpu={gpuHits[r].Surface}@{gpuHits[r].Fraction:E6} "
                               + $"oracle={idGpu}@{tGpu / tmax:E6}";
                }
            }
            else if (!band && !far && gpuBitTrue)
            {
                gpuCommitErr++;
                firstBad ??= $"gmiss ray {r} oracle={idGpu}@{tGpu / tmax:E6}";
            }

            if (cpuHits[r].Surface >= 0)
            {
                double ec = Math.Abs(cpuHits[r].Fraction - tCpu / tmax);
                if (ec > worstCpuErr)
                {
                    worstCpuErr = ec;
                }

                bool ok = !band
                    ? tCpu < double.PositiveInfinity && cpuHits[r].Surface == idCpu && ec <= 1e-3
                    : InList(crossId, crossT, nCross, cpuHits[r].Surface, cpuHits[r].Fraction, tmax);
                if (!ok)
                {
                    cpuCommitErr++;
                    firstBad ??= $"ccommit ray {r} cpu={cpuHits[r].Surface}@{cpuHits[r].Fraction:E6} "
                               + $"oracle={idCpu}@{tCpu / tmax:E6} band={band}";
                }
            }
            else if (!band && !far && cpuBitTrue)
            {
                cpuCommitErr++;
                firstBad ??= $"cmiss ray {r} oracle={idCpu}@{tCpu / tmax:E6}";
            }

            if (band || far)
            {
                continue; // policy-divergent: attributed per arm above
            }

            if (c != g)
            {
                bitDiff++;
                firstBad ??= $"bit ray {r} cpu={c} gpu={g} tAny={tAny:E3}";
            }

            bool cHit = cpuHits[r].Surface >= 0;
            bool gHit = gpuHits[r].Surface >= 0;
            if (cHit != gHit)
            {
                hitMissDiff++;
                firstBad ??= $"hitmiss ray {r} cpu={cHit} gpu={gHit} tAny={tAny:E3}";
                continue;
            }

            if (!cHit)
            {
                continue;
            }

            double fc = cpuHits[r].Fraction, fg = gpuHits[r].Fraction;
            double gap = Math.Abs(fc - fg);
            if (gap > worstArmGap)
            {
                worstArmGap = gap;
            }

            if (gap > 1e-3)
            {
                gapBad++;
                firstBad ??= $"gap ray {r} gap={gap:E3} cpu={cpuHits[r].Surface}@{fc:E6} gpu={gpuHits[r].Surface}@{fg:E6}";
            }

            if (cpuHits[r].Surface != gpuHits[r].Surface)
            {
                bool tie = gap <= 1e-3
                    && secondId >= 0
                    && Math.Abs(secondT - Math.Min(tCpu, tGpu)) <= tmax * 1e-3;
                if (tie)
                {
                    tieBand++;
                }
                else
                {
                    idDiff++;
                    firstBad ??= $"id ray {r} cpu={cpuHits[r].Surface}@{fc:E6} gpu={gpuHits[r].Surface}@{fg:E6} "
                               + $"gap {gap:E6} oracleId={idCpu}/{idGpu}";
                }
            }
        }

        string stats = $"{scene}: {rays.Length} rays, near band {nearBand} "
                     + $"({nearBand * 100.0 / rays.Length:F3}%), worst fraction gap {worstArmGap:E3}, "
                     + $"in-band ties {tieBand}";
        Assert.True(gpuBitErr == 0, $"{stats}: GPU any-hit bit disagrees with the double oracle: {gpuBitErr}; first: {firstBad}");
        Assert.True(gpuCommitErr == 0, $"{stats}: GPU closest-hit violates its own policy: {gpuCommitErr}; first: {firstBad}");
        Assert.True(cpuCommitErr == 0, $"{stats}: CPU closest-hit commits a non-crossing: {cpuCommitErr}; first: {firstBad}");
        Assert.True(bitDiff == 0, $"{stats}: any-hit hit-bit disagreement outside the policy bands: {bitDiff}; first: {firstBad}");
        Assert.True(hitMissDiff == 0, $"{stats}: hit/miss disagreement outside the policy bands: {hitMissDiff}; first: {firstBad}");
        Assert.True(idDiff == 0, $"{stats}: closest-hit id disagreement outside the 1e-3 tie band: {idDiff}; first: {firstBad}");
        Assert.True(gapBad == 0, $"{stats}: closest-hit fraction gap over 1e-3 outside the bands: {gapBad}; first: {firstBad}");
        Assert.True(worstArmGap <= 1e-3, $"{stats}: worst outside-band fraction gap {worstArmGap:E6} exceeds the 1e-3 contract");
        Assert.Contains(cpuHits, h => h.Surface >= 0);    // the scene really blocks rays
        Assert.Contains(cpuBits, w => w != 0);
    }

    /// <summary>
    /// Double brute force: earliest crossing under the CPU's leaf clip
    /// (t&gt;1e-10), the GPU's TMIN (t&#8805;1e-3), and of any kind; both
    /// arms' any-hit windows; the second distinct-id crossing; and every
    /// crossing the CPU arm could legally commit, for in-band attribution.
    /// </summary>
    private static (double TCpu, int IdCpu, double TGpu, int IdGpu, double TAny,
        bool CpuBitTrue, bool GpuBitTrue, double SecondT, int SecondId, int NCross)
        Policies(in Ray q, TracedTriangle[] tris, int[] crossId, double[] crossT)
    {
        double[] d = [q.DirectionX, q.DirectionY, q.DirectionZ];
        double[] o = [q.OriginX, q.OriginY, q.OriginZ];
        double tmax = q.MaxDistance;
        double tCpu = double.PositiveInfinity, tGpu = double.PositiveInfinity, tAny = double.PositiveInfinity;
        int idCpu = -1, idGpu = -1;
        bool cpuBit = false, gpuBit = false;
        double best = double.PositiveInfinity, second = double.PositiveInfinity;
        int bestId = -1, secondId = -1;
        int nCross = 0;
        double gpuFar = tmax * (double)(1f - 1f / 8_388_608f); // any-hit kernel's (1-2^-23) tmax scale
        for (int i = 0; i < tris.Length; i++)
        {
            TracedTriangle t = tris[i];
            double e1x = t.V1.X - t.V0.X, e1y = t.V1.Y - t.V0.Y, e1z = t.V1.Z - t.V0.Z;
            double e2x = t.V2.X - t.V0.X, e2y = t.V2.Y - t.V0.Y, e2z = t.V2.Z - t.V0.Z;
            double hx = d[1] * e2z - d[2] * e2y, hy = d[2] * e2x - d[0] * e2z, hz = d[0] * e2y - d[1] * e2x;
            double aa = e1x * hx + e1y * hy + e1z * hz;
            if (Math.Abs(aa) < 1e-300)
            {
                continue;
            }

            double f = 1.0 / aa;
            double sx = o[0] - t.V0.X, sy = o[1] - t.V0.Y, sz = o[2] - t.V0.Z;
            double u = f * (sx * hx + sy * hy + sz * hz);
            if (u < -1e-6 || u > 1 + 1e-6)
            {
                continue;
            }

            double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
            double v = f * (d[0] * qx + d[1] * qy + d[2] * qz);
            if (v < -1e-6 || u + v > 1 + 1e-6)
            {
                continue;
            }

            double tt = f * (e2x * qx + e2y * qy + e2z * qz);
            if (tt < 0 || tt > tmax * (1 + 1e-6))
            {
                continue;
            }

            if (tt < tAny)
            {
                tAny = tt;
            }

            if (tt > 1e-10 && tt < tCpu)
            {
                tCpu = tt;
                idCpu = t.Id;
            }

            if (tt >= 1e-3 && tt < tGpu)
            {
                tGpu = tt;
                idGpu = t.Id;
            }

            if (tt > 1e-10)
            {
                cpuBit = true;
                crossId[nCross] = t.Id;
                crossT[nCross] = tt;
                nCross++;
            }

            if (tt >= 1e-3 && tt <= gpuFar)
            {
                gpuBit = true;
            }

            if (tt < best)
            {
                if (t.Id != bestId)
                {
                    second = best;
                    secondId = bestId;
                }

                best = tt;
                bestId = t.Id;
            }
            else if (t.Id != bestId && tt < second)
            {
                second = tt;
                secondId = t.Id;
            }
        }

        return (tCpu, idCpu, tGpu, idGpu, tAny, cpuBit, gpuBit, second, secondId, nCross);
    }

    /// <summary>True when an arm committed one of the oracle's real crossings.</summary>
    private static bool InList(int[] crossId, double[] crossT, int nCross, int surface, double fraction, double tmax)
    {
        for (int j = 0; j < nCross; j++)
        {
            if (crossId[j] == surface && Math.Abs(fraction - crossT[j] / tmax) <= 1e-3)
            {
                return true;
            }
        }

        return false;
    }

    private static TracedTriangle[] TwoTriangles() =>
    [
        new(0, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), 0),
        new(1, new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1), 0),
    ];

    /// <summary>
    /// The spike's lattice: a planes-at-x=k grid, two triangles per cell,
    /// rays starting upstream and aimed at face interiors with a 5% margin —
    /// the geometry the 0/1M proof ran on.
    /// </summary>
    private static (TracedTriangle[] Tris, Ray[] Rays) Lattice(int rays, int planes, int seed)
    {
        List<TracedTriangle> tris = [];
        int id = 0;
        for (int gx = 0; gx < planes; gx++)
        {
            for (int gy = 0; gy < planes; gy++)
            {
                float x = gx + 1f;
                tris.Add(new TracedTriangle(id++,
                    new Vec3(x, gy, 0), new Vec3(x, gy + 1f, 0), new Vec3(x, gy, 1), 0));
                tris.Add(new TracedTriangle(id++,
                    new Vec3(x, gy + 1f, 0), new Vec3(x, gy + 1f, 1), new Vec3(x, gy, 1), 0));
            }
        }

        Random rng = new(seed);
        Ray[] rs = new Ray[rays];
        const float OX = -2f, OY = 0.5f, OZ = 0.5f;
        for (int i = 0; i < rays; i++)
        {
            int gx = rng.Next(planes), gy = rng.Next(planes);
            float y = gy + 0.05f + 0.9f * (float)rng.NextDouble();
            float z = 0.05f + 0.9f * (float)rng.NextDouble();
            rs[i] = Aim(OX, OY, OZ, gx + 1f, y, z);
        }

        return ([.. tris], rs);
    }

    /// <summary>
    /// Real-map rays: fire from a random point of the scene bounds at a point
    /// strictly inside a random cast triangle (barycentrics ≥5% off every
    /// edge), reaching 1.0001× the crossing so the hit is strictly inside the
    /// segment for either tracer's epsilon.
    /// </summary>
    private static Ray[] FaceInteriorRays(TracedTriangle[] tris, int rays, int seed)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (TracedTriangle t in tris)
        {
            foreach (Vec3 v in new Vec3[] { t.V0, t.V1, t.V2 })
            {
                minX = MathF.Min(minX, v.X); maxX = MathF.Max(maxX, v.X);
                minY = MathF.Min(minY, v.Y); maxY = MathF.Max(maxY, v.Y);
                minZ = MathF.Min(minZ, v.Z); maxZ = MathF.Max(maxZ, v.Z);
            }
        }

        Random rng = new(seed);
        Ray[] rs = new Ray[rays];
        for (int i = 0; i < rays; i++)
        {
            TracedTriangle t = tris[rng.Next(tris.Length)];
            float u = 0.05f + 0.9f * (float)rng.NextDouble();
            float w = (1f - u) * (0.05f + 0.9f * (float)rng.NextDouble());
            float v = 1f - u - w;
            float tx = u * t.V0.X + v * t.V1.X + w * t.V2.X;
            float ty = u * t.V0.Y + v * t.V1.Y + w * t.V2.Y;
            float tz = u * t.V0.Z + v * t.V1.Z + w * t.V2.Z;

            float ox = minX + (maxX - minX) * (float)rng.NextDouble();
            float oy = minY + (maxY - minY) * (float)rng.NextDouble();
            float oz = minZ + (maxZ - minZ) * (float)rng.NextDouble();
            float dx = tx - ox, dy = ty - oy, dz = tz - oz;
            if (dx * dx + dy * dy + dz * dz < 1e-4f)
            {
                i--;
                continue;
            }

            rs[i] = Aim(ox, oy, oz, tx, ty, tz);
        }

        return rs;
    }

    /// <summary>Ray from O through T, reaching 1.0001× the crossing (un-normalised, stock's parameterisation).</summary>
    private static Ray Aim(float ox, float oy, float oz, float tx, float ty, float tz)
    {
        float dx = tx - ox, dy = ty - oy, dz = tz - oz;
        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        return new Ray(ox, oy, oz, dx, dy, dz, len * 1.0001f);
    }
}
