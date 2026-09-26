//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapGen;

using Xunit;

namespace SourceSharp.Tests.MapFormats;

/// <summary>
/// The compiled maps this tree can compare a managed BSP against.
/// </summary>
/// <remarks>
/// <para>
/// There are two, and they are not equivalent. <c>dm_lockdown.bsp</c> is
/// COMMITTED, so every checkout and every worktree has it and a fact over it
/// can never skip; <c>ss_sandbox.bsp</c> is build output, gitignored, and
/// absent until someone runs <c>make map</c>.
/// </para>
/// <para>
/// That difference is why the committed one carries the round-trip gate. This
/// project has already had a verdict decided by gitignored build output being
/// present in one tree and missing in another, where the missing input read as
/// a pass. A gate whose subject is guaranteed present cannot fail that way.
/// </para>
/// </remarks>
internal static class GoldenBsp
{
    /// <summary>Where the committed golden map sits inside a checkout.</summary>
    private static readonly string[] LockdownParts =
        ["game", "mod_sharp", "maps", "dm_lockdown.bsp"];

    /// <summary>
    /// The committed golden map of the checkout this test binary belongs to.
    /// </summary>
    /// <returns>An absolute path.</returns>
    /// <exception cref="InvalidOperationException">
    /// The enclosing checkout could not be found, or does not hold the map.
    /// Both are thrown rather than skipped: the file is tracked, so its absence
    /// means the layout moved, and a fact that quietly skips on a broken
    /// assumption is how a real gap stays open.
    /// </exception>
    public static string Lockdown()
    {
        string? root = RepoTree.FindRoot(AppContext.BaseDirectory);
        if (root is null)
        {
            throw new InvalidOperationException(
                $"no checkout root at or above \"{AppContext.BaseDirectory}\". The golden BSP is "
                + "located relative to the tree this binary was built from, so that the fact "
                + "reads THIS worktree's file and not some enclosing checkout's.");
        }

        string path = Path.Combine(root, Path.Combine(LockdownParts));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{path} is missing. It is a COMMITTED file, not build output, so this is not "
                + "the legitimate \"nobody has compiled a map here yet\" case that "
                + $"{nameof(SandboxMapFactAttribute)} skips on: something has moved or deleted a "
                + "tracked file.");
        }

        return path;
    }

    /// <summary>
    /// The generated sandbox map, when this tree has compiled one.
    /// </summary>
    /// <returns>An absolute path, or null when the tree has no compiled map.</returns>
    public static string? Sandbox()
    {
        SandboxMapLookup lookup = SandboxArtefact.Locate(AppContext.BaseDirectory);
        return lookup.State == SandboxMapState.Present ? lookup.Path : null;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips when this tree has no compiled
/// sandbox map, naming the reason in the runner's output.
/// </summary>
/// <remarks>
/// A thin alias for <see cref="SandboxMapFactAttribute"/>, which already
/// encodes the judgement, kept in this namespace so the map-format facts read
/// consistently.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SandboxBspFactAttribute : FactAttribute
{
    /// <summary>Skips unless the running binary's own tree holds a compiled map.</summary>
    public SandboxBspFactAttribute()
    {
        SandboxMapLookup lookup = SandboxArtefact.Locate(AppContext.BaseDirectory);
        if (lookup.State == SandboxMapState.NoMapInTree)
        {
            Skip = lookup.Explanation;
        }
    }
}
