using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Validation;

using Xunit;

namespace SourceSharp.Tests.MapTools.Validation;

/// <summary>
/// One fact per content rule: the rules that are about what a lump SAYS rather
/// than where it points.
/// </summary>
public class ContentRuleTests
{
    [Corrupts(BspRuleCodes.Leaf0Solid)]
    public async Task Leaf0MustBeSolid()
    {
        // and:520-522, "Map leaf 0 is not
        // CONTENTS_SOLID". The collision code then sets solidleaf = 0 and uses
        // leaf 0 AS the solid leaf for every trace that leaves the world.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DLeafVersion0> leaves = Corrupted.Edit<DLeafVersion0>(bsp, BspLump.Leafs);
        leaves[0].Contents = 0;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.Leaf0Solid);
        Severity.Is(report, BspRuleCodes.Leaf0Solid, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.SurfaceExtents)]
    public async Task ALitFaceWhoseLightmapIsTooBigIsReported()
    {
        //, "Bad surface extents on texture
        // %s". A brush face's limit is MAX_BRUSH_LIGHTMAP_DIM_INCLUDING_BORDER
        // (35); face 0 of the golden map is a lit brush face with extents of
        // 1 by 1, so 200 is unambiguously over.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DFace> faces = Corrupted.Edit<DFace>(bsp, BspLump.Faces);
        faces[0].LightmapTextureSizeInLuxels[0] = 200;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.SurfaceExtents);
        Severity.Is(report, BspRuleCodes.SurfaceExtents, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ANoLightFaceIsExemptFromTheExtentLimit()
    {
        // The engine's test is guarded by !(tex->flags & SURF_NOLIGHT): a face
        // that is never lit has no lightmap to overflow. Applying the limit to
        // it would reject maps the engine loads.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DFace> faces = Corrupted.Edit<DFace>(bsp, BspLump.Faces);
        Span<TexInfo> texInfo = Corrupted.Edit<TexInfo>(bsp, BspLump.TexInfo);

        faces[0].LightmapTextureSizeInLuxels[0] = 200;
        texInfo[faces[0].TexInfo].Flags |= BspLimits.SurfNoLight;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Assert.True(report.ForCode(BspRuleCodes.SurfaceExtents).IsEmpty);
    }

    [Fact]
    public async Task ADisplacementFaceGetsTheLargerLimit()
    {
        // picks 128 for a face with a
        // dispinfo and 35 for anything else, so an extent of 100 is legal on
        // one and not on the other. Using the brush limit everywhere would
        // reject every finely-lit displacement in the game.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DFace> faces = Corrupted.Edit<DFace>(bsp, BspLump.Faces);
        faces[0].LightmapTextureSizeInLuxels[0] = 100;
        faces[0].DispInfo = 0;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Assert.True(report.ForCode(BspRuleCodes.SurfaceExtents).IsEmpty);
    }

    [Corrupts(BspRuleCodes.NoCubemaps)]
    public async Task AMapWithNoCubemapSamplesIsReported()
    {
        //. Fatal under -requirecubemaps, and
        // otherwise a silent fallback to engine/defaultcubemap on every
        // reflective surface in the map.
        BspData bsp = await Corrupted.GoldenAsync();
        Corrupted.Replace(bsp, BspLump.Cubemaps, [], 0);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.NoCubemaps);
        Severity.Is(report, BspRuleCodes.NoCubemaps, DiagnosticSeverity.Warning);
    }

    [Corrupts(BspRuleCodes.PhysFraming)]
    public async Task APhysicsRecordWhoseSolidsDoNotAddUpIsReported()
    {
        // walks the lump by these sizes and
        // only notices it has gone past the end AFTER reading there. The solid
        // framing inside dataSize.
        BspData bsp = await Corrupted.GoldenAsync();
        byte[] phys = Corrupted.EditBytes(bsp, BspLump.PhysCollide);
        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(phys.AsSpan(4));
        BinaryPrimitives.WriteInt32LittleEndian(phys.AsSpan(4), dataSize + 4);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.PhysFraming);
        Severity.Is(report, BspRuleCodes.PhysFraming, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task APhysicsRecordWhoseSolidCountIsShortLeavesBytesUnaccountedFor()
    {
        // The other side of the same accounting, and it needed its own fact: a
        // mutation that removed the "the solids do not fill dataSize" test
        // survived every other phys fact, because each of those trips an
        // earlier branch. walks exactly
        // solidCount length-prefixed solids, so a count one short leaves the
        // last solid's bytes inside dataSize and unread.
        BspData bsp = await Corrupted.GoldenAsync();
        byte[] phys = Corrupted.EditBytes(bsp, BspLump.PhysCollide);
        int solidCount = BinaryPrimitives.ReadInt32LittleEndian(phys.AsSpan(12));
        Assert.True(solidCount > 1, "the golden map's first physics record has several solids");
        BinaryPrimitives.WriteInt32LittleEndian(phys.AsSpan(12), solidCount - 1);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.PhysFraming);
    }

    [Fact]
    public async Task APhysicsLumpWithNoTerminatorIsTheSameRule()
    {
        // "The last physmodel is a NULL pointer with modelIndex -1, dataSize
        // -1". Take the terminator away and
        // the walk has nothing to stop it.
        BspData bsp = await Corrupted.GoldenAsync();
        ReadOnlySpan<byte> phys = bsp[BspLump.PhysCollide].Data.Span;
        Corrupted.Replace(bsp, BspLump.PhysCollide, phys[..^16].ToArray(), 0);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.PhysFraming);
    }

    [Corrupts(BspRuleCodes.PhysModelIndex)]
    public async Task APhysicsRecordForAModelThatDoesNotExistIsReported()
    {
        // -- map_cmodels[ physModel.modelIndex
        // ] with nothing bounding it.
        BspData bsp = await Corrupted.GoldenAsync();
        byte[] phys = Corrupted.EditBytes(bsp, BspLump.PhysCollide);
        BinaryPrimitives.WriteInt32LittleEndian(phys, 9999);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.PhysModelIndex);
        Severity.Is(report, BspRuleCodes.PhysModelIndex, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.DispPower)]
    public async Task ADisplacementPowerAboveFourIsReported()
    {
        // reads NUM_DISP_POWER_VERTS(power)
        // vertices into a stack buffer sized for MAX_MAP_DISP_POWER, which is
        // 4.
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DispInfo> disps = Corrupted.Edit<DispInfo>(bsp, BspLump.DispInfo);
        disps[0].Power = 6;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.DispPower);
        Severity.Is(report, BspRuleCodes.DispPower, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.DispRuns)]
    public async Task ADisplacementWhoseVertexRunLeavesTheLumpIsReported()
    {
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DispInfo> disps = Corrupted.Edit<DispInfo>(bsp, BspLump.DispInfo);
        disps[0].DispVertStart = 99999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.DispRuns);
        Severity.Is(report, BspRuleCodes.DispRuns, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ADisplacementWhoseTriangleRunLeavesTheLumpIsTheSameRule()
    {
        BspData bsp = await Corrupted.GoldenAsync();
        Span<DispInfo> disps = Corrupted.Edit<DispInfo>(bsp, BspLump.DispInfo);
        disps[0].DispTriStart = 99999;

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.DispRuns);
    }

    [Corrupts(BspRuleCodes.SubLumpFraming)]
    public async Task AVisibilityLumpTooShortForItsOwnClusterTableIsReported()
    {
        // A lump that is a header plus counted runs cannot be checked by a
        // modulus. The engine reads these through a CUtlBuffer whose Get past
        // the end is an overflow with no message.
        BspData bsp = await Corrupted.GoldenAsync();
        ReadOnlySpan<byte> vis = bsp[BspLump.Visibility].Data.Span;
        Corrupted.Replace(bsp, BspLump.Visibility, vis[..8].ToArray(), 0);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.SubLumpFraming);
        Severity.Is(report, BspRuleCodes.SubLumpFraming, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AnOcclusionLumpTooShortForItsCountsIsTheSameRule()
    {
        BspData bsp = await Corrupted.GoldenAsync();
        byte[] occlusion = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(occlusion, 4);
        Corrupted.Replace(bsp, BspLump.Occlusion, occlusion, 2);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.SubLumpFraming);
    }

    [Fact]
    public async Task ALumpDirectoryThatRunsPastTheEndOfTheFileIsTheSameRule()
    {
        // CheckFileAsync's half of the rule: the file passed the ident and
        // version gate, so the engine would have opened it, and then the
        // directory does not describe something that can be read.
        byte[] bytes = Corrupted.GoldenBytes();
        int entry = 8 + ((int)BspLump.Planes * 16);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 4), 1 << 30);

        using MemoryStream stream = new(bytes);
        ValidationReport report = await BspValidator.CheckFileAsync(stream, CancellationToken.None);

        Corrupted.OnlyFires(report, BspRuleCodes.SubLumpFraming);
    }
}
