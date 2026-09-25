using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Validation;

using Xunit;

namespace SourceSharp.Tests.MapTools.Validation;

/// <summary>
/// One fact per lump-version rule.
/// </summary>
/// <remarks>
/// A lump version is not a revision counter: it says which STRUCT the lump
/// holds. Getting one wrong is not "an old map", it is every element after the
/// first read at the wrong stride, which is why each of these is its own rule.
/// </remarks>
public class VersionRuleTests
{
    private static int IndexOfGameLump(BspData bsp, string code)
    {
        for (int i = 0; i < bsp.GameLumps.Count; i++)
        {
            if (bsp.GameLumps[i].IdString() == code)
            {
                return i;
            }
        }

        throw new InvalidOperationException(
            $"the golden map has no {code} game lump; the facts below are written against one");
    }

    [Corrupts(BspRuleCodes.OcclusionVersion)]
    public async Task AnOcclusionLumpVersionOutsideZeroToTwoIsRejected()
    {
        // engine/modelloader.cpp:1331-1399, "Invalid occlusion lump version!".
        BspData bsp = await Corrupted.GoldenAsync();
        BspLumpData occlusion = bsp[BspLump.Occlusion];
        Corrupted.Replace(bsp, BspLump.Occlusion, occlusion.Data.ToArray(), 3);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.OcclusionVersion);
        Severity.Is(report, BspRuleCodes.OcclusionVersion, DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AnEmptyOcclusionLumpIsNeverAskedWhatVersionItIs()
    {
        // engine/modelloader.cpp:1599-1602 returns on a zero-length lump
        // BEFORE the version switch, so an absent occlusion lump carrying junk
        // in its version field is not a defect.
        BspData bsp = await Corrupted.GoldenAsync();
        Corrupted.Replace(bsp, BspLump.Occlusion, [], 7);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Assert.True(report.ForCode(BspRuleCodes.OcclusionVersion).IsEmpty);
    }

    [Corrupts(BspRuleCodes.LeafsVersion)]
    public async Task ALeafsLumpVersionOutsideZeroToOneIsRejected()
    {
        // engine/modelloader.cpp:2306-2308 and engine/cmodel_bsp.cpp:542-544.
        //
        // This corruption necessarily produces BSP0003 as well, and that is
        // correct rather than sloppy: the version IS the element size, so a
        // version of 2 means the 152,600-byte lump is being measured against a
        // stride it does not divide by. The fact asserts the version rule
        // fired; the element-size rule has its own fact.
        BspData bsp = await Corrupted.GoldenAsync();
        BspLumpData leafs = bsp[BspLump.Leafs];
        Corrupted.Replace(bsp, BspLump.Leafs, leafs.Data.ToArray(), 2);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.Fires(report, BspRuleCodes.LeafsVersion);
        Severity.Is(report, BspRuleCodes.LeafsVersion, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.LeafAmbientLegacyPath)]
    public async Task AnAmbientLumpWithoutItsIndexFallsBackToTheLegacyPath()
    {
        // engine/modelloader.cpp:2203-2205. dm_lockdown.bsp has no ambient
        // lumps at all, so the field that decides this does not exist to
        // corrupt: the corruption is to GIVE it one, at the wrong version and
        // with no index, which is exactly the state the engine's else-branch
        // exists for.
        BspData bsp = await Corrupted.GoldenAsync();
        int leaves = bsp[BspLump.Leafs].Length / Unsafe.SizeOf<DLeafVersion0>();
        Corrupted.Replace(
            bsp,
            BspLump.LeafAmbientLighting,
            new byte[leaves * Unsafe.SizeOf<CompressedLightCube>()],
            0);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.LeafAmbientLegacyPath);
        Severity.Is(report, BspRuleCodes.LeafAmbientLegacyPath, DiagnosticSeverity.Warning);
    }

    [Corrupts(BspRuleCodes.LeafAmbientLegacyCount)]
    public async Task TheLegacyAmbientPathNeedsOneLightCubePerLeaf()
    {
        // engine/modelloader.cpp:2210-2212 asserts it and :2226 memcpy's
        // inLightCubes[i] for every leaf regardless, so a short lump is read
        // past its end. The legacy-path warning necessarily rides along,
        // because taking that path is the precondition for this rule.
        BspData bsp = await Corrupted.GoldenAsync();
        Corrupted.Replace(
            bsp,
            BspLump.LeafAmbientLighting,
            new byte[100 * Unsafe.SizeOf<CompressedLightCube>()],
            0);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.Fires(report, BspRuleCodes.LeafAmbientLegacyCount);
        Severity.Is(report, BspRuleCodes.LeafAmbientLegacyCount, DiagnosticSeverity.Error);
    }

    [Corrupts(BspRuleCodes.StaticPropVersion)]
    public async Task AStaticPropLumpBelowVersion4LosesEveryProp()
    {
        // engine/staticpropmgr.cpp:1320-1325: a Warning and a return. The map
        // loads, and every static prop in it is simply gone -- which is why
        // "it loaded" is not the bar this instrument sets.
        BspData bsp = await Corrupted.GoldenAsync();
        int index = IndexOfGameLump(bsp, GameLumpId.StaticProps);
        bsp.GameLumps[index] = bsp.GameLumps[index] with { Version = 3 };

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.StaticPropVersion);
        Severity.Is(report, BspRuleCodes.StaticPropVersion, DiagnosticSeverity.Warning);
    }

    [Corrupts(BspRuleCodes.DetailPropVersion)]
    public async Task ADetailPropLumpBelowVersion4LosesEveryProp()
    {
        // game/client/detailobjectsystem.cpp:1447-1451, the same shape.
        BspData bsp = await Corrupted.GoldenAsync();
        int index = IndexOfGameLump(bsp, GameLumpId.DetailProps);
        bsp.GameLumps[index] = bsp.GameLumps[index] with { Version = 3 };

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.DetailPropVersion);
        Severity.Is(report, BspRuleCodes.DetailPropVersion, DiagnosticSeverity.Warning);
    }

    [Corrupts(BspRuleCodes.HdrLumpPair)]
    public async Task HdrLightingWithoutHdrWorldlightsIsUnusable()
    {
        // engine/modelloader.cpp:1029-1031. Half a set of HDR lumps is
        // megabytes the engine will never look at, and nothing says so.
        BspData bsp = await Corrupted.GoldenAsync();
        Corrupted.Replace(bsp, BspLump.LightingHdr, new byte[16], 1);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.OnlyFires(report, BspRuleCodes.HdrLumpPair);
        Severity.Is(report, BspRuleCodes.HdrLumpPair, DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task AVersion20MapWithHdrButNoHdrLeafAmbientTurnsHdrBackOff()
    {
        // engine/modelloader.cpp:1034-1037, the second half of the same rule.
        BspData bsp = await Corrupted.GoldenAsync();
        bsp.FileVersion = 20;
        Corrupted.Replace(bsp, BspLump.LightingHdr, new byte[16], 1);
        Corrupted.Replace(bsp, BspLump.WorldLightsHdr, new byte[88], 0);

        ValidationReport report = await Corrupted.CheckAsync(bsp);

        Corrupted.Fires(report, BspRuleCodes.HdrLumpPair);
    }
}
