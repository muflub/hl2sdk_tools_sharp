//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapTools.Phys;

/// <summary>
/// The float-state guard around the native cooker, with no native library:
/// <see cref="FloatEnvironment"/> saves and restores the thread's MXCSR,
/// <see cref="NativeScope"/> holds the library's environment for one call,
/// and <see cref="DenormalProbe"/> reports what the thread does when it runs,
/// not what it did when the JIT compiled it.
/// </summary>
/// <remarks>
/// <para>
/// The vphysics library turns on flush-to-zero and denormals-are-zero on the
/// thread that loads it, and every thread that thread creates inherits them.
/// The guard confines that to the native calls. The facts that watched it
/// with the real library went with the native tier; these set FTZ/DAZ by
/// hand instead, so the guard is checked on every Linux x64 run.
/// </para>
/// <para>
/// The probe's failure was Release-only: RyuJIT folded the denormal into a
/// float constant, and a thread in FTZ/DAZ at JIT time folded it to +0.0 for
/// good. CI runs the suite in Release, which is where the probe fact bites;
/// in Debug it still holds, it just cannot catch that regression.
/// </para>
/// <para>
/// Every fact runs its body on a thread of its own: MXCSR is per thread, and
/// a fact that failed halfway through would otherwise leave FTZ on a pool
/// thread the next fact (any fact, in this parallel suite) runs on.
/// </para>
/// </remarks>
public sealed class FloatEnvironmentTests
{
    [LinuxX64Fact]
    public void TheFtzProbeMeasuresTheThreadNotTheJit()
    {
        // The probe's first call in this process happens here, with FTZ and
        // DAZ forced on, so the JIT compiles it in the environment the
        // cooker thread compiles it in. The answer afterwards, on the same
        // compiled code with the thread restored, must track MXCSR again.
        (bool underFtz, bool afterRestore) = OnOwnThread(() => new DenormalProbe().RunUnderForcedFtz());

        Assert.True(underFtz);
        Assert.False(afterRestore);
    }

    [LinuxX64Fact]
    public void SettingMxcsrTakesEffectAndASavedEnvironmentRestoresIt()
    {
        (uint before, uint forced, uint restored) = OnOwnThread(() =>
        {
            FloatEnv saved = FloatEnvironment.Get();
            uint before = FloatEnvironment.GetMxcsr();
            uint forced;
            try
            {
                FloatEnvironment.SetMxcsr(before | DenormalProbe.FtzDaz);
                forced = FloatEnvironment.GetMxcsr();
            }
            finally
            {
                FloatEnvironment.Set(saved);
            }

            return (before, forced, FloatEnvironment.GetMxcsr());
        });

        // A fresh .NET thread starts IEEE: neither bit is set.
        Assert.Equal(0u, before & DenormalProbe.FtzDaz);
        Assert.Equal(DenormalProbe.FtzDaz, forced & DenormalProbe.FtzDaz);
        // Only the two bits changed, and the saved environment put them back.
        Assert.Equal(before, forced & ~DenormalProbe.FtzDaz);
        Assert.Equal(before, restored);
    }

    [LinuxX64Fact]
    public void ANativeScopeHoldsTheLibrarysEnvironmentOnlyWhileItIsOpen()
    {
        (uint inside, uint afterwards, uint afterSecondDispose) = OnOwnThread(() =>
        {
            FloatEnv library = LibraryEnvironment();
            NativeScope scope = new(library);
            uint inside = FloatEnvironment.GetMxcsr();
            scope.Dispose();
            uint afterwards = FloatEnvironment.GetMxcsr();

            // A second Dispose restores nothing: it must not put back the
            // environment saved when the scope opened over a thread that has
            // moved on since.
            FloatEnvironment.SetMxcsr(afterwards | (1u << 13));   // round down, a visible change
            scope.Dispose();
            uint second = FloatEnvironment.GetMxcsr();
            FloatEnvironment.SetMxcsr(afterwards);
            return (inside, afterwards, second);
        });

        Assert.Equal(DenormalProbe.FtzDaz, inside & DenormalProbe.FtzDaz);
        Assert.Equal(0u, afterwards & DenormalProbe.FtzDaz);
        Assert.Equal(afterwards | (1u << 13), afterSecondDispose);
    }

    [LinuxX64Fact]
    public void ANativeScopeRestoresTheCallersEnvironmentWhenTheCallThrows()
    {
        (uint before, uint after) = OnOwnThread(() =>
        {
            uint before = FloatEnvironment.GetMxcsr();
            FloatEnv library = LibraryEnvironment();
            try
            {
                using NativeScope scope = new(library);
                throw new InvalidOperationException("the native call failed");
            }
            catch (InvalidOperationException)
            {
            }

            return (before, FloatEnvironment.GetMxcsr());
        });

        Assert.Equal(before, after);
    }

    /// <summary>
    /// An environment like the one the library leaves on its loading thread
    /// (FTZ and DAZ on), captured and then undone so the thread is IEEE again.
    /// </summary>
    private static FloatEnv LibraryEnvironment()
    {
        FloatEnv ieee = FloatEnvironment.Get();
        FloatEnvironment.SetMxcsr(FloatEnvironment.GetMxcsr() | DenormalProbe.FtzDaz);
        FloatEnv library = FloatEnvironment.Get();
        FloatEnvironment.Set(ieee);
        return library;
    }

    private static T OnOwnThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("the fact's thread failed", failure);
        }

        return result;
    }

    /// <summary>
    /// Runs only on Linux x64, where the guard can work at all.
    /// </summary>
    /// <remarks>
    /// <see cref="FloatEnvironment"/> calls glibc's <c>fegetenv</c> and
    /// <c>fesetenv</c> in <c>libm.so.6</c> and reads MXCSR at its offset in
    /// the x86-64 <c>fenv_t</c>. The native cooker it guards loads the 64-bit
    /// Linux <c>vphysics.so</c>, so no other platform ever runs it: Windows
    /// and macOS have no <c>libm.so.6</c>, and on arm64 the environment holds
    /// FPCR, whose flush-to-zero bit is a different bit in a different layout,
    /// so reading "MXCSR" there would read something else.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method)]
    private sealed class LinuxX64FactAttribute : FactAttribute
    {
        public LinuxX64FactAttribute()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                Skip = "the float-state guard is glibc x86-64 only (fenv_t's MXCSR through libm.so.6), "
                     + "like the native cooker it guards; this is "
                     + RuntimeInformation.OSDescription + " " + RuntimeInformation.ProcessArchitecture;
            }
        }
    }
}
