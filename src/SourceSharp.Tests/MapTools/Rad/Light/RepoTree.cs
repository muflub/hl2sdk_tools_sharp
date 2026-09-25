namespace SourceSharp.Tests.MapTools.Rad.Light;

/// <summary>
/// Locates the checkout a test binary was built from.
/// </summary>
/// <remarks>
/// The walk stops at the FIRST directory holding a <c>.git</c> entry, file or
/// directory. A worktree's root holds a <c>.git</c> FILE, so a binary built in
/// a worktree resolves to that worktree and never escapes upward into the main
/// checkout that encloses it.
/// </remarks>
internal static class RepoTree
{
    /// <summary>Finds the nearest checkout root at or above a directory.</summary>
    /// <param name="start">The directory to start from.</param>
    /// <returns>The root, or <see langword="null"/> when there is none.</returns>
    internal static string? FindRoot(string start)
    {
        for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
        {
            string marker = Path.Combine(dir.FullName, ".git");
            if (File.Exists(marker) || Directory.Exists(marker))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
