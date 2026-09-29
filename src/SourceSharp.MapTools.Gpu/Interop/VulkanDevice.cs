//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SourceSharp.MapTools.Gpu.Interop;

/// <summary>
/// One Vulkan compute device with the ray-query pipeline, the scene's
/// acceleration structures, and a ring of pinned slab slots — everything the
/// traced batches need.
/// </summary>
/// <remarks>
/// <para>
/// Grown from an early ray-query feasibility spike, keeping its hard-won
/// fixes:
/// the vertex buffer carries <c>ACCELERATION_STRUCTURE_BUILD_INPUT_READ_ONLY
/// _BIT_KHR</c> (lavapipe silently builds an empty BLAS without it) and the
/// AS buffer carries <c>ACCELERATION_STRUCTURE_STORAGE_BIT_KHR</c>; compute
/// and transfer commands that touch the same buffer are separated by an
/// explicit <c>vkCmdPipelineBarrier</c> inside the command buffer rather than
/// by same-queue luck; and the probe-only environment hooks are gone — the
/// modes they selected are reached by explicit
/// <c>TraceMode</c> calls from the capability self-test.
/// </para>
/// <para>
/// The scene is two structures: a bottom-level one (the BLAS) holding the
/// triangles, and a top-level one (the TLAS) holding one identity instance
/// of it, and the kernel's descriptor is the TLAS. A ray query must be given
/// a top-level structure; the spec does not allow a bottom-level one in that
/// descriptor. RADV lays both levels out alike, so tracing a BLAS directly
/// works there, and it was the only arrangement this code had for a long
/// time. NVIDIA's driver starts every traversal at an instance, finds none
/// in a BLAS, and reports every ray as a miss: the RTX 2070 SUPER failed the
/// known-hit self-test with no candidates at all, and passes it with the
/// TLAS. On RADV the TLAS changes no answer (the output of a whole map is
/// byte-identical either way).
/// </para>
/// <para>
/// Threading: not thread-safe by design. One tracer instance owns one of
/// these, and its <c>SlabBatcher</c> makes every staging, submit and
/// completion from a single drainer at a time. Several slots may be in
/// flight on the one queue, each with its own buffers, descriptor set,
/// command buffer and fence, so nothing one slab's commands touch is touched
/// by another's. A batch's bytes depend only on its rays because each lane
/// of the kernel reads only its own ray.
/// </para>
/// </remarks>
internal sealed unsafe class VulkanDevice : IDisposable, ISlabDevice
{
    private const uint QueueFamilyIgnored = 0xFFFFFFFFu;
    private const nuint Vulkan13 = (1u << 22) | (13u << 12);

    /// <summary>Kernel workgroup size; the bit-out layout assumes 64 rays/workgroup.</summary>
    internal const int Invocations = 64;

    /// <summary>The any-hit tmax shrink as float bits: <c>1 - 2^-24</c> = 0x3F7FFFFF, the largest float below 1. A boundary hit goes to the miss side.</summary>
    internal const uint TmaxScaleBits = 0x3F7FFFFFu;

    private readonly Vk _vk = Vk.GetApi();
    private Instance _instance;
    private PhysicalDevice _physical;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private KhrAccelerationStructure _blasApi = null!;
    private KhrBufferDeviceAddress _bdaApi = null!;
    private PhysicalDeviceMemoryProperties _memory;
    private uint[] _typeFlags = [];
    private int[] _typeHeaps = [];
    private ulong[] _heapSizes = [];
    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;
    private DescriptorSetLayout _descriptorLayout;
    private DescriptorPool _descriptorPool;
    private CommandPool _commandPool;
    private Fence _fence;

    private GpuBuffer? _vertexBuffer;
    private GpuBuffer? _asBuffer;
    private GpuBuffer? _tlasBuffer;
    private SlabSlot[] _slots = [];
    private AccelerationStructureKHR _blasHandle;
    private AccelerationStructureKHR _tlasHandle;
    private uint _triangleCount;

    /// <summary>The structure the slots' descriptors were last pointed at.</summary>
    private AccelerationStructureKHR _bound;

    /// <summary>
    /// Whether the kernel's scene binding holds the TOP-level structure of
    /// the current scene: true after every successful scene load. Facts read
    /// it, because a directly bound BLAS is the one mistake no RADV answer
    /// shows.
    /// </summary>
    internal bool BindsTopLevel => _bound.Handle != 0 && _bound.Handle == _tlasHandle.Handle;

    /// <summary>Buffers allocated so far whose usage lets them be asked for a device address.</summary>
    internal int AddressableAllocations { get; private set; }

    /// <summary>
    /// Of <see cref="AddressableAllocations"/>, how many had their memory
    /// allocated with <c>DEVICE_ADDRESS</c> chained in. Facts check the two agree.
    /// </summary>
    internal int DeviceAddressFlaggedAllocations { get; private set; }

    /// <summary>
    /// The device's <c>minAccelerationStructureScratchOffsetAlignment</c>:
    /// every build's scratch address is rounded up to it.
    /// </summary>
    private ulong _scratchAlignment = 256;

    /// <summary>Vulkan 1.3, the API version the kernel's SPIR-V 1.6 module needs.</summary>
    internal const uint MinimumApiVersion = (1u << 22) | (3u << 12);

    /// <summary>Selected device name, e.g. <c>AMD Radeon RX 9070 XT (RADV GFX1201)</c>.</summary>
    public string DeviceName { get; private set; } = "?";

    /// <summary>Driver name and info string.</summary>
    public string DriverName { get; private set; } = "?";

    /// <summary>
    /// What the selected device and its driver say about themselves, for the
    /// self-test report; <see cref="DeviceIdentity.Unknown"/> until
    /// <see cref="Construct"/> selects one.
    /// </summary>
    public DeviceIdentity Identity { get; private set; } = DeviceIdentity.Unknown;

    /// <summary>Whether the selected device presents as a CPU rasteriser (llvmpipe).</summary>
    public bool IsCpuDevice { get; private set; }

    /// <summary>Bytes of Vulkan memory this device currently has allocated.</summary>
    private long _liveBytes;

    /// <summary>Bytes of Vulkan memory this device holds now; facts compare it across failed and clean builds.</summary>
    internal long LiveBytes => _liveBytes;

    /// <summary>
    /// Called at each <see cref="VulkanStep"/> this device reaches, or null.
    /// Facts throw from it to fail a build at a point that holds a
    /// temporary native object, and watch for its release.
    /// </summary>
    internal Action<VulkanStep>? Observe { get; set; }

    /// <summary>
    /// Temporary buffers (an upload's staging, a build's scratch) whose work
    /// failed: a submit that timed out may still be reading them, so they are
    /// not freed on the spot but with the device, once it is idle.
    /// </summary>
    private readonly List<GpuBuffer> _parked = [];

    /// <summary>
    /// Whether a set-up submit (scene upload, BLAS build) is on the device
    /// and its fence, <see cref="_fence"/>, has not been seen to signal: set
    /// once the submit is accepted, cleared once its wait succeeds. A wait
    /// that timed out leaves it set, and <see cref="Dispose"/> waits for it
    /// with the slots' fences.
    /// </summary>
    private bool _setupPending;

    private IFenceWaits? _fenceWaits;

    /// <summary>How long a slab or set-up submit may leave its fence unsignalled before the device is called hung.</summary>
    internal const ulong SubmitWaitNs = 120UL * 1_000_000_000;

    /// <summary>
    /// The fence calls this device makes; the driver's unless a fact has
    /// replaced them (see <see cref="IFenceWaits"/>).
    /// </summary>
    internal IFenceWaits FenceWaits
    {
        get => _fenceWaits ??= new DriverFenceWaits(this);
        set => _fenceWaits = value;
    }

    /// <summary>
    /// How long <see cref="Dispose"/> waits for work still on the device
    /// before it abandons the device instead of releasing it.
    /// </summary>
    /// <remarks>
    /// Work is still on the device at dispose only after something already
    /// went wrong: the batcher completes every slab it submits before the
    /// device is released, so a pending fence means a slab's or a set-up
    /// submit's own wait failed, usually after <see cref="SubmitWaitNs"/>.
    /// A short second chance is enough to catch a device that was merely
    /// slow; a hung one will not finish in any bound worth holding a
    /// service thread for.
    /// </remarks>
    internal TimeSpan DisposeWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether the last <see cref="Dispose"/> gave up waiting and left the
    /// device, its memory and the loader as they were. A later
    /// <see cref="Dispose"/> tries again.
    /// </summary>
    internal bool Abandoned { get; private set; }

    /// <summary>Bytes of Vulkan memory this device has ever had allocated at once.</summary>
    public long PeakAllocationBytes { get; private set; }

    /// <summary>The largest ray batch one slot's dispatch may hold, from the budget and the device's buffer-size limits.</summary>
    public int MaxSlabRays { get; private set; }

    /// <summary>How many slabs may be on the device at once.</summary>
    public int SlotCount => _slots.Length;

    /// <summary>Where the slab buffers live; see <see cref="SlabMemory"/>.</summary>
    public SlabMemoryLayout SlabLayout { get; private set; }

    /// <summary>How many triangles the BLAS was built from.</summary>
    public uint TriangleCount => _triangleCount;

