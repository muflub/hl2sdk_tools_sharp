//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// <see cref="MapFile.SideIdToIndex"/>, now answered from a table instead of a
/// scan of every side. The specification is the scan: the FIRST side with the
/// id, or -1 -- so each fact asks both and demands they agree, including after
/// every change that could make a remembered answer wrong.
/// </summary>
public class SideIdIndexTests
{
    [Fact]
    public void AnIdIsFoundAtItsIndex()
    {
        MapFile map = MapWithIds(5, 6, 7);

        Assert.Equal(1, map.SideIdToIndex(6));
        AssertAgreesWithTheScan(map, 5, 6, 7);
    }

    [Fact]
    public void AnAbsentIdIsMinusOne()
    {
        MapFile map = MapWithIds(5, 6, 7);

        Assert.Equal(-1, map.SideIdToIndex(8));
        Assert.Equal(-1, new MapFile(new WindingArena()).SideIdToIndex(0));
    }

    [Fact]
    public void ARepeatedIdIsFoundAtItsFirstSide()
    {
        // Ids are not promised unique (an instance merge offsets them, a hand
        // edit can repeat them), and the scan stops at the first.
        MapFile map = MapWithIds(3, 9, 3, 9);

        Assert.Equal(0, map.SideIdToIndex(3));
        Assert.Equal(1, map.SideIdToIndex(9));
    }

    [Fact]
    public void ASideAddedAfterALookupIsFound()
    {
        MapFile map = MapWithIds(1, 2);
        Assert.Equal(-1, map.SideIdToIndex(3));

        map.AddBrushSide(new MapBrushSide { Id = 3 }, default);

        Assert.Equal(2, map.SideIdToIndex(3));
    }

    [Fact]
    public void AnAddedDuplicateDoesNotDisplaceTheFirst()
    {
        MapFile map = MapWithIds(1, 2);
        Assert.Equal(1, map.SideIdToIndex(2));

        map.AddBrushSide(new MapBrushSide { Id = 2 }, default);

        Assert.Equal(1, map.SideIdToIndex(2));
    }

    [Fact]
    public void AnIdChangedAfterALookupIsSeen()
    {
        MapFile map = MapWithIds(1, 2, 3);
        Assert.Equal(1, map.SideIdToIndex(2));

        map.BrushSides[1].Id = 20;

        Assert.Equal(-1, map.SideIdToIndex(2));
        Assert.Equal(1, map.SideIdToIndex(20));
        AssertAgreesWithTheScan(map, 1, 2, 3, 20);
    }

    [Fact]
    public void AnEarlierSideTakingAnIdBecomesItsFirst()
    {
        // The case a remembered answer could get wrong without being told:
        // the table says 3 is at index 2, then index 0 is renamed 3.
        MapFile map = MapWithIds(1, 2, 3);
        Assert.Equal(2, map.SideIdToIndex(3));

        map.BrushSides[0].Id = 3;

        Assert.Equal(0, map.SideIdToIndex(3));
        Assert.Equal(-1, map.SideIdToIndex(1));
    }

    [Fact]
    public void ASideInTwoMapsTellsBoth()
    {
        MapBrushSide shared = new() { Id = 4 };
        MapFile first = MapWithIds(1);
        MapFile second = MapWithIds(2);
        first.AddBrushSide(shared, default);
        second.AddBrushSide(shared, default);
        Assert.Equal(1, first.SideIdToIndex(4));
        Assert.Equal(1, second.SideIdToIndex(4));

        shared.Id = 40;

        Assert.Equal(1, first.SideIdToIndex(40));
        Assert.Equal(1, second.SideIdToIndex(40));
        Assert.Equal(-1, first.SideIdToIndex(4));
    }

    [Fact]
    public void BevelOrderingThatMovesSidesIsSeen()
    {
        // AddBrushBevels puts the axial sides in -X,+X,-Y,+Y,-Z,+Z order by
        // swapping; a table built before the swaps would point at the old
        // slots.
        MapFile map = new(new WindingArena());
        (Vec3 Normal, float Dist, int Id)[] sides =
        [
            (new Vec3(0, 0, 1), 64, 10),
            (new Vec3(0, 0, -1), 0, 11),
            (new Vec3(0, 1, 0), 64, 12),
            (new Vec3(0, -1, 0), 0, 13),
            (new Vec3(1, 0, 0), 64, 14),
            (new Vec3(-1, 0, 0), 0, 15),
        ];

        foreach ((Vec3 normal, float dist, int id) in sides)
        {
            map.AddBrushSide(new MapBrushSide { Id = id, PlaneNumber = map.Planes.Find(normal, dist) }, default);
        }

        MapBrush brush = new() { FirstSide = 0, SideCount = 6 };
        Assert.Equal(5, map.SideIdToIndex(15));

        map.AddBrushBevels(brush);

        Assert.Equal(0, map.SideIdToIndex(15));
        Assert.Equal(5, map.SideIdToIndex(10));
        AssertAgreesWithTheScan(map, 10, 11, 12, 13, 14, 15);
    }

    [Fact]
    public void SettingTheSameIdAgainIsNotAChange()
    {
        MapBrushSide side = new() { Id = 7 };
        int raised = 0;
        side.IdChanged += () => raised++;

        side.Id = 7;
        Assert.Equal(0, raised);

        side.Id = 8;
        Assert.Equal(1, raised);
        Assert.Equal(8, side.Id);
    }

    [Fact]
    public void ASideNotInAnyMapCanChangeItsId()
    {
        MapBrushSide side = new() { Id = 1 };

        side.Id = 2;

        Assert.Equal(2, side.Id);
    }

    private static MapFile MapWithIds(params int[] ids)
    {
        MapFile map = new(new WindingArena());
        foreach (int id in ids)
        {
            map.AddBrushSide(new MapBrushSide { Id = id }, default);
        }

        return map;
    }

    private static void AssertAgreesWithTheScan(MapFile map, params int[] ids)
    {
        foreach (int id in ids)
        {
            int expected = -1;
            for (int i = 0; i < map.BrushSides.Count; i++)
            {
                if (map.BrushSides[i].Id == id)
                {
                    expected = i;
                    break;
                }
            }

            Assert.Equal(expected, map.SideIdToIndex(id));
        }
    }
}
