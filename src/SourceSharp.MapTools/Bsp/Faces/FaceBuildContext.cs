using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// Everything the face stage reads or writes, in one place instead of the
/// eighteen file-scope globals <c>faces.cpp</c> keeps it in.
/// </summary>
/// <remarks>
/// <para>
/// One instance is one model's face build — <c>MakeFaces</c> through
/// <c>FixTjuncs</c> — because that is the span over which stock's globals hold
/// meaning. The vertex table and the edge table are shared across ALL models of
/// a map, which is why they are passed in rather than created here.
/// </para>
/// <para>
/// <see cref="Materials"/> is the one place this stage needs the material
/// system. It is an interface rather than a pair of lookup tables because the
/// bottom-material path CREATES a texinfo, and the order texinfos are created
/// in is the order they appear in the lump — so the answer has to be computed
/// at the moment stock computes it, not prefetched into a table.
/// </para>
/// </remarks>
public sealed class FaceBuildContext
{
    /// <summary>Creates the stage's state.</summary>
    /// <param name="windings">The arena faces take windings from.</param>
    /// <param name="planes">The map's plane table.</param>
    /// <param name="texInfos">The compile's texinfo table.</param>
    /// <param name="options">The vbsp command line.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public FaceBuildContext(
        WindingArena windings,
        PlaneTable planes,
        TexInfoTable texInfos,
        VbspOptions options)
    {
        ArgumentNullException.ThrowIfNull(windings);
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(texInfos);
        ArgumentNullException.ThrowIfNull(options);

        Windings = windings;
        Planes = planes;
        TexInfos = texInfos;
        Options = options;
        Faces = new FaceAllocator(windings);
        Vertices = new VertexWeld(Counters);
    }

    /// <summary>The arena every face winding lives in.</summary>
    public WindingArena Windings { get; }

    /// <summary>The map's plane table, <c>g_MainMap-&gt;mapplanes</c>.</summary>
    public PlaneTable Planes { get; }

    /// <summary>The compile's texinfo table, <c>texinfo</c>.</summary>
    public TexInfoTable TexInfos { get; }

    /// <summary>The vbsp command line.</summary>
    public VbspOptions Options { get; }

    /// <summary>Which stock defects to reproduce.</summary>
    public ComplianceOptions Compliance { get; init; } = ComplianceOptions.Correct;

    /// <summary>Where warnings go.</summary>
    public IList<CompileDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>The stage's counters, which are its observable output.</summary>
    public FaceCounters Counters { get; } = new();

    /// <summary>The face allocator and its <c>c_faces</c> balance.</summary>
    public FaceAllocator Faces { get; }

    /// <summary>The per-node face lists.</summary>
    public NodeFaceLists Lists { get; init; } = new();

    /// <summary>The welded vertex table, <c>dvertexes</c>.</summary>
    public VertexWeld Vertices { get; init; }

    /// <summary>The primitive tables, <c>g_primitives</c> and friends.</summary>
    public PrimitiveTable Primitives { get; init; } = new();

    /// <summary>
    /// <c>entity_num</c>: 0 for the world, higher for each brush model.
    /// </summary>
    /// <remarks>
    /// Read in two places that behave completely differently for the world:
    /// <c>FaceFromPortal</c> writes the raw plane number rather than the
    /// side-tagged one for brush models (<c>faces.cpp:1334</c>), and
    /// <c>FixFaceEdges</c> builds a re-triangulation primitive only for the
    /// world (<c>:652</c>).
    /// </remarks>
    public int EntityNumber { get; set; }

    /// <summary>
    /// <c>g_maxLightmapDimension</c> (<c>faces.cpp:62</c>), which
    /// <c>-maxlightmapdim</c> overrides.
    /// </summary>
    public float MaxLightmapDimension { get; init; } = 32f;

    /// <summary>
    /// The two material questions this stage asks, or null when no material
    /// system is attached.
    /// </summary>
    /// <remarks>
    /// Null is not a neutral default. Without a resolver every water underside
    /// loses its <c>$bottommaterial</c> lookup, and stock DISCARDS a face whose
    /// material has none — so a map with water compiles to fewer faces than
    /// stock. Any gate on a map with water has to supply one.
    /// </remarks>
    public IFaceMaterialResolver? Materials { get; init; }
}
