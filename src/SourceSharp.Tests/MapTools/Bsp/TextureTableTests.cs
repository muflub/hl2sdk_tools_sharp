//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

using SourceSharp.MapFormats.Bsp.Structs;

using SourceSharp.MapTools.Bsp;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp;

/// <summary>
/// The three texture tables' dedup rules, which disagree with each other on
/// purpose.
/// </summary>
public class TextureTableTests
{
    [Fact]
    public void AddOrFindStoresTheNameWithItsTerminator()
    {
        TexDataStringTable table = new();

        table.AddOrFind("tools/toolsnodraw");

        Assert.Equal("tools/toolsnodraw".Length + 1, table.ToData().Length);
        Assert.Equal(0, table.ToData()[^1]);
    }

    [Fact]
    public void OffsetsPointAtEachNameInTurn()
    {
        TexDataStringTable table = new();

        int first = table.AddOrFind("a");
        int second = table.AddOrFind("bb");

        Assert.Equal(0, table.Offsets[first]);
        Assert.Equal(2, table.Offsets[second]);
    }

    /// <summary>
    /// compares with <c>stricmp</c> but stores the
    /// string verbatim, so a second spelling collapses onto the first and its
    /// casing never reaches the lump.
    /// </summary>
    [Fact]
    public void ADifferentCasingCollapsesOntoTheFirstSpelling()
    {
        TexDataStringTable table = new();

        int first = table.AddOrFind("TOOLS/TOOLSNODRAW");
        int second = table.AddOrFind("tools/toolsnodraw");

        Assert.Equal(first, second);
        Assert.Equal(1, table.Count);
        Assert.Equal("TOOLS/TOOLSNODRAW", table.GetString(first));
    }

    [Fact]
    public void TheDataLumpCarriesTheFirstSpellingsBytes()
    {
        TexDataStringTable table = new();
        table.AddOrFind("TOOLS/TOOLSNODRAW");
        table.AddOrFind("tools/toolsnodraw");

        Assert.Equal(
            Encoding.Latin1.GetBytes("TOOLS/TOOLSNODRAW\0"),
            table.ToData());
    }

    [Fact]
    public void TheDecodedNameAgreesWithTheStoredName()
    {
        TexDataStringTable table = new();
        int a = table.AddOrFind("first");
        int b = table.AddOrFind("second");

        Assert.Equal(table.GetString(a), table.DecodeString(a));
        Assert.Equal(table.GetString(b), table.DecodeString(b));
    }

    [Fact]
    public void AnEmptyTexDataTableFindsNothing()
    {
        TexDataTable table = new(new TexDataStringTable());

        Assert.Equal(-1, table.Find("anything"));
    }

    /// <summary>
    /// <c>FindTexData</c> is <c>Q_stricmp</c> while
    /// <c>FindMiptex</c> is <c>strcmp</c>. The two
    /// disagreeing is stock, and it means two spellings of one material give
    /// two textureref entries and ONE texdata.
    /// </summary>
    [Fact]
    public async Task TexDataDedupsCaseInsensitivelyAndTextureRefDoesNot()
    {
        VbspContext context = await UnitMap.ContextAsync();

        int lower = await context.TexDatas.FindOrCreateAsync(UnitMap.Plain, context.Materials);
        int upper = await context.TexDatas.FindOrCreateAsync(
            UnitMap.Plain.ToUpperInvariant(), context.Materials);

        int refLower = await context.TextureReferences
            .FindMiptexAsync(UnitMap.Plain, context.Materials, context.MaterialOptions);
        int refUpper = await context.TextureReferences
            .FindMiptexAsync(UnitMap.Plain.ToUpperInvariant(), context.Materials, context.MaterialOptions);

        Assert.Equal(lower, upper);
        Assert.Equal(1, context.TexDatas.Count);
        Assert.NotEqual(refLower, refUpper);
        Assert.Equal(2, context.TextureReferences.Count);
    }

    /// <summary>
    /// <c>FindMiptex</c> returns 0 without advancing <c>nummiptex</c> when the
    /// material does not resolve, so the caller
    /// reads slot 0 — which is some OTHER material once the map has loaded
    /// one.
    /// </summary>
    [Fact]
    public async Task AMissingMaterialIsMiptexZeroAndInheritsWhateverIsThere()
    {
        VbspContext context = await UnitMap.ContextAsync();

        int nodraw = await context.TextureReferences
            .FindMiptexAsync(UnitMap.NoDraw, context.Materials, context.MaterialOptions);
        int missing = await context.TextureReferences
            .FindMiptexAsync("unit/not_a_material", context.Materials, context.MaterialOptions);

        Assert.Equal(0, nodraw);
        Assert.Equal(0, missing);
        Assert.Equal(1, context.TextureReferences.Count);
        Assert.NotEqual(0, context.TextureReferences[missing].Flags);
    }

