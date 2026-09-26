//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Immutable;

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;

using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Materials;

/// <summary>
/// The Phase 1f gate: every material in a stock-compiled map, compared against
/// what stock vbsp itself wrote into that map.
/// </summary>
/// <remarks>
/// <para>
/// <c>game/mod_sharp/maps/dm_lockdown.bsp</c> is committed and was compiled by
/// stock vbsp. Its TEXDATA lump records, per material, the reflectivity
/// and the dimensions the real material system gave that compiler, and its
/// TEXINFO lump records the <c>SURF_*</c> flags <c>FindMiptex</c> produced.
/// Both are read back here and required to equal what this port computes from
/// the same installed content. 485 materials, 2765 comparable texinfo entries.
/// </para>
/// <para>
/// THREE materials are excluded, by name, and each exclusion is a finding
/// about the map rather than a tolerance:
/// <see cref="TheFenceMaterialsBaseTextureWasRepointedAfterTheMapWasCompiled"/>.
/// <see cref="TheCompilerThatBuiltThisMapPredatesSurfTrigger"/> and
/// <see cref="TheMapReferencesOneMaterialThisInstallDoesNotHave"/> each pin
/// their own case, so the exclusion cannot quietly widen. Everything else has
/// to match exactly.
/// </para>
/// <para>
/// Skips VISIBLY, with the reason in the runner's output, when the game is not
/// installed — never an early return that reports a pass.
/// </para>
/// </remarks>
public class InstalledMaterialFactsTests : IClassFixture<StockTexDataFixture>
{
    /// <summary>
    /// The material whose VMT no longer names the texture it was compiled
    /// against.
    /// </summary>
    private const string RepointedFence = "METAL/METALFENCE004A";

    /// <summary>The material the 2013 install does not carry.</summary>
    private const string MissingDetailSprites = "ATI_2004/DETAIL/DETAILSPRITES";

    /// <summary>The material whose flag postdates this map's compiler.</summary>
    private const string Trigger = "TOOLS/TOOLSTRIGGER";

    private readonly StockTexDataFixture _stock;

    /// <summary>Takes the differential, run once for the class.</summary>
    /// <param name="stock">The fixture xUnit constructs once.</param>
    public InstalledMaterialFactsTests(StockTexDataFixture stock) => _stock = stock;

    private static ImmutableArray<string> Excluded =>
        [RepointedFence, MissingDetailSprites, Trigger];

    [InstalledGameFact]
    public void TheMapHoldsAWorthwhileNumberOfMaterials()
    {
        // A differential over four materials would pass for the wrong reason.
        Assert.True(
            _stock.Compared > 400,
            $"only {_stock.Compared} texdata entries were read; the map has hundreds");
    }

    [InstalledGameFact]
    public void EveryReflectivityEqualsWhatStockWrote()
    {
        // THE gate. vbsp copied GetMaterialReflectivity straight into
        // dtexdata_t::reflectivity, so this compares the
        // VTF header read, the $reflectivity override and the patch
        // resolution all at once, over every material in a real map.
        ImmutableArray<TexDataMismatch> unexplained = Remaining(_stock.ReflectivityMismatches);

        Assert.True(
            unexplained.IsEmpty,
            $"{unexplained.Length} of {_stock.Compared} reflectivities differ from stock:"
            + $"{Environment.NewLine}{Join(unexplained)}");
    }

    [InstalledGameFact]
    public void EveryDimensionEqualsWhatStockWrote()
    {
        ImmutableArray<TexDataMismatch> unexplained = Remaining(_stock.DimensionMismatches);

        Assert.True(
            unexplained.IsEmpty,
            $"{unexplained.Length} of {_stock.Compared} dimensions differ from stock:"
            + $"{Environment.NewLine}{Join(unexplained)}");
    }

