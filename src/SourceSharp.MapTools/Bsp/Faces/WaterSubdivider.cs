using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Geometry;

namespace SourceSharp.MapTools.Bsp.Faces;

/// <summary>
/// Cutting a water surface into a grid of tiles and emitting it as one
/// triangle strip
/// (<c>SubdivideFaceBySubdivSize</c>, <c>src/utils/vbsp/faces.cpp:1514</c>).
/// </summary>
/// <remarks>
/// <para>
/// Driven by a material's <c>$subdivsize</c>. The face is clipped four times
/// per tile against an axis-aligned box, every surviving tile is fanned into
/// triangles, the whole list is stripped, and the result becomes one
/// <see cref="PrimitiveType.TriStrip"/> primitive on the face.
/// </para>
/// <para>
/// <b>Stock calls this feature unsupported</b> and prints a warning when it
/// runs ("NOTE: Subdivision is unsupported and should be phased out",
/// <c>faces.cpp:1751</c>). It is ported because it is in this stage's file and
/// its output goes into the file verbatim, not because any current map uses
/// it. The whole pass is also unreachable in a stock compile: the only caller,
/// <c>SplitSubdividedFaces</c>, is commented out in <c>ProcessWorldModel</c>
/// (<c>vbsp.cpp:349</c>). That is recorded rather than acted on — a port that
/// silently dropped a function because the shipped tool never called it would
/// be deciding something the port is not entitled to decide.
/// </para>
/// <para>
/// The surface must be near-horizontal: faces whose normal has |z| below 0.9
/// are skipped, because the tiling is done in x and y only.
/// </para>
/// </remarks>
public sealed class WaterSubdivider
{
    /// <summary>A material asked for subdivision, which stock deprecates.</summary>
    public const string SubdivisionUsed = "VBSP0321";

    private readonly FaceBuildContext _context;

    /// <summary>Creates a subdivider over one model's face stage.</summary>
    /// <param name="context">The stage's state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public WaterSubdivider(FaceBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <summary>
    /// Subdivides a face if its material asks for it
    /// (<c>SubdivideFaceBySubdivSize</c>, <c>faces.cpp:1727</c>).
    /// </summary>
    /// <param name="face">The face.</param>
    public void SubdivideFaceBySubdivSize(Face face)
    {
        ArgumentNullException.ThrowIfNull(face);

        if (face.NumPoints == 0 || face.IsDead || face.Winding.IsNull)
        {
            return;
        }

        if (_context.Materials is null)
        {
            return;
        }

        float size = _context.Materials.SubdivSize(face.TexInfo);

        if (size <= 0f)
        {
            return;
        }

        _context.Diagnostics.Add(new CompileDiagnostic(
            SubdivisionUsed,
            DiagnosticSeverity.Warning,
            $"Using subdivision on texinfo {face.TexInfo}"));

        SubdivideFaceBySubdivSize(face, size);
    }

