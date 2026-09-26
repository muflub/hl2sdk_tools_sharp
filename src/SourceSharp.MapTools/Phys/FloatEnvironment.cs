//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SourceSharp.MapTools.Phys;

/// <summary>A saved C floating-point environment (<c>fenv_t</c>, 32 bytes on x86-64 glibc; room to spare).</summary>
internal unsafe struct FloatEnv
{
    /// <summary>Byte offset of <c>__mxcsr</c> in glibc's 32-byte <c>fenv_t</c> (measured: <c>offsetof</c>).</summary>
    public const int MxcsrOffset = 28;

    public fixed byte Bytes[64];
}

/// <summary>
/// <c>fegetenv</c>/<c>fesetenv</c>: the x87 control word and MXCSR of the
/// calling thread.
/// </summary>
/// <remarks>
/// WHY: the SDK build of <c>vphysics.so</c> links <c>crtfastmath</c>, whose
/// constructor turns on flush-to-zero and denormals-are-zero in MXCSR on the
/// thread that loads it. MXCSR is per thread, but a thread CREATED by that
/// thread inherits it (POSIX: the floating-point environment is inherited),
/// and the .NET thread pool creates workers from whichever thread asks, the
/// cooker thread included -- measured: a pool thread resuming after a cook
/// flushed denormals. So the cooker keeps the library's environment for the
/// duration of each native call only, and IEEE everywhere else.
/// </remarks>
internal static unsafe partial class FloatEnvironment
{
    public static FloatEnv Get()
    {
        FloatEnv env;
        if (fegetenv(&env) != 0)
        {
            throw new InvalidOperationException("fegetenv failed.");
        }

        return env;
    }

    public static void Set(FloatEnv env)
    {
        if (fesetenv(&env) != 0)
        {
            throw new InvalidOperationException("fesetenv failed.");
        }
    }

    /// <summary>The thread's MXCSR, read through a saved environment.</summary>
    /// <returns>The MXCSR bits.</returns>
    public static uint GetMxcsr()
    {
        FloatEnv env = Get();
        return *(uint*)&env.Bytes[FloatEnv.MxcsrOffset];
    }

    /// <summary>Replaces the thread's MXCSR, keeping the rest of the environment.</summary>
    /// <param name="mxcsr">The new MXCSR bits.</param>
    public static void SetMxcsr(uint mxcsr)
    {
        FloatEnv env = Get();
        *(uint*)&env.Bytes[FloatEnv.MxcsrOffset] = mxcsr;
        Set(env);
    }

    [LibraryImport("libm.so.6")]
    private static partial int fegetenv(FloatEnv* env);

    [LibraryImport("libm.so.6")]
    private static partial int fesetenv(FloatEnv* env);
}

/// <summary>
/// Produces the denormal the cook's flush-to-zero probe multiplies.
/// </summary>
/// <remarks>
/// WHY the bits live in an instance field of a heap object: writing
/// <c>BitConverter.Int32BitsToSingle(0x00000100)</c> inline is a lie on a
/// thread that is in FTZ/DAZ when the enclosing method is first JIT-compiled.
/// Release's RyuJIT constant-folds the call and materialises the result as
/// the method's float constant -- measured: identical IL, compiled twice on
/// one thread, emits <c>vmov eax,0x100; vmovd xmm0,eax</c> under IEEE and
/// <c>vxorps xmm0,xmm0,xmm0</c> under FTZ/DAZ: DAZ reads the denormal as zero
/// while the JIT is doing its work (the JIT's own folding uses the FPU), and
/// +0.0 is baked into the shared native code, so the probe then reports
/// "flushes" on an IEEE thread forever -- the Release-only gate failure.
/// A volatile field read cannot constant-fold (nothing is constant to the
/// JIT), an SSE load is not flushed (only arithmetic and MXCSR stores flush),
/// and tier0-to-tier1 promotion no longer matters: no compilation context
/// can change the answer. The field is an INSTANCE field because the library
/// rule (<c>LibraryRuleTests</c>) bans mutable statics in MapTools.
/// </remarks>
internal sealed class DenormalProbe
{
    /// <summary>MXCSR's flush-to-zero (bit 15) and denormals-are-zero (bit 6) bits.</summary>
    internal const uint FtzDaz = (1u << 15) | (1u << 6);

    private volatile int _bits = 0x00000100;

    /// <summary>The smallest positive float, loaded from the heap.</summary>
    /// <returns>A denormal whose bits no compile could have flushed.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public float Tiny() => BitConverter.Int32BitsToSingle(_bits);

    /// <summary>
    /// Runs the probe on the current thread with FTZ and DAZ forced on, then
    /// again with the thread's environment restored.
    /// </summary>
    /// <returns>The probe's answer under forced FTZ and its answer after the
    /// restore, in that order.</returns>
    /// <remarks>
    /// FOR TESTS: pins the regression the cooker-thread flag exposed — the
    /// probe must answer from MXCSR when it RUNS. The first call is this
    /// shape's first-ever call, so the JIT compiles it with FTZ/DAZ in force:
    /// the exact context that once baked <c>+0.0</c> into the probe's native
    /// code and made the restored answer report "flushes" forever.
    /// </remarks>
    public (bool UnderFtz, bool AfterRestore) RunUnderForcedFtz()
    {
        FloatEnv ieee = FloatEnvironment.Get();
        bool underFtz;
        try
        {
            FloatEnvironment.SetMxcsr(FloatEnvironment.GetMxcsr() | FtzDaz);
            underFtz = VPhysicsCollisionCooker.FlushesDenormals(Tiny());
        }
        finally
        {
            FloatEnvironment.Set(ieee);
        }

        return (underFtz, VPhysicsCollisionCooker.FlushesDenormals(Tiny()));
    }
}
/// <summary>Runs one native call under the library's floating-point environment and restores the caller's.</summary>
internal unsafe struct NativeScope : IDisposable
{
    private FloatEnv _saved;
    private bool _active;

    public NativeScope(FloatEnv library)
    {
        _saved = FloatEnvironment.Get();
        FloatEnvironment.Set(library);
        _active = true;
    }

    public void Dispose()
    {
        if (_active)
        {
            FloatEnvironment.Set(_saved);
            _active = false;
        }
    }
}
