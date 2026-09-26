//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapGen;

namespace SourceSharp.Tests.MapFormats.Assets;

/// <summary>
/// The asset files this tree commits, which a fact can therefore read without
/// a skip.
/// </summary>
/// <remarks>
/// There is exactly one, and it is a good one: <c>new_tf2_logo.vtf</c> is a
/// 1.4 MB, 2048x512 DXT5 texture at VTF version 7.4 with a resource table and
/// a low-res thumbnail. Its file length is the sum of a header size, a
/// thumbnail and a twelve-level mip chain, so a single length comparison
/// exercises the entire offset arithmetic.
/// </remarks>
internal static class GoldenAssets
{
    private static readonly string[] LogoParts =
        ["game", "mod_tf", "materials", "logo", "new_tf2_logo.vtf"];

    /// <summary>The committed TF2 logo texture of the checkout this binary belongs to.</summary>
    /// <returns>An absolute path.</returns>
    /// <exception cref="InvalidOperationException">
    /// The enclosing checkout could not be found, or does not hold the file.
    /// Thrown rather than skipped: the file is tracked, so its absence means
    /// the layout moved.
    /// </exception>
    public static string TfLogoVtf()
    {
        string? root = RepoTree.FindRoot(AppContext.BaseDirectory);
        if (root is null)
        {
            throw new InvalidOperationException(
                $"no checkout root at or above \"{AppContext.BaseDirectory}\". The golden VTF is "
                + "located relative to the tree this binary was built from, so that the fact "
                + "reads THIS worktree's file and not some enclosing checkout's.");
        }

        string path = Path.Combine(root, Path.Combine(LogoParts));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{path} is missing. It is a COMMITTED file, not build output, so this is "
                + "something having moved or deleted a tracked file.");
        }

        return path;
    }
}
