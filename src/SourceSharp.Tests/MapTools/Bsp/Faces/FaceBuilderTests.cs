using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Bsp.Portals;
using SourceSharp.MapTools.Materials;

using Xunit;

using BspNode = SourceSharp.MapTools.Bsp.Portals.BspNode;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// Turning one portal into a face
///(<c>FaceFromPortal</c>).
/// </summary>
public class FaceBuilderTests
{
    [Fact]
    public void APortalWithNoChosenSideMakesNoFace()
    {
        FaceBuildContext context = Stage();
        FaceBuilder builder = new(context);

        Portal portal = Portal(context, front: 0, back: (int)BrushContents.Solid, side: null);

        Assert.Null(builder.FaceFromPortal(portal, 0));
    }

    [Fact]
    public void AFaceTakesItsMaterialFromTheChosenBrushSide()
    {
        FaceBuildContext context = Stage();
        FaceBuilder builder = new(context);

        MapBrushSide side = new() { PlaneNumber = 0, TexInfo = 0, SmoothingGroups = 9 };
        Portal portal = Portal(context, front: 0, back: (int)BrushContents.Solid, side: side);

        Face face = builder.FaceFromPortal(portal, 0)!;

        Assert.Same(side, face.OriginalFace);
        Assert.Equal(0, face.TexInfo);
        Assert.Equal(9u, face.SmoothingGroups);
        Assert.Equal(-1, face.DispInfo);
    }

    [Fact]
    public void TheWorldsFacePlaneCarriesTheSideBitInItsLowBit()
    {
        FaceBuildContext context = Stage();
        FaceBuilder builder = new(context);

        MapBrushSide side = new() { PlaneNumber = 0, TexInfo = 0 };
        Portal portal = Portal(context, front: 0, back: (int)BrushContents.Solid, side: side);

        Assert.Equal(0, builder.FaceFromPortal(portal, 0)!.PlaneNumber);
        Assert.Equal(1, builder.FaceFromPortal(portal, 1)!.PlaneNumber);
    }

    [Fact]
    public void ABrushModelsFacePlaneIsTheSidesOwnPlane()
    {
        FaceBuildContext context = Stage();
        context.EntityNumber = 1;
        FaceBuilder builder = new(context);

        MapBrushSide side = new() { PlaneNumber = 0, TexInfo = 0 };
        Portal portal = Portal(context, front: 0, back: (int)BrushContents.Solid, side: side);

        // The brush model renderer doesn't use PLANEBACK, so side 1 does NOT
        // get the low bit set.
        Assert.Equal(0, builder.FaceFromPortal(portal, 1)!.PlaneNumber);
    }

    [Fact]
    public void TheFrontSidesWindingIsACopyAndTheBackSidesIsReversed()
    {
        FaceBuildContext context = Stage();
        FaceBuilder builder = new(context);

        MapBrushSide side = new() { PlaneNumber = 0, TexInfo = 0 };
        Portal portal = Portal(context, front: 0, back: (int)BrushContents.Solid, side: side);

        Vec3[] forward = FaceStageFixture.Points(context, builder.FaceFromPortal(portal, 0)!);
        Vec3[] backward = FaceStageFixture.Points(context, builder.FaceFromPortal(portal, 1)!);

        Assert.Equal(forward, backward.Reverse());
    }

    [Fact]
    public void TheInsideOfAWindowIsNotDrawn()
    {
        FaceBuildContext context = Stage();
        FaceBuilder builder = new(context);

        MapBrushSide side = new() { PlaneNumber = 0, TexInfo = 0 };
        Portal portal = Portal(
            context, front: (int)BrushContents.Window, back: 0, side: side);

        // Looking from inside the window out at empty space: the content
        // difference IS the window bit, and the near side carries it.
        Assert.Null(builder.FaceFromPortal(portal, 0));

        // Looking from the empty side at the window: drawn.
        Assert.NotNull(builder.FaceFromPortal(portal, 1));
    }

