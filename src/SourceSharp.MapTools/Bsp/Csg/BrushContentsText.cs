using System.Text;

using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Bsp.Csg;

/// <summary>
/// Contents bits as the names stock prints: <c>PrintBrushContentsToString</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reachable, not debug-only: builds three of
/// these to name the two leaves and the visible contents in the
/// "mixed face contents" error, which is Phase 3c's to raise. That is why it
/// is ported and why its exact text matters — the message is what a level
/// designer sees when a brush is wrong.
/// </para>
/// <para>
/// <b>Five bits have no name here and stock's list is the reason.</b> The
/// <c>ADD_CONTENTS</c> block covers 26 of the 31 defined flags and simply
/// omits <c>CONTENTS_UNUSED</c>, <c>CONTENTS_UNUSED6</c>,
/// <c>CONTENTS_TEAM1</c>, <c>CONTENTS_TEAM2</c> and
/// <c>CONTENTS_IGNORE_NODRAW_OPAQUE</c>, so a brush carrying only one of those
/// renders as an empty string. Adding them would make this port's error
/// messages differ from stock's on exactly the maps where the message is hard
/// to read.
/// </para>
/// </remarks>
public static class BrushContentsText
{
    /// <summary>
    /// The 26 flags stock names, in the order its <c>ADD_CONTENTS</c> block
    /// lists them.
    /// </summary>
    /// <remarks>
    /// An iterator and not a <c>static readonly</c> array, because a static
    /// array's elements are writable and this assembly's rule is that it holds
    /// no mutable static state at all — the rule that makes a second compile in
    /// one process, and Phase 3p, legal.
    /// </remarks>
    private static IEnumerable<(int Flag, string Name)> Names()
    {
        yield return ((int)BrushContents.Solid, "CONTENTS_SOLID");
        yield return ((int)BrushContents.Window, "CONTENTS_WINDOW");
        yield return ((int)BrushContents.Aux, "CONTENTS_AUX");
        yield return ((int)BrushContents.Grate, "CONTENTS_GRATE");
        yield return ((int)BrushContents.Slime, "CONTENTS_SLIME");
        yield return ((int)BrushContents.Water, "CONTENTS_WATER");
        yield return ((int)BrushContents.BlockLos, "CONTENTS_BLOCKLOS");
        yield return ((int)BrushContents.Opaque, "CONTENTS_OPAQUE");
        yield return ((int)BrushContents.TestFogVolume, "CONTENTS_TESTFOGVOLUME");
        yield return ((int)BrushContents.Moveable, "CONTENTS_MOVEABLE");
        yield return ((int)BrushContents.AreaPortal, "CONTENTS_AREAPORTAL");
        yield return ((int)BrushContents.PlayerClip, "CONTENTS_PLAYERCLIP");
        yield return ((int)BrushContents.MonsterClip, "CONTENTS_MONSTERCLIP");
        yield return ((int)BrushContents.Current0, "CONTENTS_CURRENT_0");
        yield return ((int)BrushContents.Current90, "CONTENTS_CURRENT_90");
        yield return ((int)BrushContents.Current180, "CONTENTS_CURRENT_180");
        yield return ((int)BrushContents.Current270, "CONTENTS_CURRENT_270");
        yield return ((int)BrushContents.CurrentUp, "CONTENTS_CURRENT_UP");
        yield return ((int)BrushContents.CurrentDown, "CONTENTS_CURRENT_DOWN");
        yield return ((int)BrushContents.Origin, "CONTENTS_ORIGIN");
        yield return ((int)BrushContents.Monster, "CONTENTS_MONSTER");
        yield return ((int)BrushContents.Debris, "CONTENTS_DEBRIS");
        yield return ((int)BrushContents.Detail, "CONTENTS_DETAIL");
        yield return ((int)BrushContents.Translucent, "CONTENTS_TRANSLUCENT");
        yield return ((int)BrushContents.Ladder, "CONTENTS_LADDER");
        yield return ((int)BrushContents.Hitbox, "CONTENTS_HITBOX");
    }

    /// <summary>The names of the bits set, each followed by a space.</summary>
    /// <param name="contents">The <c>CONTENTS_*</c> mask.</param>
    /// <returns>
    /// The names in stock's order, space-terminated, or an empty string when no
    /// named bit is set.
    /// </returns>
    /// <remarks>
    /// The trailing space is stock's: the macro appends <c>#flag " "</c>, so
    /// every name carries one and the result never has a separator-free form.
    /// Stock also truncates at 1024 characters
    /// (<c>PrintBrushContents</c>'s buffer); the longest
    /// possible output here is 528 characters, so the truncation cannot fire
    /// and is not reproduced.
    /// </remarks>
    public static string Describe(int contents)
    {
        StringBuilder text = new();

        foreach ((int flag, string name) in Names())
        {
            if ((contents & flag) != 0)
            {
                text.Append(name).Append(' ');
            }
        }

        return text.ToString();
    }
}
