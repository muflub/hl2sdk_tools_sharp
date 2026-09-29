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

    /// <summary>
    /// When true, and the device is not pinned (<see cref="IsPinned"/>), decline a device the CPU tracer
    /// would beat: a CPU implementation of Vulkan, or a device whose
    /// measured ray upload rate is below <see cref="MinUploadBytesPerSecond"/>.
    /// A pinned device is always used. False (the default) keeps every
    /// device that passes the self-test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Use the faster one by default." <c>ssmap</c>'s unpinned
    /// <c>-gpu auto</c> sets it: a user who asked for the GPU without
    /// naming one wants the run to go faster, and on a machine whose only
    /// Vulkan device is llvmpipe, or whose card sits behind a slow link, the
    /// KD tracer is the faster one. Naming the device is how to say "this
    /// one anyway", which diagnostics and the device facts do.
    /// </para>
    /// <para>
    /// Unpinned with this on, an attempt walks every ray-query device in
    /// ranking order and takes the first one that is not a CPU
    /// implementation, passes the self-test and clears the upload floor;
    /// only when none does is the attempt declined, with each device's
    /// reason. A declined device is released before the next is opened.
    /// </para>
    /// <para>
    /// An init property, off by default, rather than a change of the
    /// default: a host that already relies on "any device that passes"
    /// keeps that, and one that wants the policy says so.
    /// </para>
    /// </remarks>
    public bool DeclineSlowDevicesUnlessPinned { get; init; }

    /// <summary>
    /// The upload-rate floor for <see cref="DeclineSlowDevicesUnlessPinned"/>,
    /// in bytes per second; 0 (what a defaulted value holds) means
    /// <see cref="DefaultMinUploadBytesPerSecond"/>.
    /// </summary>
    public double MinUploadBytesPerSecond { get; init; }

    /// <summary>
    /// 2.5 GB/s: the upload rate below which tracing on a device that
    /// stages its rays plausibly loses to the CPU KD tracer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ray was 32 bytes on the wire when this was set, and the KD tracer
    /// answers about 80 million rays a second on 32 threads, so it needs no
    /// more than 32 B × 80 M/s ≈ 2.56 GB/s worth of time for a batch; a
    /// device whose upload alone is slower than that cannot finish first,
    /// whatever its traversal speed (the answers' download only adds to it).
    /// Rays now travel in 24- or 28-byte records (<see cref="RayRecord"/>),
    /// which moves the break-even to about 2.1 GB/s; the floor is left where
    /// it was, which errs toward the CPU on a link in between. The case
    /// that set it: an RTX 2070 SUPER in a PCIe Gen2 x1 slot moved about
    /// 23 GB of 2fort's rays at about 0.67 GB/s, 34.3 s of copying against
    /// 0.44 s of tracing, for 43 to 47 s against the CPU's 9 s. A card on a
    /// full-width Gen3 or better link measures several times the floor, and
    /// one that reads rays in place is not probed at all.
    /// </para>
    /// <para>
    /// The number is rounded down to 2.5 so the floor errs toward using a
    /// GPU on a borderline link; a host whose CPU is much faster or slower
    /// than 32 threads can set <see cref="MinUploadBytesPerSecond"/>.
    /// </para>
    /// </remarks>
    public const double DefaultMinUploadBytesPerSecond = 2.5e9;

    /// <summary>Whether the options name the device, by name or by an index past the first.</summary>
    /// <remarks>
    /// Index 0 is not a pin: it is what a defaulted options value holds, and
    /// selection reads it as "rank every device", the same as −1.
    /// </remarks>
    public bool IsPinned => DeviceMatch is { Length: > 0 } || DeviceIndex > 0;

    /// <summary>The upload floor in force: <see cref="MinUploadBytesPerSecond"/>, or the default for 0.</summary>
    public double UploadFloor => MinUploadBytesPerSecond > 0 ? MinUploadBytesPerSecond : DefaultMinUploadBytesPerSecond;
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
    string? Reason)
{
    /// <summary>
    /// The self-test in full, passed or not: the device, the driver name and
    /// version, the Vulkan and conformance versions, the flags the scene and
    /// the rays were built with, and each ray's expected and actual hit,
    /// primitive, t and proceed iterations. Null only when no self-test ran.
    /// </summary>
    /// <remarks>
    /// A rejected device's <see cref="Reason"/> ends with this text too, so
    /// the one warning a host prints is enough to diagnose a device nobody
    /// here can run: that is what the NVIDIA rejection lacked. It names the
    /// driver build and nothing that identifies the machine.
    /// </remarks>
    public string? Detail { get; init; }

    /// <summary>
    /// The measured ray upload rate in bytes per second, or null when the
    /// device reads rays in place (nothing to upload, so nothing probed) or
    /// the self-test failed before the probe.
    /// </summary>
    public double? UploadBytesPerSecond { get; init; }
}

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
public sealed class VulkanRayTracer : IRayTracer, IGpuTraceStatistics, IDisposable
{
    /// <summary>The kernel's any-hit tmax shrink as float bits: <c>1 - 2^-24</c>, the largest float below 1.</summary>
    private const uint TmaxScaleBits = 0x3F7FFFFFu;

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