    /// <summary>
    /// Creates the instance, selects and opens the device, builds the pipeline,
    /// and pins the slab slots.
    /// </summary>
    /// <param name="deviceMatch">Device-name substring, or null for the best-scoring device.</param>
    /// <param name="deviceIndex">Physical-device index pin (over ray-query-capable devices), or −1.</param>
    /// <param name="maxRaysPerSlab">
    /// Requested ray budget, shared by every slot (see <see cref="SlabMemory"/>);
    /// the device's limits can only lower it.
    /// </param>
    /// <param name="slots">Slabs that may be in flight at once, 1 to <see cref="SlabMemory.MaxSlots"/>.</param>
    /// <param name="forceStaged">
    /// Keep the upload and download copies even where the device could read
    /// rays and write answers in place; facts use it to compare the two paths.
    /// </param>
    /// <param name="physicalDevice">
    /// A physical-device index (loader order) to open exactly, overriding
    /// the pins, or −1. An unpinned walk uses it to try each device in turn.
    /// </param>
    /// <exception cref="VulkanException">Any driver refusal, with the failing call and result.</exception>
    /// <exception cref="NotSupportedException">No device matches and exposes ray query.</exception>
    public void Construct(
        string? deviceMatch,
        int deviceIndex,
        int maxRaysPerSlab,
        int slots = SlabMemory.DefaultSlots,
        bool forceStaged = false,
        int physicalDevice = -1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slots, SlabMemory.MaxSlots);
        byte* appName = (byte*)SilkMarshal.StringToPtr("maptools-gpu");
        ApplicationInfo app = new()
        {
            PApplicationName = appName,
            ApplicationVersion = 1,
            PEngineName = appName,
            EngineVersion = 1,
            ApiVersion = (uint)Vulkan13,
        };
        string[] instExts =
        [
            "VK_KHR_get_physical_device_properties2",
            "VK_KHR_portability_enumeration",
            "VK_KHR_external_memory_capabilities",
            "VK_KHR_external_semaphore_capabilities",
        ];
        byte** extNames = AllocNames(instExts);
        InstanceCreateInfo ici = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &app,
            EnabledExtensionCount = (uint)instExts.Length,
            PpEnabledExtensionNames = extNames,
        };
        Result r = _vk.CreateInstance(&ici, null, out _instance);
        FreeNames(extNames, instExts.Length);
        SilkMarshal.Free((nint)appName);
        ThrowOn(r, "vkCreateInstance (no Vulkan loader/driver on this machine?)");

        uint count = 0;
        _vk.EnumeratePhysicalDevices(_instance, &count, null);
        if (count == 0)
        {
            throw new NotSupportedException(
                "no Vulkan physical devices exposed; install a Vulkan driver (mesa-vulkan-drivers) "
                + "or run the CPU tracer");
        }

        PhysicalDevice[] devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
        {
            ThrowOn(_vk.EnumeratePhysicalDevices(_instance, &count, p), "vkEnumeratePhysicalDevices");
        }

        // The acceleration-structure properties give the scratch alignment
        // the builds need (the ray-tracing-PIPELINE properties this chain
        // used to query belong to an extension the tracer never enables);
        // the driver properties name the driver. A device without the
        // extension leaves the acceleration-structure structs untouched,
        // and such a device is passed over below before anything reads them.
        PhysicalDeviceAccelerationStructurePropertiesKHR asProps = new()
        {
            SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr,
        };
        PhysicalDeviceDriverProperties drv = new()
        {
            SType = StructureType.PhysicalDeviceDriverProperties,
        };
        PhysicalDeviceAccelerationStructureFeaturesKHR asFeatures = new()
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
        };
        PhysicalDeviceRayQueryFeaturesKHR rqFeatures = new()
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
            PNext = &asFeatures,
        };
        PhysicalDeviceFeatures2 features = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &rqFeatures,
        };
        PhysicalDeviceProperties2 props = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &asProps,
        };
        asProps.PNext = &drv;

        DeviceCandidate[] candidates = new DeviceCandidate[devices.Length];
        for (int i = 0; i < devices.Length; i++)
        {
            _vk.GetPhysicalDeviceProperties2(devices[i], &props);
            _vk.GetPhysicalDeviceFeatures2(devices[i], &features);
            candidates[i] = new DeviceCandidate(
                SilkMarshal.PtrToString((nint)props.Properties.DeviceName) ?? "unknown",
                props.Properties.DeviceType,
                Traceable(rqFeatures.RayQuery, asFeatures.AccelerationStructure));
        }

        int chosen = physicalDevice >= 0
            ? ChoosePhysical(candidates, physicalDevice)
            : ChooseDevice(candidates, deviceMatch, deviceIndex);
        if (chosen < 0)
        {
            string what = physicalDevice >= 0
                ? $"at physical-device index {physicalDevice}"
                : deviceIndex >= 0
                ? $"physical-device index {deviceIndex} among ray-query-capable devices"
                : $"with name matching '{deviceMatch}'";
            throw new NotSupportedException(
                $"no Vulkan device {what} exposes VK_KHR_ray_query "
                + "(llvmpipe does expose it; a driver without ray query cannot run this tracer)");
        }

        _physical = devices[chosen];
        _vk.GetPhysicalDeviceProperties2(_physical, &props);
        _vk.GetPhysicalDeviceFeatures2(_physical, &features);
        DeviceName = SilkMarshal.PtrToString((nint)props.Properties.DeviceName) ?? "unknown";
        DriverName = (SilkMarshal.PtrToString((nint)drv.DriverName) ?? "?") + " / "
                   + (SilkMarshal.PtrToString((nint)drv.DriverInfo) ?? "?");
        IsCpuDevice = props.Properties.DeviceType == PhysicalDeviceType.Cpu;
        Identity = new DeviceIdentity(
            DeviceName,
            props.Properties.VendorID,
            props.Properties.DeviceID,
            drv.DriverID.ToString(),
            SilkMarshal.PtrToString((nint)drv.DriverName) ?? "?",
            SilkMarshal.PtrToString((nint)drv.DriverInfo) ?? "?",
            props.Properties.DriverVersion,
            props.Properties.ApiVersion,
            string.Create(CultureInfo.InvariantCulture,
                $"{drv.ConformanceVersion.Major}.{drv.ConformanceVersion.Minor}."
                + $"{drv.ConformanceVersion.Subminor}.{drv.ConformanceVersion.Patch}"));

        // Before anything is created on it (RequireApiVersion says why).
        RequireApiVersion(DeviceName, props.Properties.ApiVersion);
        _scratchAlignment = Math.Max(1UL, asProps.MinAccelerationStructureScratchOffsetAlignment);
        // maxStorageBufferRange is the storage-buffer binding limit that
        // matters here; the spec guarantees >= 128 MiB on every conformant
        // device, and we clamp slab sizes to what the device reports.
        ulong maxStorage = Math.Max(134_217_728UL, props.Properties.Limits.MaxStorageBufferRange);

        _memory = _vk.GetPhysicalDeviceMemoryProperties(_physical);
        _typeFlags = new uint[_memory.MemoryTypeCount];
        _typeHeaps = new int[_memory.MemoryTypeCount];
        for (int i = 0; i < _typeFlags.Length; i++)
        {
            _typeFlags[i] = (uint)_memory.MemoryTypes[i].PropertyFlags;
            _typeHeaps[i] = (int)_memory.MemoryTypes[i].HeapIndex;
        }

        _heapSizes = new ulong[_memory.MemoryHeapCount];
        for (int i = 0; i < _heapSizes.Length; i++)
        {
            _heapSizes[i] = _memory.MemoryHeaps[i].Size;
        }

        uint qc = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physical, &qc, (QueueFamilyProperties*)null);
        QueueFamilyProperties[] qf = new QueueFamilyProperties[qc];
        fixed (QueueFamilyProperties* p = qf)
        {
            _vk.GetPhysicalDeviceQueueFamilyProperties(_physical, &qc, p);
        }

        _queueFamily = QueueFamilyIgnored;
        for (int i = 0; i < qf.Length; i++)
        {
            if ((qf[i].QueueFlags & QueueFlags.ComputeBit) != 0 && qf[i].QueueCount >= 1)
            {
                _queueFamily = (uint)i;
                break;
            }
        }

        if (_queueFamily == QueueFamilyIgnored)
        {
            throw new NotSupportedException($"{DeviceName} has no compute queue");
        }

        string[] want =
        [
            "VK_KHR_acceleration_structure",
            "VK_KHR_buffer_device_address",
            "VK_KHR_ray_query",
            "VK_KHR_deferred_host_operations",
        ];
        uint ec = 0;
        _vk.EnumerateDeviceExtensionProperties(_physical, (byte*)null, &ec, (ExtensionProperties*)null);
        ExtensionProperties[] ep = new ExtensionProperties[ec];
        fixed (ExtensionProperties* p = ep)
        {
            _vk.EnumerateDeviceExtensionProperties(_physical, (byte*)null, &ec, p);
        }

        HashSet<string> have = [];
        for (int i = 0; i < (int)ec; i++)
        {
            fixed (ExtensionProperties* epp = &ep[i])
            {
                have.Add(SilkMarshal.PtrToString((nint)epp) ?? string.Empty);
            }
        }

        foreach (string w in want)
        {
            if (!have.Contains(w))
            {
                throw new NotSupportedException($"{DeviceName} lacks required device extension {w}");
            }
        }

        float priority = 1f;
        DeviceQueueCreateInfo dq = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        PhysicalDeviceVulkan12Features vk12 = new()
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            BufferDeviceAddress = true,
        };

        // The acceleration-structure FEATURE must be enabled for any
        // structure to be created, built, sized or destroyed; this chain
        // once enabled only ray query, which radv and llvmpipe tolerated and
        // the validation layer reports on every structure call. Only the
        // two features the tracer uses are switched on from these structs,
        // not whatever else the query reported (capture-replay and host
        // builds change how a driver treats structures and are not wanted).
        PhysicalDeviceAccelerationStructureFeaturesKHR asEnable = new()
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            PNext = &vk12,
            AccelerationStructure = true,
        };
        PhysicalDeviceRayQueryFeaturesKHR rqEnable = new()
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
            PNext = &asEnable,
            RayQuery = true,
        };
        features.PNext = &rqEnable;
        byte** devExts = AllocNames(want);
        DeviceCreateInfo dci = new()
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &features,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &dq,
            EnabledExtensionCount = (uint)want.Length,
            PpEnabledExtensionNames = devExts,
        };
        r = _vk.CreateDevice(_physical, &dci, null, out _device);
        FreeNames(devExts, want.Length);
        ThrowOn(r, $"vkCreateDevice on {DeviceName}");

        _queue = _vk.GetDeviceQueue(_device, _queueFamily, 0);
        if (!_vk.TryGetDeviceExtension(_instance, _device, out _blasApi))
        {
            throw new NotSupportedException($"{DeviceName}: KhrAccelerationStructure binding unavailable");
        }

        if (!_vk.TryGetDeviceExtension(_instance, _device, out _bdaApi))
        {
            throw new NotSupportedException($"{DeviceName}: KhrBufferDeviceAddress binding unavailable");
        }

        CommandPoolCreateInfo cpci = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _queueFamily,
        };
        ThrowOn(_vk.CreateCommandPool(_device, &cpci, null, out _commandPool), "vkCreateCommandPool");

        FenceCreateInfo fci = new() { SType = StructureType.FenceCreateInfo };
        ThrowOn(_vk.CreateFence(_device, &fci, null, out _fence), "vkCreateFence");

        // The budget is split across the slots, and each slot's ray buffer
        // (32 B a ray, the largest of its buffers) must fit one storage
        // binding: the cap can only come DOWN from the caller's request, and
        // a buffer past maxStorageBufferRange is invalid usage.
        MaxSlabRays = SlabMemory.RaysPerSlot(maxRaysPerSlab, slots, maxStorage);

        byte[] spirv = ShadercCompiler.CompileGlsl(Kernels.RayGlsl, "vis.glsl");
        BuildPipeline(spirv);
        AllocateSlots(slots, forceStaged);
        CreateDescriptorSets();
    }

    /// <summary>
    /// Picks the device to open from what the loader listed: among the
    /// traceable ones, the one an index pin names, else the most GPU-like
    /// one whose name contains the match (any name when there is none).
    /// </summary>
    /// <param name="candidates">Every physical device, in the loader's order.</param>
    /// <param name="deviceMatch">Name substring pin, case-insensitive, or null/empty.</param>
    /// <param name="deviceIndex">Index pin among traceable devices, or −1; wins over the match.</param>
    /// <returns>The chosen device's position in <paramref name="candidates"/>, or −1.</returns>
    /// <remarks>
    /// <para>
    /// The ranking is discrete, integrated, virtual, CPU, other: the first
    /// of the best type wins. A CPU implementation such as llvmpipe is still
    /// a candidate here, so a pin can reach it and a machine that has
    /// nothing else still opens it; whether an UNPINNED attempt should use
    /// it is the tracer's policy
    /// (<see cref="VulkanRayTracerOptions.DeclineSlowDevicesUnlessPinned"/>),
    /// decided after this choice so the decline can name the device.
    /// </para>
    /// <para>
    /// An index pin skips that many traceable devices and ranks the rest,
    /// so it picks the best-ranked device from that index on. That is how
    /// the loop this was lifted out of behaved, and it is kept on purpose:
    /// a defaulted <see cref="VulkanRayTracerOptions"/> holds index 0, and
    /// hosts and facts that pass <c>default</c> rely on it meaning "the best
    /// device", not "whichever device the loader lists first".
    /// </para>
    /// </remarks>
    internal static int ChooseDevice(ReadOnlySpan<DeviceCandidate> candidates, string? deviceMatch, int deviceIndex)
    {
        int chosen = -1;
        int bestScore = int.MinValue;
        int traceableSeen = 0;
        for (int i = 0; i < candidates.Length; i++)
        {
            DeviceCandidate c = candidates[i];
            if (!c.Traceable)
            {
                continue;
            }

            if (deviceIndex >= 0)
            {
                // An index pin skips the traceable devices before it in
                // instance order, then ranks the rest (see the remarks).
                if (traceableSeen < deviceIndex)
                {
                    traceableSeen++;
                    continue;
                }
            }
            else if (deviceMatch is { Length: > 0 } && !c.Name.Contains(deviceMatch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int score = DeviceScore(c.Type);
            if (score > bestScore)
            {
                bestScore = score;
                chosen = i;
            }
        }

        return chosen;
    }

    /// <summary>The device at <paramref name="physicalDevice"/> when it is traceable, else −1.</summary>
    /// <param name="candidates">Every physical device, in the loader's order.</param>
    /// <param name="physicalDevice">The index to open.</param>
    /// <returns><paramref name="physicalDevice"/>, or −1 when it is out of range or cannot trace.</returns>
    internal static int ChoosePhysical(ReadOnlySpan<DeviceCandidate> candidates, int physicalDevice) =>
        physicalDevice < candidates.Length && candidates[physicalDevice].Traceable ? physicalDevice : -1;

    /// <summary>How GPU-like a device type is, for <see cref="ChooseDevice"/>: higher wins.</summary>
    /// <param name="type">The device's type.</param>
    /// <returns>5 discrete, 4 integrated, 3 virtual, 2 CPU, 1 other.</returns>
    internal static int DeviceScore(PhysicalDeviceType type) => type switch
    {
        PhysicalDeviceType.DiscreteGpu => 5,
        PhysicalDeviceType.IntegratedGpu => 4,
        PhysicalDeviceType.VirtualGpu => 3,
        PhysicalDeviceType.Cpu => 2,
        _ => 1,
    };

    /// <summary>
    /// Whether a device can hold and trace the scene: it must offer the
    /// ray-query feature AND the acceleration-structure feature.
    /// </summary>
    /// <param name="rayQuery">The device's <c>rayQuery</c> feature.</param>
    /// <param name="accelerationStructure">The device's <c>accelerationStructure</c> feature.</param>
    /// <returns>True when both are offered.</returns>
    /// <remarks>
    /// The FEATURE, not only the extension: creating or building a
    /// structure on a device that did not enable it is invalid usage. A
    /// device that offers ray query without it cannot hold a scene at all,
    /// so it is passed over like one without ray query.
    /// </remarks>
    internal static bool Traceable(bool rayQuery, bool accelerationStructure) =>
        rayQuery && accelerationStructure;

    /// <summary>
    /// Refuses a device whose API version is below <see cref="MinimumApiVersion"/>.
    /// </summary>
    /// <param name="deviceName">The device, for the message.</param>
    /// <param name="apiVersion">The version the device reports.</param>
    /// <exception cref="NotSupportedException">The version is below Vulkan 1.3.</exception>
    /// <remarks>
    /// The kernel is compiled for the Vulkan 1.3 environment (SPIR-V 1.6)
    /// and the device is created with the Vulkan 1.2 feature struct in its
    /// chain. A device reporting an older version may be handed neither:
    /// the module would be one its driver never promised to accept, which
    /// is undefined behaviour rather than an error, so it is refused here,
    /// with the version it reported, before anything is created on it.
    /// </remarks>
    internal static void RequireApiVersion(string deviceName, uint apiVersion)
    {
        if (apiVersion < MinimumApiVersion)
        {
            throw new NotSupportedException(
                $"{deviceName} reports Vulkan {DeviceIdentity.FormatApiVersion(apiVersion)}; "
                + "the ray-query kernel needs Vulkan 1.3 (update the driver)");
        }
    }

    /// <summary>
    /// Whether <paramref name="e"/> is the loader itself being absent, as
    /// <c>Vk.GetApi</c> reports it: Silk.NET throws
    /// <see cref="FileNotFoundException"/> when none of the loader's library
    /// names resolves (a machine with no Vulkan runtime, such as a stock
    /// macOS), and the runtime can raise <see cref="DllNotFoundException"/>.
    /// </summary>
    /// <param name="e">The exception from opening the API.</param>
    /// <returns>True when it means "no loader here".</returns>
    public static bool IsMissingLoader(Exception e) => e is FileNotFoundException or DllNotFoundException;

    /// <summary>Enumerated device inventory for diagnostics (creates no device).</summary>
    /// <returns>One row per physical device, ray-query flag included.</returns>
    public static List<VulkanDeviceInfo> ProbeDevices() => ProbeDevices([], null);

    /// <summary>
    /// <see cref="ProbeDevices()"/> with extra instance extensions to ask for
    /// and an observer. Facts ask for an extension no loader has, to make
    /// the instance fail for real, and throw from the observer to fail the
    /// enumeration; either way the instance and the loaded API are released.
    /// </summary>
    /// <param name="instanceExtensions">Instance extensions to enable, usually none.</param>
    /// <param name="observe">Called at each probe <see cref="VulkanStep"/>, or null.</param>
    /// <returns>One row per physical device, ray-query flag included.</returns>
    internal static List<VulkanDeviceInfo> ProbeDevices(string[] instanceExtensions, Action<VulkanStep>? observe)
    {
        List<VulkanDeviceInfo> rows = [];
        Vk vk;
        try
        {
            vk = Vk.GetApi();
        }
        catch (Exception e) when (IsMissingLoader(e))
        {
            rows.Add(new VulkanDeviceInfo(-1, "(no Vulkan loader)", "loader not found: " + e.Message, false));
            return rows;
        }

        // The API holds the loaded loader library; a probe runs per compile
        // attempt in a long-lived host, so it goes back however the probe ends.
        try
        {
            byte* appName = (byte*)SilkMarshal.StringToPtr("maptools-gpu-probe");
            byte** extNames = instanceExtensions.Length == 0 ? null : AllocNames(instanceExtensions);
            ApplicationInfo app = new()
            {
                PApplicationName = appName,
                ApplicationVersion = 1,
                PEngineName = appName,
                EngineVersion = 1,
                ApiVersion = (uint)Vulkan13,
            };
            InstanceCreateInfo ici = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &app,
                EnabledExtensionCount = (uint)instanceExtensions.Length,
                PpEnabledExtensionNames = extNames,
            };
            Result r = vk.CreateInstance(&ici, null, out Instance instance);
            if (extNames != null)
            {
                FreeNames(extNames, instanceExtensions.Length);
            }

            SilkMarshal.Free((nint)appName);
            if (r != Result.Success)
            {
                rows.Add(new VulkanDeviceInfo(-1, "(no Vulkan loader)", "instance failed: " + r, false));
                return rows;
            }

            try
            {
                observe?.Invoke(VulkanStep.ProbeInstanceCreated);
                uint count = 0;
                vk.EnumeratePhysicalDevices(instance, &count, null);
                PhysicalDevice[] devices = new PhysicalDevice[count];
                fixed (PhysicalDevice* p = devices)
                {
                    vk.EnumeratePhysicalDevices(instance, &count, p);
                }

                for (int i = 0; i < devices.Length; i++)
                {
                    PhysicalDeviceRayQueryFeaturesKHR rq = new()
                    {
                        SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
                    };
                    PhysicalDeviceFeatures2 f = new()
                    {
                        SType = StructureType.PhysicalDeviceFeatures2,
                        PNext = &rq,
                    };
                    PhysicalDeviceProperties2 props = new() { SType = StructureType.PhysicalDeviceProperties2 };
                    vk.GetPhysicalDeviceProperties2(devices[i], &props);
                    vk.GetPhysicalDeviceFeatures2(devices[i], &f);
                    rows.Add(new VulkanDeviceInfo(
                        i,
                        SilkMarshal.PtrToString((nint)props.Properties.DeviceName) ?? "unknown",
                        props.Properties.DeviceType.ToString(),
                        rq.RayQuery));
                }
            }
            finally
            {
                vk.DestroyInstance(instance, null);
                observe?.Invoke(VulkanStep.ProbeInstanceDestroyed);
            }

            return rows;
        }
        finally
        {
            vk.Dispose();
            observe?.Invoke(VulkanStep.ProbeApiReleased);
        }
    }

    private void BuildPipeline(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            ShaderModuleCreateInfo smci = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            ThrowOn(_vk.CreateShaderModule(_device, &smci, null, out ShaderModule module),
                "vkCreateShaderModule");
            // The pipeline keeps what it needs of the module and the entry
            // name, so both go once it is built, and on every failure before
            // that: the name is unmanaged memory no device teardown reclaims.
            byte* entry = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                Observe?.Invoke(VulkanStep.ShaderModuleCreated);
                DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[3];
                for (uint b = 0; b < 3; b++)
                {
                    bindings[b] = new DescriptorSetLayoutBinding
                    {
                        Binding = b,
                        DescriptorType = b == 2 ? DescriptorType.AccelerationStructureKhr : DescriptorType.StorageBuffer,
                        DescriptorCount = 1,
                        StageFlags = ShaderStageFlags.ComputeBit,
                    };
                }

                DescriptorSetLayoutCreateInfo dslci = new()
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = 3,
                    PBindings = bindings,
                };
                ThrowOn(_vk.CreateDescriptorSetLayout(_device, &dslci, null, out _descriptorLayout),
                    "vkCreateDescriptorSetLayout");
                PushConstantRange pcRange = new()
                {
                    StageFlags = ShaderStageFlags.ComputeBit,
                    Offset = 0,
                    Size = 32,
                };
                DescriptorSetLayout* sets = stackalloc DescriptorSetLayout[1] { _descriptorLayout };
                PipelineLayoutCreateInfo plci = new()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = 1,
                    PSetLayouts = sets,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pcRange,
                };
                ThrowOn(_vk.CreatePipelineLayout(_device, &plci, null, out _pipelineLayout),
                    "vkCreatePipelineLayout");
                PipelineShaderStageCreateInfo stage = new()
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entry,
                };
                ComputePipelineCreateInfo cpci = new()
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stage,
                    Layout = _pipelineLayout,
                };
                ThrowOn(_vk.CreateComputePipelines(_device, default, 1, &cpci, null, out _pipeline),
                    "vkCreateComputePipelines");
            }
            finally
            {
                SilkMarshal.Free((nint)entry);
                _vk.DestroyShaderModule(_device, module, null);
                Observe?.Invoke(VulkanStep.ShaderModuleReleased);
            }
        }
    }

    private sealed class GpuBuffer
    {
        public Silk.NET.Vulkan.Buffer Vk;

        public DeviceMemory Memory;

        public ulong DeviceAddress;

        public nint Mapped;
        public ulong Size;

        public ulong AllocationSize;
    }

    /// <summary>
    /// Creates a buffer and binds it to fresh memory of the first type the
    /// buffer may use that has <paramref name="required"/> (and
    /// <paramref name="preferred"/> when some type has that too), or of
    /// exactly <paramref name="forcedType"/>. Maps it when
    /// <paramref name="required"/> asks for host visibility. Nothing is left
    /// behind when a step fails.
    /// </summary>
    private GpuBuffer Allocate(
        ulong size,
        MemoryPropertyFlags required,
        BufferUsageFlags usage,
        bool deviceAddress,
        MemoryPropertyFlags preferred = 0,
        int forcedType = -1)
    {
        size = Math.Max(16UL, size);
        BufferCreateInfo bci = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        ThrowOn(_vk.CreateBuffer(_device, &bci, null, out Silk.NET.Vulkan.Buffer buffer), "vkCreateBuffer");
        DeviceMemory memory = default;
        try
        {
            MemoryRequirements req;
            _vk.GetBufferMemoryRequirements(_device, buffer, &req);
            int type = forcedType >= 0
                ? forcedType
                : SlabMemory.FindType(_typeFlags, req.MemoryTypeBits, (uint)required, (uint)(required | preferred));
            if (type < 0 || (req.MemoryTypeBits & (1u << type)) == 0)
            {
                throw new NotSupportedException($"{DeviceName}: no memory type with {required}");
            }

            MemoryAllocateFlagsInfo flagsInfo = new()
            {
                SType = StructureType.MemoryAllocateFlagsInfo,
                Flags = AllocateFlagsFor(usage),
            };
            MemoryAllocateInfo mai = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = flagsInfo.Flags != 0 ? &flagsInfo : null,
                AllocationSize = req.Size,
                MemoryTypeIndex = (uint)type,
            };
            ThrowOn(_vk.AllocateMemory(_device, &mai, null, out memory), "vkAllocateMemory");
            if ((usage & BufferUsageFlags.ShaderDeviceAddressBit) != 0)
            {
                AddressableAllocations++;
                if (mai.PNext == (void*)&flagsInfo && (flagsInfo.Flags & MemoryAllocateFlags.DeviceAddressBit) != 0)
                {
                    DeviceAddressFlaggedAllocations++;
                }
            }
            ThrowOn(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
            nint mapped = 0;
            if ((required & MemoryPropertyFlags.HostVisibleBit) != 0)
            {
                void* m = null;
                ThrowOn(_vk.MapMemory(_device, memory, 0, req.Size, 0, &m), "vkMapMemory");
                mapped = (nint)m;
            }

            ulong address = 0;
            if (deviceAddress)
            {
                BufferDeviceAddressInfo bda = new()
                {
                    SType = StructureType.BufferDeviceAddressInfo,
                    Buffer = buffer,
                };
                address = _bdaApi.GetBufferDeviceAddress(_device, &bda);
            }

            _liveBytes += (long)req.Size;
            PeakAllocationBytes = Math.Max(PeakAllocationBytes, _liveBytes);
            return new GpuBuffer
            {
                Vk = buffer,
                Memory = memory,
                DeviceAddress = address,
                Mapped = mapped,
                Size = size,
                AllocationSize = req.Size,
            };
        }
        catch
        {
            // Freeing memory unmaps it; the buffer goes first because it is
            // bound to that memory.
            _vk.DestroyBuffer(_device, buffer, null);
            if (memory.Handle != 0)
            {
                _vk.FreeMemory(_device, memory, null);
            }

            throw;
        }
    }

    /// <summary>The memory-allocate flags a buffer with <paramref name="usage"/> needs.</summary>
    /// <param name="usage">The buffer's usage.</param>
    /// <returns><c>DEVICE_ADDRESS</c> for a buffer whose device address is taken, otherwise none.</returns>
    /// <remarks>
    /// A buffer created with <c>SHADER_DEVICE_ADDRESS</c> usage must be bound
    /// to memory allocated with <c>VK_MEMORY_ALLOCATE_DEVICE_ADDRESS_BIT</c>.
    /// That covers the vertex, scratch and instance buffers whose addresses
    /// go to the builds, and the structures' own storage, whose address the
    /// TLAS instance holds. RADV makes every allocation addressable, so
    /// leaving the flag off worked there; the spec does not promise it.
    /// </remarks>
    internal static MemoryAllocateFlags AllocateFlagsFor(BufferUsageFlags usage) =>
        (usage & BufferUsageFlags.ShaderDeviceAddressBit) != 0 ? MemoryAllocateFlags.DeviceAddressBit : 0;

    /// <summary>Frees a temporary buffer whose work finished, or parks it until <see cref="Dispose"/> when it failed.</summary>
    /// <param name="b">The buffer.</param>
    /// <param name="finished">Whether the work that used it completed.</param>
    private void FreeOrPark(GpuBuffer b, bool finished)
    {
        if (finished)
        {
            Free(b);
        }
        else
        {
            _parked.Add(b);
        }
    }

    private void Free(GpuBuffer? b)
    {
        if (b is null)
        {
            return;
        }

        if (b.Mapped != 0)
        {
            _vk.UnmapMemory(_device, b.Memory);
        }

        _vk.DestroyBuffer(_device, b.Vk, null);
        _vk.FreeMemory(_device, b.Memory, null);
        _liveBytes -= (long)b.AllocationSize;
    }

    /// <summary>
    /// The <c>memoryTypeBits</c> of a buffer with <paramref name="usage"/>.
    /// The spec makes them the same for every buffer created with the same
    /// usage and flags, so a small probe buffer answers for the slab-sized
    /// ones.
    /// </summary>
    private uint AllowedTypes(BufferUsageFlags usage)
    {
        BufferCreateInfo bci = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = 256,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        ThrowOn(_vk.CreateBuffer(_device, &bci, null, out Silk.NET.Vulkan.Buffer probe), "vkCreateBuffer");
        MemoryRequirements req;
        _vk.GetBufferMemoryRequirements(_device, probe, &req);
        _vk.DestroyBuffer(_device, probe, null);
        return req.MemoryTypeBits;
    }

    /// <summary>
    /// One slab's resources: what the host packs rays into and reads answers
    /// from, what the kernel reads and writes (the same buffers when the
    /// layout is direct), the descriptor set naming them, a command buffer
    /// re-recorded per slab, and the fence that says the slab is done.
    /// </summary>
    private sealed class SlabSlot
    {
        public GpuBuffer? HostRays;
        public GpuBuffer? KernelRays;
        public GpuBuffer? KernelOut;
        public GpuBuffer? HostOut;
        public DescriptorSet Set;
        public CommandBuffer Commands;
        public Fence Fence;

        /// <summary>Submitted and not yet waited for: the slot's buffers belong to the device.</summary>
        public bool Pending;
    }

    /// <summary>
    /// Pins every slot's buffers, command buffer and fence at slab
    /// capacity, so a trace never allocates Vulkan memory per batch. Where
    /// the buffers live is <see cref="SlabMemory.Choose"/>'s decision. A
    /// failure part way leaves what was made in <see cref="_slots"/> for
    /// <see cref="Dispose"/>.
    /// </summary>
    private void AllocateSlots(int slots, bool forceStaged)
    {
        ulong slabRayBytes = (ulong)MaxSlabRays * 32UL;
        ulong slabOutBytes = (ulong)MaxSlabRays * 8UL; // closest: 2 words/ray; bits: 1/8 of that
        const BufferUsageFlags DirectRayUsage = BufferUsageFlags.StorageBufferBit;
        const BufferUsageFlags DirectOutUsage = BufferUsageFlags.StorageBufferBit;
        uint rayAllowed = AllowedTypes(DirectRayUsage);
        uint outAllowed = AllowedTypes(DirectOutUsage);
        SlabLayout = SlabMemory.Choose(
            _typeFlags, _typeHeaps, _heapSizes, rayAllowed, outAllowed,
            slabRayBytes * (ulong)slots, slabOutBytes * (ulong)slots, forceStaged);
        const uint InPlace = SlabMemory.DeviceLocal | SlabMemory.HostVisible | SlabMemory.HostCoherent;
        int directRayType = SlabLayout.DirectRays
            ? SlabMemory.FindRoomyType(_typeFlags, _typeHeaps, _heapSizes, rayAllowed, InPlace,
                slabRayBytes * (ulong)slots)
            : -1;
        int directOutType = SlabLayout.DirectOut
            ? SlabMemory.FindRoomyType(_typeFlags, _typeHeaps, _heapSizes, outAllowed,
                InPlace | SlabMemory.HostCached, slabOutBytes * (ulong)slots)
            : -1;
        const MemoryPropertyFlags HostCoherent = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

        _slots = new SlabSlot[slots];
        for (int i = 0; i < slots; i++)
        {
            SlabSlot slot = new();
            _slots[i] = slot;
            if (SlabLayout.DirectRays)
            {
                slot.HostRays = Allocate(slabRayBytes, HostCoherent, DirectRayUsage, deviceAddress: false,
                    forcedType: directRayType);
                slot.KernelRays = slot.HostRays;
            }
            else
            {
                slot.KernelRays = Allocate(slabRayBytes, MemoryPropertyFlags.DeviceLocalBit,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, deviceAddress: false);
                slot.HostRays = Allocate(slabRayBytes, HostCoherent, BufferUsageFlags.TransferSrcBit,
                    deviceAddress: false);
            }

            if (SlabLayout.DirectOut)
            {
                slot.HostOut = Allocate(slabOutBytes, HostCoherent, DirectOutUsage, deviceAddress: false,
                    forcedType: directOutType);
                slot.KernelOut = slot.HostOut;
            }
            else
            {
                slot.KernelOut = Allocate(slabOutBytes, MemoryPropertyFlags.DeviceLocalBit,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit, deviceAddress: false);

                // The host reads every answer back: cached memory makes those
                // reads ordinary loads instead of uncached bus reads.
                slot.HostOut = Allocate(slabOutBytes, HostCoherent, BufferUsageFlags.TransferDstBit,
                    deviceAddress: false, preferred: MemoryPropertyFlags.HostCachedBit);
            }

            Observe?.Invoke(VulkanStep.SlotBuffersAllocated);
            CommandBufferAllocateInfo cbai = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            ThrowOn(_vk.AllocateCommandBuffers(_device, &cbai, out slot.Commands), "vkAllocateCommandBuffers");
            FenceCreateInfo fci = new() { SType = StructureType.FenceCreateInfo };
            ThrowOn(_vk.CreateFence(_device, &fci, null, out slot.Fence), "vkCreateFence");
        }
    }

    private void FreeSlots()
    {
        foreach (SlabSlot slot in _slots)
        {
            if (slot is null)
            {
                continue;
            }

            if (!ReferenceEquals(slot.KernelRays, slot.HostRays))
            {
                Free(slot.KernelRays);
            }

            Free(slot.HostRays);
            if (!ReferenceEquals(slot.KernelOut, slot.HostOut))
            {
                Free(slot.KernelOut);
            }

            Free(slot.HostOut);
            if (slot.Fence.Handle != 0)
            {
                _vk.DestroyFence(_device, slot.Fence, null);
            }

            // The command buffer goes with the pool.
        }

        _slots = [];
    }


    /// <summary>
    /// Uploads the triangles and builds the opaque BLAS once, in
    /// triangle-array form so primitive index == caller array position.
    /// </summary>
    /// <param name="vertices">XYZ triples, three per triangle, caller order.</param>
    /// <exception cref="ArgumentException">The vertex span is not a multiple of nine floats.</exception>
    /// <exception cref="VulkanException">The driver refused the build.</exception>
    public void LoadScene(ReadOnlySpan<float> vertices)
    {
        if (vertices.Length % 9 != 0)
        {
            throw new ArgumentException("vertices must be XYZ triples", nameof(vertices));
        }

        // A second LoadScene — the self-test's two-triangle scene and then
        // the real one — replaces the vertex buffer and both structures;
        // release the old ones instead of leaking them. The queue is idle
        // here (the last dispatch's fence signalled), so the descriptor
        // refresh in BuildScene is safe.
        ReleaseScene();
        if (_vertexBuffer is not null)
        {
            Free(_vertexBuffer);
            _vertexBuffer = null;
        }

        _triangleCount = (uint)(vertices.Length / 9);
        ulong bytes = (ulong)vertices.Length * sizeof(float);
        _vertexBuffer = Allocate(bytes, MemoryPropertyFlags.DeviceLocalBit,
            BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.StorageBufferBit
            | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr
            | BufferUsageFlags.TransferDstBit,
            deviceAddress: true);

        GpuBuffer staging = Allocate(bytes,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            BufferUsageFlags.TransferSrcBit,
            deviceAddress: false);
        bool uploaded = false;
        try
        {
            Observe?.Invoke(VulkanStep.StagingAllocated);
            vertices.CopyTo(new Span<float>((void*)staging.Mapped, vertices.Length));

            // One command buffer: the upload's write, then the ordering that makes
            // it visible to the AS build's read (same queue is not a
            // synchronisation promise).
            CommandBuffer upload = Begin();
            BufferCopy region = new() { SrcOffset = 0, DstOffset = 0, Size = bytes };
            _vk.CmdCopyBuffer(upload, staging.Vk, _vertexBuffer.Vk, 1, &region);
            Barrier(upload, UploadToBuild);
            End(upload);
            Submit(upload);
            uploaded = true;
        }
        finally
        {
            FreeOrPark(staging, uploaded);
        }

        BuildScene();
    }

    /// <summary>Destroys both structures and frees their storage; the TLAS first, since it refers to the BLAS.</summary>
    private void ReleaseScene()
    {
        // The descriptors name the old TLAS until the next build rebinds
        // them; nothing dispatches in between (a failed load declines the
        // device), and the flag says so.
        _bound = default;
        if (_tlasHandle.Handle != 0)
        {
            _blasApi.DestroyAccelerationStructure(_device, _tlasHandle, null);
            _tlasHandle = default;
        }

        Free(_tlasBuffer);
        _tlasBuffer = null;
        if (_blasHandle.Handle != 0)
        {
            _blasApi.DestroyAccelerationStructure(_device, _blasHandle, null);
            _blasHandle = default;
        }

        Free(_asBuffer);
        _asBuffer = null;
    }

    private void BuildScene()
    {
        AccelerationStructureGeometryTrianglesDataKHR tris = new()
        {
            // The nested struct's sType is required like any other; an
            // object initializer leaves it zero, which the validation layer
            // reports on every build and a driver is free to reject.
            SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
            VertexFormat = Format.R32G32B32Sfloat,
            VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = _vertexBuffer!.DeviceAddress },
            IndexType = IndexType.NoneKhr,
            VertexStride = 12,
            MaxVertex = (_triangleCount * 3) - 1,
        };
        // On the stack, not in a field: the driver reads it through the
        // pointer below, and a field of this object moves whenever a
        // compacting GC runs between taking the address and the call. The
        // build then reads whatever lies there as its geometry, and the
        // kernel later traverses a BLAS full of wild addresses.
        AccelerationStructureGeometryKHR geometry = new()
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.TrianglesKhr,
            // Opaque: coverage callbacks are not ported — the KD tracer treats
            // transparent triangles as opaque too (TestLines' own docs say
            // so), and the parity contract is against that behaviour.
            Flags = GeometryFlagsKHR.OpaqueBitKhr,
        };
        geometry.Geometry.Triangles = tris;

        AccelerationStructureBuildGeometryInfoKHR info = new()
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1,
            PGeometries = &geometry,
        };

        uint primCount = _triangleCount;
        AccelerationStructureBuildSizesInfoKHR sizes = _blasApi.GetAccelerationStructureBuildSizes(
            _device, AccelerationStructureBuildTypeKHR.DeviceKhr, &info, &primCount);
        ulong asSize = Math.Max(16UL, sizes.AccelerationStructureSize);

        // ACCELERATION_STRUCTURE_STORAGE_BIT_KHR on the backing buffer is spec
        // required; lavapipe silently produced an empty BLAS without it (the
        // all-miss blocker's second contributor). SHADER_DEVICE_ADDRESS is
        // there because the TLAS instance refers to the BLAS by address.
        _asBuffer = Allocate(asSize, MemoryPropertyFlags.DeviceLocalBit,
            BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.StorageBufferBit
            | BufferUsageFlags.AccelerationStructureStorageBitKhr,
            deviceAddress: false);
        GpuBuffer scratch = Allocate(ScratchSize(sizes.BuildScratchSize, _scratchAlignment), MemoryPropertyFlags.DeviceLocalBit,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            deviceAddress: true);
        GpuBuffer? instances = null;
        GpuBuffer? topScratch = null;
        bool built = false;
        try
        {
            Observe?.Invoke(VulkanStep.ScratchAllocated);
            AccelerationStructureCreateInfoKHR asci = new()
            {
                SType = StructureType.AccelerationStructureCreateInfoKhr,
                Buffer = _asBuffer.Vk,
                Size = asSize,
                Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            };
            ThrowOn(_blasApi.CreateAccelerationStructure(_device, &asci, null, out _blasHandle),
                "vkCreateAccelerationStructureKHR");
            info.DstAccelerationStructure = _blasHandle;
            info.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = AlignUp(scratch.DeviceAddress, _scratchAlignment) };

            // The TLAS: one instance of the BLAS, identity transform, every
            // ray's mask, no culling (the kernel asks for none either). The
            // BLAS's address is valid once it is created, before its build.
            AccelerationStructureDeviceAddressInfoKHR blasAddress = new()
            {
                SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
                AccelerationStructure = _blasHandle,
            };
            AccelerationStructureInstanceKHR instance = SceneInstance(
                _blasApi.GetAccelerationStructureDeviceAddress(_device, &blasAddress));
            instances = Allocate((ulong)sizeof(AccelerationStructureInstanceKHR),
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
                deviceAddress: true);
            *(AccelerationStructureInstanceKHR*)instances.Mapped = instance;

            AccelerationStructureGeometryKHR top = new()
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.InstancesKhr,
                Flags = GeometryFlagsKHR.OpaqueBitKhr,
            };
            top.Geometry.Instances = new AccelerationStructureGeometryInstancesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                ArrayOfPointers = false,
                Data = new DeviceOrHostAddressConstKHR { DeviceAddress = instances.DeviceAddress },
            };
            AccelerationStructureBuildGeometryInfoKHR topInfo = new()
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.TopLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
                Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                GeometryCount = 1,
                PGeometries = &top,
            };
            uint instanceCount = 1;
            AccelerationStructureBuildSizesInfoKHR topSizes = _blasApi.GetAccelerationStructureBuildSizes(
                _device, AccelerationStructureBuildTypeKHR.DeviceKhr, &topInfo, &instanceCount);
            ulong tlasSize = Math.Max(16UL, topSizes.AccelerationStructureSize);
            _tlasBuffer = Allocate(tlasSize, MemoryPropertyFlags.DeviceLocalBit,
                BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.StorageBufferBit
                | BufferUsageFlags.AccelerationStructureStorageBitKhr,
                deviceAddress: false);
            topScratch = Allocate(ScratchSize(topSizes.BuildScratchSize, _scratchAlignment), MemoryPropertyFlags.DeviceLocalBit,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                deviceAddress: true);
            Observe?.Invoke(VulkanStep.TopLevelScratchAllocated);
            AccelerationStructureCreateInfoKHR topCreate = new()
            {
                SType = StructureType.AccelerationStructureCreateInfoKhr,
                Buffer = _tlasBuffer.Vk,
                Size = tlasSize,
                Type = AccelerationStructureTypeKHR.TopLevelKhr,
            };
            ThrowOn(_blasApi.CreateAccelerationStructure(_device, &topCreate, null, out _tlasHandle),
                "vkCreateAccelerationStructureKHR");
            topInfo.DstAccelerationStructure = _tlasHandle;
            topInfo.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = AlignUp(topScratch.DeviceAddress, _scratchAlignment) };

            // Both builds in one command buffer: the TLAS build reads the
            // BLAS the first build wrote, so a barrier orders them on the
            // device (submission order alone makes no memory promise), and
            // the second barrier makes the TLAS visible to the kernel's
            // ray queries, which read it as an acceleration structure.
            AccelerationStructureBuildRangeInfoKHR range = new() { PrimitiveCount = _triangleCount };
            AccelerationStructureBuildRangeInfoKHR* rangePtr = &range;
            AccelerationStructureBuildRangeInfoKHR topRange = new() { PrimitiveCount = 1 };
            AccelerationStructureBuildRangeInfoKHR* topRangePtr = &topRange;
            CommandBuffer cmd = Begin();
            _blasApi.CmdBuildAccelerationStructures(cmd, 1, &info, &rangePtr);
            Barrier(cmd, BlasToTlas);
            _blasApi.CmdBuildAccelerationStructures(cmd, 1, &topInfo, &topRangePtr);
            Barrier(cmd, BuildToTrace);
            End(cmd);
            Submit(cmd);
            built = true;
        }
        finally
        {
            FreeOrPark(scratch, built);
            if (instances is not null)
            {
                FreeOrPark(instances, built);
            }

            if (topScratch is not null)
            {
                FreeOrPark(topScratch, built);
            }
        }

        UpdateSceneBinding();
    }

    /// <summary>
    /// The scene's one TLAS instance: the BLAS at
    /// <paramref name="blasAddress"/> under the identity transform, custom
    /// index 0, mask 0xFF (every ray's cull mask is 0xFF, so it is never
    /// masked out), binding-table offset 0, and facing culling disabled
    /// (the kernel asks for no culling either, so no driver's facing
    /// convention can enter into an answer).
    /// </summary>
    /// <param name="blasAddress">The BLAS's device address.</param>
    /// <returns>The instance.</returns>
    /// <remarks>
    /// The identity transform multiplies each ray component by exactly 1 and
    /// adds exact zeros, so primitive indices and t stay the BLAS's own; only
    /// the sign of a zero component can change, and the triangle test's
    /// comparisons treat the two zeros alike. That is why RADV's output is
    /// byte-identical with the TLAS.
    /// </remarks>
    internal static AccelerationStructureInstanceKHR SceneInstance(ulong blasAddress)
    {
        AccelerationStructureInstanceKHR instance = new()
        {
            InstanceCustomIndex = 0,
            Mask = 0xFF,
            InstanceShaderBindingTableRecordOffset = 0,
            Flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr,
            AccelerationStructureReference = blasAddress,
        };
        instance.Transform.Matrix[0] = 1f;
        instance.Transform.Matrix[5] = 1f;
        instance.Transform.Matrix[10] = 1f;
        return instance;
    }

    /// <summary>
    /// The bytes to allocate for a build's scratch: the size it needs,
    /// rounded up to 256 bytes (and never zero), plus room to round the
    /// buffer's address up to <paramref name="alignment"/>.
    /// </summary>
    /// <param name="required">The build's scratch size.</param>
    /// <param name="alignment"><c>minAccelerationStructureScratchOffsetAlignment</c>, a power of two.</param>
    /// <returns>The allocation size.</returns>
    /// <remarks>
    /// The spec asks each build's scratch ADDRESS to be a multiple of the
    /// device's scratch alignment. A fresh buffer's address follows its
    /// memory requirements, which drivers commonly make large enough, but
    /// nothing ties the two numbers together, so the build rounds the
    /// address up with <see cref="AlignUp"/> and the slack is allocated for
    /// it. The size alone was rounded before, and the address never looked
    /// at.
    /// </remarks>
    internal static ulong ScratchSize(ulong required, ulong alignment) =>
        Math.Max(16UL, (required + 255UL) & ~255UL) + (Math.Max(1UL, alignment) - 1);

    /// <summary>Rounds <paramref name="value"/> up to a multiple of <paramref name="alignment"/>.</summary>
    /// <param name="value">The value.</param>
    /// <param name="alignment">A power of two; 0 counts as 1.</param>
    /// <returns>The smallest multiple of the alignment not below the value.</returns>
    internal static ulong AlignUp(ulong value, ulong alignment)
    {
        ulong a = Math.Max(1UL, alignment);
        return (value + (a - 1)) & ~(a - 1);
    }

    /// <summary>
    /// The upload-to-build barrier: the staging copy's write, before the
    /// BLAS build reads the vertices.
    /// </summary>
    /// <remarks>
    /// A build reads its geometry input as a SHADER read at the build
    /// stage. The acceleration-structure read access, which this barrier
    /// once named alone, covers only reads of acceleration structures, so
    /// nothing promised the build would see the uploaded vertices. Both are
    /// named; the second is harmless and keeps the old promise.
    /// </remarks>
    internal static BarrierMasks UploadToBuild => new(
        PipelineStageFlags.TransferBit,
        AccessFlags.TransferWriteBit,
        PipelineStageFlags.AccelerationStructureBuildBitKhr,
        AccessFlags.ShaderReadBit | AccessFlags.AccelerationStructureReadBitKhr);

    /// <summary>The BLAS build's write, before the TLAS build reads the BLAS through its instance.</summary>
    internal static BarrierMasks BlasToTlas => new(
        PipelineStageFlags.AccelerationStructureBuildBitKhr,
        AccessFlags.AccelerationStructureWriteBitKhr,
        PipelineStageFlags.AccelerationStructureBuildBitKhr,
        AccessFlags.AccelerationStructureReadBitKhr);

    /// <summary>The builds' writes, before the kernel's ray queries read the scene.</summary>
    /// <remarks>
    /// A ray query's traversal is an ACCELERATION_STRUCTURE_READ at the
    /// stage of the shader that runs it, here compute. This barrier once
    /// named SHADER_READ, which does not cover that access.
    /// </remarks>
    internal static BarrierMasks BuildToTrace => new(
        PipelineStageFlags.AccelerationStructureBuildBitKhr,
        AccessFlags.AccelerationStructureWriteBitKhr,
        PipelineStageFlags.ComputeShaderBit,
        AccessFlags.AccelerationStructureReadBitKhr);

    /// <summary>
    /// Creates one descriptor set per slot, binding that slot's kernel ray
    /// and output buffers. Runs once at construction. The TLAS binding is
    /// written by <see cref="UpdateSceneBinding"/> on each build (self-test
    /// scene, then the real scene), which always comes before the first
    /// dispatch.
    /// </summary>
    private void CreateDescriptorSets()
    {
        uint slots = (uint)_slots.Length;
        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[2]
        {
            new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 2 * slots },
            new DescriptorPoolSize { Type = DescriptorType.AccelerationStructureKhr, DescriptorCount = slots },
        };
        DescriptorPoolCreateInfo dpci = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
            MaxSets = slots,
        };
        ThrowOn(_vk.CreateDescriptorPool(_device, &dpci, null, out _descriptorPool),
            "vkCreateDescriptorPool");
        Silk.NET.Vulkan.DescriptorBufferInfo* infos = stackalloc Silk.NET.Vulkan.DescriptorBufferInfo[2];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
        foreach (SlabSlot slot in _slots)
        {
            DescriptorSetLayout layout = _descriptorLayout;
            DescriptorSetAllocateInfo dsai = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };
            ThrowOn(_vk.AllocateDescriptorSets(_device, &dsai, out slot.Set), "vkAllocateDescriptorSets");
            infos[0] = new Silk.NET.Vulkan.DescriptorBufferInfo
            {
                Buffer = slot.KernelRays!.Vk,
                Offset = 0,
                Range = slot.KernelRays.Size,
            };
            infos[1] = new Silk.NET.Vulkan.DescriptorBufferInfo
            {
                Buffer = slot.KernelOut!.Vk,
                Offset = 0,
                Range = slot.KernelOut.Size,
            };
            for (int i = 0; i < 2; i++)
            {
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = slot.Set,
                    DstBinding = (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = infos + i,
                };
            }

            _vk.UpdateDescriptorSets(_device, 2, writes, 0, null);
        }
    }

    /// <summary>
    /// Points every slot's set at the TLAS after a build. Legal because no
    /// slot is in flight then: builds happen only while the device is being
    /// set up, and every self-test dispatch has been waited for.
    /// </summary>
    private void UpdateSceneBinding()
    {
        fixed (AccelerationStructureKHR* tlasPtr = &_tlasHandle)
        {
            foreach (SlabSlot slot in _slots)
            {
                WriteDescriptorSetAccelerationStructureKHR asWrite = new()
                {
                    SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
                    AccelerationStructureCount = 1,
                    PAccelerationStructures = tlasPtr,
                };
                WriteDescriptorSet asBinding = new()
                {
                    SType = StructureType.WriteDescriptorSet,
                    PNext = &asWrite,
                    DstSet = slot.Set,
                    DstBinding = 2,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.AccelerationStructureKhr,
                };
                _vk.UpdateDescriptorSets(_device, 1, &asBinding, 0, null);
            }
        }

        _bound = _tlasHandle;
    }

    /// <summary>
    /// Hands out a slot's pinned host memory for <paramref name="rayCount"/>
    /// rays in the 8-float wire layout — two vec4 per ray:
    /// <c>(ox,oy,oz,0)</c> and <c>(dx,dy,dz,tmax)</c>. The caller packs
    /// straight into it, so a batch is copied at most once more, by the
    /// upload copy, and not at all when the kernel reads it in place.
    /// </summary>
    /// <param name="slot">The slot; not in flight.</param>
    /// <param name="rayCount">Rays the slot's next dispatch will carry; at most <see cref="MaxSlabRays"/>.</param>
    /// <returns>A span of <c>8 * rayCount</c> floats to fill.</returns>
    /// <exception cref="VulkanException">
    /// The slab exceeds the pinned buffers, or the slot's last slab failed
    /// its wait and still has not finished.
    /// </exception>
    public Span<float> StageRays(int slot, int rayCount)
    {
        SlabSlot s = _slots[slot];
        Retire(s);
        ulong rayBytes = (ulong)rayCount * 32UL;
        if (rayBytes > s.HostRays!.Size)
        {
            throw new VulkanException(Result.ErrorFragmentation,
                $"a {rayCount}-ray slab exceeds the pinned slab buffers ({MaxSlabRays} rays)", null);
        }

        return new Span<float>((void*)s.HostRays.Mapped, rayCount * 8);
    }

    /// <summary>
    /// Records and submits one slot's slab as a single command buffer and
    /// returns without waiting: the upload copy, the dispatch and the
    /// download copy, each ordering between them an explicit barrier.
    /// </summary>
    /// <param name="slot">The slot staged last; not in flight.</param>
    /// <param name="mode">Kernel mode: 0 any-hit, 1 closest, 4 readback sanity, 5 telemetry.</param>
    /// <param name="rayCount">Rays staged.</param>
    /// <param name="outWordCount">Raw out words: 2/ray for modes 1/5, 2/workgroup for 0/4.</param>
    /// <param name="tminBits">Ray epsilon as float bits.</param>
    /// <param name="tmaxScaleBits">Any-hit tmax scale (<c>1 - 2^-24</c>) as float bits.</param>
    /// <exception cref="VulkanException">
    /// Recording or submission failed (the slot stays free), or the slot's
    /// last slab failed its wait and still has not finished.
    /// </exception>
    /// <remarks>
    /// <para>
    /// One submit and one fence per slab, where there used to be two of
    /// each: a submit is a kernel call and a fence wait a scheduler round
    /// trip, so the second pair cost as much as a small slab's whole trace.
    /// </para>
    /// <para>
    /// Barriers, in recording order: the upload copy's write before the
    /// kernel's ray reads; the kernel's output writes before the download
    /// copy's reads; the download's writes before the host's reads. When the
    /// kernel reads rays in place, the host's writes need no barrier (a
    /// queue submit makes every earlier host write to coherent memory
    /// visible to the device); when it writes answers in place, its writes
    /// go straight to the host barrier. A slot's buffers are never touched
    /// by another slot's commands, and a slot is re-recorded only after its
    /// fence has been waited on, so slabs in flight together need no
    /// ordering between them.
    /// </para>
    /// </remarks>
    public void Submit(int slot, int mode, int rayCount, int outWordCount, uint tminBits, uint tmaxScaleBits)
    {
        SlabSlot s = _slots[slot];
        Retire(s);

        // A partial tail workgroup is fine and normal: the kernel guards every
        // lane by index < rayCount, so the dispatch covers ceil(rays/64) and
        // the tail lanes run no query at all. Only the slab cap is a hard
        // bound.
        ulong rayBytes = (ulong)rayCount * 32UL;
        ulong outBytes = (ulong)outWordCount * sizeof(uint);
        if (rayBytes > s.HostRays!.Size || outBytes > s.HostOut!.Size)
        {
            throw new VulkanException(Result.ErrorFragmentation,
                $"a {rayCount}-ray slab exceeds the pinned slab buffers ({MaxSlabRays} rays)", null);
        }

        CommandBuffer cmd = s.Commands;

        // The pool lets buffers reset one by one; an explicit reset also
        // clears a recording an earlier failure left half done.
        ThrowOn(_vk.ResetCommandBuffer(cmd, 0), "vkResetCommandBuffer");
        CommandBufferBeginInfo cbbi = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        ThrowOn(_vk.BeginCommandBuffer(cmd, &cbbi), "vkBeginCommandBuffer");
        if (!ReferenceEquals(s.HostRays, s.KernelRays))
        {
            BufferCopy upload = new() { SrcOffset = 0, DstOffset = 0, Size = rayBytes };
            _vk.CmdCopyBuffer(cmd, s.HostRays.Vk, s.KernelRays!.Vk, 1, &upload);
            Barrier(
                cmd,
                PipelineStageFlags.TransferBit,
                AccessFlags.TransferWriteBit,
                PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit);
        }

        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        DescriptorSet set = s.Set;
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        uint* pc = stackalloc uint[8];
        pc[0] = (uint)mode;
        pc[1] = (uint)rayCount;
        pc[2] = _triangleCount;
        pc[3] = 0;
        pc[4] = tminBits;
        pc[5] = tmaxScaleBits;
        pc[6] = 0;
        pc[7] = 0;
        _vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, 32, pc);
        _vk.CmdDispatch(cmd, (uint)((rayCount + Invocations - 1) / Invocations), 1, 1);
        if (!ReferenceEquals(s.HostOut, s.KernelOut))
        {
            Barrier(
                cmd,
                PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderWriteBit,
                PipelineStageFlags.TransferBit,
                AccessFlags.TransferReadBit);
            BufferCopy download = new() { SrcOffset = 0, DstOffset = 0, Size = outBytes };
            _vk.CmdCopyBuffer(cmd, s.KernelOut!.Vk, s.HostOut.Vk, 1, &download);
            Barrier(
                cmd,
                PipelineStageFlags.TransferBit,
                AccessFlags.TransferWriteBit,
                PipelineStageFlags.HostBit,
                AccessFlags.HostReadBit);
        }
        else
        {
            Barrier(
                cmd,
                PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderWriteBit,
                PipelineStageFlags.HostBit,
                AccessFlags.HostReadBit);
        }

        End(cmd);
        SubmitInfo si = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        ThrowOn(_vk.QueueSubmit(_queue, 1, &si, s.Fence), "vkQueueSubmit");
        s.Pending = true;
    }

    /// <summary>
    /// Waits for a slot's slab and copies its answers out of the slot's host
    /// memory; the slot is free again afterwards.
    /// </summary>
    /// <param name="slot">A submitted slot.</param>
    /// <param name="outWords">Receives the words the submit promised.</param>
    /// <exception cref="VulkanException">
    /// The wait failed or timed out. The slot then stays in flight, and is
    /// refused until a later wait sees its fence signal, because the device
    /// may still be using its buffers.
    /// </exception>
    /// <exception cref="InvalidOperationException">The slot was not submitted.</exception>
    public void Complete(int slot, Span<uint> outWords)
    {
        SlabSlot s = _slots[slot];
        if (!s.Pending)
        {
            throw new InvalidOperationException($"slab slot {slot} is completed without a submit");
        }

        Retire(s);
        new ReadOnlySpan<uint>((void*)s.HostOut!.Mapped, outWords.Length).CopyTo(outWords);
    }

    /// <summary>
    /// Waits out a slot still in flight. The batcher only stages a slot
    /// after completing it, so this waits only when that completion failed
    /// (a timeout leaves the device possibly still using the buffers): the
    /// slot is used again once its fence signals, and never before.
    /// </summary>
    private void Retire(SlabSlot s)
    {
        if (s.Pending)
        {
            WaitAndReset(s.Fence);
            s.Pending = false;
        }
    }

    /// <summary>
    /// Rays the known-hit micro-scene's buffer holds: one workgroup's worth,
    /// of which only <see cref="SelfTestRealRays"/> are dispatched as rays.
    /// </summary>
    internal const int SelfTestRays = Invocations;

    /// <summary>
    /// How many rays the self-test traces. The dispatch still covers one
    /// whole workgroup, but the kernel's range guard keeps the other lanes
    /// from starting a query at all.
    /// </summary>
    /// <remarks>
    /// The self-test used to pass all 64 lanes as rays, 62 of them zeroed:
    /// zero direction, <c>tmax</c> 0, and the test's <c>tmin</c> of 1e-3. A
    /// ray query with <c>tmin</c> above <c>tmax</c> is undefined behaviour,
    /// and those lanes ran in the same workgroup as the two rays that decide
    /// the gate, and in any-hit mode folded their bits through the same
    /// shared words. The self-test no longer offers any such ray, and the
    /// kernel refuses one itself.
    /// </remarks>
    internal const int SelfTestRealRays = 2;

    /// <summary><c>1e-3f</c> as float bits: the self-test's <c>tmin</c>.</summary>
    internal const uint SelfTestTminBits = 0x3A83126Fu;

    /// <summary>
    /// The capability gate: traces a two-triangle known-hit micro-scene
    /// through every kernel mode and decides whether THIS device answers
    /// ray queries correctly. The verdict rests on the known answers alone
    /// (modes 4, 0 and 1). The telemetry (mode 5) is recorded for the report
    /// and decides nothing: the BLAS is opaque, so a conformant driver offers
    /// no candidates and reports zero proceed iterations, the same numbers a
    /// device that never traversed would give. While the kernel was handed a
    /// BLAS, llvmpipe reported candidates and NVIDIA reported none, and both
    /// failed the known answers; with the TLAS both pass.
    /// </summary>
    /// <returns>Whether to trust the device, and every ray's raw answers either way.</returns>
    /// <remarks>
    /// The scene is exact by construction: one unit quad on x=0 split into
    /// triangles 0 (the y&#8805;z half) and 1 (the z&#8805;y half), and two
    /// rays fired straight at the interior of each triangle, so the only
    /// acceptable answers are "bit set" and "primitive 0 / primitive 1 at
    /// t = 0.5". A device that answers anything else is not marginal — it is
    /// broken, and a real trace on it would silently produce all-miss
    /// lighting.
    /// </remarks>
    public (bool Passed, SelfTestOutcome Outcome) RunSelfTest()
    {
        SelfTestGeometry(out float[] vertices, out float[] rays, out _);
        // The self-test runs through the SAME upload + BLAS/TLAS-build
        // machinery a real trace uses, so an empty structure from a bad
        // build fails the gate too (that was the lavapipe all-miss's second
        // contributor).
        LoadScene(vertices);
        uint tminBits = SelfTestTminBits;
        const int OutWordsAny = 2;   // one workgroup
        ReadOnlySpan<float> real = rays.AsSpan(0, SelfTestRealRays * 8);

        // Mode 4 first: pure compute-write/copy/readback. If this reads back
        // zeroes, nothing about ray answers means anything yet.
        uint[] w = new uint[Math.Max(OutWordsAny, SelfTestRealRays * 2)];
        DispatchWithStagedRays(4, SelfTestRealRays, real, w.AsSpan(0, OutWordsAny), tminBits, TmaxScaleBits);
        (uint First, uint Second) readback = (w[0], w[1]);
        bool readbackOk = readback == (0xFFFFFFFFu, 0xFFFFFFFFu);

        bool anyHitOk = false;
        bool closestOk = false;
        int iters = 0;
        int cands = 0;
        SelfTestRayResult[] results = [];
        if (readbackOk)
        {
            DispatchWithStagedRays(0, SelfTestRealRays, real, w.AsSpan(0, OutWordsAny), tminBits, TmaxScaleBits);
            uint anyBits = w[0];

            DispatchWithStagedRays(1, SelfTestRealRays, real, w.AsSpan(0, SelfTestRealRays * 2), tminBits, TmaxScaleBits);
            uint[] closest = w[..(SelfTestRealRays * 2)];

            // Telemetry says what the traversal offered, for the report.
            DispatchWithStagedRays(5, SelfTestRealRays, real, w.AsSpan(0, SelfTestRealRays * 2), tminBits, TmaxScaleBits);
            results = new SelfTestRayResult[SelfTestRealRays];
            for (int i = 0; i < SelfTestRealRays; i++)
            {
                results[i] = new SelfTestRayResult(
                    i,
                    (anyBits & (1u << i)) != 0,
                    closest[i * 2],
                    closest[(i * 2) + 1],
                    unchecked((int)w[i * 2]),
                    unchecked((int)w[(i * 2) + 1]));
                iters = Math.Max(iters, results[i].Iterations);
                cands += results[i].Candidates != 0 ? 1 : 0;
            }

            // Ray i must hit triangle i at t = 0.5 in both modes.
            anyHitOk = Array.TrueForAll(results, r => r.AnyHit);
            closestOk = Array.TrueForAll(results, r => r.ClosestMatches);
        }

        bool passed = readbackOk && anyHitOk && closestOk;
        return (passed, new SelfTestOutcome(readbackOk, anyHitOk, closestOk, iters, cands)
        {
            Rays = results,
            ReadbackWords = readback,
        });
    }

    /// <summary>
    /// The geometry and flags the self-test traces with, in words, for the
    /// report: what the structures are built from and what each kernel mode
    /// asks the ray query for.
    /// </summary>
    /// <remarks>
    /// Kept beside the code that sets these values, and a fact checks each
    /// number against the constant or code that sets it, so the report
    /// cannot quietly describe a configuration other than the one that ran.
    /// </remarks>
    internal const string SelfTestConfiguration =
        "scene: 2 triangles in 1 OPAQUE geometry, R32G32B32_SFLOAT vertices, stride 12, no index buffer, "
        + "no geometry transform, PREFER_FAST_TRACE; TLAS: 1 instance, identity transform, mask 0xFF, "
        + "custom index 0, binding-table offset 0, flags TRIANGLE_FACING_CULL_DISABLE; "
        + "rays: tmin 0.001 (0x3A83126F), cull mask 0xFF; any-hit ray flags TerminateOnFirstHit with tmax "
        + "scaled by the largest float below 1 (0x3F7FFFFF); closest-hit and telemetry ray flags None";

    /// <summary>
    /// The self-test in full for the report: the device and driver build,
    /// the configuration, and each ray's expected and actual answer in every
    /// mode.
    /// </summary>
    /// <param name="identity">The device and driver.</param>
    /// <param name="outcome">What the self-test saw.</param>
    /// <returns>The detail, on one line, clauses separated by semicolons.</returns>
    internal static string SelfTestDetail(DeviceIdentity identity, SelfTestOutcome outcome)
    {
        StringBuilder sb = new();
        sb.Append(identity.Describe()).Append("; ").Append(SelfTestConfiguration);
        sb.Append(CultureInfo.InvariantCulture,
            $"; readback words 0x{outcome.ReadbackWords.First:X8} 0x{outcome.ReadbackWords.Second:X8}");
        sb.Append(" (expected 0xFFFFFFFF 0xFFFFFFFF)");
        if (outcome.Rays.Count == 0)
        {
            sb.Append("; no ray was traced");
        }

        foreach (SelfTestRayResult r in outcome.Rays)
        {
            sb.Append("; ").Append(r.Describe());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the known-hit micro-scene: a unit quad on x=0 as two triangles
    /// (0: the y&#8805;z half, 1: the z&#8805;y half) and the two rays that
    /// fire at each half's interior from x=0.5 travelling -X, tmax 1.
    /// </summary>
    /// <param name="vertices">XYZ triples for the two triangles.</param>
    /// <param name="rays">The wire-layout rays: two vec4 per ray.</param>
    /// <param name="rayCount">Rays in <paramref name="rays"/>.</param>
    /// <remarks>
    /// Ray A origin (0.5, 0.7, 0.3) hits (0, 0.7, 0.3): y&gt;z, triangle 0's
    /// interior. Ray B origin (0.5, 0.3, 0.7) hits triangle 1's interior.
    /// Neither lands on the shared diagonal (y=z), so the expected primitive
    /// id is not a coin flip. Directions follow the caller-segment
    /// convention: (d, tmax) with the crossing at t = 0.5.
    /// </remarks>
    internal static void SelfTestGeometry(out float[] vertices, out float[] rays, out int rayCount)
    {
        vertices =
        [
            0f, 0f, 0f, 0f, 1f, 0f, 0f, 1f, 1f,
            0f, 0f, 0f, 0f, 1f, 1f, 0f, 0f, 1f,
        ];
        rayCount = SelfTestRays;
        rays = new float[SelfTestRays * 8];
        rays[0] = 0.5f;
        rays[1] = 0.7f;
        rays[2] = 0.3f;
        rays[4] = -1f;
        rays[7] = 1f;
        rays[8] = 0.5f;
        rays[9] = 0.3f;
        rays[10] = 0.7f;
        rays[12] = -1f;
        rays[15] = 1f;
    }

    /// <summary>The most bytes one upload-probe copy moves: 64 MiB, or the slot's ray buffer if smaller.</summary>
    internal const ulong UploadProbeBytes = 64UL << 20;

    /// <summary>How many copies the upload probe times; the fastest counts.</summary>
    internal const int UploadProbeRepetitions = 3;

    /// <summary>
    /// Measures how fast rays reach the device on the path slabs use: slot
    /// 0's host ray buffer copied into its device ray buffer, the same
    /// command the slab upload records, timed from submit to fence.
    /// </summary>
    /// <returns>
    /// The best of <see cref="UploadProbeRepetitions"/> rates in bytes per
    /// second, or null when the kernel reads rays in place (there is no
    /// upload to measure, so the link costs nothing a probe could see).
    /// </returns>
    /// <exception cref="VulkanException">Recording, submission or the wait failed.</exception>
    /// <remarks>
    /// <para>
    /// Why a probe at all: on a card with a slow link the upload, not the
    /// traversal, sets the pace. An RTX 2070 SUPER in a PCIe Gen2 x1 slot
    /// (about 0.5 GB/s, no resizable BAR, so rays are staged) spent 34.3 s
    /// of a 2fort light copying rays against 0.44 s tracing them, and lost
    /// to the CPU tracer by a factor of five. Only a measurement shows that:
    /// the device's name and type look like any other discrete GPU.
    /// </para>
    /// <para>
    /// It allocates nothing. It borrows slot 0's buffers, command buffer
    /// and fence exactly as a slab would, while no slab is in flight (the
    /// probe runs during set-up), so there is nothing of its own to release
    /// on success or failure; a wait that fails leaves the slot pending,
    /// which the slab path and dispose already handle. The copied bytes are
    /// whatever the buffer holds: no reader looks at them.
    /// </para>
    /// </remarks>
    public double? MeasureUploadRate()
    {
        SlabSlot s = _slots[0];
        if (ReferenceEquals(s.HostRays, s.KernelRays))
        {
            return null;
        }

        Retire(s);
        ulong bytes = Math.Min(UploadProbeBytes, s.HostRays!.Size);
        double best = 0;
        for (int rep = 0; rep < UploadProbeRepetitions; rep++)
        {
            CommandBuffer cmd = s.Commands;
            ThrowOn(_vk.ResetCommandBuffer(cmd, 0), "vkResetCommandBuffer");
            CommandBufferBeginInfo cbbi = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            ThrowOn(_vk.BeginCommandBuffer(cmd, &cbbi), "vkBeginCommandBuffer");
            BufferCopy upload = new() { SrcOffset = 0, DstOffset = 0, Size = bytes };
            _vk.CmdCopyBuffer(cmd, s.HostRays.Vk, s.KernelRays!.Vk, 1, &upload);
            End(cmd);
            Observe?.Invoke(VulkanStep.UploadProbeRecorded);
            SubmitInfo si = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd,
            };
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            ThrowOn(_vk.QueueSubmit(_queue, 1, &si, s.Fence), "vkQueueSubmit");
            s.Pending = true;
            Retire(s);
            double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;
            best = Math.Max(best, UploadRate(bytes, seconds));
        }

        return best;
    }

    /// <summary>Bytes per second for <paramref name="bytes"/> moved in <paramref name="seconds"/>.</summary>
    /// <param name="bytes">Bytes copied.</param>
    /// <param name="seconds">Wall time from submit to fence.</param>
    /// <returns>The rate; a copy too fast for the clock counts as infinitely fast.</returns>
    internal static double UploadRate(ulong bytes, double seconds) =>
        seconds > 0 ? bytes / seconds : double.PositiveInfinity;

    /// <summary>Stages pre-built wire rays in slot 0 and traces them to completion (the self-test's path).</summary>
    /// <param name="mode">Kernel mode.</param>
    /// <param name="rayCount">Rays in <paramref name="rays"/>.</param>
    /// <param name="rays">Wire-layout rays.</param>
    /// <param name="outWords">Raw out words.</param>
    /// <param name="tminBits">Ray epsilon as float bits.</param>
    /// <param name="tmaxScaleBits">Any-hit tmax scale as float bits.</param>
    public void DispatchWithStagedRays(
        int mode, int rayCount, ReadOnlySpan<float> rays, Span<uint> outWords,
        uint tminBits, uint tmaxScaleBits)
    {
        rays.CopyTo(StageRays(0, rayCount));
        Submit(0, mode, rayCount, outWords.Length, tminBits, tmaxScaleBits);
        Complete(0, outWords);
    }

    /// <summary>Submits a set-up command buffer (scene upload, BLAS build) and waits for it.</summary>
    private void Submit(CommandBuffer cmd)
    {
        SubmitInfo si = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        ThrowOn(_vk.QueueSubmit(_queue, 1, &si, _fence), "vkQueueSubmit");
        _setupPending = true;
        WaitAndReset(_fence);
        _setupPending = false;
        _vk.FreeCommandBuffers(_device, _commandPool, 1, &cmd);
    }

    /// <summary>
    /// Waits up to <see cref="SubmitWaitNs"/> for <paramref name="fence"/>
    /// and resets it. On any failure the fence is left as it was, so the
    /// caller's pending flag stays set and the work is still waited for
    /// before anything it uses is reused or freed.
    /// </summary>
    private void WaitAndReset(Fence fence)
    {
        Result r = FenceWaits.Wait([fence], SubmitWaitNs);
        if (r == Result.Timeout)
        {
            throw new VulkanException(
                Result.Timeout,
                "driver hang: a fence did not signal within 120 s; the work it guards may still be running",
                null);
        }

        ThrowOn(r, "vkWaitForFences");
        ThrowOn(FenceWaits.Reset(fence), "vkResetFences");
    }

    /// <summary>
    /// The fences of work that is, or may still be, on the device: every
    /// slot submitted and not yet waited out, and a set-up submit whose wait
    /// failed.
    /// </summary>
    /// <returns>The fences; empty when nothing was left in flight.</returns>
    private Fence[] PendingFences()
    {
        List<Fence> pending = [];
        foreach (SlabSlot slot in _slots)
        {
            if (slot is { Pending: true })
            {
                pending.Add(slot.Fence);
            }
        }

        if (_setupPending)
        {
            pending.Add(_fence);
        }

        return [.. pending];
    }

    /// <summary>
    /// Whether the work behind <paramref name="pending"/> is over, waiting
    /// at most <paramref name="bound"/>: true when there is none, when every
    /// fence signalled, or when the device is lost.
    /// </summary>
    /// <param name="waits">The fence calls.</param>
    /// <param name="pending">Fences of work that may still be on the device.</param>
    /// <param name="bound">The longest wait; negative counts as zero.</param>
    /// <returns>True when memory the work uses may be freed.</returns>
    /// <remarks>
    /// A lost device counts as finished: Vulkan keeps a lost device's child
    /// objects valid and has the application destroy them, then the device,
    /// to recover, so releasing is what a lost device calls for. Any other failure (a
    /// timeout above all, or an error the driver gives for the wait itself)
    /// says nothing about whether the GPU is still reading, so it counts as
    /// not finished.
    /// </remarks>
    internal static bool DrainedWithin(IFenceWaits waits, ReadOnlySpan<Fence> pending, TimeSpan bound)
    {
        if (pending.IsEmpty)
        {
            return true;
        }

        ulong ns = (ulong)Math.Max(0L, bound.Ticks) * 100UL;
        Result r = waits.Wait(pending, ns);
        return r is Result.Success or Result.ErrorDeviceLost;
    }

    /// <summary>The driver's fence calls on this device.</summary>
    private sealed class DriverFenceWaits(VulkanDevice owner) : IFenceWaits
    {
        public Result Wait(ReadOnlySpan<Fence> fences, ulong timeoutNs)
        {
            fixed (Fence* p = fences)
            {
                return owner._vk.WaitForFences(owner._device, (uint)fences.Length, p, true, timeoutNs);
            }
        }

        public Result Reset(Fence fence) => owner._vk.ResetFences(owner._device, 1, in fence);
    }

    private void End(CommandBuffer cmd) => ThrowOn(_vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");


    private CommandBuffer Begin()
    {
        CommandBufferAllocateInfo cbai = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        ThrowOn(_vk.AllocateCommandBuffers(_device, &cbai, out CommandBuffer cmd),
            "vkAllocateCommandBuffers");
        CommandBufferInheritanceInfo inh = default;
        CommandBufferBeginInfo cbbi = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            PInheritanceInfo = &inh,
        };
        ThrowOn(_vk.BeginCommandBuffer(cmd, &cbbi), "vkBeginCommandBuffer");
        return cmd;
    }

    /// <summary>
    /// Records a src→dst dependency barrier into <paramref name="cmd"/>.
    /// The spike relied on same-queue submission order and got away with it
    /// on radv; production never does — every upload→build→dispatch→download
    /// edge gets an explicit <c>vkCmdPipelineBarrier</c>.
    /// </summary>
    private void Barrier(CommandBuffer cmd, BarrierMasks m) =>
        Barrier(cmd, m.SrcStage, m.SrcAccess, m.DstStage, m.DstAccess);

    private void Barrier(
        CommandBuffer cmd,
        PipelineStageFlags srcStage,
        AccessFlags srcAccess,
        PipelineStageFlags dstStage,
        AccessFlags dstAccess)
    {
        MemoryBarrier mb = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
        };
        // vkCmdPipelineBarrier is void; a recording error surfaces at
        // submit/wait time, which ThrowOn in Submit already checks.
        _vk.CmdPipelineBarrier(
            cmd, srcStage, dstStage, default, 1, &mb, 0, null, 0, (ImageMemoryBarrier*)null);
    }

    private static byte** AllocNames(string[] names)
    {
        byte** p = (byte**)Marshal.AllocHGlobal(names.Length * sizeof(nint));
        for (int i = 0; i < names.Length; i++)
        {
            p[i] = (byte*)SilkMarshal.StringToPtr(names[i]);
        }

        return p;
    }

    private static void FreeNames(byte** names, int count)
    {
        for (int i = 0; i < count; i++)
        {
            SilkMarshal.Free((nint)names[i]);
        }

        Marshal.FreeHGlobal((nint)names);
    }

    private void ThrowOn(Result r, string what)
    {
        if (r != Result.Success)
        {
            throw new VulkanException(r, what, null);
        }
    }

    /// <summary>
    /// Tears down whatever <see cref="Construct"/> actually opened. Every handle is
    /// guarded: a device whose construction was rejected (no matching device, missing
    /// ray-tracing extensions) is disposed from the <c>TryCreate</c> catch path with
    /// most fields still null, and an unguarded destroy call hands the loader a null
    /// or uninitialized handle — that logs "vkDestroyPipelineLayout: Invalid device"
    /// under the loader's own validation and can abort the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing may be destroyed while the device could still use it, and a
    /// slab or set-up submit whose own wait failed may still be running. So
    /// dispose first waits for exactly those fences (the device tracks
    /// every submit it has not seen finish), for at most
    /// <see cref="DisposeWait"/>. Every submit carries a fence, so when they
    /// have all signalled the queue has nothing left and no
    /// <c>vkDeviceWaitIdle</c> is needed. That call is what dispose used
    /// before, and it has no timeout: after a GPU hang it can block
    /// forever, and the thread disposing a failed or cancelled compile,
    /// which in a long-lived service is one the service needs back, never
    /// returned.
    /// </para>
    /// <para>
    /// When the bound passes first, the device is ABANDONED: no buffer is
    /// freed, nothing is destroyed and the loader stays loaded, because the
    /// GPU may still read and write that memory, and freeing it would hand
    /// it to the next allocation while a hung kernel scribbles on it.
    /// <see cref="Abandoned"/> reports it. That leaks the device, which is
    /// the lesser harm: a hung GPU needs an operator either way, and the
    /// service keeps its thread. A later dispose tries again, and releases
    /// everything if the fences have signalled by then.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_device.Handle == 0)
        {
            // Construct never opened a logical device: nothing device-scoped exists.
            if (_instance.Handle != 0)
            {
                _vk.DestroyInstance(_instance, null);
                _instance = default;
            }

            _vk.Dispose();
            return;
        }

        if (!DrainedWithin(FenceWaits, PendingFences(), DisposeWait))
        {
            Abandoned = true;
            return;
        }

        Abandoned = false;
        foreach (GpuBuffer parked in _parked)
        {
            Free(parked);
        }

        _parked.Clear();
        if (_blasApi is not null)
        {
            ReleaseScene();
        }

        FreeSlots();
        Free(_vertexBuffer);
        if (_descriptorPool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        }

        if (_pipelineLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        }

        if (_descriptorLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_device, _descriptorLayout, null);
        }

        if (_pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_device, _pipeline, null);
        }

        if (_fence.Handle != 0)
        {
            _vk.DestroyFence(_device, _fence, null);
        }

        if (_commandPool.Handle != 0)
        {
            _vk.DestroyCommandPool(_device, _commandPool, null);
        }

        _vk.DestroyDevice(_device, null);
        _device = default;
        if (_instance.Handle != 0)
        {
            _vk.DestroyInstance(_instance, null);
            _instance = default;
        }

        _vk.Dispose();
    }
}