    [InstalledGameFact]
    public void FourHundredAndEightyThreeOfFourHundredAndEightyFiveAgreeOnBoth()
    {
        // Stated as a number so the gate has one, and so a partial regression
        // cannot hide behind an empty-list assertion that was never reached.
        // The two are the repointed fence and the material this install does
        // not have; both have facts of their own below.
        Assert.Equal(_stock.Compared - 2, _stock.Matched);
    }

    [InstalledGameFact]
    public void EveryTexInfoFlagsFieldEqualsWhatFindMiptexProduced()
    {
        // The other half of the gate, and a stronger claim than it looks:
        // texinfo_t::flags is assigned from the brush side's,
        // copied out of textureref[].flags, and
        // nothing else in vbsp sets a SURF_ bit. So this lump IS FindMiptex's
        // output, 2765 times over.
        ImmutableArray<TexDataMismatch> unexplained = Remaining(_stock.TexInfoMismatches);

        Assert.True(
            unexplained.IsEmpty,
            $"{_stock.TexInfoCompared - _stock.TexInfoMatched} of {_stock.TexInfoCompared} "
            + $"texinfo entries carry flags this port does not produce; {unexplained.Length} "
            + $"distinct materials are unaccounted for:{Environment.NewLine}{Join(unexplained)}");
    }

    [InstalledGameFact]
    public void OverlayTexInfosAreExcludedBecauseTheyAreNotClassifiedAtAll()
    {
        // The overlay pass builds a texinfo with flags = 0 and
        // -99999 in both axis offsets; FindMiptex never sees it. Pinned as a
        // count so the exclusion cannot silently grow to cover a real
        // disagreement.
        Assert.Equal(3, _stock.TexInfoOverlaysSkipped);
    }

    [InstalledGameFact]
    public async Task TheFenceMaterialsBaseTextureWasRepointedAfterTheMapWasCompiled()
    {
        // Not a defect in the reader, and the numbers say so exactly.
        // metalfence004a.vmt today reads
        //   "$basetexture" "Models/props_c17/fence_alpha"
        // and this port's reflectivity for it equals THAT texture's VTF header
        // to the bit. Stock's TEXDATA value equals metal/metalfence004a.vtf's
        // header to the bit. The same VMT also reads "$AlphaTest" "1" today
        // while stock's texinfo carries SURF_TRANS, which only $translucent
        // produces. One cause, two symptoms: the VMT in this install is not
        // the VMT the map was compiled from.
        Vec3 ours = _stock.OurReflectivity(RepointedFence);
        Vec3 pointedAt =
            await _stock.TextureReflectivityAsync("materials/models/props_c17/fence_alpha.vtf");

        Assert.Equal(pointedAt, ours);
    }

    [InstalledGameFact]
    public void TheCompilerThatBuiltThisMapPredatesSurfTrigger()
    {
        // toolstrigger.vmt sets %compiletrigger, so FindMiptex as it stands
        // produces SURF_TRIGGER | SURF_NOLIGHT. Stock's 29 texinfo entries for
        // it carry SURF_NOLIGHT alone -- and not one texinfo in the whole map
        // has the bit. calls SURF_TRIGGER "an xbox hack"; this map
        // is BSP version 19, from before it existed. The port matches the
        // 2013 source it was ported from, which is the right answer.
        Assert.False(_stock.AnyTexInfoHasTheTriggerBit);
    }

    [InstalledGameFact]
    public void TheMapReferencesOneMaterialThisInstallDoesNotHave()
    {
        // ATI_2004/DETAIL/DETAILSPRITES: neither the VMT nor the VTF is in
        // the installed game's content -- it came with the ATI 2004
        // bundle. Nothing about the reader; there is no file to read.
        Assert.Equal(MissingDetailSprites, Assert.Single(_stock.NotFound));
    }

    private static ImmutableArray<TexDataMismatch> Remaining(
        ImmutableArray<TexDataMismatch> mismatches) =>
        [.. mismatches.Where(static m => !Excluded.Contains(m.Material, StringComparer.OrdinalIgnoreCase))];

    private static string Join(ImmutableArray<TexDataMismatch> items) =>
        string.Join(Environment.NewLine, items.Take(40));
}