    /// <inheritdoc/>
    /// <remarks>
    /// The slab batcher's counters: slabs submitted, the host-side span with
    /// a slab on the device, the drainer's fence waits and the deepest the
    /// slot ring ran. Readable while the tracer works and after it is
    /// disposed, so the bench can print them once the compile has released
    /// the device.
    /// </remarks>
    public GpuTraceStatistics GpuStatistics => _batcher.Statistics;

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
    /// <remarks>
    /// <para>
    /// Pinned, or without
    /// <see cref="VulkanRayTracerOptions.DeclineSlowDevicesUnlessPinned"/>,
    /// this is one attempt on the device selection picks. Unpinned with the
    /// policy on, it WALKS the ranking: every ray-query device, most
    /// GPU-like first, until one is fast enough (<see cref="Walk"/>). "Use
    /// the faster one by default" means the next device, not the CPU, when
    /// the best-ranked one loses: the owner's machine lists an RTX 2070 on a
    /// Gen2 x1 link beside an RX 9070, and the 2070 being declined for its
    /// link must lead to the 9070.
    /// </para>
    /// <para>
    /// Each device is opened, tested and, when declined, released before
    /// the next is opened, so a walk never holds two devices and a declined
    /// one leaves nothing behind.
    /// </para>
    /// </remarks>
    internal static VulkanTracerAttempt TryCreate(
        ReadOnlyMemory<TracedTriangle> triangles,
        Action<TryCreateStage>? observe,
        Func<VulkanDevice> open,
        VulkanRayTracerOptions options,
        CancellationToken cancellationToken)
    {
        // Checked before anything native is opened, so a bad value cannot
        // leave a device behind.
        _ = SlotsFor(options);
        if (!options.DeclineSlowDevicesUnlessPinned || options.IsPinned)
        {
            return TryOne(triangles, observe, open, options, -1, cancellationToken);
        }

        IReadOnlyList<VulkanDeviceInfo> rows = VulkanDevice.ProbeDevices();
        int[] order = RankedDevices(rows);
        if (order.Length == 0)
        {
            // Nothing to walk (no loader, or no ray-query device): one
            // ordinary attempt says why in its own words.
            return TryOne(triangles, observe, open, options, -1, cancellationToken);
        }

        return Walk(
            order,
            physical => TryOne(triangles, observe, open, options, physical, cancellationToken),
            rows,
            cancellationToken);
    }

