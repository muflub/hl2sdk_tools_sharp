//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapFormats.Zip;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Validation;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The base bake through the link (the rooms design, section 9, and its
/// prototype checks, 9.7): a room alone, every socket capped, links to its
/// own bake, which is vrad of the link itself and the full compile of the
/// flattened level at every quarter turn; a jointed level keeps each room's
/// base and differs from the full compile only by the light the doors let
/// through, which the door terms of PR 10 add; the sky flags, the switchable
/// styles, the stored turn a placement takes and the sharing of stored turns
/// between placements.
/// </summary>
public sealed class LevelLinkerLightingTests(LitRoomsFixture fixture, ITestOutputHelper output) : IClassFixture<LitRoomsFixture>
{
    /// <summary>Both rooms at the four quarter turns.</summary>
    public static TheoryData<string, int> RoomsAndTurns => new()
    {
        { "hub", 0 }, { "hub", 90 }, { "hub", 180 }, { "hub", 270 },
        { "other", 0 }, { "other", 90 }, { "other", 180 }, { "other", 270 },
    };

    // ---- 9.7 check 5: a capped room is exact ---------------------------------------------------

    /// <summary>
    /// A room alone, every socket capped, links to vrad of that very linked
    /// map at every quarter turn (the hub, which no sun reaches, from its one
    /// stored bake; the other room, whose sky ceiling the sun lights, from
    /// its bake for that turn): the same styles on every face, the same
    /// luxels byte for byte on every face more than one luxel across, the
    /// same world lights, leaf ambient index, vertex normals, sky flags and
    /// map flags. A face one luxel across has its luxel on its edges, where a
    /// sample point's side of a plane is decided by the float rounding of the
    /// turn, so vrad does not light it the same at every turn; those faces
    /// are the one exception, and are held to agree at turn 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoomsAndTurns))]
    public async Task ACappedRoomLinksToVradOfItsOwnLinkAtEveryTurn(string room, int rotation)
    {
        string row = $"{room}@{rotation}";
        BspData linked = await fixture.LinkedAsync(row);
        BspData relit = await fixture.RelitAsync(row);

        (bool styles, int luxels, int differ, int thin) = LitCompare.SameFaces(
            linked, relit, (f, i, a, b) => output.WriteLine($"  face {f} luxel {i}: {a} against {b}, {LitCompare.Relative(a, b):G3}"));
        output.WriteLine($"{row}: {luxels} luxels, {differ} differ, {thin} on thin faces differ");
        Assert.True(styles);
        Assert.True(luxels > 0);
        Assert.Equal(0, differ);
        if (rotation == 0)
        {
            Assert.Equal(0, thin);
        }

        Vec3[] na = BspStructView.As<Vec3>(linked[BspLump.VertNormals]).ToArray();
        Vec3[] nb = BspStructView.As<Vec3>(relit[BspLump.VertNormals]).ToArray();
        output.WriteLine("  normals " + string.Join(" ", na.Select(v => $"({v.X},{v.Y},{v.Z})")) + " | " + string.Join(" ", nb.Select(v => $"({v.X},{v.Y},{v.Z})")));
        foreach (BspLump lump in (ReadOnlySpan<BspLump>)[BspLump.WorldLights, BspLump.LeafAmbientIndex, BspLump.VertNormals, BspLump.VertNormalIndices, BspLump.Leafs, BspLump.MapFlags])
        {
            Assert.True(linked[lump].Data.Span.SequenceEqual(relit[lump].Data.Span), $"{row}: {lump}");
        }

        // The ambient samples: the same bytes at turn 0; at a turn, the cube
        // faces permuted and the positions turned, and vrad's own samples
        // of the turned room drawn along world-fixed directions at positions
        // it picks in the turned box, so they agree to a tolerance.
        DLeafAmbientLighting[] ours = BspStructView.As<DLeafAmbientLighting>(linked[BspLump.LeafAmbientLighting]).ToArray();
        DLeafAmbientLighting[] theirs = BspStructView.As<DLeafAmbientLighting>(relit[BspLump.LeafAmbientLighting]).ToArray();
        DLeafAmbientIndex[] index = BspStructView.As<DLeafAmbientIndex>(linked[BspLump.LeafAmbientIndex]).ToArray();
        Assert.Equal(theirs.Length, ours.Length);
        // Per leaf and cube face, the mean of the leaf's samples: which of
        // its candidates vrad keeps depends on their values to the last bit,
        // so a leaf's light is compared as the mean of what it kept.
        List<double> relative = [];
        DLeafAmbientIndex[] theirIndex = BspStructView.As<DLeafAmbientIndex>(relit[BspLump.LeafAmbientIndex]).ToArray();
        for (int l = 0; l < index.Length; l++)
        {
            if (index[l].AmbientSampleCount == 0)
            {
                continue;
            }

            Assert.Equal(theirIndex[l].AmbientSampleCount, index[l].AmbientSampleCount);
            for (int side = 0; side < 6; side++)
            {
                relative.Add(LitCompare.Relative(Mean(ours, index[l], side), Mean(theirs, theirIndex[l], side)));
            }
        }

        static Vec3 Mean(DLeafAmbientLighting[] samples, DLeafAmbientIndex leaf, int side)
        {
            Vec3 sum = Vec3.Zero;
            for (int s = leaf.FirstAmbientSample; s < leaf.FirstAmbientSample + leaf.AmbientSampleCount; s++)
            {
                sum += samples[s].Cube.Color[side].ToLinear();
            }

            return sum * (1f / leaf.AmbientSampleCount);
        }

        relative.Sort();
        output.WriteLine($"  ambient: {ours.Length} samples, leaf means p50 {LitCompare.Quantile(relative, 0.5):G3} p95 {LitCompare.Quantile(relative, 0.95):G3} max {LitCompare.Quantile(relative, 1):G3}");
        if (rotation == 0)
        {
            Assert.True(linked[BspLump.LeafAmbientLighting].Data.Span.SequenceEqual(relit[BspLump.LeafAmbientLighting].Data.Span));
        }
        else if (room == "hub")
        {
            // Lit once, turned at link: the leaf's light is the turned room's.
            Assert.All(relative, r => Assert.Equal(0, r));
        }
        else
        {
            // Lit per turn: vrad keeps a leaf's best samples out of its
            // candidates (AmbientSampleList.Compress), and the candidates of
            // the room lit in its frame stand a rounding away from the turned
            // room's, so it keeps others; measured, a leaf face's mean within
            // 0.23 of the turned room's.
            Assert.True(LitCompare.Quantile(relative, 0.95) <= 0.3, $"{row}: p95 {LitCompare.Quantile(relative, 0.95)}");
        }

        // The prop lighting: the hub's one prop, lit in its file under its
        // linked index.
        (string Name, byte[] Data)[] ourFiles = await VhvFiles(linked);
        (string Name, byte[] Data)[] theirFiles = await VhvFiles(relit);
        Assert.Equal(theirFiles.Select(f => f.Name), ourFiles.Select(f => f.Name));
        Assert.Equal(room == "hub" ? 1 : 0, ourFiles.Length);
        Assert.All(ourFiles.Zip(theirFiles), pair => Assert.True(pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data), pair.First.Name));
    }

    /// <summary>The static prop lighting files of a map's pak, by name.</summary>
    internal static async Task<(string Name, byte[] Data)[]> VhvFiles(BspData bsp)
    {
        if (bsp[BspLump.PakFile].IsEmpty)
        {
            return [];
        }

        ZipArchiveReader pak = await ZipArchiveReader.ParseAsync(bsp[BspLump.PakFile].Data, CancellationToken.None);
        return [.. pak.Entries.Where(e => e.Name.EndsWith(".vhv", StringComparison.Ordinal)).OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => (e.Name, e.Data))];
    }

    /// <summary>
    /// A room alone, every socket capped, against the full compile of its
    /// flattened level (<c>link --flatten</c>, then vbsp, vvis and vrad with
    /// the rooms' switches; the rooms design, 9.7 check 5). At turn 0 the two
    /// maps are the same luxels, byte for byte, at every point of the world
    /// where both have one, and neither has a luxel the other lacks. At a
    /// turn, vbsp compiling the turned VMF cuts faces and places lightmap
    /// grids its own way (it is not invariant under a quarter turn, where
    /// the link is), so luxels meet only where the two grids do and the
    /// bounce off differently cut patches differs: the luxels both have are
    /// held to the tolerance measured here (9.8), and exactness at a turn is
    /// the fact above, against vrad of the link itself.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoomsAndTurns))]
    public async Task ACappedRoomAgreesWithItsFlattenedFullCompile(string room, int rotation)
    {
        string row = $"{room}@{rotation}";
        var linked = LitCompare.Lattice(await fixture.LinkedAsync(row));
        var flat = LitCompare.Lattice(await fixture.FlatAsync(row));
        List<double> relative = [];
        int exact = 0, onlyLinked = 0;
        foreach ((var key, (List<ColorRgbExp32> colours, bool thin)) in linked)
        {
            if (!flat.TryGetValue(key, out var other))
            {
                onlyLinked += thin ? 0 : 1;
                continue;
            }

            if (thin || other.Thin)
            {
                continue;
            }

            exact += colours.Any(c => other.Colours.Any(o => LitCompare.Same(c, o))) ? 1 : 0;
            relative.Add(colours.Min(c => other.Colours.Min(o => LitCompare.Relative(c.ToLinear(), o.ToLinear()))));
        }

        int onlyFlat = flat.Count(k => !k.Value.Thin && !linked.ContainsKey(k.Key));
        relative.Sort();
        double p50 = LitCompare.Quantile(relative, 0.5), p95 = LitCompare.Quantile(relative, 0.95), p99 = LitCompare.Quantile(relative, 0.99);
        output.WriteLine(
            $"{row}: {relative.Count} luxels in both, {exact} exact, only linked {onlyLinked}, only flattened {onlyFlat}, "
            + $"p50 {p50:G3} p95 {p95:G3} p99 {p99:G3} max {LitCompare.Quantile(relative, 1):G3}");
        Assert.True(relative.Count > 0);
        if (rotation == 0)
        {
            Assert.Equal(relative.Count, exact);
            Assert.Equal(0, onlyLinked);
            Assert.Equal(0, onlyFlat);
        }

        // At a turn: the grids that do not meet hold as many luxels each; of
        // the luxels both hold, all but a handful by the prop's shadow are
        // exact (measured: at most 6 of 462 differ, by at most 0.2).
        Assert.Equal(onlyLinked, onlyFlat);
        Assert.True(p99 <= 0.05 && LitCompare.Quantile(relative, 1) <= 0.25, $"{row}: p99 {p99}, max {LitCompare.Quantile(relative, 1)}");
        Assert.True(exact >= relative.Count * 0.98, $"{row}: {exact} of {relative.Count} exact");
    }

    // ---- a jointed level: the base, and what the doors add later -------------------------------

    /// <summary>
    /// Two rooms jointed (the hub, lit by a lamp, and the sky room beside it)
    /// against the full compile of their flattened level, at two turns: the
    /// base bake is each room lit alone with its doors shut, so the luxels
    /// the light through the doorway reaches in the full compile (the sun
    /// through the sky room's ceiling onto the hub's floor, each lamp into
    /// the other room) are darker in the link, by up to all of their light,
    /// within a door width of the joint and across the room, which the door
    /// terms of PR 10 add (the rooms design, 9.1 parts 2 to 4). What the
    /// base alone must hold, and does: at least half the luxels are exact,
    /// and it invents no light, no luxel more than 5% brighter than the full
    /// compile's but one in a thousand (the plug, lit in the room's bake,
    /// reflects a little light the open doorway does not).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task AJointedLevelKeepsEachRoomsBaseAndInventsNoLight(int rotation)
    {
        string row = $"hub@{rotation}, other@{rotation}";
        var linked = LitCompare.Lattice(await fixture.LinkedAsync(row));
        var flat = LitCompare.Lattice(await fixture.FlatAsync(row));
        List<double> near = [], far = [];
        int brighter = 0;
        foreach ((var key, (List<ColorRgbExp32> colours, bool thin)) in linked)
        {
            if (!flat.TryGetValue(key, out var other) || thin || other.Thin)
            {
                continue;
            }

            double r = colours.Min(c => other.Colours.Min(o => LitCompare.Relative(c.ToLinear(), o.ToLinear())));
            (Math.Abs((key.Item1 / 100.0) - RoomHarness.Cell) <= RoomHarness.WalkableKit.Width ? near : far).Add(r);
            Vec3 a = colours[0].ToLinear();
            Vec3 b = other.Colours[0].ToLinear();
            if (a.X + a.Y + a.Z > ((b.X + b.Y + b.Z) * 1.05) + 0.01)
            {
                brighter++;
            }
        }

        near.Sort();
        far.Sort();
        output.WriteLine(
            $"{row}: within a door width of the joint {near.Count} luxels, p50 {LitCompare.Quantile(near, .5):G3} p95 {LitCompare.Quantile(near, .95):G3}"
            + $" p99 {LitCompare.Quantile(near, .99):G3}; elsewhere {far.Count}, p50 {LitCompare.Quantile(far, .5):G3} p95 {LitCompare.Quantile(far, .95):G3}"
            + $" p99 {LitCompare.Quantile(far, .99):G3}; brighter by 5% {brighter}");
        Assert.Equal(0, LitCompare.Quantile(near, 0.5));
        Assert.Equal(0, LitCompare.Quantile(far, 0.5));
        Assert.True(brighter <= (near.Count + far.Count) / 1000, $"{brighter} luxels brighter");
    }

    // ---- which stored turn, and stored once --------------------------------------------------------

    /// <summary>
    /// A placement takes its room's payload <c>rotation mod count</c> (1.1):
    /// the sky room, stored per turn, links turn <i>r</i>'s luxels at turn
    /// <i>r</i>; the hub, stored once, links its one payload at every turn;
    /// either way the level's lighting lump is the payload encoded, byte
    /// for byte.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoomsAndTurns))]
    public async Task APlacementTakesThePayloadOfItsTurn(string room, int rotation)
    {
        RoomLighting lighting = fixture.Lit.Get(room).Lighting!;
        Assert.Equal(room == "hub" ? 1 : 4, lighting.RotationCount);
        RoomLightingPayload payload = lighting.Payloads[(rotation / 90) % lighting.RotationCount];
        Assert.Same(payload, lighting.For(rotation / 90));
        ColorRgbExp32[] expected = new ColorRgbExp32[payload.Ldr!.Luxels.Length / 3];
        RoomLighting.EncodeColors(payload.Ldr.Luxels, expected);
        Assert.Equal(expected, LitCompare.Colours(await fixture.LinkedAsync($"{room}@{rotation}")));
    }

    /// <summary>
    /// Each stored turn of a room is written once, however many placements
    /// take it: two hubs share one block of lightmaps (at one turn or two,
    /// since the hub is stored once), two sky rooms at two turns take two;
    /// a face of each placement points at its block.
    /// </summary>
    [Theory]
    [InlineData("hub@0, hub@0", 1)]
    [InlineData("hub@0, hub@90", 1)]
    [InlineData("other@0, other@0", 1)]
    [InlineData("other@0, other@90", 2)]
    public async Task EachStoredTurnIsWrittenOnce(string row, int blocks)
    {
        RoomLibrary rooms = RoomLightHarness.RoomsOf(fixture.Lit, "hub", "other");
        LinkedLevel linked = await RoomLightHarness.LinkAsync(rooms, RoomPropHarness.Level(row));
        string room = row[..row.IndexOf('@', StringComparison.Ordinal)];
        int block = fixture.Lit.Get(room).Lighting!.Payloads[0].Ldr!.Luxels.Length / 3 * 4;
        Assert.Equal(blocks * block, linked.Bsp[BspLump.Lighting].Length);
        DFace[] faces = BspStructView.As<DFace>(linked.Bsp[BspLump.Faces]).ToArray();
        int perRoom = BspStructView.As<DModel>(fixture.Lit.Get(room).Bsp[BspLump.Models])[0].NumFaces;
        int lit = Array.FindIndex(faces, f => f.LightOfs >= 0);
        Assert.Equal(faces[lit].LightOfs + (blocks == 1 ? 0 : block), faces[lit + perRoom].LightOfs);
    }

    // ---- sky flags ------------------------------------------------------------------------------

    /// <summary>
    /// The sky flags (the 2D sky PR's half, the rooms design 4.12): pass one
    /// from each room's sky leaves, pass two over the linked PVS, so the leaf
    /// of every point of the level has the flags the full compile gives it:
    /// the sky room's own leaves, and the hub's, which see the sky room's
    /// leaves through the doorway, flagged 3D sky; with 2D sky, 2D sky.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 90)]
    [InlineData(true, 0)]
    [InlineData(true, 270)]
    public async Task SkyFlagsAreTheFullCompilesAtEveryPoint(bool twoD, int rotation)
    {
        VmfDocument library = RoomLightHarness.Library(
            true, [1], twoD ? RoomLightHarness.Sky2D : RoomLightHarness.Sky, (0, RoomLightHarness.Light(800, new Vec3(128, 128, 150))));
        RoomLibrary rooms = await RoomLightHarness.CompileAsync(library);
        LevelGrid level = RoomPropHarness.Level($"hub@{rotation}, other@{rotation}");
        BspData linked = (await RoomLightHarness.LinkAsync(rooms, level)).Bsp;
        BspData flat = await RoomLightHarness.CompileFlatLitAsync(library, level);
        int flagged = 0;
        for (float x = 24; x < 2 * RoomHarness.Cell; x += 40)
        {
            for (float y = 24; y < RoomHarness.Cell; y += 40)
            {
                Vec3 point = new(x, y, 100);
                LeafFlags ours = RoomHarness.LeafAt(linked, point).GetFlags() & (LeafFlags.Sky | LeafFlags.Sky2D);
                LeafFlags theirs = RoomHarness.LeafAt(flat, point).GetFlags() & (LeafFlags.Sky | LeafFlags.Sky2D);
                if ((RoomHarness.LeafAt(linked, point).Contents & 1) == 0 && (RoomHarness.LeafAt(flat, point).Contents & 1) == 0)
                {
                    Assert.True(ours == theirs, $"{point}: {ours} against {theirs}");
                    flagged += ours == (twoD ? LeafFlags.Sky2D : LeafFlags.Sky) ? 1 : 0;
                }
            }
        }

        Assert.True(flagged > 0);
    }

    /// <summary>
    /// Without a library sun there is no sun or sky light: a sky room is
    /// stored once, and the level's leaf flags are its rooms' compiles', as
    /// an unlit level's are, since vrad recomputes them only with a sun.
    /// </summary>
    [Fact]
    public async Task WithoutASunTheLeafFlagsAreTheCompiles()
    {
        VmfDocument library = RoomLightHarness.Library(false, [1], (0, RoomLightHarness.Light(800, new Vec3(128, 128, 150))));
        RoomLibrary rooms = await RoomLightHarness.CompileAsync(library);
        Assert.All(rooms.Rooms, r => Assert.Equal(1, r.Lighting!.RotationCount));
        Assert.All(rooms.Rooms, r => Assert.False(r.Lighting!.HasSun));
        BspData linked = (await RoomLightHarness.LinkAsync(rooms, RoomPropHarness.Level("hub, other"))).Bsp;
        BspData unlit = (await RoomLightHarness.LinkAsync(await RoomLightHarness.CompileAsync(library, light: false), RoomPropHarness.Level("hub, other"))).Bsp;
        Assert.True(linked[BspLump.Leafs].Data.Span.SequenceEqual(unlit[BspLump.Leafs].Data.Span));
    }

    // ---- refusals -----------------------------------------------------------------------------

    /// <summary>A level placing a lit room and an unlit one is refused, naming both: one level is lit one way.</summary>
    [Fact]
    public async Task ALevelOfLitAndUnlitRoomsIsRefused()
    {
        RoomLibrary rooms = RoomPropHarness.RoomsOf(fixture.Lit.Get("hub"), fixture.Unlit.Get("other"));
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomLightHarness.LinkAsync(rooms, RoomPropHarness.Level("hub, other")));
        Assert.Equal(
            "room other has no baked lighting, but room hub of the same level has; a level's rooms are lit alike. Recompile the library with ssmap room.",
            refused.Message);
    }

    /// <summary>Two rooms lit with different settings (here, the map flags vrad writes for static prop lighting) are refused, naming both.</summary>
    [Fact]
    public async Task RoomsLitDifferentlyAreRefused()
    {
        RoomLibrary plain = await RoomLightHarness.CompileAsync(LitRoomsFixture.Library);
        RoomLibrary rooms = RoomPropHarness.RoomsOf(fixture.Lit.Get("hub"), plain.Get("other"));
        LinkException refused = await Assert.ThrowsAsync<LinkException>(() => RoomLightHarness.LinkAsync(rooms, RoomPropHarness.Level("hub, other")));
        Assert.Equal(
            "rooms hub and other were lit with different settings (ranges, sun or map flags); a level's rooms are lit alike. Recompile the library with ssmap room.",
            refused.Message);
    }

    // ---- a lit level is a map ----------------------------------------------------------------------

    /// <summary>
    /// A lit level passes the loader's validation at every turn, and its
    /// carved doorway leaves take their facing leaf's ambient samples (the
    /// rooms design, 9.4): the leaf at the middle of the doorway, empty in
    /// the link, points at the same run of samples as the room leaf beside
    /// it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task ALitLevelPassesTheValidatorAndItsDoorwaysTakeTheirFacingLeafsSamples(int rotation)
    {
        BspData linked = await fixture.LinkedAsync($"hub@{rotation}, other@{rotation}");
        ValidationReport report = await BspValidator.CheckAsync(linked, CancellationToken.None);
        Assert.True(report.ErrorCount == 0, string.Join("; ", report.Diagnostics));

        DLeafAmbientIndex[] index = BspStructView.As<DLeafAmbientIndex>(linked[BspLump.LeafAmbientIndex]).ToArray();
        Assert.Equal(BspStructView.Count<DLeaf>(linked[BspLump.Leafs]), index.Length);
        Vec3 doorway = new(RoomHarness.Cell, RoomHarness.Cell / 2, 100);
        int leaf = LevelLinker.PointInLeaf(linked, doorway);
        Assert.Equal(0, BspStructView.As<DLeaf>(linked[BspLump.Leafs])[leaf].Contents);
        Assert.True(index[leaf].AmbientSampleCount > 0);
        int beside = LevelLinker.PointInLeaf(linked, doorway - new Vec3(RoomHarness.WalkableKit.Depth + 8, 0, 0));
        int across = LevelLinker.PointInLeaf(linked, doorway + new Vec3(RoomHarness.WalkableKit.Depth + 8, 0, 0));
        Assert.Contains(index[leaf].FirstAmbientSample, (int[])[index[beside].FirstAmbientSample, index[across].FirstAmbientSample]);
    }
}
