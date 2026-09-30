//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The door light's arithmetic (<see cref="DoorLightMath"/>) and frames
/// (<see cref="DoorFrame"/>): the cells of an opening, a source's falloff as
/// vrad's direct lighting has it, the light a receiver gets through both
/// openings of a joint, a face's sample cells and luxels, and the response
/// grid's nodes, flux and shares. Each branch, on numbers worked by hand.
/// </summary>
public sealed class DoorLightMathTests
{
    private const float W = 96, H = 224, D = 16;

    private static DoorSource Point(Vec3 origin, float constant = 1, float quadratic = 0, UInt128? cells = null) => new(
        EmitType.Point, origin, default, new Vec3(1, 1, 1), 0, constant, 0, quadratic, 0, 0, 0, cells ?? DoorLightMath.AllCells);

    // ---- frames -----------------------------------------------------------------------------------

    /// <summary>
    /// A socket's frame: out of the room through the opening, across = up x
    /// out, centred on the opening's inner face (the plug's box less half
    /// its depth); local and room coordinates round-trip, and crossing the
    /// joint twice comes back.
    /// </summary>
    [Theory]
    [InlineData(RoomFacing.PositiveX, 1, 0, 0, 1)]
    [InlineData(RoomFacing.NegativeX, -1, 0, 0, -1)]
    [InlineData(RoomFacing.PositiveY, 0, 1, -1, 0)]
    [InlineData(RoomFacing.NegativeY, 0, -1, 1, 0)]
    public void ASocketsFrameFacesOutOfTheRoom(RoomFacing facing, float ox, float oy, float ax, float ay)
    {
        RoomDefinition room = RoomHarness.WalkableRoom("hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);
        RoomSocket socket = room.Sockets.First(s => s.Facing == facing);
        DoorFrame frame = DoorFrame.Of(room, socket);
        Assert.Equal(new Vec3(ox, oy, 0), frame.Out);
        Assert.Equal(new Vec3(ax, ay, 0), frame.Across);
        Assert.Equal(room.Kit.Width, frame.Width);
        Assert.Equal(room.Kit.Height, frame.Height);
        Assert.Equal(room.Kit.Depth, frame.Depth);

        Vec3 p = new(37, 81, 44);
        Vec3 local = frame.ToLocal(p);
        Assert.True((p - frame.ToRoom(local)).Length() < 1e-3f);
        Vec3 dir = new(0.6f, 0, 0.8f);
        Assert.True((dir - frame.DirectionToRoom(frame.DirectionToLocal(dir))).Length() < 1e-6f);
        Assert.Equal(local, frame.ToNeighbour(frame.ToNeighbour(local)));
        Assert.Equal(dir, DoorFrame.DirectionToNeighbour(DoorFrame.DirectionToNeighbour(dir)));

        // The opening's inner face is at door-local x 0, its plug's outer at depth.
        Assert.Equal(new Vec3(2 * D, 0, 0), frame.ToNeighbour(Vec3.Zero));
    }

    // ---- cells ------------------------------------------------------------------------------------

    /// <summary>A point of the opening falls in the cell whose centre is nearest; outside it, none.</summary>
    [Fact]
    public void ACellsCentreIsInThatCell()
    {
        for (int c = 0; c < DoorLightMath.CellCount; c++)
        {
            Vec3 centre = DoorLightMath.CellCentre(c, W, H, 3);
            Assert.Equal(3, centre.X);
            Assert.Equal(c, DoorLightMath.Cell(centre.Y, centre.Z, W, H));
        }

        Assert.Equal(0, DoorLightMath.Cell(-W / 2, -H / 2, W, H));
        Assert.Equal(-1, DoorLightMath.Cell(W / 2, 0, W, H));
        Assert.Equal(-1, DoorLightMath.Cell(-W / 2 - 1, 0, W, H));
        Assert.Equal(-1, DoorLightMath.Cell(0, H / 2, W, H));
        Assert.Equal(-1, DoorLightMath.Cell(0, -H / 2 - 1, W, H));
        Assert.True(DoorLightMath.Has(DoorLightMath.AllCells, DoorLightMath.CellCount - 1));
        Assert.False(DoorLightMath.Has(DoorLightMath.AllCells, DoorLightMath.CellCount));
        Assert.False(DoorLightMath.Has(UInt128.One << 5, 4));
    }

    /// <summary>
    /// Receivers' masks pack to a code each (none, every cell, some) and the
    /// partial masks in order, and unpack to the same masks.
    /// </summary>
    [Fact]
    public void SeenMasksRoundTripThroughTheirCodes()
    {
        UInt128[] masks = [UInt128.Zero, DoorLightMath.AllCells, UInt128.One << 7, UInt128.Zero, (UInt128.One << 80) | UInt128.One];
        DoorSeen seen = DoorSeen.Of(masks);
        Assert.Equal([0, 1, 2, 0, 2], seen.Codes);
        Assert.Equal([UInt128.One << 7, (UInt128.One << 80) | UInt128.One], seen.Partial);
        Assert.Equal(masks, seen.Masks());
        Assert.False(seen.None);
        Assert.True(DoorSeen.Of([UInt128.Zero, UInt128.Zero]).None);
    }

    // ---- falloff ----------------------------------------------------------------------------------

    /// <summary>
    /// A point light's falloff is its attenuation's reciprocal times the
    /// receiver's cosine; nothing reaches a receiver facing away or at the
    /// light; distances under one unit count as one.
    /// </summary>
    [Fact]
    public void APointLightFallsOffAsVradsDirectLight()
    {
        Vec3 up = new(0, 0, 1);
        DoorSource constant = Point(default);
        Assert.Equal(1f, DoorLightMath.Falloff(constant, new Vec3(0, 0, 50), up), 5);
        Assert.Equal(0.6f, DoorLightMath.Falloff(constant, new Vec3(80, 0, 60), up), 5);
        Assert.Equal(0f, DoorLightMath.Falloff(constant, new Vec3(0, 0, -50), up));
        Assert.Equal(0f, DoorLightMath.Falloff(constant, Vec3.Zero, up));

        DoorSource quadratic = Point(default, constant: 0, quadratic: 1);
        Assert.Equal(1f / 2500, DoorLightMath.Falloff(quadratic, new Vec3(0, 0, 50), up), 7);
        Assert.Equal(1f, DoorLightMath.Falloff(quadratic, new Vec3(0, 0, 0.5f), up), 5);
        Assert.False(quadratic.Flat);
        Assert.True(constant.Flat);
    }

    /// <summary>
    /// A spotlight: full inside its inner cone, nothing beyond its outer,
    /// between them the linear fringe raised to its exponent; times the
    /// cosine at the light (vrad's <c>dot2</c>).
    /// </summary>
    [Fact]
    public void ASpotlightHasItsConesAndExponent()
    {
        Vec3 up = new(0, 0, 1);
        DoorSource spot = new(EmitType.Spotlight, default, new Vec3(0, 0, -1), new Vec3(1, 1, 1), 0, 1, 0, 0, 0.9f, 0.5f, 1, DoorLightMath.AllCells);
        Assert.Equal(1f, DoorLightMath.Falloff(spot, new Vec3(0, 0, 10), up), 5);
        Assert.Equal(0f, DoorLightMath.Falloff(spot, new Vec3(10, 0, 1), up));

        // dot2 = 0.7: halfway through the fringe, times dot2, times the cosine 0.7.
        Vec3 fringe = new(0.71414284f, 0, 0.7f);
        Assert.Equal(0.7f * 0.5f * 0.7f, DoorLightMath.Falloff(spot, fringe * 10, up), 4);
        Assert.Equal(0.7f * 0.25f * 0.7f, DoorLightMath.Falloff(spot with { Exponent = 2 }, fringe * 10, up), 4);
        Assert.True(spot.Flat);
    }

    /// <summary>A surface light falls off with the square of distance and its own cosine; the sun only with the receiver's.</summary>
    [Fact]
    public void ASurfaceLightAndTheSunFallOffTheirOwnWays()
    {
        Vec3 up = new(0, 0, 1);
        DoorSource surface = new(EmitType.Surface, default, new Vec3(0, 0, -1), new Vec3(1, 1, 1), 0, 0, 0, 0, 0, 0, 0, DoorLightMath.AllCells);
        Assert.Equal(1f / 100, DoorLightMath.Falloff(surface, new Vec3(0, 0, 10), up), 6);
        Assert.Equal(0f, DoorLightMath.Falloff(surface with { Normal = new Vec3(0, 0, 1) }, new Vec3(0, 0, 10), up));
        Assert.False(surface.Flat);

        DoorSource sun = surface with { Type = EmitType.SkyLight };
        Assert.Equal(0.8f, DoorLightMath.Falloff(sun, new Vec3(0, 60, 80), up), 5);
        Assert.True(sun.Flat);
    }

    // ---- through both openings --------------------------------------------------------------------

    /// <summary>
    /// A receiver gets a source's light only when the segment toward it
    /// crosses this opening in a cell it sees and the neighbour's in a cell
    /// the source reaches; then exactly the source's falloff.
    /// </summary>
    [Fact]
    public void LightPassesOnlyThroughCellsBothSidesSee()
    {
        Vec3 point = new(-100, 0, 0);
        Vec3 facing = new(1, 0, 0);
        DoorSource source = Point(new Vec3(200, 0, 0));
        float full = DoorLightMath.Falloff(source, source.Origin - point, facing);
        Assert.True(full > 0);
        Assert.Equal(full, DoorLightMath.Through(source, point, facing, W, H, D, DoorLightMath.AllCells));

        int mine = DoorLightMath.Cell(0, 0, W, H);
        Assert.Equal(full, DoorLightMath.Through(source, point, facing, W, H, D, UInt128.One << mine));
        Assert.Equal(0f, DoorLightMath.Through(source, point, facing, W, H, D, DoorLightMath.AllCells ^ (UInt128.One << mine)));
        Assert.Equal(0f, DoorLightMath.Through(source with { Cells = UInt128.Zero }, point, facing, W, H, D, DoorLightMath.AllCells));

        // The neighbour's cells run the other way across.
        Vec3 aside = new(-100, 30, 0);
        DoorSource ahead = Point(new Vec3(100, -30, 0));
        int theirs = DoorLightMath.Cell(-(30 - (60 * (2 * D + 100) / 200)), 0, W, H);
        Assert.True(DoorLightMath.Through(ahead with { Cells = UInt128.One << theirs }, aside, facing, W, H, D, DoorLightMath.AllCells) > 0);
        int mirrored = DoorLightMath.Cell(-(60 * (2 * D + 100) / 200) + 30, 0, W, H);
        Assert.NotEqual(theirs, mirrored);
        Assert.Equal(0f, DoorLightMath.Through(ahead with { Cells = UInt128.One << mirrored }, aside, facing, W, H, D, DoorLightMath.AllCells));

        // A receiver on the far side of its opening, a source behind it, a segment missing the opening.
        Assert.Equal(0f, DoorLightMath.Through(source, new Vec3(1, 0, 0), facing, W, H, D, DoorLightMath.AllCells));
        Assert.Equal(0f, DoorLightMath.Through(Point(new Vec3(-200, 0, 0)), point, facing, W, H, D, DoorLightMath.AllCells));
        Assert.Equal(0f, DoorLightMath.Through(Point(new Vec3(100, 500, 0)), point, facing, W, H, D, DoorLightMath.AllCells));
        Assert.Equal(0f, DoorLightMath.Through(Point(new Vec3(100, 150, 0)), new Vec3(-10, 0, 0), facing, W, H, D, DoorLightMath.AllCells));

        // The sun comes along its rays, not from its origin.
        DoorSource sun = new(EmitType.SkyLight, new Vec3(-1000, 0, 0), new Vec3(-1, 0, 0), new Vec3(1, 1, 1), 0, 0, 0, 0, 0, 0, 0, DoorLightMath.AllCells);
        Assert.Equal(1f, DoorLightMath.Through(sun, point, facing, W, H, D, DoorLightMath.AllCells), 5);
    }

    /// <summary>
    /// A receiver's outcode names why a source's light misses it: behind
    /// the source, on the far side of its own opening, or past an edge of
    /// either opening (the neighbour's across order mirrored); zero where
    /// <see cref="DoorLightMath.Through"/> finds both openings crossed.
    /// </summary>
    [Fact]
    public void AnOutcodeNamesTheEdgeALightMisses()
    {
        Vec3 point = new(-100, 0, 0);
        Assert.Equal(0, DoorLightMath.Outcode(Point(new Vec3(200, 0, 0)), point, W, H, D));
        Assert.Equal(1, DoorLightMath.Outcode(Point(new Vec3(-200, 0, 0)), point, W, H, D));
        Assert.Equal(3, DoorLightMath.Outcode(Point(new Vec3(-200, 0, 0)), new Vec3(1, 0, 0), W, H, D));
        Assert.Equal(2, DoorLightMath.Outcode(Point(new Vec3(200, 0, 0)), new Vec3(1, 0, 0), W, H, D));
        Assert.Equal(8 | 64, DoorLightMath.Outcode(Point(new Vec3(100, 500, 0)), point, W, H, D));
        Assert.Equal(4 | 128, DoorLightMath.Outcode(Point(new Vec3(100, -500, 0)), point, W, H, D));
        Assert.Equal(32 | 512, DoorLightMath.Outcode(Point(new Vec3(100, 0, 900)), point, W, H, D));
        Assert.Equal(16 | 256, DoorLightMath.Outcode(Point(new Vec3(100, 0, -900)), point, W, H, D));

        // Through this opening, past the neighbour's.
        Assert.Equal(64, DoorLightMath.Outcode(Point(new Vec3(100, 150, 0)), new Vec3(-10, 0, 0), W, H, D));
        DoorSource sun = new(EmitType.SkyLight, default, new Vec3(-1, 0, 0), new Vec3(1, 1, 1), 0, 0, 0, 0, 0, 0, 0, DoorLightMath.AllCells);
        Assert.Equal(0, DoorLightMath.Outcode(sun, point, W, H, D));
        Assert.Equal(1, DoorLightMath.Outcode(sun with { Normal = new Vec3(1, 0, 0) }, point, W, H, D));
    }

    /// <summary>
    /// Over patches of receivers facing the opening and sources beyond it,
    /// the link's two shortcuts agree with evaluating every receiver: when
    /// the four corners' outcodes share a bit no receiver of the patch is
    /// lit, and when all four are zero and every cell is open to both, each
    /// receiver's light is the source's falloff alone.
    /// </summary>
    [Fact]
    public void ThePatchShortcutsAgreeWithEveryReceiver()
    {
        const int n = DoorLightMath.Subsamples;
        Random random = new(10);
        Vec3 facing = new(0.8f, 0, 0.6f);
        int culled = 0, open = 0;
        Span<Vec3> points = stackalloc Vec3[n * n];
        for (int trial = 0; trial < 4000; trial++)
        {
            Vec3 corner = new(-20 - (random.NextSingle() * 200), (random.NextSingle() * 400) - 200, (random.NextSingle() * 300) - 150);
            DoorSource source = Point(new Vec3((2 * D) + 10 + (random.NextSingle() * 220), (random.NextSingle() * 240) - 120, (random.NextSingle() * 240) - 120), quadratic: 1, constant: 0);
            for (int sy = 0; sy < n; sy++)
            {
                for (int sx = 0; sx < n; sx++)
                {
                    points[(sy * n) + sx] = corner + new Vec3(0, sx * 16f / n, sy * 16f / n);
                }
            }

            int c0 = DoorLightMath.Outcode(source, points[0], W, H, D), c1 = DoorLightMath.Outcode(source, points[n - 1], W, H, D);
            int c2 = DoorLightMath.Outcode(source, points[n * (n - 1)], W, H, D), c3 = DoorLightMath.Outcode(source, points[(n * n) - 1], W, H, D);
            for (int p = 0; p < n * n; p++)
            {
                float through = DoorLightMath.Through(source, points[p], facing, W, H, D, DoorLightMath.AllCells);
                if ((c0 & c1 & c2 & c3) != 0)
                {
                    Assert.Equal(0f, through);
                    culled++;
                }
                else if ((c0 | c1 | c2 | c3) == 0)
                {
                    Assert.Equal(DoorLightMath.Falloff(source, source.Origin - points[p], facing), through);
                    open++;
                }
            }
        }

        Assert.True(culled > 1000 && open > 1000, $"{culled} culled, {open} open");
    }

    /// <summary>A source carried across a joint lands where the neighbour's frame puts it.</summary>
    [Fact]
    public void ASourceCrossesTheJoint()
    {
        RoomDefinition room = RoomHarness.WalkableRoom("hub", RoomFacing.PositiveX, RoomFacing.NegativeX, RoomFacing.PositiveY, RoomFacing.NegativeY);
        DoorFrame frame = DoorFrame.Of(room, room.Sockets[0]);
        DoorSource source = new(EmitType.Spotlight, frame.ToRoom(new Vec3(-40, 10, 5)), frame.DirectionToRoom(new Vec3(1, 0, 0)), new Vec3(1, 1, 1), 0, 1, 0, 0, 0, 0, 0, 5);
        DoorSource moved = DoorLightMath.ToNeighbour(source, frame);
        Assert.True((new Vec3((2 * D) + 40, -10, 5) - moved.Origin).Length() < 1e-3f);
        Assert.True((new Vec3(-1, 0, 0) - moved.Normal).Length() < 1e-6f);
        Assert.Equal(source.Cells, moved.Cells);
    }

    // ---- faces ------------------------------------------------------------------------------------

    private static (DFace[] Faces, DPlane[] Planes, TexInfo[] Tex) Floor(int flags)
    {
        TexInfo tex = default;
        tex.LightmapVecsLuxelsPerWorldUnits[0] = 1 / 16f;
        tex.LightmapVecsLuxelsPerWorldUnits[5] = 1 / 16f;
        tex.TextureVecsTexelsPerWorldUnits[0] = 1;
        tex.TextureVecsTexelsPerWorldUnits[5] = 1;
        tex.Flags = flags;
        DFace face = default;
        face.LightmapTextureMinsInLuxels[0] = 2;
        face.LightmapTextureMinsInLuxels[1] = 1;
        face.LightmapTextureSizeInLuxels[0] = 4;
        face.LightmapTextureSizeInLuxels[1] = 2;
        DFace thin = face;
        thin.LightmapTextureSizeInLuxels[0] = 0;
        DFace unmapped = face;
        unmapped.TexInfo = 5;
        return ([face, thin, unmapped], [new DPlane { Normal = new Vec3(0, 0, 1), Dist = 8 }], [tex]);
    }

    /// <summary>
    /// A face's cells lie between its luxels on its plane (one on a face a
    /// luxel across); a bumped face has four pages; a face vrad does not
    /// light has none.
    /// </summary>
    [Fact]
    public void AFacesCellsLieBetweenItsLuxels()
    {
        (DFace[] faces, DPlane[] planes, TexInfo[] tex) = Floor(0);
        DoorFaceCells cells = DoorLightMath.FaceCells(faces, planes, tex, 0)!;
        Assert.Equal((5, 3, 4, 2, 8, 1), (cells.Width, cells.Height, cells.CellsAcross, cells.CellsUp, cells.Count, cells.Pages));
        Assert.True((new Vec3(40, 24, 8) - cells.Point(0)).Length() < 1e-3f);
        Assert.True((new Vec3(64, 44, 8) - cells.Point(5, 0.5f, 0.25f)).Length() < 1e-3f);
        Assert.Equal(new Vec3(0, 0, 1), cells.Normal);

        Assert.Equal((3.5f, 2.5f), cells.Coordinates(5));
        DoorFaceCells thin = DoorLightMath.FaceCells(faces, planes, tex, 1)!;
        Assert.Equal((1, 2), (thin.CellsAcross, thin.Count));
        Assert.True((new Vec3(32, 24, 8) - thin.Point(0)).Length() < 1e-3f);

        Assert.Null(DoorLightMath.FaceCells(faces, planes, tex, 2));
        foreach (SurfaceFlags flag in (ReadOnlySpan<SurfaceFlags>)[SurfaceFlags.NoLight, SurfaceFlags.Sky, SurfaceFlags.Sky2D, SurfaceFlags.NoDraw])
        {
            (DFace[] f, DPlane[] p, TexInfo[] t) = Floor((int)flag);
            Assert.Null(DoorLightMath.FaceCells(f, p, t, 0));
        }

        (DFace[] bf, DPlane[] bp, TexInfo[] bt) = Floor((int)SurfaceFlags.BumpLight);
        DoorFaceCells bumped = DoorLightMath.FaceCells(bf, bp, bt, 0)!;
        Assert.Equal(4, bumped.Pages);
        Assert.Equal(3, bumped.Bumps.Length);

        // A lightmap axis along the plane's normal leaves no solution.
        (DFace[] sf, DPlane[] sp, TexInfo[] st) = Floor(0);
        st[0].LightmapVecsLuxelsPerWorldUnits[0] = 0;
        st[0].LightmapVecsLuxelsPerWorldUnits[2] = 1 / 16f;
        Assert.Null(DoorLightMath.FaceCells(sf, sp, st, 0));
    }

    /// <summary>
    /// Each luxel is the mean of the cells around it: a uniform light stays
    /// uniform, a corner cell's light reaches only the luxels at its
    /// corners, pages stay apart, and a face a luxel across takes its one
    /// column of cells.
    /// </summary>
    [Fact]
    public void LuxelsAreTheMeanOfTheirCells()
    {
        DoorFaceCells face = new(0, 3, 2, default, default, default, 0, 0, new Vec3(0, 0, 1), []);
        float[] uniform = [1, 2, 3, 1, 2, 3];
        float[] luxels = new float[3 * 2 * 3];
        DoorLightMath.CellsToLuxels(face, uniform, luxels);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal([1f, 2f, 3f], luxels[(i * 3)..((i * 3) + 3)]);
        }

        float[] corner = [3, 0, 0, 0, 0, 0];
        luxels = new float[18];
        DoorLightMath.CellsToLuxels(face, corner, luxels);
        Assert.Equal([3f, 1.5f, 0f, 3f, 1.5f, 0f], Enumerable.Range(0, 6).Select(i => luxels[i * 3]));

        DoorFaceCells bumped = face with { Bumps = [default, default, default] };
        float[] pages = [1, 0, 0, 2, 0, 0, 3, 0, 0, 4, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0, 4, 0, 0];
        luxels = new float[4 * 6 * 3];
        DoorLightMath.CellsToLuxels(bumped, pages, luxels);
        Assert.Equal([1f, 2f, 3f, 4f], Enumerable.Range(0, 4).Select(p => luxels[p * 18]));

        DoorFaceCells column = new(0, 1, 3, default, default, default, 0, 0, new Vec3(0, 0, 1), []);
        luxels = new float[9];
        DoorLightMath.CellsToLuxels(column, [2, 0, 0, 4, 0, 0], luxels);
        Assert.Equal([2f, 3f, 4f], Enumerable.Range(0, 3).Select(i => luxels[i * 3]));

        DoorFaceCells row = new(0, 3, 1, default, default, default, 0, 0, new Vec3(0, 0, 1), []);
        luxels = new float[9];
        DoorLightMath.CellsToLuxels(row, [2, 0, 0, 4, 0, 0], luxels);
        Assert.Equal([2f, 3f, 4f], Enumerable.Range(0, 3).Select(i => luxels[i * 3]));
    }