/// <summary>What device selection knows of one physical device.</summary>
/// <param name="Name">The device's name.</param>
/// <param name="Type">The device's type.</param>
/// <param name="Traceable">Whether it offers ray query and acceleration structures.</param>
internal readonly record struct DeviceCandidate(string Name, PhysicalDeviceType Type, bool Traceable);

/// <summary>One memory barrier's two stage masks and two access masks.</summary>
/// <param name="SrcStage">The stage whose work must finish first.</param>
/// <param name="SrcAccess">The writes made available.</param>
/// <param name="DstStage">The stage that waits.</param>
/// <param name="DstAccess">The accesses the writes are made visible to.</param>
internal readonly record struct BarrierMasks(
    PipelineStageFlags SrcStage, AccessFlags SrcAccess, PipelineStageFlags DstStage, AccessFlags DstAccess);

/// <summary>One row of the device inventory <see cref="VulkanDevice.ProbeDevices()"/> reports.</summary>
/// <param name="Index">Physical-device index.</param>
/// <param name="Name">Device name.</param>
/// <param name="DeviceType">What the device presents as.</param>
/// <param name="RayQuery">Whether <c>VK_KHR_ray_query</c> is feature-enabled.</param>
public readonly record struct VulkanDeviceInfo(int Index, string Name, string DeviceType, bool RayQuery);

