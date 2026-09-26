//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Gpu.Interop;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapTools.Gpu;

/// <summary>How <see cref="VulkanRayTracer.TryCreateAsync"/> should pick and size the device.</summary>
/// <param name="DeviceMatch">
/// Device-name substring pin (case-insensitive), or null to prefer the most
/// GPU-like ray-query-capable device. Diagnostics pin <c>"llvmpipe"</c> and
/// <c>"NVIDIA"</c> to prove the self-test rejects them.
/// </param>
/// <param name="DeviceIndex">
/// Physical-device index pin among ray-query-capable devices, or −1 for no
/// index pin. Takes effect before <paramref name="DeviceMatch"/>.
/// </param>
/// <param name="MaxRaysPerSlab">
/// Requested rays per dispatch; the device's buffer-binding limit can only
/// lower it. 4,194,304 rays = 128 MB of ray bytes, the spec's minimum
/// <c>maxStorageBufferBindingSize</c>, so every conformant device takes it.
/// </param>
/// <param name="DispatchTimeoutSeconds">
/// How long a dispatched slab may leave its fence unsignalled before the
/// tracer calls the driver hung. A device that never traverses (the nvidia
/// signature) surfaces as a rejection rather than a wedge.
/// </param>
public readonly record struct VulkanRayTracerOptions(
    string? DeviceMatch = null,
    int DeviceIndex = -1,
    int MaxRaysPerSlab = 4_194_304,
    int DispatchTimeoutSeconds = 120);

/// <summary>
/// What the capability self-test saw, kept verbatim for the caller's
/// diagnostics and for the facts that assert rejection signatures.
/// </summary>
/// <param name="Passed">Whether this device may trace real rays.</param>
/// <param name="DeviceName">Selected device's name.</param>
/// <param name="DriverName">Selected device's driver string.</param>
/// <param name="ReadbackOk">
/// Whether the no-traversal write/copy/readback path worked (kernel mode 4).
/// </param>
/// <param name="AnyHitOk">Whether both known-hit rays returned their bits.</param>
/// <param name="ClosestOk">
/// Whether closest-hit returned the two expected primitive ids at the
/// expected distance.
/// </param>
/// <param name="Iters">
/// Maximum proceed-iterations any telemetry ray counted (kernel mode 5). Zero
/// on every ray is the compute-only queue never traversing (the nvidia
/// signature).
/// </param>
/// <param name="Candidates">
/// Telemetry rays that reached a candidate intersection. Candidates with
/// modes 0/1 committing nothing is the Mesa lavapipe candidate→committed bug.
/// </param>
/// <param name="Reason">
/// Why the device was rejected, naming the signature it matches, or null when
/// it passed.
/// </param>
public readonly record struct SelfTestRecord(
    bool Passed,
    string DeviceName,
    string DriverName,
    bool ReadbackOk,
    bool AnyHitOk,
    bool ClosestOk,
    int Iters,
    int Candidates,
    string? Reason);

/// <summary>A device inventory row set plus the self-test outcome of the last attempt.</summary>
/// <param name="Devices">Every physical device the loader exposed, even rejected ones.</param>
/// <param name="Selected">The self-test outcome for the chosen device, or null when selection failed.</param>
/// <param name="Failure">
/// The selection failure in the caller's language when no device was even
/// opened (no loader, no match, no ray query), or null.
/// </param>
public sealed record VulkanDeviceReport(
    IReadOnlyList<VulkanDeviceInfo> Devices,
    SelfTestRecord? Selected,
    string? Failure);

/// <summary>Outcome of <see cref="VulkanRayTracer.TryCreateAsync"/>.</summary>
/// <param name="Tracer">
/// The ready tracer, or null when the device was rejected — in which case the
/// caller keeps its CPU tracer. A GPU path that cannot prove itself is not a
/// reason to crash; the gate here is "capable devices only".
/// </param>
/// <param name="Report">What the attempt saw, always present.</param>
/// <param name="Success">Whether <paramref name="Tracer"/> is usable.</param>
public readonly record struct VulkanTracerAttempt(
    VulkanRayTracer? Tracer,
    VulkanDeviceReport Report,
    bool Success);

