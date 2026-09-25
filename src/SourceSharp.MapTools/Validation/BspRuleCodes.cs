namespace SourceSharp.MapTools.Validation;

/// <summary>
/// The stable diagnostic codes <see cref="BspValidator"/> reports under.
/// </summary>
/// <remarks>
/// <para>
/// These are a contract with whatever reads a report: a host suppresses,
/// escalates or links documentation by code, so a code may never change meaning
/// and a withdrawn rule's number is retired rather than reused. The message text
/// may be reworded freely; the code may not.
/// </para>
/// <para>
/// Every code here has a row in <see cref="BspRuleCatalog.All"/> carrying its
/// severity and the <c>file:line</c> it was transcribed from, and a fact that
/// corrupts one field of the golden map to make exactly this code fire.
/// </para>
/// </remarks>
public static class BspRuleCodes
{
    /// <summary>The file header's ident must be <c>VBSP</c>.</summary>
    public const string Ident = "BSP0001";

    /// <summary>The file version must be 19 or 20.</summary>
    public const string FileVersion = "BSP0002";

    /// <summary>A lump's length must divide by its element size.</summary>
    public const string LumpElementSize = "BSP0003";

    /// <summary>A lump's element count must be within its <c>MAX_MAP_*</c> cap.</summary>
    public const string LumpCap = "BSP0004";

    /// <summary>A lump the collision loader requires must not be empty.</summary>
    public const string RequiredLumpEmpty = "BSP0005";

    /// <summary>The PAKFILE lump must be last in the file.</summary>
    public const string PakFileLast = "BSP0006";

    /// <summary>The OCCLUSION lump's version must be 0, 1 or 2.</summary>
    public const string OcclusionVersion = "BSP0007";

    /// <summary>The LEAFS lump's version must be 0 or 1.</summary>
    public const string LeafsVersion = "BSP0008";

    /// <summary>Leaf ambient data falls back to the legacy conversion path.</summary>
    public const string LeafAmbientLegacyPath = "BSP0009";

    /// <summary>The legacy leaf ambient lump must hold one cube per leaf.</summary>
    public const string LeafAmbientLegacyCount = "BSP0010";

    /// <summary>The <c>sprp</c> game lump's version must be at least 4.</summary>
    public const string StaticPropVersion = "BSP0011";

    /// <summary>The <c>dprp</c> game lump's version must be at least 4.</summary>
    public const string DetailPropVersion = "BSP0012";

    /// <summary>The two HDR lumps must be present as a pair.</summary>
    public const string HdrLumpPair = "BSP0013";

    /// <summary>A face's texinfo index must be in range.</summary>
    public const string FaceTexInfo = "BSP0014";

    /// <summary>A leafface must index a face that exists.</summary>
    public const string LeafFaceSurface = "BSP0015";

    /// <summary>The surfedge count must be in <c>[1, MAX_MAP_SURFEDGES)</c>.</summary>
    public const string SurfEdgeCount = "BSP0016";

    /// <summary>A surfedge must index an edge that exists.</summary>
    public const string SurfEdgeEdge = "BSP0017";

    /// <summary>An edge's endpoints must index vertices that exist.</summary>
    public const string EdgeVertex = "BSP0018";

    /// <summary>A node's children must index a node or leaf that exists.</summary>
    public const string NodeChildren = "BSP0019";

    /// <summary>A leaf's cluster must be inside the visibility cluster table.</summary>
    public const string LeafCluster = "BSP0020";

    /// <summary>A brush side's texinfo must be in range, or -1.</summary>
    public const string BrushSideTexInfo = "BSP0021";

    /// <summary>A model's head node must index a node that exists.</summary>
    public const string ModelHeadNode = "BSP0022";

    /// <summary>A texdata's name must resolve into the string data.</summary>
    public const string TexDataStringIndex = "BSP0023";

    /// <summary>An overlay's faces must fit its array and exist.</summary>
    public const string OverlayFaces = "BSP0024";

    /// <summary>A static prop's type must index the model dictionary.</summary>
    public const string StaticPropDictIndex = "BSP0025";

    /// <summary>A static prop's leaf run must lie inside the leaf list.</summary>
    public const string StaticPropLeafRun = "BSP0026";

    /// <summary>Leaf 0's contents must be <c>CONTENTS_SOLID</c>.</summary>
    public const string Leaf0Solid = "BSP0027";

    /// <summary>A lit face's lightmap extents must be within the limit.</summary>
    public const string SurfaceExtents = "BSP0028";

    /// <summary>The map should carry at least one cubemap sample.</summary>
    public const string NoCubemaps = "BSP0029";

    /// <summary>The texdata string data must end in a NUL.</summary>
    public const string TexDataStringNul = "BSP0030";

    /// <summary>The physics lump must frame as <c>dphysmodel_t</c> records.</summary>
    public const string PhysFraming = "BSP0031";

    /// <summary>A physics record's model index must exist.</summary>
    public const string PhysModelIndex = "BSP0032";

    /// <summary>Every plane reference must be inside the PLANES lump.</summary>
    public const string PlaneIndex = "BSP0033";

    /// <summary>A brush's side run must lie inside the BRUSHSIDES lump.</summary>
    public const string BrushSideRun = "BSP0034";

    /// <summary>A leaf's leafface and leafbrush runs must lie inside their lumps.</summary>
    public const string LeafRuns = "BSP0035";

    /// <summary>A displacement's power must be at most 4.</summary>
    public const string DispPower = "BSP0036";

    /// <summary>The displacement vertex and triangle runs must fit their lumps.</summary>
    public const string DispRuns = "BSP0037";

    /// <summary>A lump of counted runs must frame exactly within its bytes.</summary>
    public const string SubLumpFraming = "BSP0038";
}