    /// <summary>
    /// <c>FindOrCreateTexData</c> returns a VALID index for a material that did
    /// not resolve, leaving the entry's dimensions
    /// and reflectivity at zero. <c>FindAliasedTexData</c> returns -1 in the
    /// same situation — the two are not interchangeable.
    /// </summary>
    [Fact]
    public async Task AMissingMaterialStillGetsATexDataEntry()
    {
        VbspContext context = await UnitMap.ContextAsync();

        int index = await context.TexDatas
            .FindOrCreateAsync("unit/not_a_material", context.Materials);

        Assert.Equal(0, index);
        Assert.Equal(1, context.TexDatas.Count);
        Assert.Equal(0, context.TexDatas[index].Width);
    }

    [Fact]
    public void TexInfoFindTreatsPositiveAndNegativeZeroAsDifferent()
    {
        TexInfo a = default;
        TexInfo b = default;
        b.TextureVecsTexelsPerWorldUnits[0] = -0f;

        Assert.Equal(0f, a.TextureVecsTexelsPerWorldUnits[0]);
        Assert.Equal(0f, b.TextureVecsTexelsPerWorldUnits[0]);
        Assert.False(TexInfoTable.AreIdentical(a, b));
    }

    /// <summary>
    /// The other direction of the same rule: <c>memcmp</c> compares bytes, so
    /// an identical NaN DOES match where a float <c>==</c> would not. That is
    /// what makes a map with no <c>lightmapscale</c> keys — whose lightmap
    /// rows are infinities and NaNs — collapse onto one texinfo, which is what
    /// stock produces.
    /// </summary>
    [Fact]
    public void TexInfoFindMatchesAnIdenticalNaN()
    {
        TexInfo a = default;
        a.LightmapVecsLuxelsPerWorldUnits[0] = float.NaN;
        TexInfo b = default;
        b.LightmapVecsLuxelsPerWorldUnits[0] = float.NaN;

#pragma warning disable CS1718 // The point of the fact is that == disagrees.
        Assert.False(a.LightmapVecsLuxelsPerWorldUnits[0] == b.LightmapVecsLuxelsPerWorldUnits[0]);
#pragma warning restore CS1718
        Assert.True(TexInfoTable.AreIdentical(a, b));
    }

    [Fact]
    public void ATexInfoIsIdenticalToItsOwnCopy()
    {
        TexInfo a = default;
        a.TextureVecsTexelsPerWorldUnits[3] = 12.5f;
        a.LightmapVecsLuxelsPerWorldUnits[7] = -3.25f;
        a.Flags = 0x80;
        a.TexData = 4;

        Assert.True(TexInfoTable.AreIdentical(a, a));
    }

    // FindTexInfo is a memcmp scan from 0; the table
    // answers it from a hash index (plan 3p), so these pin the scan's answer.

    [Fact]
    public void FindDoesNotMatchANegativeZeroEntry()
    {
        TexInfoTable table = new();
        table.Add(default);
        TexInfo negative = default;
        negative.TextureVecsTexelsPerWorldUnits[0] = -0f;

        Assert.Equal(-1, table.Find(negative));
    }

    [Fact]
    public void FindMatchesAnEntryWithTheIdenticalNaN()
    {
        TexInfoTable table = new();
        TexInfo nan = default;
        nan.LightmapVecsLuxelsPerWorldUnits[0] = float.NaN;
        table.Add(default);
        table.Add(nan);

        Assert.Equal(1, table.Find(nan));
    }

    [Fact]
    public void FindReturnsTheFirstOfTwoIdenticalAppends()
    {
        TexInfoTable table = new();
        TexInfo entry = default;
        entry.TexData = 3;
        table.Add(default);
        table.Add(entry);
        table.Add(entry);

        Assert.Equal(1, table.Find(entry));
    }

    [Fact]
    public void FindTellsEntriesApartByTheirLastField()
    {
        TexInfoTable table = new();
        TexInfo a = default;
        a.LightmapVecsLuxelsPerWorldUnits[7] = 1f;
        TexInfo b = a;
        b.Flags = 1;
        table.Add(a);

        Assert.Equal(-1, table.Find(b));
        Assert.Equal(0, table.Find(a));
    }

    [Fact]
    public void FindOrCreateReturnsTheExistingIndex()
    {
        TexInfoTable table = new();
        TexInfo entry = default;
        entry.Flags = 0x40;

        int first = table.FindOrCreate(entry);
        int second = table.FindOrCreate(entry);

        Assert.Equal(first, second);
        Assert.Equal(1, table.Count);
    }

    /// <summary>
    /// <c>CreateBrushVersionOfWorldVertexTransitionMaterial</c> appends
    /// directly rather than going through <c>FindOrCreateTexInfo</c>,
    /// so the table must offer an
    /// append that does not dedup.
    /// </summary>
    [Fact]
    public void AddAppendsWithoutDeduping()
    {
        TexInfoTable table = new();
        TexInfo entry = default;

        int first = table.Add(entry);
        int second = table.Add(entry);

        Assert.NotEqual(first, second);
        Assert.Equal(2, table.Count);
    }