    [Fact]
    public void TheInsideOfAGrateIsNotDrawn()
    {
        FaceBuildContext context = Stage();
        FaceBuilder builder = new(context);

        MapBrushSide side = new() { PlaneNumber = 0, TexInfo = 0 };
        Portal portal = Portal(context, front: (int)BrushContents.Grate, back: 0, side: side);

        Assert.Null(builder.FaceFromPortal(portal, 0));
    }

    [Fact]
    public void AFaceBoundingWaterRemembersTheWaterLeaf()
    {
        FaceBuildContext context = Stage();
        FaceBuilder builder = new(context);

        MapBrushSide side = new() { PlaneNumber = 0, TexInfo = 0 };
        Portal portal = Portal(
            context, front: 0, back: (int)BrushContents.Water, side: side);

        // From the empty side: the FAR node is the water one.
        Face face = builder.FaceFromPortal(portal, 0)!;

        Assert.Same(portal.BackNode, face.FogVolumeLeaf);
    }

    [Fact]
    public void AWaterUndersideWithNoBottomMaterialIsDiscarded()
    {
        FaceBuildContext context = Stage();
        FaceBuilder builder = new(context);

        MapBrushSide side = new() { PlaneNumber = 0, TexInfo = 0 };
        Portal portal = Portal(
            context, front: (int)BrushContents.Water, back: 0, side: side);

        // No resolver at all, which is the "material has no $bottommaterial"
        // case: stock frees the face and returns NULL.
        Assert.Null(builder.FaceFromPortal(portal, 0));
        Assert.Contains(
            context.Diagnostics, d => d.Code == FaceBuilder.MissingBottomMaterial);
    }

    [Fact]
    public void AWaterUndersideWithABottomMaterialGetsItsTexInfo()
    {
        FaceBuildContext context = Stage(new FixedBottomMaterial(0, 7));
        FaceBuilder builder = new(context);

        MapBrushSide side = new() { PlaneNumber = 0, TexInfo = 0 };
        Portal portal = Portal(
            context, front: (int)BrushContents.Water, back: 0, side: side);

        Face face = builder.FaceFromPortal(portal, 0)!;

        Assert.Equal(7, face.TexInfo);
    }

    [Fact]
    public void ASolidLeafNeverMakesFaces()
    {
        FaceBuildContext context = Stage();
        FaceBuilder builder = new(context);

        BspNode leaf = new(0) { Contents = (int)BrushContents.Solid };

        builder.MakeFaces(leaf);

        Assert.Equal(0, context.Counters.NodeFaces);
    }

    private static FaceBuildContext Stage(IFaceMaterialResolver? materials = null)
    {
        FaceBuildContext context = FaceStageFixture.Create();
        context.Planes.Create(new Vec3(0f, 0f, 1f), 0f);
        return new FaceBuildContext(
            context.Windings, context.Planes, context.TexInfos, context.Options)
        {
            Materials = materials,
        };
    }

    private static Portal Portal(FaceBuildContext context, int front, int back, MapBrushSide? side)
    {
        BspNode onNode = new(0);
        onNode.SplitOn(0, new BspNode(1), new BspNode(2));

        return new Portal(0)
        {
            OnNode = onNode,
            FrontNode = new BspNode(3) { Contents = front },
            BackNode = new BspNode(4) { Contents = back },
            Side = side,
            SideFound = true,
            Winding = context.Windings.Create(
            [
                new Vec3(0f, 0f, 0f),
                new Vec3(64f, 0f, 0f),
                new Vec3(64f, 64f, 0f),
                new Vec3(0f, 64f, 0f),
            ]),
        };
    }

    /// <summary>A resolver that answers one fixed pair, for the water facts.</summary>
    private sealed class FixedBottomMaterial(int top, int bottom) : IFaceMaterialResolver
    {
        public bool TryGetBottomTexInfo(int texInfo, out int bottomTexInfo, out bool warn)
        {
            warn = true;
            bottomTexInfo = bottom;
            return texInfo == top;
        }

        public float SubdivSize(int texInfo) => 0f;
    }
}
