using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SourceSharp.MapTools.Gpu.Interop;

/// <summary>
/// One Vulkan compute device with the ray-query pipeline, the scene BLAS, and
/// pinned staging buffers — everything the traced batches need.
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
/// Threading: not thread-safe by design. One tracer instance owns one of
/// these, and <c>VulkanRayTracer</c> serialises every entry with an internal
/// lock, which is also what makes a batch's bytes depend only on its rays.
/// </para>
/// </remarks>
internal sealed unsafe class VulkanDevice : IDisposable
{
    private const uint QueueFamilyIgnored = 0xFFFFFFFFu;
    private const nuint Vulkan13 = (1u << 22) | (13u << 12);

    /// <summary>Kernel workgroup size; the bit-out layout assumes 64 rays/workgroup.</summary>
    internal const int Invocations = 64;

    /// <summary>The any-hit tmax shrink as float bits: <c>1 - 2^-23</c> = 0x3F7FFFFF. A boundary hit goes to the miss side.</summary>
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
    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;
    private DescriptorSetLayout _descriptorLayout;
    private DescriptorSet _descriptorSet;
    private DescriptorPool _descriptorPool;
    private CommandPool _commandPool;
    private Fence _fence;

    private GpuBuffer? _vertexBuffer;
    private GpuBuffer? _rayBuffer;
    private GpuBuffer? _outBuffer;
    private GpuBuffer? _asBuffer;
    private GpuBuffer? _hostRays;
    private GpuBuffer? _hostOut;
    private AccelerationStructureKHR _blasHandle;
    private uint _triangleCount;
    private AccelerationStructureGeometryKHR _geometry;

    /// <summary>Selected device name, e.g. <c>AMD Radeon RX 9070 XT (RADV GFX1201)</c>.</summary>
    public string DeviceName { get; private set; } = "?";

    /// <summary>Driver name and info string.</summary>
    public string DriverName { get; private set; } = "?";

    /// <summary>Whether the selected device presents as a CPU rasteriser (llvmpipe).</summary>
    public bool IsCpuDevice { get; private set; }

    /// <summary>Bytes of Vulkan memory this device currently has allocated.</summary>
    private long _liveBytes;

    /// <summary>Bytes of Vulkan memory this device has ever had allocated at once.</summary>
    public long PeakAllocationBytes { get; private set; }

    /// <summary>The largest ray batch one dispatch may hold, from the device's buffer-size limits.</summary>
    public int MaxSlabRays { get; private set; }

    /// <summary>How many triangles the BLAS was built from.</summary>
    public uint TriangleCount => _triangleCount;