/// <summary>
/// <see cref="IRayTracer"/> over Vulkan <c>VK_KHR_ray_query</c>: both seam
/// operations answered by the one kernel, one ordered slab per dispatch, no
/// atomics outside a workgroup.
/// </summary>
/// <remarks>
/// <para>
/// The lighting workload fits this backend exactly: the reference
/// lightmapper's rays are batch any-hit
/// visibility queries and the leaf-ambient closest-hit fan, and the seam
/// returns only what a ray query returns natively — a bit, or an id and a
/// fraction. Falloff, dot products and colour encoding stay on the CPU in
/// their fixed order, so parity is testable where it matters: equal bits
/// means equal lightmap bytes.
/// </para>
/// <para>
/// <b>DETERMINISM.</b> Rays are cut into ordered slabs of at
/// most <see cref="MaxRaysPerSlab"/>, each slab is one kernel dispatch, and
/// each dispatch's output is a pure function of its rays: the bit words are
/// sample-major (workgroup <c>g</c> owns words <c>2g</c> and <c>2g+1</c>),
/// folded with shared-memory atomics that cannot cross a workgroup. No
/// global atomics, no inter-workgroup ordering, no retry paths. Threads 1 and
/// 32 feeding the same scene the same rays therefore observe the same words
/// byte for byte — the invariant the CPU tracer carries for the GPU path.
/// </para>
/// <para>
/// <b>THE GATE.</b> Construction traces a known-hit micro-scene (two
/// triangles, two rays whose answers are exact by construction) through every
/// kernel mode before the tracer is handed out. A driver that commits nothing
/// (llvmpipe's Mesa candidate→committed bug: candidates found, committed
/// never) or never traverses (nvidia from a compute-only queue: zero proceed
/// iterations) is REJECTED with the telemetry that proves which signature it
/// matched; <see cref="TryCreateAsync"/> never throws a driver failure — it
/// returns the report so callers fall back to the CPU tracer. What it does
/// NOT do is optimistically trust a device because the extension list said
/// yes: the extension list is exactly why this gate exists.
/// </para>
/// <para>
/// One device per tracer instance, held for the instance's life; dispose to
/// release it. Tracing serialises internally, so concurrent callers on the
/// same instance are safe and each batch's answer still depends only on its
/// rays.
/// </para>
/// </remarks>
public sealed class VulkanRayTracer : IRayTracer, IDisposable
{
    /// <summary>The kernel's any-hit tmax shrink as float bits: <c>1 - 2^-23</c>.</summary>
    private const uint TmaxScaleBits = 0x3F7FFFFFu;

    /// <summary><c>1e-3f</c> as float bits — the epsilon the self-test ray traces with.</summary>
    private const uint SelfTestTminBits = 0x3A83126Fu;

    private readonly VulkanDevice _device;
    private readonly int[] _triangleIds;
    private readonly object _gate = new();
    private readonly uint[] _scratchWords;
    private bool _disposed;

    /// <inheritdoc />
    /// <remarks>
    /// Device and driver names, spaces hyphenated, so the whole identity is
    /// one cache-key token. A lighting product traced on the RX 9070 must
    /// never be read back on the 2070 or on llvmpipe, and unlike the CPU
    /// tracers this identity's arithmetic really does differ per device.
    /// </remarks>
    public string TracerIdentity { get; }

    /// <summary>The device's name, e.g. <c>AMD Radeon RX 9070 XT (RADV GFX1201)</c>.</summary>
    public string DeviceName => _device.DeviceName;

    /// <summary>The driver's name and info string.</summary>
    public string DriverName => _device.DriverName;

    /// <summary>The self-test record this instance passed — kept for diagnostics.</summary>
    public SelfTestRecord SelfTest { get; }