    /// <summary>
    /// <c>AddCloneTexData</c> copies the struct and then overwrites only the
    /// name, so the copy keeps the original's
    /// dimensions, reflectivity and surface property.
    /// </summary>
    [Fact]
    public async Task TheClonedTexDataInheritsEverythingButTheName()
    {
        VbspContext context = await UnitMap.ContextAsync();
        int source = await context.TexDatas.FindOrCreateAsync(UnitMap.Plain, context.Materials);

        int clone = context.TexDatas.AddClone(source, "maps/unit/plain_wvt_patch");

        Assert.Equal("maps/unit/plain_wvt_patch", context.TexDatas.NameOf(clone));
        Assert.Equal(context.TexDatas[source].Width, context.TexDatas[clone].Width);
        Assert.Equal(context.TexDatas[source].Height, context.TexDatas[clone].Height);
        Assert.Equal(context.TexDatas[source].Reflectivity, context.TexDatas[clone].Reflectivity);
        Assert.Equal(
            context.TexDatas.SurfaceProperties[source],
            context.TexDatas.SurfaceProperties[clone]);
        Assert.NotEqual(source, clone);
    }

    /// <summary>
    /// The texdata lookup is a table now, not a scan; a clone given a name
    /// that already exists (in any casing) makes a SECOND entry with that
    /// name, and the lookup must still answer the first, as the scan did.
    /// </summary>
    [Fact]
    public async Task FindAnswersTheFirstOfTwoEntriesWithOneName()
    {
        VbspContext context = await UnitMap.ContextAsync();
        int first = await context.TexDatas.FindOrCreateAsync(UnitMap.Plain, context.Materials);

        int clone = context.TexDatas.AddClone(first, UnitMap.Plain.ToUpperInvariant());

        Assert.NotEqual(first, clone);
        Assert.Equal(first, context.TexDatas.Find(UnitMap.Plain));
        Assert.Equal(first, context.TexDatas.Find(UnitMap.Plain.ToUpperInvariant()));
    }

    [Fact]
    public async Task FindIgnoresCaseAgainstTheStoredSpelling()
    {
        VbspContext context = await UnitMap.ContextAsync();
        int index = await context.TexDatas.FindOrCreateAsync(UnitMap.Plain, context.Materials);

        Assert.Equal(index, context.TexDatas.Find(UnitMap.Plain.ToUpperInvariant()));
        Assert.Equal(-1, context.TexDatas.Find(UnitMap.Plain + "x"));
    }

    [Fact]
    public void AddOrFindAnswersTheFirstIdForEverySpellingAfterManyNames()
    {
        TexDataStringTable strings = new();
        for (int i = 0; i < 500; i++)
        {
            Assert.Equal(i, strings.AddOrFind($"Maps/Name{i}"));
        }

        Assert.Equal(123, strings.AddOrFind("MAPS/NAME123"));
        Assert.Equal("Maps/Name123", strings.GetString(123));
        Assert.Equal(500, strings.Count);
    }

    /// <summary>
    /// The texture-reference lookup remembers only COMMITTED names. A material
    /// that did not resolve is never committed, so every later side naming it
    /// asks again -- and is warned about again, as in stock.
    /// </summary>
    [Fact]
    public async Task AMissingMaterialIsLookedUpAndReportedEveryTime()
    {
        VbspContext context = await UnitMap.ContextAsync();
        List<SourceSharp.MapTools.Diagnostics.CompileDiagnostic> diagnostics = [];

        int first = await context.TextureReferences.FindMiptexAsync(
            "unit/not_a_material", context.Materials, context.MaterialOptions, diagnostics);
        int second = await context.TextureReferences.FindMiptexAsync(
            "unit/not_a_material", context.Materials, context.MaterialOptions, diagnostics);

        Assert.Equal(0, first);
        Assert.Equal(0, second);
        Assert.Equal(0, context.TextureReferences.Count);
        Assert.Equal(2, diagnostics.Count);
    }

    [Fact]
    public async Task ACommittedNameIsFoundAtItsIndexWithoutAnotherWarning()
    {
        VbspContext context = await UnitMap.ContextAsync();
        List<SourceSharp.MapTools.Diagnostics.CompileDiagnostic> diagnostics = [];

        int plain = await context.TextureReferences.FindMiptexAsync(
            UnitMap.Plain, context.Materials, context.MaterialOptions, diagnostics);
        int nodraw = await context.TextureReferences.FindMiptexAsync(
            UnitMap.NoDraw, context.Materials, context.MaterialOptions, diagnostics);
        int again = await context.TextureReferences.FindMiptexAsync(
            UnitMap.Plain, context.Materials, context.MaterialOptions, diagnostics);

        Assert.Equal(plain, again);
        Assert.NotEqual(plain, nodraw);
        Assert.Equal(2, context.TextureReferences.Count);
        Assert.Empty(diagnostics);
    }
}
