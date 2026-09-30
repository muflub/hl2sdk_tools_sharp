//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// What <c>ssmap rooms</c> lists of a room's displacement, water, detail
/// prop and map sections (<see cref="RoomPack.ReadFeatureSummariesAsync"/>): read from the
/// sections alone, a room without them not listed.
/// </summary>
public sealed class RoomFeatureSummaryTests
{
    /// <summary>
    /// A pack of the displacement harness's rooms says the hub has two
    /// displacements and the other room one, neither water, and each its
    /// map; a pack of the pool room says it has one water volume.
    /// </summary>
    [Fact]
    public async Task APacksSectionsSayWhatEachRoomCarries()
    {
        RoomLibrary patched = await RoomDisplacementHarness.CompileAsync(RoomDisplacementHarness.Library(RoomDisplacementHarness.Patches), cook: false);
        IReadOnlyDictionary<string, RoomFeatureSummary> summaries = await SummariesAsync(patched.Get("hub"), patched.Get("other"));
        Assert.Equal((2, (int?)null), (summaries["hub"].Displacements, summaries["hub"].WaterVolumes));
        Assert.Equal((1, (int?)null), (summaries["other"].Displacements, summaries["other"].WaterVolumes));
        RoomMapView hubMap = patched.Get("hub").MapViewOfCompile!;
        Assert.Equal(
            new RoomMapSummary(hubMap.Polygons.Count, hubMap.Polygons.Sum(p => 1 + p.Holes.Count), 0, string.Empty),
            summaries["hub"].Map);

        RoomLibrary pool = await RoomWaterHarness.CompileAsync(RoomWaterHarness.PoolLibrary());
        RoomFeatureSummary wet = (await SummariesAsync(pool.Get("hub")))["hub"];
        Assert.Equal((0, (int?)1), (wet.Displacements, wet.WaterVolumes));
        Assert.NotNull(wet.Map);
        Assert.Equal(0, wet.DetailProps);
    }

    /// <summary>
    /// A pack of the detail prop harness's rooms says how many detail props
    /// each room's <c>DPRP</c> section holds (the room's own lump's count),
    /// and a room without detail props says none.
    /// </summary>
    [Fact]
    public async Task APacksDetailPropSectionsSayHowManyPropsEachRoomHas()
    {
        RoomLibrary grassed = await RoomDetailHarness.CompileAsync(RoomDetailHarness.Library());
        IReadOnlyDictionary<string, RoomFeatureSummary> summaries = await SummariesAsync(grassed.Get("hub"), grassed.Get("other"));
        Assert.True(summaries["hub"].DetailProps > 0);
        Assert.Equal(RoomDetailProps.CountOf(grassed.Get("hub").Bsp), summaries["hub"].DetailProps);
        Assert.Equal(RoomDetailProps.CountOf(grassed.Get("other").Bsp), summaries["other"].DetailProps);
        Assert.NotEqual(summaries["hub"].DetailProps, summaries["other"].DetailProps);

        RoomLibrary bare = await RoomDisplacementHarness.CompileAsync(RoomDisplacementHarness.Library([]), cook: false);
        Assert.Equal(0, (await SummariesAsync(bare.Get("hub")))["hub"].DetailProps);
    }

    /// <summary>A room packed with its container alone (a pack written before the sections) is not listed.</summary>
    [Fact]
    public async Task ARoomWithoutTheSectionsIsNotListed()
    {
        RoomLibrary patched = await RoomDisplacementHarness.CompileAsync(RoomDisplacementHarness.Library([]), cook: false);
        RoomPackItem item = await RoomPackItem.CreateAsync(patched.Get("hub"));
        using MemoryStream stream = new();
        await RoomPack.SaveAsync([new RoomPackItem("hub", item.Room)], stream);
        stream.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        Assert.Empty(await RoomPack.ReadFeatureSummariesAsync(stream, index));
    }