    /// <summary>Rays one dispatch carries at most on this device.</summary>
    public int MaxRaysPerSlab => _device.MaxSlabRays;

    /// <summary>Triangles in the loaded BLAS.</summary>
    public int TriangleCount => (int)_device.TriangleCount;

    /// <summary>Peak bytes of Vulkan memory this instance has had allocated at once.</summary>
    public long PeakDeviceBytes => _device.PeakAllocationBytes;

    private VulkanRayTracer(VulkanDevice device, int[] triangleIds, SelfTestRecord selfTest)
    {
        _device = device;
        _triangleIds = triangleIds;
        SelfTest = selfTest;
        // The closest-mode readback is 2 words/ray and any-hit is 2 words per
        // 64 rays, so sizing for closest covers both; allocated once per
        // instance, not per batch.
        _scratchWords = new uint[device.MaxSlabRays * 2];
        TracerIdentity = string.Concat(
            "gpu-vulkan-rayquery-",
            device.DeviceName.Replace(' ', '-'),
            "-",
            device.DriverName.Replace(' ', '-'));
    }

    /// <summary>
    /// What the Vulkan loader exposes, without opening anything: the rows
    /// carry each device's name and whether it offers
    /// <c>VK_KHR_ray_query</c>. Facts and tools use it to decide whether GPU
    /// work is even possible on this machine.
    /// </summary>
    /// <returns>One row per physical device the loader saw.</returns>
    public static IReadOnlyList<VulkanDeviceInfo> ProbeDevices() => VulkanDevice.ProbeDevices();

    /// <summary>
    /// Opens a device, loads the scene, and gates the device with the known-hit
    /// self-test. Never crashes on driver failure: every failure path returns
    /// a report with a reason so the caller can fall back to a CPU tracer.
    /// </summary>
    /// <param name="triangles">
    /// The scene, in the caller's own order. BLAS primitive index equals
    /// position in this list, and the ids read back are the
    /// <see cref="TracedTriangle.Id"/> values, matching what
    /// <see cref="KdRayTracer"/> would report for the same list.
    /// </param>
    /// <param name="options">Device pin and sizing.</param>
    /// <param name="cancellationToken">
    /// Cancels the attempt; a dispatch already in flight cannot be recalled
    /// and runs to its completion or timeout.
    /// </param>
    /// <returns>The tracer or the reason it was not created.</returns>
    public static Task<VulkanTracerAttempt> TryCreateAsync(
        ReadOnlyMemory<TracedTriangle> triangles,
        VulkanRayTracerOptions options = default,
        CancellationToken cancellationToken = default)
    {
        // The work is synchronous Vulkan; the offload is what makes the
        // seam's async shape honest (device opens can take seconds).
        return Task.Run(
            () => TryCreate(triangles, options, cancellationToken),
            cancellationToken);
    }

