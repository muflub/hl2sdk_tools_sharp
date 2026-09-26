//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Materials;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Csg;

/// <summary>
/// <c>FixupAreaportalWaterBrushes</c>: the one part of the CSG stage whose
/// output reaches the BRUSHES and BRUSHSIDES lumps.
/// </summary>
public class AreaportalWaterFixupTests
{
    private const int ApTexInfo = 41;
    private const int WaterTexInfo = 77;

    [Fact]
    public async Task AnAreaportalInsideWaterTakesTheWatersContents()
    {
        (BspBuildContext build, MapFile map) = await Overlapping();

        AreaportalWaterFixup.FixupAreaportalWaterBrushes(build, CsgFixture.AllBrushes(build));

        Assert.Equal(
            (int)(BrushContents.AreaPortal | BrushContents.Water),
            map.Brushes[0].Contents);
    }

    /// <summary>
    /// The MAP sides, not just the carved copies — this is what
    /// <c>EmitBrushes</c> writes into BRUSHSIDES.
    /// </summary>
    [Fact]
    public async Task TheAreaportalsMapSidesTakeTheWatersTexinfo()
    {
        (BspBuildContext build, MapFile map) = await Overlapping();

        AreaportalWaterFixup.FixupAreaportalWaterBrushes(build, CsgFixture.AllBrushes(build));

        MapBrush areaportal = map.Brushes[0];
        for (int i = 0; i < areaportal.SideCount; i++)
        {
            Assert.Equal(WaterTexInfo, map.BrushSides[areaportal.FirstSide + i].TexInfo);
        }
    }

    [Fact]
    public async Task TheWaterBrushIsLeftAlone()
    {
        (BspBuildContext build, MapFile map) = await Overlapping();

        AreaportalWaterFixup.FixupAreaportalWaterBrushes(build, CsgFixture.AllBrushes(build));

        MapBrush water = map.Brushes[1];
        Assert.Equal((int)BrushContents.Water, water.Contents);
        for (int i = 0; i < water.SideCount; i++)
        {
            Assert.Equal(WaterTexInfo, map.BrushSides[water.FirstSide + i].TexInfo);
        }
    }

    [Fact]
    public async Task AnAreaportalNowhereNearWaterIsUntouched()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Plain, (512, 512, 512), (576, 576, 576))));

        Prepare(map);

        AreaportalWaterFixup.FixupAreaportalWaterBrushes(build, CsgFixture.AllBrushes(build));

        Assert.Equal((int)BrushContents.AreaPortal, map.Brushes[0].Contents);
        Assert.Equal(ApTexInfo, map.BrushSides[map.Brushes[0].FirstSide].TexInfo);
    }

    /// <summary>
    /// The block loop runs this once per block and the world is compiled twice,
    /// so it has to be idempotent — and it is, because both effects are an
    /// <c>|=</c> of the same bits and an assignment of the same texinfo.
    /// </summary>
    [Fact]
    public async Task RunningItTwiceChangesNothingTheSecondTime()
    {
        (BspBuildContext build, MapFile map) = await Overlapping();

        AreaportalWaterFixup.FixupAreaportalWaterBrushes(build, CsgFixture.AllBrushes(build));
        int contents = map.Brushes[0].Contents;
        int texInfo = map.BrushSides[map.Brushes[0].FirstSide].TexInfo;

        AreaportalWaterFixup.FixupAreaportalWaterBrushes(build, CsgFixture.AllBrushes(build));

        Assert.Equal(contents, map.Brushes[0].Contents);
        Assert.Equal(texInfo, map.BrushSides[map.Brushes[0].FirstSide].TexInfo);
    }

    /// <summary>
    /// The inner loop skips brushes that already carry
    /// <c>CONTENTS_AREAPORTAL</c>, so an areaportal that has just been given
    /// water contents cannot then donate them to a second areaportal.
    /// </summary>
    [Fact]
    public async Task AFixedUpAreaportalDoesNotBecomeADonorForAnother()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Plain, (32, 32, 32), (96, 96, 96)),
                (UnitMap.Plain, (16, 16, 16), (80, 80, 80))));

        SetContents(map, 0, BrushContents.AreaPortal, ApTexInfo);
        SetContents(map, 1, BrushContents.Water, WaterTexInfo);
        SetContents(map, 2, BrushContents.AreaPortal, 99);

        AreaportalWaterFixup.FixupAreaportalWaterBrushes(build, CsgFixture.AllBrushes(build));

        // Brush 2 overlaps brush 0, which now has water bits -- but brush 0 is
        // still an areaportal, so it is skipped as a donor and brush 2 only
        // ever sees the real water brush.
        Assert.Equal(
            (int)(BrushContents.AreaPortal | BrushContents.Water),
            map.Brushes[2].Contents);
        Assert.Equal(WaterTexInfo, map.BrushSides[map.Brushes[2].FirstSide].TexInfo);
    }

    private static async Task<(BspBuildContext Build, MapFile Map)> Overlapping()
    {
        (BspBuildContext build, MapFile map) = await CsgFixture.LoadAsync(
            CsgFixture.World(
                (UnitMap.Plain, (0, 0, 0), (64, 64, 64)),
                (UnitMap.Plain, (32, 32, 32), (96, 96, 96))));

        Prepare(map);
        return (build, map);
    }

    private static void Prepare(MapFile map)
    {
        SetContents(map, 0, BrushContents.AreaPortal, ApTexInfo);
        SetContents(map, 1, BrushContents.Water, WaterTexInfo);
    }

    private static void SetContents(MapFile map, int brush, BrushContents contents, int texInfo)
    {
        map.Brushes[brush].Contents = (int)contents;
        for (int i = 0; i < map.Brushes[brush].SideCount; i++)
        {
            map.BrushSides[map.Brushes[brush].FirstSide + i].TexInfo = texInfo;
        }
    }
}
