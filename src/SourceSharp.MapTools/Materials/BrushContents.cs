namespace SourceSharp.MapTools.Materials;

/// <summary>
/// <c>CONTENTS_*</c> from the reference implementation.
/// </summary>
/// <remarks>
/// Only a handful are reachable from a material — <c>FindMiptex</c> sets
/// origin, detail, the two clip bits, grate, opaque, blockLOS, ladder, water,
/// slime and window — but the whole set is here because a brush side's
/// contents is this value ORed with the brush's own, and a partial enum turns
/// the rest into unnamed bits in every diagnostic.
/// </remarks>
[Flags]
public enum BrushContents
{
    /// <summary><c>CONTENTS_EMPTY</c>.</summary>
    Empty = 0,

    /// <summary><c>CONTENTS_SOLID</c>.</summary>
    Solid = 0x1,

    /// <summary><c>CONTENTS_WINDOW</c>: translucent but not watery.</summary>
    Window = 0x2,

    /// <summary><c>CONTENTS_AUX</c>.</summary>
    Aux = 0x4,

    /// <summary>
    /// <c>CONTENTS_GRATE</c>: bullets and sight pass through, solids do not.
    /// </summary>
    Grate = 0x8,

    /// <summary><c>CONTENTS_SLIME</c>.</summary>
    Slime = 0x10,

    /// <summary><c>CONTENTS_WATER</c>.</summary>
    Water = 0x20,

    /// <summary><c>CONTENTS_BLOCKLOS</c>: blocks AI line of sight.</summary>
    BlockLos = 0x40,

    /// <summary><c>CONTENTS_OPAQUE</c>: cannot be seen through.</summary>
    Opaque = 0x80,

    /// <summary><c>CONTENTS_TESTFOGVOLUME</c>.</summary>
    TestFogVolume = 0x100,

    /// <summary><c>CONTENTS_UNUSED</c>.</summary>
    Unused = 0x200,

    /// <summary><c>CONTENTS_UNUSED6</c>.</summary>
    Unused6 = 0x400,

    /// <summary><c>CONTENTS_TEAM1</c>.</summary>
    Team1 = 0x800,

    /// <summary><c>CONTENTS_TEAM2</c>.</summary>
    Team2 = 0x1000,

    /// <summary><c>CONTENTS_IGNORE_NODRAW_OPAQUE</c>.</summary>
    IgnoreNoDrawOpaque = 0x2000,

    /// <summary><c>CONTENTS_MOVEABLE</c>.</summary>
    Moveable = 0x4000,

    /// <summary><c>CONTENTS_AREAPORTAL</c>.</summary>
    AreaPortal = 0x8000,

    /// <summary><c>CONTENTS_PLAYERCLIP</c>.</summary>
    PlayerClip = 0x10000,

    /// <summary><c>CONTENTS_MONSTERCLIP</c>.</summary>
    MonsterClip = 0x20000,

    /// <summary><c>CONTENTS_CURRENT_0</c>.</summary>
    Current0 = 0x40000,

    /// <summary><c>CONTENTS_CURRENT_90</c>.</summary>
    Current90 = 0x80000,

    /// <summary><c>CONTENTS_CURRENT_180</c>.</summary>
    Current180 = 0x100000,

    /// <summary><c>CONTENTS_CURRENT_270</c>.</summary>
    Current270 = 0x200000,

    /// <summary><c>CONTENTS_CURRENT_UP</c>.</summary>
    CurrentUp = 0x400000,

    /// <summary><c>CONTENTS_CURRENT_DOWN</c>.</summary>
    CurrentDown = 0x800000,

    /// <summary>
    /// <c>CONTENTS_ORIGIN</c>: removed before BSPing an entity.
    /// </summary>
    Origin = 0x1000000,

    /// <summary><c>CONTENTS_MONSTER</c>.</summary>
    Monster = 0x2000000,

    /// <summary><c>CONTENTS_DEBRIS</c>.</summary>
    Debris = 0x4000000,

    /// <summary>
    /// <c>CONTENTS_DETAIL</c>: brushes added after the vis leaves.
    /// </summary>
    Detail = 0x8000000,

    /// <summary><c>CONTENTS_TRANSLUCENT</c>.</summary>
    Translucent = 0x10000000,

    /// <summary><c>CONTENTS_LADDER</c>.</summary>
    Ladder = 0x20000000,

    /// <summary><c>CONTENTS_HITBOX</c>.</summary>
    Hitbox = 0x40000000,
}