    /// <summary>Synchronous body of <see cref="TryCreateAsync"/>; runs on the caller's thread.</summary>
    /// <param name="triangles">The scene.</param>
    /// <param name="options">Device pin and sizing.</param>
    /// <param name="cancellationToken">Checked at each stage boundary.</param>
    /// <returns>The tracer or the reason it was not created.</returns>
    public static VulkanTracerAttempt TryCreate(
        ReadOnlyMemory<TracedTriangle> triangles,
        VulkanRayTracerOptions options = default,
        CancellationToken cancellationToken = default)
    {
        TracedTriangle[] scene = triangles.ToArray();
        List<VulkanDeviceInfo> inventory = [];
        VulkanDevice device = new();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The inventory comes first so even a total failure reports what
            // the box really has (the device-pin diagnostic the tools owe).
            inventory.AddRange(VulkanDevice.ProbeDevices());
            device.Construct(options.DeviceMatch, options.DeviceIndex, options.MaxRaysPerSlab);
            cancellationToken.ThrowIfCancellationRequested();

            // The gate runs before real geometry: two triangles whose answer
            // is known by construction, through every kernel mode.
            (bool ready, SelfTestOutcome outcome) = device.RunSelfTest();
            SelfTestRecord record = new(
                ready,
                device.DeviceName,
                device.DriverName,
                outcome.ReadbackOk,
                outcome.AnyHitOk,
                outcome.ClosestOk,
                outcome.Iters,
                outcome.Candidates,
                ReasonFor(ready, device, outcome));
            if (!ready)
            {
                device.Dispose();
                return new VulkanTracerAttempt(null, new VulkanDeviceReport(inventory, record, null), false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            float[] vertices = new float[scene.Length * 9];
            int[] ids = new int[scene.Length];
            for (int i = 0; i < scene.Length; i++)
            {
                ids[i] = scene[i].Id;
                int b = i * 9;
                Vec3 v0 = scene[i].V0;
                Vec3 v1 = scene[i].V1;
                Vec3 v2 = scene[i].V2;
                vertices[b] = v0.X;
                vertices[b + 1] = v0.Y;
                vertices[b + 2] = v0.Z;
                vertices[b + 3] = v1.X;
                vertices[b + 4] = v1.Y;
                vertices[b + 5] = v1.Z;
                vertices[b + 6] = v2.X;
                vertices[b + 7] = v2.Y;
                vertices[b + 8] = v2.Z;
            }

            device.LoadScene(vertices);
            return new VulkanTracerAttempt(
                new VulkanRayTracer(device, ids, record),
                new VulkanDeviceReport(inventory, record, null),
                true);
        }
        catch (Exception e) when (e is VulkanException or NotSupportedException or InvalidOperationException)
        {
            // A driver that refuses at open time gets the same treatment as
            // one that fails the gate: a clear message, not a crash.
            device.Dispose();
            string why = e.Message;
            if (e.InnerException is { } inner)
            {
                why += " [" + inner.Message + "]";
            }

            return new VulkanTracerAttempt(null, new VulkanDeviceReport(inventory, null, why), false);
        }
    }

    /// <summary>Names the failure signature the telemetry matches, for the report and the facts.</summary>
    private static string? ReasonFor(bool ready, VulkanDevice device, SelfTestOutcome o)
    {
        if (ready)
        {
            return null;
        }

        if (!o.ReadbackOk)
        {
            return $"{device.DeviceName}: the compute write/readback path itself is broken "
                + "(mode 4 wrote all-ones and they did not read back) — no ray answer from this "
                + "device can be trusted";
        }

        if (o.Iters == 0)
        {
            return $"{device.DeviceName}: traversal telemetry is 0 proceed-iterations on every "
                + "ray — the driver never traverses ray queries from a compute queue (the nvidia "
                + "signature); rejecting the device";
        }

        if (o.Candidates > 0)
        {
            return $"{device.DeviceName}: traversal found candidates on {o.Candidates} of the "
                + "known-hit rays yet modes 0/1 committed nothing — a known Mesa lavapipe "
                + "candidate->committed bug; rejecting the device";
        }

        return $"{device.DeviceName}: traversal ran ({o.Iters} iterations) but the BLAS offered no "
            + "candidates for the known-hit rays — the acceleration-structure build produced an "
            + "empty BLAS; rejecting the device";
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="hitBits"/> is shorter than the batch needs.
    /// </exception>
    public ValueTask TraceVisibilityAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<ulong> hitBits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int words = (rays.Length + 63) / 64;
        if (hitBits.Length < words)
        {
            throw new ArgumentException(
                $"{rays.Length} rays need {words} words of hit bits, and {hitBits.Length} "
                + "were given",
                nameof(hitBits));
        }

        if (rays.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        uint tminBits = (uint)BitConverter.SingleToInt32Bits(options.MinDistance);
        Ray[] batch = rays.ToArray();
        ulong[] bits = hitBits.ToArray();
        return new ValueTask(Task.Run(() =>
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                for (int start = 0; start < batch.Length; start += _device.MaxSlabRays)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(_device.MaxSlabRays, batch.Length - start);
                    int wordCount = (count + 63) / 64 * 2;
                    Span<uint> written = TraceSlab(0, batch, start, count, tminBits, wordCount);
                    // The kernel zeroes and writes each workgroup's two words
                    // itself, sample-major, so this slab's words are a pure
                    // function of its rays — copy them straight through.
                    // Workgroup g's pair of uint words lands at uint index
                    // start/32 + 2g in the batch's bit array (start is a slab
                    // boundary, hence a multiple of 64 rays).
                    written.CopyTo(
                        MemoryMarshal.Cast<ulong, uint>(bits.AsSpan())
                            .Slice(start / 32, wordCount));
                }

                bits.CopyTo(hitBits);
            }
        }, cancellationToken));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="hits"/> is shorter than the batch.
    /// </exception>
    public ValueTask TraceClosestAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<HitId> hits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (hits.Length < rays.Length)
        {
            throw new ArgumentException(
                $"{rays.Length} rays need {rays.Length} hit slots, and {hits.Length} were given",
                nameof(hits));
        }