    /// <summary>
    /// Creates the instance, selects and opens the device, builds the pipeline,
    /// and pins the staging buffers.
    /// </summary>
    /// <param name="deviceMatch">Device-name substring, or null for the best-scoring device.</param>
    /// <param name="deviceIndex">Physical-device index pin (over ray-query-capable devices), or −1.</param>
    /// <param name="maxRaysPerSlab">Requested slab cap; the device's limits can only lower it.</param>
    /// <exception cref="VulkanException">Any driver refusal, with the failing call and result.</exception>
    /// <exception cref="NotSupportedException">No device matches and exposes ray query.</exception>
    public void Construct(string? deviceMatch, int deviceIndex, int maxRaysPerSlab)
    {
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

        PhysicalDeviceRayTracingPipelinePropertiesKHR rtProps = new()
        {
            SType = StructureType.PhysicalDeviceRayTracingPipelinePropertiesKhr,
        };
        PhysicalDeviceDriverProperties drv = new()
        {
            SType = StructureType.PhysicalDeviceDriverProperties,
        };
        PhysicalDeviceRayQueryFeaturesKHR rqFeatures = new()
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
        };
        PhysicalDeviceFeatures2 features = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &rqFeatures,
        };
        PhysicalDeviceProperties2 props = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &rtProps,
        };
        rtProps.PNext = &drv;

        int chosen = -1;
        int bestScore = int.MinValue;
        int rqSeen = 0;
        for (int i = 0; i < devices.Length; i++)
        {
            _vk.GetPhysicalDeviceProperties2(devices[i], &props);
            _vk.GetPhysicalDeviceFeatures2(devices[i], &features);
            if (!rqFeatures.RayQuery)
            {
                continue;
            }

            string name = SilkMarshal.PtrToString((nint)props.Properties.DeviceName) ?? "unknown";
            if (deviceIndex >= 0)
            {
                // An explicit pin picks the index among ray-query-capable
                // devices in instance order — diagnostics must be able to
                // reach a device a name substring cannot (or must reach a
                // known-broken one on purpose).
                if (rqSeen != deviceIndex)
                {
                    rqSeen++;
                    continue;
                }
            }
            else if (deviceMatch is { Length: > 0 }
                     && !name.Contains(deviceMatch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int score = props.Properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 5,
                PhysicalDeviceType.IntegratedGpu => 4,
                PhysicalDeviceType.VirtualGpu => 3,
                PhysicalDeviceType.Cpu => 2,
                _ => 1,
            };
            if (score > bestScore)
            {
                bestScore = score;
                chosen = i;
            }
        }

        if (chosen < 0)
        {
            string what = deviceIndex >= 0
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
        // maxStorageBufferRange is the storage-buffer binding limit that
        // matters here; the spec guarantees >= 128 MiB on every conformant
        // device, and we clamp slab sizes to what the device reports.
        ulong maxStorage = Math.Max(134_217_728UL, props.Properties.Limits.MaxStorageBufferRange);

        _memory = _vk.GetPhysicalDeviceMemoryProperties(_physical);

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
        rqFeatures.PNext = &vk12;
        features.PNext = &rqFeatures;
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

        // A dispatch's buffers all fit the slab: rays are 8 floats (32 B) and
        // the largest out mode is 2 words (8 B)/ray, so the ray buffer is the
        // binding limit. The cap can only come DOWN from the caller's
        // request: a slab whose buffer exceeds MaxStorageBufferBindingSize is
        // invalid usage (the spec floor is 128 MB, so the default
        // 4,194,304-ray slab at 128 MB of ray bytes fits exactly).
        ulong perRayBytes = 32UL;
        ulong byBinding = maxStorage / perRayBytes;
        ulong byBuffer = byBinding; // same limit governs both roles here
        ulong byDispatch = (ulong)uint.MaxValue / Invocations * Invocations;
        ulong cap = Math.Min((ulong)maxRaysPerSlab, Math.Min(byBinding, Math.Min(byBuffer, byDispatch)));
        cap &= ~(ulong)(Invocations - 1); // whole workgroups; the bit-word layout needs it
        MaxSlabRays = (int)Math.Max(Invocations, cap);

        byte[] spirv = ShadercCompiler.CompileGlsl(Kernels.RayGlsl, "vis.glsl");
        BuildPipeline(spirv);
        AllocateSceneBuffers();
        CreateDescriptorSet();
    }

    /// <summary>Enumerated device inventory for diagnostics (creates no device).</summary>
    /// <returns>One row per physical device, ray-query flag included.</returns>
    public static List<VulkanDeviceInfo> ProbeDevices()
    {
        List<VulkanDeviceInfo> rows = [];
        Vk vk = Vk.GetApi();
        byte* appName = (byte*)SilkMarshal.StringToPtr("maptools-gpu-probe");
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
        };
        Result r = vk.CreateInstance(&ici, null, out Instance instance);
        SilkMarshal.Free((nint)appName);
        if (r != Result.Success)
        {
            rows.Add(new VulkanDeviceInfo(-1, "(no Vulkan loader)", "instance failed: " + r, false));
            return rows;
        }

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

        vk.DestroyInstance(instance, null);
        vk.Dispose();
        return rows;
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
            byte* entry = (byte*)SilkMarshal.StringToPtr("main");
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
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, module, null);
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

    private GpuBuffer Allocate(ulong size, MemoryPropertyFlags wanted, BufferUsageFlags usage, bool deviceAddress)
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
        MemoryRequirements req;
        _vk.GetBufferMemoryRequirements(_device, buffer, &req);
        int type = -1;
        for (int i = 0; i < _memory.MemoryTypeCount; i++)
        {
            MemoryPropertyFlags have = _memory.MemoryTypes[i].PropertyFlags;
            if ((req.MemoryTypeBits & (1u << i)) != 0 && (have & wanted) == wanted)
            {
                type = i;
                break;
            }
        }

        if (type < 0)
        {
            throw new NotSupportedException($"{DeviceName}: no memory type with {wanted}");
        }

        MemoryAllocateInfo mai = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = (uint)type,
        };
        ThrowOn(_vk.AllocateMemory(_device, &mai, null, out DeviceMemory memory), "vkAllocateMemory");
        ThrowOn(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
        nint mapped = 0;
        if ((wanted & MemoryPropertyFlags.HostVisibleBit) != 0)
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
    /// Pins the persistent scene/staging set: one ray buffer and one out
    /// buffer at slab capacity, plus host-visible staging for both
    /// directions, so a trace never allocates Vulkan memory per batch.
    /// </summary>
    private void AllocateSceneBuffers()
    {
        ulong slabRayBytes = (ulong)MaxSlabRays * 32UL;
        ulong slabOutBytes = (ulong)MaxSlabRays * 8UL; // closest: 2 words/ray; bits: 1/8 of that
        _rayBuffer = Allocate(slabRayBytes, MemoryPropertyFlags.DeviceLocalBit,
            BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.StorageBufferBit
            | BufferUsageFlags.TransferDstBit,
            deviceAddress: true);
        _outBuffer = Allocate(slabOutBytes, MemoryPropertyFlags.DeviceLocalBit,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
            deviceAddress: false);
        _hostRays = Allocate(slabRayBytes,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            BufferUsageFlags.TransferSrcBit,
            deviceAddress: false);
        _hostOut = Allocate(slabOutBytes,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            BufferUsageFlags.TransferDstBit,
            deviceAddress: false);
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
        // the real one — replaces the vertex buffer and the BLAS; release
        // the old pair instead of leaking them. The queue is idle here (the
        // last dispatch's fence signalled), so the descriptor refresh in
        // BuildBlas is safe.
        if (_vertexBuffer is not null)
        {
            Free(_vertexBuffer);
            _vertexBuffer = null;
        }

        if (_asBuffer is not null)
        {
            _blasApi.DestroyAccelerationStructure(_device, _blasHandle, null);
            Free(_asBuffer);
            _asBuffer = null;
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
        vertices.CopyTo(new Span<float>((void*)staging.Mapped, vertices.Length));

        // One command buffer: the upload's write, then the ordering that makes
        // it visible to the AS build's read (same queue is not a
        // synchronisation promise).
        CommandBuffer upload = Begin();
        BufferCopy region = new() { SrcOffset = 0, DstOffset = 0, Size = bytes };
        _vk.CmdCopyBuffer(upload, staging.Vk, _vertexBuffer.Vk, 1, &region);
        Barrier(
            upload,
            PipelineStageFlags.TransferBit,
            AccessFlags.TransferWriteBit,
            PipelineStageFlags.AccelerationStructureBuildBitKhr,
            AccessFlags.AccelerationStructureReadBitKhr);
        End(upload);
        Submit(upload);
        Free(staging);

        BuildBlas();
    }

    private void BuildBlas()
    {
        AccelerationStructureGeometryTrianglesDataKHR tris = new()
        {
            VertexFormat = Format.R32G32B32Sfloat,
            VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = _vertexBuffer!.DeviceAddress },
            IndexType = IndexType.NoneKhr,
            VertexStride = 12,
            MaxVertex = (_triangleCount * 3) - 1,
        };
        _geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.TrianglesKhr,
            // Opaque: coverage callbacks are not ported — the KD tracer treats
            // transparent triangles as opaque too (TestLines' own docs say
            // so), and the parity contract is against that behaviour.
            Flags = GeometryFlagsKHR.OpaqueBitKhr,
        };
        _geometry.Geometry.Triangles = tris;

        AccelerationStructureBuildGeometryInfoKHR info = new()
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1,
            PGeometries = (AccelerationStructureGeometryKHR*)Unsafe.AsPointer(ref _geometry),
        };

        uint primCount = _triangleCount;
        AccelerationStructureBuildSizesInfoKHR sizes = _blasApi.GetAccelerationStructureBuildSizes(
            _device, AccelerationStructureBuildTypeKHR.DeviceKhr, &info, &primCount);
        ulong asSize = Math.Max(16UL, sizes.AccelerationStructureSize);
        ulong scratchSize = Math.Max(16UL, ((ulong)sizes.BuildScratchSize + 255UL) & ~255UL);

        // ACCELERATION_STRUCTURE_STORAGE_BIT_KHR on the backing buffer is spec
        // required; lavapipe silently produced an empty BLAS without it (the
        // all-miss blocker's second contributor).
        _asBuffer = Allocate(asSize, MemoryPropertyFlags.DeviceLocalBit,
            BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.StorageBufferBit
            | BufferUsageFlags.AccelerationStructureStorageBitKhr,
            deviceAddress: false);
        GpuBuffer scratch = Allocate(scratchSize, MemoryPropertyFlags.DeviceLocalBit,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            deviceAddress: true);
        AccelerationStructureCreateInfoKHR asci = new()
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = _asBuffer.Vk,
            Size = asSize,
            Type = AccelerationStructureTypeKHR.BottomLevelKhr,
        };
        ThrowOn(_blasApi.CreateAccelerationStructure(_device, &asci, null, out _blasHandle),
            "vkCreateAccelerationStructureKHR");

        AccelerationStructureBuildRangeInfoKHR range = new() { PrimitiveCount = _triangleCount };
        AccelerationStructureBuildRangeInfoKHR* rangePtr = &range;
        info.DstAccelerationStructure = _blasHandle;
        info.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = scratch.DeviceAddress };

        CommandBuffer cmd = Begin();
        _blasApi.CmdBuildAccelerationStructures(cmd, 1, &info, &rangePtr);
        // The build's write must be visible to the kernel's AS reads.
        Barrier(
            cmd,
            PipelineStageFlags.AccelerationStructureBuildBitKhr,
            AccessFlags.AccelerationStructureWriteBitKhr,
            PipelineStageFlags.ComputeShaderBit,
            AccessFlags.ShaderReadBit);
        End(cmd);
        Submit(cmd);
        Free(scratch);

        UpdateBlasBinding();
    }

    /// <summary>
    /// Creates the one descriptor set and binds the pinned ray/out buffers.
    /// Runs once at construction; the BLAS binding is refreshed by
    /// <see cref="UpdateBlasBinding"/> on each build (self-test scene, then
    /// the real scene). Updating only binding 2 is legal because every
    /// dispatch's fence has signalled before the next build runs — the queue
    /// is never in flight while its descriptors are rewritten.
    /// </summary>
    private void CreateDescriptorSet()
    {
        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[2]
        {
            new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 2 },
            new DescriptorPoolSize { Type = DescriptorType.AccelerationStructureKhr, DescriptorCount = 1 },
        };
        DescriptorPoolCreateInfo dpci = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
            MaxSets = 1,
        };
        ThrowOn(_vk.CreateDescriptorPool(_device, &dpci, null, out _descriptorPool),
            "vkCreateDescriptorPool");
        DescriptorSetLayout layout = _descriptorLayout;
        DescriptorSetAllocateInfo dsai = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        ThrowOn(_vk.AllocateDescriptorSets(_device, &dsai, out _descriptorSet),
            "vkAllocateDescriptorSets");
        Silk.NET.Vulkan.DescriptorBufferInfo* infos = stackalloc Silk.NET.Vulkan.DescriptorBufferInfo[2];
        infos[0] = new Silk.NET.Vulkan.DescriptorBufferInfo
        {
            Buffer = _rayBuffer!.Vk,
            Offset = 0,
            Range = _rayBuffer.Size,
        };
        infos[1] = new Silk.NET.Vulkan.DescriptorBufferInfo
        {
            Buffer = _outBuffer!.Vk,
            Offset = 0,
            Range = _outBuffer.Size,
        };
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[3];
        for (int i = 0; i < 2; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSet,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = infos + i,
            };
        }

        fixed (AccelerationStructureKHR* blasPtr = &_blasHandle)
        {
            WriteDescriptorSetAccelerationStructureKHR asWrite = new()
            {
                SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
                AccelerationStructureCount = 1,
                PAccelerationStructures = blasPtr,
            };
            writes[2] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                PNext = &asWrite,
                DstSet = _descriptorSet,
                DstBinding = 2,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.AccelerationStructureKhr,
            };
            _vk.UpdateDescriptorSets(_device, 3, writes, 0, null);
        }
    }

    /// <summary>Refreshes the BLAS binding of the pinned set after a rebuild.</summary>
    private void UpdateBlasBinding()
    {
        fixed (AccelerationStructureKHR* blasPtr = &_blasHandle)
        {
            WriteDescriptorSetAccelerationStructureKHR asWrite = new()
            {
                SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
                AccelerationStructureCount = 1,
                PAccelerationStructures = blasPtr,
            };
            WriteDescriptorSet asBinding = new()
            {
                SType = StructureType.WriteDescriptorSet,
                PNext = &asWrite,
                DstSet = _descriptorSet,
                DstBinding = 2,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.AccelerationStructureKhr,
            };
            _vk.UpdateDescriptorSets(_device, 1, &asBinding, 0, null);
        }
    }

    /// <summary>
    /// Hands out the pinned host staging for <paramref name="rayCount"/> rays
    /// in the 8-float wire layout — two vec4 per ray: <c>(ox,oy,oz,0)</c> and
    /// <c>(dx,dy,dz,tmax)</c>. The caller packs straight into pinned memory,
    /// so a batch is copied once, by the upload's <c>vkCmdCopyBuffer</c>.
    /// </summary>
    /// <param name="rayCount">Rays the following dispatch will carry; at most <see cref="MaxSlabRays"/>.</param>
    /// <returns>A span of <c>8 * rayCount</c> floats to fill.</returns>
    /// <exception cref="VulkanException">The slab exceeds the pinned buffers.</exception>
    public Span<float> StageRays(int rayCount)
    {
        ulong rayBytes = (ulong)rayCount * 32UL;
        if (rayBytes > _hostRays!.Size)
        {
            throw new VulkanException(Result.ErrorFragmentation,
                $"a {rayCount}-ray slab exceeds the pinned slab buffers ({MaxSlabRays} rays)", null);
        }

        return new Span<float>((void*)_hostRays.Mapped, rayCount * 8);
    }

    /// <summary>
    /// Uploads the staged rays, dispatches one kernel mode over them, and
    /// downloads the raw out words. Every ordering between those three stages
    /// is an explicit barrier in the command buffer.
    /// </summary>
    /// <param name="mode">Kernel mode: 0 any-hit, 1 closest, 4 readback sanity, 5 telemetry.</param>
    /// <param name="rayCount">Rays staged since the last call.</param>
    /// <param name="outWords">Raw out words: 2/ray for modes 1/5, 2/workgroup for 0/4.</param>
    /// <param name="tminBits">Ray epsilon as float bits.</param>
    /// <param name="tmaxScaleBits">Any-hit tmax scale (<c>1 - 2^-23</c>) as float bits.</param>
    /// <exception cref="VulkanException">Dispatch, sync, or copy failure, including the driver-hang timeout.</exception>
    public void Dispatch(int mode, int rayCount, Span<uint> outWords, uint tminBits, uint tmaxScaleBits)
    {
        // A partial tail workgroup is fine and normal: the kernel guards every
        // lane by index < rayCount, so the dispatch covers ceil(rays/64) and
        // the tail lanes run no query at all. Only the slab cap is a hard
        // bound.
        ulong rayBytes = (ulong)rayCount * 32UL;
        ulong outBytes = (ulong)outWords.Length * sizeof(uint);
        if (outBytes > _hostOut!.Size)
        {
            throw new VulkanException(Result.ErrorFragmentation,
                $"a {rayCount}-ray slab's output exceeds the pinned output buffer", null);
        }
        CommandBuffer upload = Begin();
        BufferCopy region = new() { SrcOffset = 0, DstOffset = 0, Size = rayBytes };
        _vk.CmdCopyBuffer(upload, _hostRays!.Vk, _rayBuffer!.Vk, 1, &region);
        // The upload's write must be visible to the kernel's ray reads.
        Barrier(
            upload,
            PipelineStageFlags.TransferBit,
            AccessFlags.TransferWriteBit,
            PipelineStageFlags.ComputeShaderBit,
            AccessFlags.ShaderReadBit);
        End(upload);
        Submit(upload);

        CommandBuffer compute = Begin();
        _vk.CmdBindPipeline(compute, PipelineBindPoint.Compute, _pipeline);
        DescriptorSet set = _descriptorSet;
        _vk.CmdBindDescriptorSets(compute, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        uint* pc = stackalloc uint[8];
        pc[0] = (uint)mode;
        pc[1] = (uint)rayCount;
        pc[2] = _triangleCount;
        pc[3] = 0;
        pc[4] = tminBits;
        pc[5] = tmaxScaleBits;
        _vk.CmdPushConstants(compute, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, 32, pc);
        _vk.CmdDispatch(compute, (uint)((rayCount + Invocations - 1) / Invocations), 1, 1);
        // The kernel's writes to OUT must be visible to the download's reads:
        // an explicit barrier inside the submit, not same-queue luck.
        Barrier(
            compute,
            PipelineStageFlags.ComputeShaderBit,
            AccessFlags.ShaderWriteBit,
            PipelineStageFlags.TransferBit,
            AccessFlags.TransferReadBit);
        BufferCopy download = new() { SrcOffset = 0, DstOffset = 0, Size = outBytes };
        _vk.CmdCopyBuffer(compute, _outBuffer!.Vk, _hostOut!.Vk, 1, &download);
        End(compute);
        Submit(compute);

        new ReadOnlySpan<uint>((void*)_hostOut.Mapped, outWords.Length).CopyTo(outWords);
    }

    /// <summary>Rays the known-hit micro-scene dispatches: 2 real, padded to one workgroup.</summary>
    internal const int SelfTestRays = Invocations;

    /// <summary>How many of them are real; the padded lanes are zero and degenerate.</summary>
    internal const int SelfTestRealRays = 2;

    /// <summary>
    /// The capability gate: traces a two-triangle known-hit micro-scene
    /// through every kernel mode and decides whether THIS device answers
    /// ray queries at all. Two known failure classes are named by their
    /// telemetry, not guessed at:
    /// <list type="bullet">
    /// <item><description>
    /// Mesa lavapipe: candidates found, committed never — proceed returns
    /// true, iterations count up, candidates count up, yet modes 0/1 see no
    /// committed intersection. A driver bug; the only mitigation is rejecting
    /// the device.</description></item>
    /// <item><description>
    /// nvidia from a compute-only queue: proceed-iterations are zero on
    /// every ray — the driver never traverses at all.</description></item>
    /// </list>
    /// </summary>
    /// <returns>Whether to trust the device, and the telemetry either way.</returns>
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
        SelfTestGeometry(out float[] vertices, out float[] rays, out int rayCount);
        // The self-test runs through the SAME upload + BLAS-build machinery a
        // real trace uses, so an empty BLAS from a bad build fails the gate
        // too (that was the lavapipe all-miss's second contributor).
        LoadScene(vertices);
        uint tminBits = 0x3A83126Fu; // 1e-3f
        const int OutWordsAny = 2;   // one workgroup

        // Mode 4 first: pure compute-write/copy/readback. If this reads back
        // zeroes, nothing about ray answers means anything yet.
        uint[] w = new uint[Math.Max(OutWordsAny, SelfTestRays * 2)];
        DispatchWithStagedRays(4, SelfTestRays, rays, w.AsSpan(0, OutWordsAny), tminBits, TmaxScaleBits);
        bool readbackOk = w[0] == 0xFFFFFFFFu && w[1] == 0xFFFFFFFFu;

        bool anyHitOk = false;
        bool closestOk = false;
        int iters = 0;
        int cands = 0;
        if (readbackOk)
        {
            DispatchWithStagedRays(0, SelfTestRays, rays, w.AsSpan(0, OutWordsAny), tminBits, TmaxScaleBits);
            anyHitOk = (w[0] & 3u) == 3u; // both rays blocked

            DispatchWithStagedRays(1, SelfTestRays, rays, w.AsSpan(0, SelfTestRays * 2), tminBits, TmaxScaleBits);
            closestOk =
                w[0] == 0u // ray A hits triangle 0
                && Math.Abs(BitConverter.Int32BitsToSingle(unchecked((int)w[1])) - 0.5f) < 1e-4f
                && w[2] == 1u // ray B hits triangle 1
                && Math.Abs(BitConverter.Int32BitsToSingle(unchecked((int)w[3])) - 0.5f) < 1e-4f;

            // Telemetry decides WHICH broken device this is, for the report.
            DispatchWithStagedRays(5, SelfTestRays, rays, w.AsSpan(0, SelfTestRays * 2), tminBits, TmaxScaleBits);
            for (int i = 0; i < SelfTestRays; i++)
            {
                iters = Math.Max(iters, unchecked((int)w[i * 2]));
                cands += w[(i * 2) + 1] != 0 ? 1 : 0;
            }
        }

        bool passed = readbackOk && anyHitOk && closestOk;
        return (passed, new SelfTestOutcome(readbackOk, anyHitOk, closestOk, iters, cands));
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

    /// <summary>Uploads pre-built wire rays and dispatches one mode (the self-test's path).</summary>
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
        StageRays(rayCount);
        rays.CopyTo(new Span<float>((void*)_hostRays!.Mapped, rays.Length));
        Dispatch(mode, rayCount, outWords, tminBits, tmaxScaleBits);
    }


    private void Submit(CommandBuffer cmd)
    {
        SubmitInfo si = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        ThrowOn(_vk.QueueSubmit(_queue, 1, &si, _fence), "vkQueueSubmit");
        const ulong TimeoutNs = 120UL * 1_000_000_000;
        Result r = _vk.WaitForFences(_device, 1, in _fence, true, TimeoutNs);
        if (r == Result.Timeout)
        {
            throw new VulkanException(
                Result.Timeout,
                "driver hang: fence did not signal within 120 s (a compute-only device that never "
                + "traverses ray queries shows this signature)",
                null);
        }

        ThrowOn(r, "vkWaitForFences");
        ThrowOn(_vk.ResetFences(_device, 1, in _fence), "vkResetFences");
        _vk.FreeCommandBuffers(_device, _commandPool, 1, &cmd);
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

        _blasApi?.DestroyAccelerationStructure(_device, _blasHandle, null);
        Free(_asBuffer);
        Free(_outBuffer);
        Free(_rayBuffer);
        Free(_hostOut);
        Free(_hostRays);
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

        _vk.DeviceWaitIdle(_device);
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

/// <summary>One row of the device inventory <see cref="VulkanDevice.ProbeDevices"/> reports.</summary>
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
public readonly record struct SelfTestOutcome(bool ReadbackOk, bool AnyHitOk, bool ClosestOk, int Iters, int Candidates);
