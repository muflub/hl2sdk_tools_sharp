using System;
using System.IO;

using Xunit;

namespace SourceSharp.Tests;

/// <summary>
/// A fact that needs one or more files from the repository next to the binary,
/// and is genuinely SKIPPED — not passed — when they are not there.
///
/// <para>
/// <b>WHY THIS EXISTS.</b> The shape it replaces is
/// <c>Assert.True(true, "…(skipped, not found)")</c> followed by a
/// <c>return</c>. That reports <b>PASSED</b>, not SKIPPED. A suite run from a
/// directory with no data tree beside it then reports a row of green for
/// checks that examined nothing, and the run is indistinguishable from one
/// where every parity check really held.
/// </para>
///
/// <para>
/// This one takes REPO-RELATIVE paths, so it reaches the committed fixtures
/// and corpus files anywhere in the tree, and takes several because a fact
/// that reads two files must skip if either is missing rather than half-run.
/// </para>
///
/// <para>
/// The search walks up from the test binary, which is the same way the facts
/// themselves find their inputs — so the attribute and the body cannot
/// disagree about whether the tree is present.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RepoSourceFactAttribute : FactAttribute
{
    /// <param name="relativePaths">
    /// Repo-relative paths, e.g. <c>src/public/const.h</c>. Forward slashes;
    /// they are split so the attribute reads the same on any platform.
    /// </param>
    public RepoSourceFactAttribute(params string[] relativePaths)
    {
        foreach (string relative in relativePaths)
        {
            if (Find(relative) is null)
            {
                Skip = $"{relative} is not above this binary — this fact reads the "
                     + "source tree it is a parity check against";
                return;
            }
        }
    }

    /// <summary>The path if it is above the binary, else null.</summary>
    public static string? Find(string relative)
    {
        string[] parts = relative.Split('/', '\\');

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, Path.Combine(parts));

            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
