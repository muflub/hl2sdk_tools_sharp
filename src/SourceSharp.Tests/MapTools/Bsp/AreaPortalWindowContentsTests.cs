//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Materials;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// <see cref="MapFileLoader.ForceFuncAreaPortalWindowContents"/>: an areaportal
/// window's <c>target</c> and <c>BackgroundBModel</c> brushes become
/// translucent windows. The name lookup is one table per pass now, not a scan
/// of every entity per target; it must pick the entity the scan picked -- the
/// FIRST with the name, compared without case.
/// </summary>
public class AreaPortalWindowContentsTests
{
    private const int Window = (int)(BrushContents.Translucent | BrushContents.Window);

    [Fact]
    public void TheTargetsBrushesBecomeWindows()
    {
        MapFile map = new(new WindingArena());
        AddWindow(map, "func_areaportalwindow", target: "glass");
        MapBrush glass = AddBrushEntity(map, "glass");

        MapFileLoader.ForceFuncAreaPortalWindowContents(map);

        Assert.Equal(Window, glass.Contents);
    }

    [Fact]
    public void TheFirstEntityWithTheNameIsTheOneChanged()
    {
        MapFile map = new(new WindingArena());
        AddWindow(map, "func_areaportalwindow", target: "glass");
        MapBrush first = AddBrushEntity(map, "glass");
        MapBrush second = AddBrushEntity(map, "glass");

        MapFileLoader.ForceFuncAreaPortalWindowContents(map);

        Assert.Equal(Window, first.Contents);
        Assert.Equal((int)BrushContents.Solid, second.Contents);
    }

    [Fact]
    public void TheNameIsMatchedWithoutCase()
    {
        MapFile map = new(new WindingArena());
        AddWindow(map, "func_areaportalwindow", target: "GLASS");
        MapBrush glass = AddBrushEntity(map, "Glass");

        MapFileLoader.ForceFuncAreaPortalWindowContents(map);

        Assert.Equal(Window, glass.Contents);
    }

    [Fact]
    public void TheBackgroundModelIsFollowedToo()
    {
        MapFile map = new(new WindingArena());
        AddWindow(map, "func_areaportalwindow", target: "glass", background: "backdrop");
        MapBrush glass = AddBrushEntity(map, "glass");
        MapBrush backdrop = AddBrushEntity(map, "backdrop");

        MapFileLoader.ForceFuncAreaPortalWindowContents(map);

        Assert.Equal(Window, glass.Contents);
        Assert.Equal(Window, backdrop.Contents);
    }

    [Fact]
    public void AMissingTargetChangesNothing()
    {
        MapFile map = new(new WindingArena());
        AddWindow(map, "func_areaportalwindow", target: "nowhere");
        MapBrush other = AddBrushEntity(map, "glass");

        MapFileLoader.ForceFuncAreaPortalWindowContents(map);

        Assert.Equal((int)BrushContents.Solid, other.Contents);
    }

    [Fact]
    public void AnEmptyTargetIsNotMatchedToAnUnnamedEntity()
    {
        // Every entity without a targetname has the empty name; a window with
        // no target must not reach the first of them.
        MapFile map = new(new WindingArena());
        MapBrush unnamed = AddBrushEntity(map, null);
        AddWindow(map, "func_areaportalwindow", target: string.Empty);

        MapFileLoader.ForceFuncAreaPortalWindowContents(map);

        Assert.Equal((int)BrushContents.Solid, unnamed.Contents);
    }

    [Fact]
    public void APlainAreaportalIsLeftAlone()
    {
        MapFile map = new(new WindingArena());
        AddWindow(map, "func_areaportal", target: "door");
        MapBrush door = AddBrushEntity(map, "door");

        MapFileLoader.ForceFuncAreaPortalWindowContents(map);

        Assert.Equal((int)BrushContents.Solid, door.Contents);
    }

    [Fact]
    public void AMapWithNoWindowsIsUntouched()
    {
        MapFile map = new(new WindingArena());
        MapBrush glass = AddBrushEntity(map, "glass");

        MapFileLoader.ForceFuncAreaPortalWindowContents(map);

        Assert.Equal((int)BrushContents.Solid, glass.Contents);
    }

    private static void AddWindow(MapFile map, string className, string target, string? background = null)
    {
        MapEntity window = new();
        window.SetKeyValue("classname", className);
        window.SetKeyValue("target", target);
        if (background is not null)
        {
            window.SetKeyValue("BackgroundBModel", background);
        }

        map.Entities.Add(window);
    }

    private static MapBrush AddBrushEntity(MapFile map, string? targetName)
    {
        MapEntity entity = new() { FirstBrush = map.Brushes.Count, BrushCount = 1 };
        entity.SetKeyValue("classname", "func_brush");
        if (targetName is not null)
        {
            entity.SetKeyValue("targetname", targetName);
        }

        MapBrush brush = new() { Contents = (int)BrushContents.Solid };
        map.Brushes.Add(brush);
        map.Entities.Add(entity);
        return brush;
    }
}