        if (rays.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        uint tminBits = (uint)BitConverter.SingleToInt32Bits(options.MinDistance);
        Ray[] batch = rays.ToArray();
        HitId[] dst = hits.ToArray();
        return new ValueTask(Task.Run(() =>
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                for (int start = 0; start < batch.Length; start += _device.MaxSlabRays)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(_device.MaxSlabRays, batch.Length - start);
                    Span<uint> words = TraceSlab(1, batch, start, count, tminBits, count * 2);
                    for (int i = 0; i < count; i++)
                    {
                        uint prim = words[i * 2];
                        if (prim == 0xFFFFFFFFu || prim >= (uint)_triangleIds.Length)
                        {
                            dst[start + i] = HitId.Missed;
                            continue;
                        }

                        float t = BitConverter.Int32BitsToSingle(unchecked((int)words[(i * 2) + 1]));
                        float length = batch[start + i].MaxDistance;
                        // KD's own fraction rule, reproduced exactly: the
                        // divide-by-1 is skipped because it is exact, and the
                        // t the kernel committed is in ray-parameter units
                        // just like stock's m_HitDistance.
                        dst[start + i] = new HitId(
                            _triangleIds[prim],
                            length == 1.0f ? t : t / length);
                    }
                }

                dst.CopyTo(hits.Span);
            }
        }, cancellationToken));
    }

    /// <summary>
    /// Packs one slab into the pinned staging in the wire layout — two vec4
    /// per ray, <c>(ox,oy,oz,0)</c> and <c>(dx,dy,dz,tmax)</c> — and runs the
    /// kernel. The direction is NOT normalised and tmax is the caller's
    /// reach: the same parameterisation stock's callers hand
    /// <c>Trace4Rays</c>, so t comes back in the same units.
    /// </summary>
    private Span<uint> TraceSlab(int mode, Ray[] batch, int start, int count, uint tminBits, int outWords)
    {
        Span<float> staging = _device.StageRays(count);
        for (int i = 0; i < count; i++)
        {
            ref readonly Ray r = ref batch[start + i];
            int b = i * 8;
            staging[b] = r.OriginX;
            staging[b + 1] = r.OriginY;
            staging[b + 2] = r.OriginZ;
            staging[b + 3] = 0f;
            staging[b + 4] = r.DirectionX;
            staging[b + 5] = r.DirectionY;
            staging[b + 6] = r.DirectionZ;
            staging[b + 7] = r.MaxDistance;
        }

        Span<uint> words = _scratchWords.AsSpan(0, outWords);
        _device.Dispatch(mode, count, words, tminBits, TmaxScaleBits);
        return words;
    }

    /// <summary>Releases the device (idle-flushed first) so the next tracer can open it.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _device.Dispose();
        }
    }
}
