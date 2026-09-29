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
/// GPU-like ray-query-capable device. Facts pin <c>"radv"</c>,
/// <c>"NVIDIA"</c> and <c>"llvmpipe"</c> to check the self-test and parity
/// on each of those devices.
/// </param>
/// <param name="DeviceIndex">
/// Physical-device index pin among ray-query-capable devices, or −1 for no
/// index pin. Takes effect before <paramref name="DeviceMatch"/>.
/// </param>
/// <param name="MaxRaysPerSlab">
/// The ray budget for the slabs in flight together: each of the
/// <see cref="SlabsInFlight"/> slabs holds an equal share, and the device's
/// buffer-binding limit can only lower that. 4,194,304 rays = 128 MB of ray
/// bytes, the spec's minimum <c>maxStorageBufferBindingSize</c> and what a
/// single slab held before slabs were pipelined, so pipelining costs no
/// extra memory.
/// </param>
/// <param name="DispatchTimeoutSeconds">
/// How long a dispatched slab may leave its fence unsignalled before the
/// tracer calls the driver hung, so a device that never finishes a
/// dispatch surfaces as a failure rather than a wedge. The device currently
/// waits a fixed 120 s per fence whatever this says.
/// </param>
public readonly record struct VulkanRayTracerOptions(
    string? DeviceMatch = null,
    int DeviceIndex = -1,
    int MaxRaysPerSlab = 4_194_304,
    int DispatchTimeoutSeconds = 120)
{
    /// <summary>
    /// How many slabs may be on the device at once, 1 to 8; 0 (what a
    /// defaulted options value holds) means 3. With more than one, the
    /// device traces one slab while the next waits queued behind it and the
    /// tracer packs or unpacks another, instead of each waiting for the
    /// other. 1 reproduces one slab at a time.
    /// </summary>
    /// <remarks>
    /// An init property rather than a constructor parameter: hosts that
    /// build the options by reflection over the four-argument constructor
    /// keep working.
    /// </remarks>
    public int SlabsInFlight { get; init; }
}

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
/// Maximum proceed-iterations any telemetry ray counted (kernel mode 5). The
/// BLAS is opaque, so zero is what a conformant driver reports: it commits
/// the triangles inside traversal and offers no candidates. Zero on a device
/// that failed says nothing about why it failed.
/// </param>
/// <param name="Candidates">
/// Telemetry rays that reached a candidate intersection. A conformant driver
/// offers none for opaque geometry. llvmpipe offered them only while the
/// kernel was handed a BLAS instead of a TLAS, and passes with the TLAS.
/// </param>
/// <param name="Reason">
/// Why the device was rejected, in terms of the legs that failed and what the
/// telemetry saw (never a guessed cause), or null when it passed.
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
/// kernel mode before the tracer is handed out. A driver that gets either
/// known answer wrong is REJECTED, with the legs
/// that failed and the telemetry in the reason, but no cause the test cannot
/// tell apart (<see cref="ReasonFor"/> says why);
/// <see cref="TryCreateAsync"/> never throws a driver failure — it
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
    private readonly SlabBatcher _batcher;
    private volatile bool _disposed;

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

    /// <summary>Rays one dispatch carries at most on this device: its share of the requested budget.</summary>
    public int MaxRaysPerSlab => _device.MaxSlabRays;

    /// <summary>How many slabs this tracer keeps on the device at once.</summary>
    public int SlabsInFlight => _device.SlotCount;

    /// <summary>Triangles in the loaded BLAS.</summary>
    public int TriangleCount => (int)_device.TriangleCount;

    /// <summary>Peak bytes of Vulkan memory this instance has had allocated at once.</summary>
    public long PeakDeviceBytes => _device.PeakAllocationBytes;

    private VulkanRayTracer(VulkanDevice device, int[] triangleIds, SelfTestRecord selfTest)
    {
        _device = device;
        _triangleIds = triangleIds;
        SelfTest = selfTest;
        // Every batch goes through one drainer that packs concurrent callers'
        // rays into shared slabs (SlabBatcher's remarks say why).
        _batcher = new SlabBatcher(device, triangleIds, TmaxScaleBits);
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
    /// <exception cref="OperationCanceledException">
    /// The attempt was cancelled. Whatever device it had opened is released
    /// first; cancellation is never reported as a decline.
    /// </exception>
    public static VulkanTracerAttempt TryCreate(
        ReadOnlyMemory<TracedTriangle> triangles,
        VulkanRayTracerOptions options = default,
        CancellationToken cancellationToken = default) =>
        TryCreate(triangles, observe: null, options, cancellationToken);

    /// <summary>
    /// <see cref="TryCreate(ReadOnlyMemory{TracedTriangle}, VulkanRayTracerOptions, CancellationToken)"/>
    /// with an observer called at each stage boundary and when the device is released.
    /// </summary>
    /// <param name="triangles">The scene.</param>
    /// <param name="observe">
    /// Called with each <see cref="TryCreateStage"/> as the attempt reaches
    /// it, or null. Facts throw from it to fail the attempt at a chosen
    /// stage, and count <see cref="TryCreateStage.Released"/> to prove the
    /// device is released exactly once on every path that does not hand it
    /// to a tracer.
    /// </param>
    /// <param name="options">Device pin and sizing.</param>
    /// <param name="cancellationToken">Checked at each stage boundary.</param>
    /// <returns>The tracer or the reason it was not created.</returns>
    internal static VulkanTracerAttempt TryCreate(
        ReadOnlyMemory<TracedTriangle> triangles,
        Action<TryCreateStage>? observe,
        VulkanRayTracerOptions options = default,
        CancellationToken cancellationToken = default) =>
        TryCreate(triangles, observe, static () => new VulkanDevice(), options, cancellationToken);

    /// <summary>
    /// <see cref="TryCreate(ReadOnlyMemory{TracedTriangle}, Action{TryCreateStage}, VulkanRayTracerOptions, CancellationToken)"/>
    /// with the step that loads the Vulkan API replaced.
    /// </summary>
    /// <param name="triangles">The scene.</param>
    /// <param name="observe">Called at each stage, or null.</param>
    /// <param name="open">
    /// Creates the device object, which loads the Vulkan loader. Facts pass
    /// one that throws what a machine with no loader throws, which no
    /// machine with a loader can otherwise show.
    /// </param>
    /// <param name="options">Device pin and sizing.</param>
    /// <param name="cancellationToken">Checked at each stage boundary.</param>
    /// <returns>The tracer or the reason it was not created.</returns>
    internal static VulkanTracerAttempt TryCreate(
        ReadOnlyMemory<TracedTriangle> triangles,
        Action<TryCreateStage>? observe,
        Func<VulkanDevice> open,
        VulkanRayTracerOptions options,
        CancellationToken cancellationToken)
    {
        // Checked before anything native is opened, so a bad value cannot
        // leave a device behind.
        int slots = SlotsFor(options);
        TracedTriangle[] scene = triangles.ToArray();
        List<VulkanDeviceInfo> inventory = [];
        VulkanDevice device;
        try
        {
            device = open();
        }
        catch (Exception e) when (VulkanDevice.IsMissingLoader(e))
        {
            // No loader to open at all: the same clear decline as a driver
            // that refuses, with the probe's one row as the inventory.
            inventory.AddRange(VulkanDevice.ProbeDevices());
            return new VulkanTracerAttempt(
                null, new VulkanDeviceReport(inventory, null, "no Vulkan loader: " + e.Message), false);
        }

        // Until a tracer owns the device, this method does: every way out
        // that does not hand it over (a decline, a failed gate, cancellation,
        // or an exception nobody expected) releases it in the finally below.
        // A long-lived host runs many attempts in one process, and a device
        // left open by any of them is native memory and a driver context it
        // never gets back.
        bool handedOver = false;
        try
        {
            observe?.Invoke(TryCreateStage.Opened);
            cancellationToken.ThrowIfCancellationRequested();
            // The inventory comes first so even a total failure reports what
            // the box really has (the device-pin diagnostic the tools owe).
            inventory.AddRange(VulkanDevice.ProbeDevices());
            device.Construct(options.DeviceMatch, options.DeviceIndex, options.MaxRaysPerSlab, slots);
            cancellationToken.ThrowIfCancellationRequested();

            observe?.Invoke(TryCreateStage.Constructed);
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
                ReasonFor(ready, device.DeviceName, outcome));
            observe?.Invoke(TryCreateStage.SelfTested);
            if (!ready)
            {
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
            observe?.Invoke(TryCreateStage.SceneLoaded);
            VulkanRayTracer tracer = new(device, ids, record);
            handedOver = true;
            return new VulkanTracerAttempt(tracer, new VulkanDeviceReport(inventory, record, null), true);
        }
        catch (Exception e) when (e is VulkanException or NotSupportedException or InvalidOperationException)
        {
            // A driver that refuses at open time gets the same treatment as
            // one that fails the gate: a clear message, not a crash. Anything
            // else (cancellation above all) is not a decline and propagates,
            // after the finally has released the device.
            string why = e.Message;
            if (e.InnerException is { } inner)
            {
                why += " [" + inner.Message + "]";
            }

            return new VulkanTracerAttempt(null, new VulkanDeviceReport(inventory, null, why), false);
        }
        finally
        {
            if (!handedOver)
            {
                device.Dispose();
                observe?.Invoke(TryCreateStage.Released);
            }
        }
    }

    /// <summary>The slot count <paramref name="options"/> asks for, 0 meaning the default.</summary>
    /// <param name="options">The options.</param>
    /// <returns>1 to <see cref="SlabMemory.MaxSlots"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Negative, or more than the maximum.</exception>
    internal static int SlotsFor(VulkanRayTracerOptions options)
    {
        int slots = options.SlabsInFlight == 0 ? SlabMemory.DefaultSlots : options.SlabsInFlight;
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1, nameof(options.SlabsInFlight));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slots, SlabMemory.MaxSlots, nameof(options.SlabsInFlight));
        return slots;
    }

    /// <summary>
    /// Says why a device failed the self-test, in terms of what the test
    /// observed, for the report and the facts.
    /// </summary>
    /// <param name="ready">Whether the device passed.</param>
    /// <param name="deviceName">The device's name, quoted first.</param>
    /// <param name="o">What the kernel modes observed.</param>
    /// <returns>Null when the device passed; otherwise the reason.</returns>
    /// <remarks>
    /// <para>
    /// The reason names only what the test can tell apart, and no cause
    /// beyond it. The readback leg is a real separation: if mode 4's
    /// all-ones do not come back, nothing traced matters. Past that, the
    /// known-answer legs say WHICH answer was wrong (any-hit missed a known
    /// hit, or closest-hit gave the wrong triangle or distance), and the
    /// telemetry adds one more observation, but it cannot say why.
    /// </para>
    /// <para>
    /// The geometry is built opaque, so a conformant driver commits every
    /// triangle inside traversal and its first <c>rayQueryProceedEXT</c>
    /// returns false: zero proceed iterations and zero candidates is what a
    /// WORKING device reports (radv does, and passes). Zero iterations on a
    /// failing device therefore cannot tell a driver that never traverses
    /// from one that traverses and commits the wrong thing, or from a scene
    /// that came out empty. NVIDIA's zeroes show why the reason must not
    /// guess: they were first put down to the device, then to the kernel's
    /// undefined terminate after the proceed loop, and the real cause was
    /// neither: the BLAS sat in the descriptor where a ray query needs a
    /// TLAS. Candidates on opaque geometry ARE unusual (llvmpipe reported
    /// them under that same mistake), so they are quoted as an observation,
    /// again without a cause.
    /// </para>
    /// </remarks>
    internal static string? ReasonFor(bool ready, string deviceName, SelfTestOutcome o)
    {
        if (ready)
        {
            return null;
        }

        if (!o.ReadbackOk)
        {
            return $"{deviceName}: the compute write/readback path itself is broken "
                + "(mode 4 wrote all-ones and they did not read back) — no ray answer from this "
                + "device can be trusted";
        }

        List<string> wrong = [];
        if (!o.AnyHitOk)
        {
            wrong.Add("any-hit missed a known hit");
        }

        if (!o.ClosestOk)
        {
            wrong.Add("closest-hit did not return the known triangle at the known distance");
        }

        string legs = wrong.Count == 0 ? "a known-answer leg failed" : string.Join(" and ", wrong);
        string telemetry = o.Candidates > 0
            ? $"traversal offered candidates on {o.Candidates} ray(s) of an opaque BLAS "
              + $"({o.Iters} proceed iteration(s) at most), which a conformant driver does not"
            : $"the telemetry saw {o.Iters} proceed iteration(s) and no candidates, which is also "
              + "what a working device reports for an opaque BLAS, so it cannot say where the "
              + "answer went wrong";
        return $"{deviceName}: {legs} on the two-triangle self-test scene; {telemetry}. "
            + "Rejecting the device";
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Everything but a skipped id. The kernel takes no id to ignore, and
    /// teaching it one is a kernel change with its own parity gate, so a batch
    /// that skips an id (a static prop's self-shadow rule) is refused here and
    /// answered by the CPU tracer: <see cref="Rad.HybridRayTracer"/> asks this
    /// first and routes it there.
    /// </para>
    /// <para>
    /// Sky pass-through needs no kernel change: it is a question about the
    /// FIRST hit, which is exactly what the closest-hit mode returns, so the
    /// batch is traced closest-hit and folded into bits on the host
    /// (<see cref="FoldSkyPassing"/>). <see cref="RayTraceOptions.IsolatedRays"/>
    /// is free: the kernel traces every ray on its own.
    /// </para>
    /// </remarks>
    public bool Supports(RayTraceOptions options) => SupportsOptions(options);

    /// <summary>The rule <see cref="Supports"/> applies, without a device.</summary>
    /// <param name="options">The options.</param>
    /// <returns>True unless an id is skipped or the minimum distance is not a ray-query <c>tmin</c>.</returns>
    /// <remarks>
    /// A negative or NaN <see cref="RayTraceOptions.MinDistance"/> is
    /// refused too: a ray query's <c>tmin</c> must be a non-negative number,
    /// and the kernel would answer every such ray a miss rather than trace
    /// a question it cannot ask. The CPU tracer defines the answer, so such
    /// a batch goes there.
    /// </remarks>
    internal static bool SupportsOptions(RayTraceOptions options) =>
        options.SkipId is null && options.MinDistance >= 0f;

    /// <summary>
    /// Turns closest hits into sky-passing visibility bits: a ray is blocked
    /// when its first hit is short of its end and is not a sky triangle.
    /// </summary>
    /// <param name="hits">The closest hit of each ray, fractions in units of its reach.</param>
    /// <param name="hitBits">Receives one bit per ray; every word the rays span is overwritten.</param>
    /// <remarks>
    /// "Short of its end" is the seam's visibility rule, <c>Fraction &lt; 1</c>
    /// in the closest-hit units, and "sky" is <see cref="Rad.TraceId.Sky"/>
    /// in the triangle's id, which the closest-hit readback already maps to.
    /// </remarks>
    internal static void FoldSkyPassing(ReadOnlySpan<HitId> hits, Span<ulong> hitBits)
    {
        hitBits[..((hits.Length + 63) >> 6)].Clear();
        for (int i = 0; i < hits.Length; i++)
        {
            HitId hit = hits[i];
            if (hit.Surface != HitId.Miss && hit.Fraction < 1.0f && (hit.Surface & Rad.TraceId.Sky) == 0)
            {
                hitBits[i >> 6] |= 1UL << (i & 63);
            }
        }
    }

    private async Task TraceSkyPassingAsync(
        ReadOnlyMemory<Ray> rays, Memory<ulong> hitBits, uint tminBits, CancellationToken cancellationToken)
    {
        // Returned to the pool only once the batcher has completed the
        // request, and it completes a request only when no slab of it is on
        // the device (cancellation takes effect between slabs).
        HitId[] hits = System.Buffers.ArrayPool<HitId>.Shared.Rent(rays.Length);
        try
        {
            await _batcher.TraceClosestAsync(rays, hits.AsMemory(0, rays.Length), tminBits, cancellationToken)
                .ConfigureAwait(false);
            FoldSkyPassing(hits.AsSpan(0, rays.Length), hitBits.Span);
        }
        finally
        {
            System.Buffers.ArrayPool<HitId>.Shared.Return(hits);
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="hitBits"/> is shorter than the batch needs.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="options"/> skips an id, which this kernel cannot (<see cref="Supports"/>).
    /// </exception>
    public ValueTask TraceVisibilityAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<ulong> hitBits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireSupported(options);
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
        return options.SkyDoesNotBlock
            ? new ValueTask(TraceSkyPassingAsync(rays, hitBits, tminBits, cancellationToken))
            : new ValueTask(_batcher.TraceVisibilityAsync(rays, hitBits, tminBits, cancellationToken));
    }

    /// <summary>Refuses options the kernel cannot honour, rather than tracing a different question.</summary>
    /// <param name="options">The batch's options.</param>
    /// <exception cref="NotSupportedException">An id is skipped.</exception>
    internal static void RequireSupported(RayTraceOptions options)
    {
        if (options.SkipId is null && !SupportsOptions(options))
        {
            throw new NotSupportedException(
                $"the Vulkan kernel cannot trace with minimum distance {options.MinDistance}: a ray query's "
                + "tmin must be non-negative; trace this batch on the CPU tracer");
        }

        if (!SupportsOptions(options))
        {
            throw new NotSupportedException(
                $"the Vulkan kernel cannot skip triangle id {options.SkipId}; trace this batch on the CPU tracer");
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="hits"/> is shorter than the batch, or <paramref name="options"/>
    /// asks for sky pass-through, which is a visibility option.
    /// </exception>
    /// <exception cref="NotSupportedException"><paramref name="options"/> skips an id.</exception>
    public ValueTask TraceClosestAsync(
        ReadOnlyMemory<Ray> rays,
        Memory<HitId> hits,
        RayTraceOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireSupported(options);
        if (options.SkyDoesNotBlock)
        {
            throw new ArgumentException(
                "sky pass-through is a visibility option; a closest-hit trace reports the sky hit itself",
                nameof(options));
        }

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
        return new ValueTask(_batcher.TraceClosestAsync(rays, hits, tminBits, cancellationToken));
    }

    /// <summary>
    /// Releases the device, once the work still on it has finished, so the
    /// next tracer can open it; if that work does not finish within a bound,
    /// the device is abandoned rather than freed under a running kernel
    /// (the device's own dispose says why).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Queued batches fail, a dispatch in flight finishes, then the device goes.
        _batcher.Close(_device.Dispose);
    }
}