/// <summary>A Vulkan call the driver or loader refused.</summary>
public sealed class VulkanException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="result">The failing <c>VkResult</c>.</param>
    /// <param name="message">What was attempted.</param>
    /// <param name="inner">The underlying cause, when one exists.</param>
    public VulkanException(Silk.NET.Vulkan.Result result, string message, Exception? inner)
        : base($"{message} (VkResult {result})", inner)
    {
        Result = result;
    }

    /// <summary>The failing <c>VkResult</c>.</summary>
    public Silk.NET.Vulkan.Result Result { get; }
}

/// <summary>What the capability self-test's kernel modes observed, verbatim.</summary>
/// <param name="ReadbackOk">Mode 4: the compute write/copy/readback path works.</param>
/// <param name="AnyHitOk">Mode 0: both known-hit rays returned their bits.</param>
/// <param name="ClosestOk">Mode 1: both known-hit rays returned their expected primitive at t≈0.5.</param>
/// <param name="Iters">Mode 5: max proceed-iterations any telemetry ray counted.</param>
/// <param name="Candidates">Mode 5: how many telemetry rays reached a candidate intersection.</param>
public readonly record struct SelfTestOutcome(bool ReadbackOk, bool AnyHitOk, bool ClosestOk, int Iters, int Candidates)
{
    /// <summary>Each traced ray's raw answers, in ray order; empty when the readback leg failed first.</summary>
    public IReadOnlyList<SelfTestRayResult> Rays { get; init; } = [];

    /// <summary>The two words mode 4 read back (all-ones on a working device).</summary>
    public (uint First, uint Second) ReadbackWords { get; init; }
}

