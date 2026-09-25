namespace SourceSharp.MapFormats.Text;

/// <summary>
/// Which of the TWO patch-resolution algorithms in Source to apply.
/// </summary>
/// <remarks>
/// <para>
/// There genuinely are two, and they disagree on results, not just on
/// implementation. The plan named
/// <c>materialsystem/cmaterial.cpp</c> as the authority, which it is for what
/// the ENGINE loads -- but a map compile does not go through it. vbsp carries
/// its own, older copy in <c>src/utils/vbsp/materialpatch.cpp</c>, and what
/// that copy does is what ends up in the BSP.
/// </para>
/// <para>
/// The differences, each pinned by a fact:
/// </para>
/// <list type="bullet">
/// <item><description>
/// vbsp's <c>InsertKeyValues</c> (<c>materialpatch.cpp:300-325</c>) has cases
/// for string, int, float and pointer and NO case for a subkey, so a nested
/// block inside <c>insert</c> or <c>replace</c> is silently DROPPED. The
/// engine's recurses (<c>cmaterial.cpp:3288-3297</c>).
/// </description></item>
/// <item><description>
/// vbsp applies each level's patch to that level's include as it walks
/// (<c>materialpatch.cpp:330-366</c>). The engine accumulates every level
/// first and applies once (<c>cmaterial.cpp:3433-3438,3515</c>), with a
/// merge that overwrites -- so under the engine a DEEPER patch's value wins.
/// </description></item>
/// <item><description>
/// vbsp LOSES a <c>replace</c> block whenever an <c>insert</c> block is also
/// present. <c>materialpatch.cpp:348-352</c> applies the insert and then does
/// <c>keyValues = *includeKeyValues</c>, replacing the whole object, so the
/// <c>FindKey("replace")</c> at <c>:355</c> searches the BASE MATERIAL rather
/// than the patch. The engine caches both pointers before applying either
/// (<c>cmaterial.cpp:3331-3332</c>) and has no such problem.
/// </description></item>
/// <item><description>
/// The engine's recursive insert into a block that ends up empty leaves a
/// synthetic <c>__vmtpatchdummy</c> key behind
/// (<c>cmaterial.cpp:3302-3307</c>). vbsp has nothing of the kind.
/// </description></item>
/// </list>
/// <para>
/// What they agree on: the keyword is <c>patch</c> matched case-insensitively;
/// the depth limit is 10 with the same warning text
/// (<c>materialpatch.cpp:330,370</c> and <c>cmaterial.cpp:3435,3489</c>); the
/// <c>include</c> value is a VFS path used VERBATIM, with no
/// <c>materials/</c> prefix and no <c>.vmt</c> appended
/// (<c>materialpatch.cpp:333</c>, <c>cmaterial.cpp:3441</c>); <c>insert</c>
/// sets unconditionally while <c>replace</c> writes only where the key already
/// exists; and <c>insert</c> is applied before <c>replace</c>.
/// </para>
/// </remarks>
public enum VmtPatchDialect
{
    /// <summary>
    /// <c>src/utils/vbsp/materialpatch.cpp</c>: what a MAP COMPILE does, bugs
    /// included. The default, because this library exists to reproduce map
    /// compiles.
    /// </summary>
    Compiler = 0,

    /// <summary>
    /// <c>materialsystem/cmaterial.cpp</c>: what the engine does when it loads
    /// the material at run time.
    /// </summary>
    Engine,
}