    /// <summary>
    /// A map's summary counts its polygons, its rings (outer rings and
    /// holes), its markers, and gives its label; without the section there is
    /// none; a section cut short is refused with the room and section named.
    /// </summary>
    [Fact]
    public void AMapSummaryCountsRingsMarkersAndItsLabel()
    {
        MapPoint[] square = [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];
        MapPoint[] hole = [new(40, 40), new(40, 60), new(60, 60), new(60, 40)];
        MapPoint[] step = [new(120, 0), new(160, 0), new(160, 40), new(120, 40)];
        RoomMapView view = new(
            "Atrium",
            [new MapPolygon(16, 16, square, [hole]), new MapPolygon(32, 40, step, [])],
            [],
            [new RoomMapMarker("shop", "Shop", new Vec3(1, 2, 3), 90), new RoomMapMarker("exit", string.Empty, new Vec3(4, 5, 6), 0)]);
        byte[] section = view.ToSection().Bytes.ToArray();
        Assert.Equal(new RoomMapSummary(2, 3, 2, "Atrium"), RoomMapView.ReadSummary(section, "hub"));
        Assert.Null(RoomMapView.ReadSummary(null, "hub"));

        LinkException damaged = Assert.Throws<LinkException>(() => RoomMapView.ReadSummary(section[..^6], "hub"));
        Assert.Contains("hub", damaged.Message, StringComparison.Ordinal);
        Assert.Contains(RoomMapView.SectionTag, damaged.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counts read without the compile: none without the section, the
    /// section's own count with it, and a count the section cannot hold
    /// (no displacement, a negative water count) refused.
    /// </summary>
    [Fact]
    public async Task TheCountsAreTheSectionsOwn()
    {
        RoomLibrary patched = await RoomDisplacementHarness.CompileAsync(RoomDisplacementHarness.Library(RoomDisplacementHarness.Patches), cook: false);
        byte[] disp = patched.Get("hub").DisplacementsOfCompile!.ToSection().Bytes.ToArray();
        Assert.Equal(2, RoomDisplacements.ReadCount(disp, "hub"));
        Assert.Null(RoomDisplacements.ReadCount(null, "hub"));
        Assert.Throws<LinkException>(() => RoomDisplacements.ReadCount(WithFirstCount(disp, 0), "hub"));

        RoomLibrary pool = await RoomWaterHarness.CompileAsync(RoomWaterHarness.PoolLibrary());
        byte[] water = pool.Get("hub").WaterOfCompile!.ToSection().Bytes.ToArray();
        Assert.Equal(1, RoomWater.ReadVolumeCount(water, "hub"));
        Assert.Null(RoomWater.ReadVolumeCount(null, "hub"));
        Assert.Throws<LinkException>(() => RoomWater.ReadVolumeCount(WithFirstCount(water, -1), "hub"));

        RoomLibrary grassed = await RoomDetailHarness.CompileAsync(RoomDetailHarness.Library());
        RoomObject hub = grassed.Get("hub");
        byte[] props = hub.DetailPropsOfCompile!.ToSection().Bytes.ToArray();
        Assert.Equal(hub.DetailPropsOfCompile!.Count, RoomDetailProps.ReadCount(props, "hub"));
        Assert.Null(RoomDetailProps.ReadCount(null, "hub"));
        LinkException none = Assert.Throws<LinkException>(() => RoomDetailProps.ReadCount(WithFirstCount(props, 0), "hub"));
        Assert.Contains(RoomDetailProps.SectionTag, none.Message, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyDictionary<string, RoomFeatureSummary>> SummariesAsync(params RoomObject[] rooms)
    {
        List<RoomPackItem> items = [];
        foreach (RoomObject room in rooms)
        {
            items.Add(await RoomPackItem.CreateAsync(room));
        }

        using MemoryStream stream = new();
        await RoomPack.SaveAsync(items, stream);
        stream.Position = 0;
        RoomPackIndex index = await RoomPack.ReadIndexAsync(stream);
        return await RoomPack.ReadFeatureSummariesAsync(stream, index);
    }

    /// <summary>
    /// A section (codec none) with the count after its revision replaced: the
    /// framing is a codec byte and the decoded length (eight bytes), then the
    /// revision (four bytes, big endian) and the count (four more).
    /// </summary>
    private static byte[] WithFirstCount(byte[] section, int count)
    {
        byte[] changed = [.. section];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(changed.AsSpan(13, 4), count);
        return changed;
    }
}