    /// <summary>
    /// Subdivides one face at a known tile size
    /// (<c>SubdivideFaceBySubdivSize</c>, <c>faces.cpp:1514</c>).
    /// </summary>
    /// <param name="face">The face.</param>
    /// <param name="subdivSize">The tile size in world units, truncated to an integer.</param>
    public void SubdivideFaceBySubdivSize(Face face, float subdivSize)
    {
        ArgumentNullException.ThrowIfNull(face);

        WindingArena arena = _context.Windings;

        Plane plane = arena.WindingPlane(face.Winding);

        // HACK - only subdivide stuff that is facing up or down (for water)
        if (Math.Abs(plane.Normal.Z) < 0.9f)
        {
            return;
        }

        int step = (int)subdivSize;

        if (step <= 0)
        {
            return;
        }

        Winding whole = arena.Copy(face.Winding);
        arena.Bounds(whole, out Vec3 min, out Vec3 max);

        int xStart = step * (int)((min.X - step) / step);
        int xEnd = step * (int)((max.X + step) / step);
        int yStart = step * (int)((min.Y - step) / step);
        int yEnd = step * (int)((max.Y + step) / step);

        int xSteps = (xEnd - xStart) / step;
        int ySteps = (yEnd - yStart) / step;

        if (xSteps <= 0 || ySteps <= 0)
        {
            arena.Free(whole);
            return;
        }

        Winding[] tiles = new Winding[xSteps * ySteps];

        for (int yi = 0, y = yStart; y < yEnd; y += step, yi++)
        {
            for (int xi = 0, x = xStart; x < xEnd; x += step, xi++)
            {
                Winding tile = ClipToBox(arena, whole, x, y, step);

                if (!tile.IsNull)
                {
                    tiles[xi + (yi * xSteps)] = tile;
                }
            }
        }

        arena.Free(whole);

        PrimitiveBuilder primitive = _context.Primitives.Begin(PrimitiveType.TriStrip);

        face.FirstPrimId = primitive.Id;
        face.NumPrims = 1;

        List<ushort> triangles = [];

        foreach (Winding tile in tiles)
        {
            if (tile.IsNull)
            {
                continue;
            }

            Span<Vec3> points = arena.Points(tile);
            ushort[] indices = new ushort[points.Length];
            primitive.AddWinding(points, indices);

            // fan-tesselate the polygon and spit out tris
            for (int j = 0; j < points.Length - 2; j++)
            {
                triangles.Add(indices[0]);
                triangles.Add(indices[j + 1]);
                triangles.Add(indices[j + 2]);
            }

            arena.Free(tile);
        }

        if (triangles.Count == 0)
        {
            // Stock returns here without committing the primitive, and without
            // undoing firstPrimID/numPrims -- so the face points at a primitive
            // slot that the next subdivided face will fill. Reproduced.
            return;
        }

        foreach (ushort index in TriangleStripper.Stripify(triangles))
        {
            primitive.AddIndex(index);
        }

        // don't increment until we get here and are sure that we have a primitive
        primitive.Commit();
    }

    /// <summary>
    /// Subdivides every face on every node of a subtree
    /// (<c>SplitSubdividedFaces_Node_r</c>, <c>faces.cpp:1758</c>).
    /// </summary>
    /// <param name="node">The subtree root.</param>
    public void SplitSubdividedFacesRecursive(IBspNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.IsLeaf())
        {
            return;
        }

        for (Face? f = _context.Lists.FacesOf(node); f is not null; f = f.Next)
        {
            SubdivideFaceBySubdivSize(f);
        }

        SplitSubdividedFacesRecursive(node.Front!);
        SplitSubdividedFacesRecursive(node.Back!);
    }

    /// <summary>
    /// Subdivides every leaf face and every node face
    /// (<c>SplitSubdividedFaces</c>, <c>faces.cpp:1777</c>).
    /// </summary>
    /// <param name="leafFaceList">The detail leaf face list.</param>
    /// <param name="headNode">The model's tree root.</param>
    public void SplitSubdividedFaces(Face? leafFaceList, IBspNode headNode)
    {
        ArgumentNullException.ThrowIfNull(headNode);

        for (Face? f = leafFaceList; f is not null; f = f.Next)
        {
            SubdivideFaceBySubdivSize(f);
        }

        SplitSubdividedFacesRecursive(headNode);
    }

    /// <summary>
    /// Clips a winding to one axis-aligned tile, four planes at a time.
    /// </summary>
    /// <param name="arena">The winding arena.</param>
    /// <param name="source">The winding to clip. It is NOT freed.</param>
    /// <param name="x">The tile's low x.</param>
    /// <param name="y">The tile's low y.</param>
    /// <param name="step">The tile size.</param>
    /// <returns>The clipped tile, or <see cref="Winding.Null"/>.</returns>
    private static Winding ClipToBox(WindingArena arena, Winding source, int x, int y, int step)
    {
        Winding current = arena.Copy(source);

        ReadOnlySpan<(Vec3 Normal, float Dist)> planes =
        [
            (new Vec3(1f, 0f, 0f), x),
            (new Vec3(-1f, 0f, 0f), -(x + step)),
            (new Vec3(0f, 1f, 0f), y),
            (new Vec3(0f, -1f, 0f), -(y + step)),
        ];

        foreach ((Vec3 normal, float dist) in planes)
        {
            arena.ClipEpsilon(
                current, normal, dist, GeometryEpsilons.OnEpsilonFloat,
                out Winding front, out Winding back);

            arena.Free(current);

            if (!back.IsNull)
            {
                arena.Free(back);
            }

            if (front.IsNull)
            {
                return Winding.Null;
            }

            current = front;
        }

        return current;
    }
}
