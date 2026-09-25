using System.Collections.Immutable;

namespace SourceSharp.MapTools.Materials;

/// <summary>
/// The compile-only material variables <c>vbsp</c> reads, one bit each.
/// </summary>
/// <remarks>
/// <para>
/// These are the whole of what <c>FindMiptex</c>
/// (<c>src/utils/vbsp/textures.cpp:49-286</c>) asks a material, and every one
/// of them is a boolean read through <c>StringIsTrue</c> — so the VALUE is
/// either "true"/"1" or it is not, and nothing else about the string matters.
/// </para>
/// <para>
/// Twenty-two of them. Not all are spelled <c>%compile…</c>:
/// <c>%playerClip</c> and <c>%noPortal</c> are not, and a table keyed on a
/// <c>%compile</c> prefix would silently drop both.
/// </para>
/// </remarks>
[Flags]
public enum MaterialCompileFlags : uint
{
    /// <summary>The material sets none of them.</summary>
    None = 0,

    /// <summary><c>%compileSky</c> (<c>textures.cpp:85</c>).</summary>
    Sky = 1u << 0,

    /// <summary><c>%compile2DSky</c> (<c>textures.cpp:90</c>).</summary>
    Sky2D = 1u << 1,

    /// <summary><c>%compileHint</c> (<c>textures.cpp:96</c>).</summary>
    Hint = 1u << 2,

    /// <summary><c>%compileSkip</c> (<c>textures.cpp:102</c>).</summary>
    Skip = 1u << 3,

    /// <summary><c>%compileOrigin</c> (<c>textures.cpp:108</c>).</summary>
    Origin = 1u << 4,

    /// <summary><c>%compileClip</c> (<c>textures.cpp:115</c>).</summary>
    Clip = 1u << 5,

    /// <summary><c>%playerClip</c> (<c>textures.cpp:121</c>).</summary>
    PlayerClip = 1u << 6,

    /// <summary><c>%compileNpcClip</c> (<c>textures.cpp:128</c>).</summary>
    NpcClip = 1u << 7,

    /// <summary><c>%compileNoChop</c> (<c>textures.cpp:135</c>).</summary>
    NoChop = 1u << 8,

    /// <summary><c>%compileTrigger</c> (<c>textures.cpp:141</c>).</summary>
    Trigger = 1u << 9,

    /// <summary><c>%compileNoLight</c> (<c>textures.cpp:151</c>).</summary>
    NoLight = 1u << 10,

    /// <summary><c>%compileWater</c> (<c>textures.cpp:152,238</c>).</summary>
    Water = 1u << 11,

    /// <summary><c>%compileLadder</c> (<c>textures.cpp:161</c>).</summary>
    Ladder = 1u << 12,

    /// <summary><c>%noPortal</c> (<c>textures.cpp:167</c>).</summary>
    NoPortal = 1u << 13,

    /// <summary><c>%compilePassBullets</c> (<c>textures.cpp:173</c>).</summary>
    PassBullets = 1u << 14,

    /// <summary><c>%compileNoDraw</c> (<c>textures.cpp:195</c>).</summary>
    NoDraw = 1u << 15,

    /// <summary><c>%compileInvisible</c> (<c>textures.cpp:202</c>).</summary>
    Invisible = 1u << 16,

    /// <summary><c>%compileNonsolid</c> (<c>textures.cpp:213</c>).</summary>
    NonSolid = 1u << 17,

    /// <summary><c>%compileBlockLOS</c> (<c>textures.cpp:220</c>).</summary>
    BlockLos = 1u << 18,

    /// <summary><c>%compileDetail</c> (<c>textures.cpp:228</c>).</summary>
    Detail = 1u << 19,

    /// <summary><c>%compileKeepLight</c> (<c>textures.cpp:234</c>).</summary>
    KeepLight = 1u << 20,

    /// <summary><c>%compileSlime</c> (<c>textures.cpp:261</c>).</summary>
    Slime = 1u << 21,
}

/// <summary>
/// The VMT variable name behind each <see cref="MaterialCompileFlags"/> bit.
/// </summary>
/// <param name="Name">
/// The variable as it is spelled in <c>textures.cpp</c>. Looked up without
/// regard to case, the way the material system's <c>FindVar</c> does.
/// </param>
/// <param name="Flag">The bit it sets.</param>
public readonly record struct MaterialCompileVar(string Name, MaterialCompileFlags Flag);

/// <summary>
/// The table of compile variables, and the truth test <c>vbsp</c> applies.
/// </summary>
public static class MaterialCompileVars
{
    /// <summary>
    /// Every compile variable <c>FindMiptex</c> reads, in the order it reads
    /// them.
    /// </summary>
    /// <remarks>
    /// An <see cref="ImmutableArray{T}"/> rather than an array: a
    /// <c>static readonly</c> array is a mutable static, because its elements
    /// stay writable.
    /// </remarks>
    public static ImmutableArray<MaterialCompileVar> All { get; } =
    [
        new("%compileSky", MaterialCompileFlags.Sky),
        new("%compile2DSky", MaterialCompileFlags.Sky2D),
        new("%compileHint", MaterialCompileFlags.Hint),
        new("%compileSkip", MaterialCompileFlags.Skip),
        new("%compileOrigin", MaterialCompileFlags.Origin),
        new("%compileClip", MaterialCompileFlags.Clip),
        new("%playerClip", MaterialCompileFlags.PlayerClip),
        new("%compileNpcClip", MaterialCompileFlags.NpcClip),
        new("%compileNoChop", MaterialCompileFlags.NoChop),
        new("%compileTrigger", MaterialCompileFlags.Trigger),
        new("%compileNoLight", MaterialCompileFlags.NoLight),
        new("%compileWater", MaterialCompileFlags.Water),
        new("%compileLadder", MaterialCompileFlags.Ladder),
        new("%noPortal", MaterialCompileFlags.NoPortal),
        new("%compilePassBullets", MaterialCompileFlags.PassBullets),
        new("%compileNoDraw", MaterialCompileFlags.NoDraw),
        new("%compileInvisible", MaterialCompileFlags.Invisible),
        new("%compileNonsolid", MaterialCompileFlags.NonSolid),
        new("%compileBlockLOS", MaterialCompileFlags.BlockLos),
        new("%compileDetail", MaterialCompileFlags.Detail),
        new("%compileKeepLight", MaterialCompileFlags.KeepLight),
        new("%compileSlime", MaterialCompileFlags.Slime),
    ];

    /// <summary>
    /// <c>StringIsTrue</c> (<c>src/utils/vbsp/textures.cpp:36-47</c>).
    /// </summary>
    /// <param name="value">The variable's value, or null when it is absent.</param>
    /// <returns>True when the compiler would treat it as set.</returns>
    /// <remarks>
    /// EXACTLY two spellings are true — <c>true</c> case-insensitively, and
    /// <c>1</c>. "yes", "2" and "0.5" are all false, which is why a material
    /// written with <c>"%compileNoDraw" "yes"</c> draws.
    /// </remarks>
    public static bool IsTrue(string? value) =>
        value is not null &&
        (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "1", StringComparison.Ordinal));
}
