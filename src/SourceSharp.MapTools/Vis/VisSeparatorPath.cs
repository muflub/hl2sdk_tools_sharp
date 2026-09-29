//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Vis;

/// <summary>
/// Which implementation of the portal flow's separator clip vvis runs. Both
/// give the same bytes; they differ only in speed, and which is faster
/// depends on the CPU.
/// </summary>
/// <remarks>
/// <para>
/// The separator clip is where vvis spends its time: deriving the planes
/// that separate a frame's source and pass portals, and chopping each
/// candidate by them. There are two implementations.
/// </para>
/// <para>
/// <see cref="Vector256"/> stores each frame's planes as a <c>Vec3</c> and a
/// distance each, derives them lazily one or two source edges at a time as a
/// clip reaches them (two at once in the halves of a 256-bit register), and
/// chops the target by one plane at a time with the target held transposed.
/// </para>
/// <para>
/// <see cref="Vector512"/> stores the planes as four columns, derives a
/// frame's whole list at the first clip that needs any of it -- four source
/// edges at once, one per 128-bit quarter of a 512-bit register, so a quad's
/// whole list is one square-root-and-divide chain -- and tests the target
/// against eight consecutive planes at once, one plane per lane, skipping
/// straight to the first that has a point behind it. It derives slightly
/// more than the lazy path (on 2fort a frame's forward list is used to its
/// end in 87 % of the frames that use it at all, the reverse in 99 %) and
/// pays for it with fewer, wider derivations.
/// </para>
/// <para>
/// <b>Measured.</b> On 2fort, byte-identical either way (vvis
/// <c>303f56e0…</c>). On a Ryzen 9 9950X (Zen 5, full-width 512-bit units)
/// the Vector512 path was 4.6 % less wall time at 16 threads and 2.9 % less at
/// 32, with CPU time down by the same. On an Ice Lake-class Xeon it was about
/// 10 % SLOWER: that core has AVX-512, but runs 512-bit double square root and
/// divide at reduced throughput, and the derivation is built on them. Zen 4
/// splits 512-bit operations into two 256-bit halves and has not been
/// measured, so it is not assumed to win.
/// </para>
/// <para>
/// Hence <see cref="Auto"/>'s rule, <see cref="VisSeparatorPaths.Resolve"/>:
/// Vector512 only where the runtime accelerates 512-bit vectors AND the CPU
/// is AMD family <c>0x1A</c> (Zen 5) or later; Vector256 everywhere else.
/// Either may be forced on any machine: .NET runs a vector width the
/// hardware lacks in software, slowly and exactly, which is also how the
/// equivalence facts run both paths on every CI runner.
/// </para>
/// </remarks>
public enum VisSeparatorPath
{
    /// <summary>
    /// Chosen per compile from the CPU by <see cref="VisSeparatorPaths.Resolve"/>.
    /// The default.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// The lazy, one-plane-at-a-time clip with 128- and 256-bit derivation.
    /// </summary>
    Vector256 = 1,

    /// <summary>
    /// The columnar, eight-planes-at-a-time clip with the whole list derived
    /// four edges at once in 512-bit registers.
    /// </summary>
    Vector512 = 2,
}

/// <summary>
/// Turns a requested <see cref="VisSeparatorPath"/> into the one a compile
/// runs.
/// </summary>
public static class VisSeparatorPaths
{
    /// <summary>
    /// The first AMD CPU family whose 512-bit units are full width: Zen 5.
    /// </summary>
    public const int FullWidthAmdFamily = 0x1A;

    /// <summary>
    /// The path a compile runs for a request on a given CPU.
    /// </summary>
    /// <param name="requested">What the options asked for.</param>
    /// <param name="cpu">The CPU the compile runs on.</param>
    /// <returns>
    /// <see cref="VisSeparatorPath.Vector256"/> or
    /// <see cref="VisSeparatorPath.Vector512"/>, never
    /// <see cref="VisSeparatorPath.Auto"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="cpu"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="requested"/> is not a defined value.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A concrete request is returned unchanged whatever the CPU: forcing a
    /// path is how it is measured on a machine <see cref="VisSeparatorPath.Auto"/>
    /// would not pick it for.
    /// </para>
    /// <para>
    /// <see cref="VisSeparatorPath.Auto"/> takes Vector512 only when all three
    /// hold: the runtime accelerates 512-bit vectors, the vendor is AMD, and
    /// the family is at least <see cref="FullWidthAmdFamily"/>. Every Intel CPU
    /// gets Vector256, because the one measured with AVX-512 was slower on it;
    /// so does Zen 4 (family <c>0x19</c>), unmeasured and double-pumped; and so
    /// does any CPU whose runtime does not accelerate 512-bit vectors, where the
    /// wide path would run in software. See <see cref="VisSeparatorPath"/> for
    /// the measurements.
    /// </para>
    /// </remarks>
    public static VisSeparatorPath Resolve(VisSeparatorPath requested, CpuCapabilities cpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        return requested switch
        {
            VisSeparatorPath.Auto when cpu.Vector512Accelerated && cpu.IsAmd && cpu.Family >= FullWidthAmdFamily
                => VisSeparatorPath.Vector512,
            VisSeparatorPath.Auto => VisSeparatorPath.Vector256,
            VisSeparatorPath.Vector256 or VisSeparatorPath.Vector512 => requested,
            _ => throw new ArgumentOutOfRangeException(nameof(requested), requested, "not a separator path"),
        };
    }
}
