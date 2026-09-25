using SourceSharp.MapGen;

using Xunit;

namespace SourceSharp.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips — visibly, with the reason in the
/// runner's own output — when the tree under test has no compiled sandbox map.
///
/// <para>
/// WHY AN ATTRIBUTE RATHER THAN AN EARLY RETURN. The shape this replaces was
/// `if (bsp is null) { Assert.True(true, "...skipped..."); return; }`, which
/// reports a PASS. xUnit never prints the message of an assertion that held, so
/// a tree with no map and a tree whose map was checked and agreed produced
/// byte-identical output: 1235 passed. That is precisely the distinction the
/// reader of a test log needs, and it was the one thing the old shape could not
/// express. A real skip prints "Skipped &lt;name&gt;" with the reason and moves
/// the count into the summary's Skipped column, so "no map here" is legible
/// without opening this file.
/// </para>
///
/// <para>
/// xUnit 2.9.3 has no runtime <c>Assert.Skip</c> — that arrived in v3 — so the
/// decision is made at discovery, which is what a Skip-setting attribute is
/// for. It costs one stat() per test.
/// </para>
///
/// <para>
/// ONLY the legitimate absence skips. <see cref="SandboxMapState.NoTreeFound"/>
/// deliberately does not: a binary that cannot find its own checkout has had
/// the layout moved underneath it, and the test body asserts on that so it goes
/// red rather than quietly reporting nothing. A skip that can swallow a broken
/// assumption is how the original gap opened.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SandboxMapFactAttribute : FactAttribute
{
    /// <summary>The running binary's own tree.</summary>
    public SandboxMapFactAttribute()
        : this(AppContext.BaseDirectory)
    {
    }

    /// <summary>
    /// Any tree, so the two branches can be exercised against synthetic layouts
    /// instead of only against whatever this machine happens to have on disk.
    /// </summary>
    internal SandboxMapFactAttribute(string startDirectory)
    {
        SandboxMapLookup lookup = SandboxArtefact.Locate(startDirectory);

        if (lookup.State == SandboxMapState.NoMapInTree)
            Skip = lookup.Explanation;
    }
}
