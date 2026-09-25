namespace SourceSharp.MapTools.Bsp.Csg;

/// <summary>
/// Diagnostic codes the CSG and BSP-build stages raise.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is a line stock prints and then carries on from. Four of
/// the five are <c>qprintf</c>, which means stock only prints them under
/// <c>-v</c> (<c>utils/common/cmdlib.cpp</c>); they are recorded here
/// unconditionally, because "the compiler noticed this and you did not ask to
/// be told" is not a distinction a library should be making on the host's
/// behalf. A host that wants stock's behaviour filters on
/// <see cref="SourceSharp.MapTools.Diagnostics.DiagnosticSeverity"/>.
/// </para>
/// <para>
/// The fifth, <see cref="MicroBrush"/>, is a real <c>Warning()</c> and stock
/// prints it always (<c>brushbsp.cpp:1417</c>).
/// </para>
/// </remarks>
public static class BspBuildCodes
{
    /// <summary>
    /// A split plane's winding still reached past ±16384 after being clipped by
    /// every side of the brush: <c>"WARNING: huge winding"</c>,
    /// <c>brushbsp.cpp:1115</c>.
    /// </summary>
    public const string HugeWinding = "VBSP0401";

    /// <summary>
    /// One half of a split came back with a bound outside the legal world:
    /// <c>"bogus brush after clip"</c>, <c>brushbsp.cpp:1188</c>.
    /// </summary>
    public const string BogusBrushAfterClip = "VBSP0402";

    /// <summary>
    /// A split produced neither half: <c>"split removed brush"</c>,
    /// <c>brushbsp.cpp:1203</c>.
    /// </summary>
    public const string SplitRemovedBrush = "VBSP0403";

    /// <summary>
    /// A split produced only one half, so the whole brush went to that side:
    /// <c>"split not on both sides"</c>, <c>brushbsp.cpp:1205</c>.
    /// </summary>
    public const string SplitNotOnBothSides = "VBSP0404";

    /// <summary>
    /// A brush entering the BSP build had a volume below
    /// <see cref="SourceSharp.MapTools.Options.VbspOptions.MicroVolume"/>:
    /// <c>"Brush %i: WARNING, microbrush"</c>, <c>brushbsp.cpp:1417</c>.
    /// </summary>
    /// <remarks>
    /// The number in stock's message is <c>b-&gt;original-&gt;id</c>, the VMF
    /// solid id, which is why the diagnostic carries a
    /// <see cref="SourceSharp.MapTools.Diagnostics.MapLocation"/> rather than
    /// formatting it into the text.
    /// </remarks>
    public const string MicroBrush = "VBSP0405";

    /// <summary>
    /// A brush side in an areaportal-in-water fixup matched no plane on the
    /// water brush: <c>"Found no matching plane for %s"</c>,
    /// <c>csg.cpp:334</c>.
    /// </summary>
    /// <remarks>
    /// Unreachable in practice — the loop it ends only fails to pick a side
    /// when every side of the water brush already carries
    /// <see cref="BspBrushSide.TexInfoNode"/>, and stock's own comment
    /// (<c>csg.cpp:291-295</c>) explains that it uses the ORIGINAL sides
    /// precisely so that cannot happen. It is kept because stock's fallback
    /// then indexes <c>texinfo[pSide-&gt;texinfo]</c> with a texinfo of -1 to
    /// build the message, which reads memory before the array.
    /// </remarks>
    public const string NoMatchingPlane = "VBSP0406";
}
