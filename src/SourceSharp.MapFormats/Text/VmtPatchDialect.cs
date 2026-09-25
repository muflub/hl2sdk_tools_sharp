namespace SourceSharp.MapFormats.Text;

/// <summary>
/// Which of the TWO patch-resolution algorithms in the reference build to
/// apply.
/// </summary>
/// <remarks>
/// <para>
/// There genuinely are two, and they disagree on results, not just on
/// implementation. The reference build resolves a patch once when the ENGINE
/// loads a material and once, differently, when a map is compiled -- the map
/// compiler carries its own, older copy of the resolver, and what that copy
/// does is what ends up in the BSP.
/// </para>
/// <para>
/// The differences, each pinned by a fact:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The compiler's <c>InsertKeyValues</c> has cases for string, int, float and
/// pointer and NO case for a subkey, so a nested block inside <c>insert</c> or
/// <c>replace</c> is silently DROPPED. The engine's recurses.
/// </description></item>
/// <item><description>
/// The compiler applies each level's patch to that level's include as it
/// walks. The engine accumulates every level first and applies once, with a
/// merge that overwrites -- so under the engine a DEEPER patch's value wins.
/// </description></item>
/// <item><description>
/// The compiler LOSES a <c>replace</c> block whenever an <c>insert</c> block
/// is also present: it applies the insert and then does
/// <c>keyValues = *includeKeyValues</c>, replacing the whole object, so the
/// later <c>FindKey("replace")</c> searches the BASE MATERIAL rather than the
/// patch. The engine caches both pointers before applying either and has no
/// such problem.
/// </description></item>
/// <item><description>
/// The engine's recursive insert into a block that ends up empty leaves a
/// synthetic <c>__vmtpatchdummy</c> key behind. The compiler has nothing of
/// the kind.
/// </description></item>
/// </list>
/// <para>
/// What they agree on: the keyword is <c>patch</c> matched case-insensitively;
/// the depth limit is 10 with the same warning text; the <c>include</c> value
/// is a VFS path used VERBATIM, with no <c>materials/</c> prefix and no
/// <c>.vmt</c> appended; <c>insert</c> sets unconditionally while
/// <c>replace</c> writes only where the key already exists; and <c>insert</c>
/// is applied before <c>replace</c>.
/// </para>
/// </remarks>
public enum VmtPatchDialect
{
    /// <summary>
    /// The map compiler's resolver: what a MAP COMPILE does, bugs included.
    /// The default, because this library exists to reproduce map compiles.
    /// </summary>
    Compiler = 0,

    /// <summary>
    /// The engine's resolver: what the reference build does when it loads the
    /// material at run time.
    /// </summary>
    Engine,
}
