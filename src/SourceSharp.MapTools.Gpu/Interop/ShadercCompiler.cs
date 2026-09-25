using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.Shaderc;

using SilkShaderc = Silk.NET.Shaderc.Shaderc;

namespace SourceSharp.MapTools.Gpu.Interop;

/// <summary>
/// Compiles the ray-query kernel's GLSL to SPIR-V in-process.
/// </summary>
/// <remarks>
/// <para>
/// The plan's §10c rule is that shader code is the one place logic lives
/// outside C#, kept minimal and tested through <c>IRayTracer</c> facts.
/// Compiling in-process through shaderc (the same route the Phase 0f spike
/// proved) keeps that the only artifact: no <c>glslc</c> on PATH, no build-time
/// toolchain, no committed SPIR-V to go stale against the kernel source. This
/// box has neither <c>glslangValidator</c> nor <c>glslc</c> installed, as the
/// plan states, so an offline compile would also fail open on every
/// <c>dotnet build</c> that wants to recompile the kernel.
/// </para>
/// <para>
/// No mutable statics: each call loads the native library (idempotent
/// <see cref="System.Runtime.InteropServices.NativeLibrary.TryLoad(string, out nint)"/>) and asks Silk for its API, so the type
/// carries no cross-tracer state — every tracer compiles its own module at
/// creation and nothing leaks between instances.
/// </para>
/// </remarks>
internal static unsafe class ShadercCompiler
{
    /// <summary>Compiles one compute kernel, throwing a readable message on rejection.</summary>
    /// <param name="glsl">GLSL 460 source with <c>GL_EXT_ray_query</c>.</param>
    /// <param name="name">Shader name for diagnostics.</param>
    /// <returns>The SPIR-V module bytes.</returns>
    /// <exception cref="VulkanException">
    /// shaderc refused the source or its native library is not deployed. The
    /// driver's own error text is carried in the message: a kernel that will
    /// not compile is a capability failure the caller reports, not a crash.
    /// </exception>
    public static byte[] CompileGlsl(string glsl, string name)
    {
        SilkShaderc api = TryLoad();
        Compiler* compiler = api.CompilerInitialize();
        if (compiler is null)
        {
            throw new VulkanException(
                Silk.NET.Vulkan.Result.ErrorInitializationFailed,
                "shaderc compiler failed to initialise (Silk.NET.Shaderc.Native not deployed?)",
                null);
        }

        CompileOptions* options = api.CompileOptionsInitialize();
        api.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
        api.CompileOptionsSetTargetSpirv(options, SpirvVersion.Shaderc16);
        api.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
        byte[] srcBytes = Encoding.UTF8.GetBytes(glsl);
        CompilationResult* result;
        fixed (byte* src = srcBytes)
        {
            result = api.CompileIntoSpv(
                compiler, src, (nuint)srcBytes.Length, ShaderKind.ComputeShader, name, "main", options);
        }

        try
        {
            CompilationStatus status = api.ResultGetCompilationStatus(result);
            if (status != CompilationStatus.Success)
            {
                string msg = api.ResultGetErrorMessageS(result) ?? status.ToString();
                throw new VulkanException(
                    Silk.NET.Vulkan.Result.ErrorUnknown, "shaderc " + name + ": " + status + ": " + msg, null);
            }

            int len = checked((int)api.ResultGetLength(result));
            byte[] spirv = new byte[len];
            fixed (byte* dst = spirv)
            {
                Unsafe.CopyBlockUnaligned(dst, api.ResultGetBytes(result), (uint)len);
            }

            return spirv;
        }
        finally
        {
            api.ResultRelease(result);
            api.CompileOptionsRelease(options);
            api.CompilerRelease(compiler);
        }
    }

    private static SilkShaderc TryLoad()
    {
        // Preload the bundled native with an absolute path so the SONAME is in
        // the process; Silk's dlopen of the bare name then resolves from
        // cache. The spike's three search paths, unchanged: it is what made
        // in-process compilation work from a plain dotnet test run.
        string dir = AppContext.BaseDirectory;
        foreach (string rel in new[]
        {
            Path.Combine("runtimes", "linux-x64", "native", "libshaderc_shared.so"),
            "libshaderc_shared.so",
            Path.Combine("..", "runtimes", "linux-x64", "native", "libshaderc_shared.so"),
        })
        {
            string path = Path.Combine(dir, rel);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out _))
            {
                break;
            }
        }

        try
        {
            return SilkShaderc.GetApi();
        }
        catch (Exception e)
        {
            throw new VulkanException(
                Silk.NET.Vulkan.Result.ErrorInitializationFailed,
                "could not load libshaderc_shared (Silk.NET.Shaderc.Native not deployed?)",
                e);
        }
    }
}