    /// <summary>
    /// The physical-device indices an unpinned walk tries, best first.
    /// </summary>
    /// <param name="rows">The probe's inventory.</param>
    /// <returns>Physical-device indices in the order to try them.</returns>
    /// <remarks>
    /// <para>
    /// Only ray-query devices are tried, ranked by, in turn:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// the device type, as selection ranks it: discrete, integrated,
    /// virtual, CPU, other. A discrete card beats an integrated one whatever
    /// their memory, because an integrated GPU's "memory" is a share of the
    /// system's;
    /// </description></item>
    /// <item><description>
    /// the largest device-local heap, larger first: more VRAM is the
    /// strongest portable sign of the bigger card within a type;
    /// </description></item>
    /// <item><description>
    /// the shader-core count the vendor's extension reports, more first.
    /// Core Vulkan has no clock speed, and no extension reports one
    /// portably, so cores stand in for throughput (the device's probe
    /// remarks list the sources). Counts from different vendors are not
    /// comparable, which is why this only breaks memory ties;
    /// </description></item>
    /// <item><description>the loader's enumeration order.</description></item>
    /// </list>
    /// <para>
    /// CPU-type devices stay in the list, last, so an unpinned walk that
    /// reaches one declines it with its reason instead of silently skipping
    /// it. The ranking only orders the walk: every device still has to pass
    /// the self-test and the upload floor to be used.
    /// </para>
    /// </remarks>
    internal static int[] RankedDevices(IReadOnlyList<VulkanDeviceInfo> rows) =>
    [
        .. rows
            .Where(r => r.RayQuery && r.Index >= 0)
            .Select((r, position) => (Row: r, Position: position, Score: VulkanDevice.DeviceScore(
                Enum.TryParse(r.DeviceType, out Silk.NET.Vulkan.PhysicalDeviceType t) ? t : Silk.NET.Vulkan.PhysicalDeviceType.Other)))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Row.DeviceLocalBytes)
            .ThenByDescending(x => x.Row.ShaderCores)
            .ThenBy(x => x.Position)
            .Select(x => x.Row.Index),
    ];

    /// <summary>
    /// Tries devices in <paramref name="order"/> until one is accepted, and
    /// otherwise declines with every device's reason on one line.
    /// </summary>
    /// <param name="order">Physical-device indices, best first.</param>
    /// <param name="tryOne">
    /// One attempt on a physical device. It must release the device itself
    /// when it does not hand it to a tracer, as <see cref="TryOne"/> does, so
    /// the next attempt opens on a clean slate.
    /// </param>
    /// <param name="inventory">The probe's rows, for the combined report.</param>
    /// <param name="cancellationToken">Checked before each attempt.</param>
    /// <returns>The first accepted attempt, or a decline naming each device's reason.</returns>
    internal static VulkanTracerAttempt Walk(
        IReadOnlyList<int> order,
        Func<int, VulkanTracerAttempt> tryOne,
        IReadOnlyList<VulkanDeviceInfo> inventory,
        CancellationToken cancellationToken)
    {
        List<string> declined = [];
        foreach (int physical in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VulkanTracerAttempt attempt = tryOne(physical);
            if (attempt.Success)
            {
                return attempt;
            }

            string name = inventory.FirstOrDefault(r => r.Index == physical).Name ?? $"device {physical}";
            declined.Add(DeclineLine(name, attempt.Report));
        }

        return new VulkanTracerAttempt(
            null,
            new VulkanDeviceReport(
                inventory,
                null,
                "no Vulkan device here is faster than the built-in CPU tracer: " + string.Join(" | ", declined)),
            false);
    }

    /// <summary>
    /// One declined device's reason, starting with the device's name exactly
    /// once: the reasons the attempt writes already start with it, and a
    /// reason that does not (a driver refusal) gets it prefixed.
    /// </summary>
    /// <param name="deviceName">The device the attempt was for.</param>
    /// <param name="report">The attempt's report.</param>
    /// <returns>The line.</returns>
    internal static string DeclineLine(string deviceName, VulkanDeviceReport report)
    {
        string reason = report.Failure ?? report.Selected?.Reason ?? "declined without a reason";
        return reason.StartsWith(deviceName, StringComparison.Ordinal) ? reason : deviceName + ": " + reason;
    }

    /// <summary>
    /// One attempt: open a device (the one <paramref name="physicalDevice"/>
    /// names, or the one selection picks for −1), gate it, and hand it to a
    /// tracer or release it.
    /// </summary>
    /// <param name="triangles">The scene.</param>
    /// <param name="observe">Called at each stage, or null.</param>
    /// <param name="open">Creates the device object.</param>
    /// <param name="options">Device pin, sizing and policy.</param>
    /// <param name="physicalDevice">A physical-device index to open exactly, or −1.</param>
    /// <param name="cancellationToken">Checked at each stage boundary.</param>
    /// <returns>The tracer or the reason it was not created.</returns>
    internal static VulkanTracerAttempt TryOne(
        ReadOnlyMemory<TracedTriangle> triangles,
        Action<TryCreateStage>? observe,
        Func<VulkanDevice> open,
        VulkanRayTracerOptions options,
        int physicalDevice,
        CancellationToken cancellationToken)
    {
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
            device.Construct(
                options.DeviceMatch, options.DeviceIndex, options.MaxRaysPerSlab, slots, physicalDevice: physicalDevice);
            cancellationToken.ThrowIfCancellationRequested();

            observe?.Invoke(TryCreateStage.Constructed);

            // A CPU implementation is turned away before the self-test: its
            // answer would not change the decision, and it is not free.
            if (SlowDeviceReason(options, device.DeviceName, device.IsCpuDevice, null) is { } cpu)
            {
                return new VulkanTracerAttempt(null, new VulkanDeviceReport(inventory, null, cpu), false);
            }

            // The gate runs before real geometry: two triangles whose answer
            // is known by construction, through every kernel mode.
            (bool ready, SelfTestOutcome outcome) = device.RunSelfTest();
            string detail = VulkanDevice.SelfTestDetail(device.Identity, outcome);
            SelfTestRecord record = new(
                ready,
                device.DeviceName,
                device.DriverName,
                outcome.ReadbackOk,
                outcome.AnyHitOk,
                outcome.ClosestOk,
                outcome.Iters,
                outcome.Candidates,
                ReasonFor(ready, device.DeviceName, outcome, detail))
            {
                Detail = detail,
            };
            observe?.Invoke(TryCreateStage.SelfTested);
            if (!ready)
            {
                return new VulkanTracerAttempt(null, new VulkanDeviceReport(inventory, record, null), false);
            }

            // Measured whether or not it decides anything, so a pinned
            // device's report still says how fast its link is.
            record = record with { UploadBytesPerSecond = device.MeasureUploadRate() };
            if (SlowDeviceReason(options, device.DeviceName, device.IsCpuDevice, record.UploadBytesPerSecond) is { } slow)
            {
                return new VulkanTracerAttempt(null, new VulkanDeviceReport(inventory, record, slow), false);
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

    /// <summary>
    /// Why a device that works should still not be used, under
    /// <see cref="VulkanRayTracerOptions.DeclineSlowDevicesUnlessPinned"/>:
    /// a CPU implementation of Vulkan, or an upload rate below the floor.
    /// </summary>
    /// <param name="options">The attempt's options: the policy switch, the pin and the floor.</param>
    /// <param name="deviceName">The device, quoted in the reason.</param>
    /// <param name="isCpuDevice">Whether the device's type is CPU.</param>
    /// <param name="uploadBytesPerSecond">The measured upload rate, or null when not measured.</param>
    /// <returns>The reason to decline, or null to use the device.</returns>
    /// <remarks>
    /// A pinned device, or any device when the policy is off, is always
    /// used. An unmeasured rate (rays read in place, or not probed yet)
    /// never declines: there is no upload to be slow. Each reason says what
    /// was seen and how to trace on the device anyway.
    /// </remarks>
    internal static string? SlowDeviceReason(
        VulkanRayTracerOptions options, string deviceName, bool isCpuDevice, double? uploadBytesPerSecond)
    {
        if (!options.DeclineSlowDevicesUnlessPinned || options.IsPinned)
        {
            return null;
        }

        if (isCpuDevice)
        {
            return $"{deviceName} is a CPU implementation of Vulkan; the built-in CPU tracer is faster, "
                + "so it is used instead. Pin the device by name to trace on it anyway";
        }

        if (uploadBytesPerSecond is double rate && rate < options.UploadFloor)
        {
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            return $"{deviceName}: rays upload at {(rate / 1e9).ToString("F2", inv)} GB/s, below the "
                + $"{(options.UploadFloor / 1e9).ToString("F2", inv)} GB/s at which tracing on it could beat the "
                + "CPU; the built-in CPU tracer is faster; pin the device by name to trace on it anyway";
        }

        return null;
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
    /// <param name="detail">
    /// The self-test's full detail (<see cref="SelfTestRecord.Detail"/>),
    /// appended to a rejection so the one warning a host prints carries
    /// every ray's answer and the driver build; null appends nothing.
    /// </param>
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
    internal static string? ReasonFor(bool ready, string deviceName, SelfTestOutcome o, string? detail = null)
    {
        if (ready)
        {
            return null;
        }

        string tail = detail is null ? string.Empty : ". Self-test detail: " + detail;
        if (!o.ReadbackOk)
        {
            return $"{deviceName}: the compute write/readback path itself is broken "
                + "(mode 4 wrote all-ones and they did not read back) — no ray answer from this "
                + "device can be trusted" + tail;
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
            + "Rejecting the device" + tail;
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