    // ---- the response grid ------------------------------------------------------------------------

    /// <summary>
    /// The grid's nodes sit at the centres of its cells over the neighbour's
    /// interior, x fastest; each node sends a positive flux through the
    /// opening, more when nearer, and the constant set more than the
    /// inverse-square set.
    /// </summary>
    [Fact]
    public void TheGridsNodesFillTheNeighboursInterior()
    {
        Vec3 first = DoorLightMath.Node(0, 256, D);
        Vec3 last = DoorLightMath.Node(DoorLightMath.NodesPerClass - 1, 256, D);
        Assert.Equal(new Vec3((2 * D) + 56, -56, -56), first);
        Assert.Equal(new Vec3((2 * D) + 168, 56, 56), last);
        Assert.Equal(first.X + 112, DoorLightMath.Node(1, 256, D).X);

        double[] flux = DoorLightMath.NodeFlux(W, H, 256, D);
        Assert.Equal(DoorLightMath.EmitterCount, flux.Length);
        Assert.All(flux, f => Assert.True(f > 0));
        Assert.True(flux[0] > flux[1]);
        Assert.True(flux[DoorLightMath.NodesPerClass] > flux[0]);
    }

    /// <summary>
    /// A source's flux through its own opening counts only the cells it
    /// reaches; a source on a node goes wholly to that node, its colour
    /// divided by the node's flux; the sun and constant lights go to the
    /// constant set, and a source beyond the grid is clamped onto it.
    /// </summary>
    [Fact]
    public void ASourcesFluxIsSharedAmongTheNodesAroundIt()
    {
        double[] nodeFlux = DoorLightMath.NodeFlux(W, H, 256, D);
        Vec3 node = DoorLightMath.Node(3, 256, D);
        DoorSource lamp = Point(node, constant: 0, quadratic: 1);
        double all = DoorLightMath.SourceFlux(lamp, W, H, D);
        Assert.True(all > 0);
        Assert.Equal(0, DoorLightMath.SourceFlux(lamp with { Cells = UInt128.Zero }, W, H, D));
        Assert.True(DoorLightMath.SourceFlux(lamp with { Cells = UInt128.One }, W, H, D) < all);

        Vec3[] shares = new Vec3[DoorLightMath.EmitterCount];
        DoorLightMath.Project(lamp, new Vec3(2, 2, 2), nodeFlux, 256, D, shares);
        for (int e = 0; e < shares.Length; e++)
        {
            Assert.Equal(e == 3 ? (float)(2 / nodeFlux[3]) : 0f, shares[e].X, 5);
        }

        shares = new Vec3[DoorLightMath.EmitterCount];
        DoorLightMath.Project(Point(node), new Vec3(1, 1, 1), nodeFlux, 256, D, shares);
        Assert.True(shares[DoorLightMath.NodesPerClass + 3].X > 0);
        Assert.Equal(1, shares.Count(s => s.X != 0));

        // Halfway between two nodes, half each; far outside the grid, onto its corner.
        Vec3 between = (DoorLightMath.Node(0, 256, D) + DoorLightMath.Node(1, 256, D)) * 0.5f;
        shares = new Vec3[DoorLightMath.EmitterCount];
        DoorLightMath.Project(lamp with { Origin = between }, new Vec3(1, 1, 1), nodeFlux, 256, D, shares);
        Assert.Equal(0.5f / (float)nodeFlux[0], shares[0].X, 5);
        Assert.Equal(0.5f / (float)nodeFlux[1], shares[1].X, 5);

        shares = new Vec3[DoorLightMath.EmitterCount];
        DoorLightMath.Project(lamp with { Origin = new Vec3(5000, 5000, 5000) }, new Vec3(1, 1, 1), nodeFlux, 256, D, shares);
        Assert.Equal(1f / (float)nodeFlux[DoorLightMath.NodesPerClass - 1], shares[DoorLightMath.NodesPerClass - 1].X, 5);
        Assert.Equal(1, shares.Count(s => s.X != 0));
    }
}
