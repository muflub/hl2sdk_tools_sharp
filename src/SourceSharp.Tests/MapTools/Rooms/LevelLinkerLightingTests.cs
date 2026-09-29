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
}
