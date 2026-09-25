using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// <c>func_instance</c> merging, end to end on files built in memory.
/// </summary>
public class MapInstanceMergerTests
{
    [Fact]
    public async Task TheInstancesWorldBrushesArriveInTheHostsWorldspawn()
    {
        MapFile map = await MergeAsync(new Vec3(256f, 0f, 0f), Vec3.Zero);

        Assert.Equal(2, map.Entities[0].BrushCount);
        Assert.Equal(2, map.BrushCount);
    }

    /// <summary>
    /// Only worldspawn and ladder brushes are physically moved
    ///; the instance's own worldspawn brush is one, so it
    /// arrives at the instance entity's origin.
    /// </summary>
    [Fact]
    public async Task TheInstancesWorldBrushIsMovedToTheInstanceOrigin()
    {
        MapFile map = await MergeAsync(new Vec3(256f, 0f, 0f), Vec3.Zero);

        MapBrush moved = map.Brushes[1];

        Assert.Equal(256f, moved.Mins.X, 3);
        Assert.Equal(320f, moved.Maxs.X, 3);
    }

    /// <summary>
    /// The <c>func_instance</c> entity itself is blanked once merged,
    /// but its SLOT survives.
    /// </summary>
    [Fact]
    public async Task TheFuncInstanceEntityIsBlankedButItsSlotSurvives()
    {
        MapFile map = await MergeAsync(new Vec3(256f, 0f, 0f), Vec3.Zero);

        Assert.Equal("worldspawn", map.Entities[0].ValueForKey("classname"));
        Assert.Empty(map.Entities[1].Pairs);
        Assert.Equal(0, map.Entities[1].BrushCount);
    }

    /// <summary>
    /// <c>MergePlanes</c> adds the instance's planes UNTRANSFORMED and the
    /// side merge then adds the transformed ones separately, so an offset instance
    /// contributes two sets of planes and the untransformed ones stay in the
    /// table whether anything references them or not.
    /// </summary>
    [Fact]
    public async Task AnOffsetInstanceContributesBothItsOwnPlanesAndTheTransformedOnes()
    {
        MapFile offset = await MergeAsync(new Vec3(256f, 0f, 0f), Vec3.Zero);
        MapFile inPlace = await MergeAsync(Vec3.Zero, Vec3.Zero);

        Assert.True(
            offset.Planes.Count > inPlace.Planes.Count,
            $"offset {offset.Planes.Count} planes, in place {inPlace.Planes.Count}");
    }

    /// <summary>
    /// A <c>func_instance</c> whose file cannot be found is a diagnostic and a
    /// blanked entity, not a failed compile.
    /// </summary>
    [Fact]
    public async Task AMissingInstanceFileIsReportedAndTheEntityIsBlanked()
    {
        VbspContext context = await UnitMap.ContextAsync();
        InMemoryFileSystem files = new();

        VmfDocument host = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("classname", "worldspawn");
        world.Children.Add(UnitMap.Box(UnitMap.Plain, (0, 0, 0), (64, 64, 64), 1));
        host.Chunks.Add(world);

        VmfChunk instance = new(MapFileLoader.EntityChunk);
        instance.AddKey("classname", FuncInstance.ClassName);
        instance.AddKey("file", "not_there.vmf");
        host.Chunks.Add(instance);

        files.AddFile("maps/host.vmf", host.ToBytes());

        MapFileReader reader = new(context, files);
        MapFile map = await reader.LoadAsync(VPath.Create("maps/host.vmf"));

        Assert.Contains(
            context.Diagnostics,
            d => d.Code == MapLoadDiagnostics.InstanceNotFound);
        Assert.Empty(map.Entities[1].Pairs);
    }

    /// <summary>
    /// <c>replace</c> keys substitute into the instance's entity values, and
    /// the variable and its replacement are separated by a SPACE.
    /// </summary>
    [Fact]
    public void ReplaceKeysSubstituteIntoAValue()
    {
        MapEntity instance = new();
        instance.SetKeyValue("replace01", "$door door_a");
        instance.SetKeyValue("replace02", "$delay 2.5");

        string result = MapInstanceMerger.ReplaceInstanceVariables("$door,Open,,$delay,-1", instance);

        Assert.Equal("door_a,Open,,2.5,-1", result);
    }

    [Fact]
    public void AReplaceValueWithNoSpaceIsSkipped()
    {
        MapEntity instance = new();
        instance.SetKeyValue("replace01", "$door");

        Assert.Equal("$door", MapInstanceMerger.ReplaceInstanceVariables("$door", instance));
    }

    private static async Task<MapFile> MergeAsync(Vec3 origin, Vec3 angles)
    {
        VbspContext context = await UnitMap.ContextAsync();
        InMemoryFileSystem files = new();

        VmfDocument host = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("classname", "worldspawn");
        world.Children.Add(UnitMap.Box(UnitMap.Plain, (0, 0, 0), (64, 64, 64), 1));
        host.Chunks.Add(world);

        VmfChunk instance = new(MapFileLoader.EntityChunk);
        instance.AddKey("classname", FuncInstance.ClassName);
        instance.AddKey("file", "room.vmf");
        instance.AddKey("fixup_style", "2");
        instance.AddKey("origin", $"{origin.X} {origin.Y} {origin.Z}");
        instance.AddKey("angles", $"{angles.X} {angles.Y} {angles.Z}");
        host.Chunks.Add(instance);

        files.AddFile("maps/host.vmf", host.ToBytes());
        files.AddFile(
            "maps/room.vmf",
            UnitMap.BoxMap(UnitMap.Plain, (0, 0, 0), (64, 64, 64)).ToBytes());

        MapFileReader reader = new(context, files);
        return await reader.LoadAsync(VPath.Create("maps/host.vmf"));
    }
}