/// <summary>
/// One self-test ray's raw answers in every mode, beside what the scene's
/// construction says they must be: ray <c>i</c> hits triangle <c>i</c> at
/// t = 0.5.
/// </summary>
/// <param name="Ray">The ray's index, which is also the primitive it must hit.</param>
/// <param name="AnyHit">Mode 0's bit.</param>
/// <param name="Primitive">Mode 1's committed primitive, 0xFFFFFFFF for a miss.</param>
/// <param name="TBits">Mode 1's committed t, as float bits (0 for a miss).</param>
/// <param name="Iterations">Mode 5's proceed iterations.</param>
/// <param name="Candidates">Mode 5's candidate count (0 or 1: the kernel stops at the first).</param>
public readonly record struct SelfTestRayResult(
    int Ray, bool AnyHit, uint Primitive, uint TBits, int Iterations, int Candidates)
{
    /// <summary>The distance every self-test ray's crossing sits at.</summary>
    public const float ExpectedT = 0.5f;

    /// <summary>How far a reported t may sit from <see cref="ExpectedT"/> and still count.</summary>
    public const float TTolerance = 1e-4f;

    /// <summary>Mode 1's t as a float.</summary>
    public float T => BitConverter.UInt32BitsToSingle(TBits);

    /// <summary>Whether mode 1 committed the expected primitive at the expected distance.</summary>
    public bool ClosestMatches => Primitive == (uint)Ray && Math.Abs(T - ExpectedT) < TTolerance;

    /// <summary>The ray's expected and actual answers, in words.</summary>
    /// <returns>One clause for the report.</returns>
    public string Describe()
    {
        string got = Primitive == 0xFFFFFFFFu
            ? string.Create(CultureInfo.InvariantCulture, $"a miss (t bits 0x{TBits:X8})")
            : string.Create(CultureInfo.InvariantCulture, $"primitive {Primitive} at t={T:R} (0x{TBits:X8})");
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ray {Ray}: any-hit expected hit, got {(AnyHit ? "hit" : "miss")}; closest-hit expected "
            + $"primitive {Ray} at t=0.5 (0x3F000000), got {got}; telemetry {Iterations} proceed "
            + $"iteration(s), {Candidates} candidate(s)");
    }
}
