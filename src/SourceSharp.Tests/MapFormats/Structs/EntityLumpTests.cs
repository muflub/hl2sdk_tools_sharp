using System.Text;

using SourceSharp.MapFormats;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Structs;

/// <summary>
/// The entity lump's text format, as <c>ParseEntities</c> and
/// <c>UnparseEntities</c> define it.
/// </summary>
public class EntityLumpTests : IClassFixture<LockdownFixture>
{
    private readonly LockdownFixture _fixture;

    /// <summary>Takes the shared golden map.</summary>
    /// <param name="fixture">The fixture xUnit constructs once.</param>
    public EntityLumpTests(LockdownFixture fixture) => _fixture = fixture;

    private static BspLumpData Lump(string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text + "\0");
        return new BspLumpData(bytes, 0, 0);
    }

    [Fact]
    public void ParsesOneEntity()
    {
        List<BspEntity> entities = EntityLump.Parse(Lump("{\n\"classname\" \"worldspawn\"\n}\n"));

        Assert.Single(entities);
    }

    [Fact]
    public void ParsesTheClassname()
    {
        List<BspEntity> entities = EntityLump.Parse(Lump("{\n\"classname\" \"worldspawn\"\n}\n"));

        Assert.Equal("worldspawn", entities[0].ClassName);
    }

    [Fact]
    public void KeepsPairsInFileOrder()
    {
        // bsplib.cpp:3056 prepends, so stock reverses these on a round trip.
        // This port keeps file order; that is the deliberate difference.
        List<BspEntity> entities =
            EntityLump.Parse(Lump("{\n\"a\" \"1\"\n\"b\" \"2\"\n\"c\" \"3\"\n}\n"));

        Assert.Equal(["a", "b", "c"], entities[0].Pairs.Select(p => p.Key));
    }

    [Fact]
    public void KeepsRepeatedKeys()
    {
        // An entity's outputs are all spelled the same. A dictionary would
        // drop three of these four.
        List<BspEntity> entities = EntityLump.Parse(Lump(
            "{\n\"OnTrigger\" \"a\"\n\"OnTrigger\" \"b\"\n\"OnTrigger\" \"c\"\n\"OnTrigger\" \"d\"\n}\n"));

        Assert.Equal(4, entities[0].Pairs.Count);
    }

    [Fact]
    public void GetReturnsTheFirstValueOfARepeatedKey()
    {
        List<BspEntity> entities =
            EntityLump.Parse(Lump("{\n\"OnTrigger\" \"a\"\n\"OnTrigger\" \"b\"\n}\n"));

        Assert.Equal("a", entities[0].Get("OnTrigger"));
    }

    [Fact]
    public void GetReturnsNullForAKeyTheEntityDoesNotHave()
    {
        List<BspEntity> entities = EntityLump.Parse(Lump("{\n\"classname\" \"light\"\n}\n"));

        Assert.Null(entities[0].Get("targetname"));
    }

    [Fact]
    public void StripsTrailingWhitespaceFromValues()
    {
        // bsplib.cpp:2986 -- StripTrailing walks back while *s <= 32, so it
        // takes control characters too, not just spaces.
        List<BspEntity> entities = EntityLump.Parse(Lump("{\n\"targetname\" \"door \t\"\n}\n"));

        Assert.Equal("door", entities[0].Pairs[0].Value);
    }

    [Fact]
    public void DoesNotStripLeadingWhitespace()
    {
        // StripTrailing only walks back from the end. A value that starts with
        // a space keeps it, and a tool that trims both ends changes the map.
        List<BspEntity> entities = EntityLump.Parse(Lump("{\n\"targetname\" \" door\"\n}\n"));

        Assert.Equal(" door", entities[0].Pairs[0].Value);
    }

    [Fact]
    public void AValueOfOnlySpacesBecomesEmpty()
    {
        List<BspEntity> entities = EntityLump.Parse(Lump("{\n\"message\" \"   \"\n}\n"));

        Assert.Equal(string.Empty, entities[0].Pairs[0].Value);
    }

    [Fact]
    public void AQuotedValueHasNoEscapeSequences()
    {
        // scriplib.cpp:664 copies bytes until the next quote with no escape
        // handling at all, so a backslash is a literal backslash.
        List<BspEntity> entities =
            EntityLump.Parse(Lump("{\n\"model\" \"models\\props\\crate.mdl\"\n}\n"));

        Assert.Equal(@"models\props\crate.mdl", entities[0].Pairs[0].Value);
    }

    [Fact]
    public void SkipsDoubleSlashComments()
    {
        // scriplib.cpp:627.
        List<BspEntity> entities =
            EntityLump.Parse(Lump("// a comment\n{\n\"classname\" \"light\"\n}\n"));

        Assert.Equal("light", entities[0].ClassName);
    }

    [Fact]
    public void SkipsSemicolonComments()
    {
        // scriplib.cpp:627 -- ';' and '#' are comment starters too, which is
        // easy to miss if you only port the '//' case.
        List<BspEntity> entities =
            EntityLump.Parse(Lump("; a comment\n{\n\"classname\" \"light\"\n}\n"));

        Assert.Equal("light", entities[0].ClassName);
    }

    [Fact]
    public void SkipsBlockComments()
    {
        // scriplib.cpp:643.
        List<BspEntity> entities =
            EntityLump.Parse(Lump("/* gone */\n{\n\"classname\" \"light\"\n}\n"));

        Assert.Equal("light", entities[0].ClassName);
    }

    [Fact]
    public void RejectsTextThatEndsInsideAnEntity()
    {
        // bsplib.cpp:3052 calls this "ParseEntity: EOF without closing brace".
        Assert.Throws<InvalidBspException>(() => EntityLump.Parse(Lump("{\n\"a\" \"1\"\n")));
    }

    [Fact]
    public void RejectsAStrayTokenWhereAnEntityShouldStart()
    {
        Assert.Throws<InvalidBspException>(() => EntityLump.Parse(Lump("oops\n{\n}\n")));
    }

    [Fact]
    public void WritesTheExactByteFormatUnparseEntitiesWrites()
    {
        // bsplib.cpp:3104 -- "{\n", then "\"%s\" \"%s\"\n" per pair, then
        // "}\n". A byte-exact golden rather than a round trip, because a round
        // trip passes with any self-consistent spelling.
        BspEntity entity = new();
        entity.Pairs.Add(new BspKeyValue("classname", "worldspawn"));
        entity.Pairs.Add(new BspKeyValue("skyname", "sky_day01_01"));

        BspLumpData lump = EntityLump.Write([entity]);

        Assert.Equal(
            "{\n\"classname\" \"worldspawn\"\n\"skyname\" \"sky_day01_01\"\n}\n\0",
            Encoding.Latin1.GetString(lump.Data.Span));
    }

    [Fact]
    public void WritesASingleTrailingNul()
    {
        BspEntity entity = new();
        entity.Pairs.Add(new BspKeyValue("classname", "light"));

        BspLumpData lump = EntityLump.Write([entity]);

        // bsplib.cpp:3118 -- entdatasize is TellPut()+1, so the terminator is
        // inside the lump's length and not an implicit extra byte.
        Assert.Equal(0, lump.Data.Span[^1]);
        Assert.DoesNotContain((byte)0, lump.Data.Span[..^1].ToArray());
    }

    [Fact]
    public void SkipsAnEntityWithNoPairsAtAll()
    {
        // bsplib.cpp:3102 -- "ent got removed". This is how vbsp deletes an
        // entity without renumbering the rest.
        BspEntity kept = new();
        kept.Pairs.Add(new BspKeyValue("classname", "light"));

        BspLumpData lump = EntityLump.Write([new BspEntity(), kept]);

        Assert.Equal(
            "{\n\"classname\" \"light\"\n}\n\0",
            Encoding.Latin1.GetString(lump.Data.Span));
    }

    [Fact]
    public void AnEmptyEntityListWritesNothingButTheTerminator()
    {
        BspLumpData lump = EntityLump.Write([]);

        Assert.Equal(1, lump.Length);
    }

    [Fact]
    public void StripsTrailingWhitespaceOnTheWaySideToo()
    {
        // UnparseEntities strips again at write time (bsplib.cpp:3109), so a
        // pair built in memory with a trailing space still writes clean.
        BspEntity entity = new();
        entity.Pairs.Add(new BspKeyValue("targetname", "door  "));

        BspLumpData lump = EntityLump.Write([entity]);

        Assert.Equal(
            "{\n\"targetname\" \"door\"\n}\n\0",
            Encoding.Latin1.GetString(lump.Data.Span));
    }

    [Fact]
    public void ParseThenWriteIsStableOnTheGoldenMap()
    {
        // Not byte-exact against the FILE: dm_lockdown was written by an older
        // bsplib whose epair order is reversed relative to file order, and its
        // lump may carry spacing this writer normalises. What must hold is
        // that this port is a fixed point -- parse, write, parse again, and
        // nothing has moved.
        List<BspEntity> first = EntityLump.Parse(_fixture.Bsp[BspLump.Entities]);
        BspLumpData written = EntityLump.Write(first);
        List<BspEntity> second = EntityLump.Parse(written);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Pairs, second[i].Pairs);
        }
    }

    [Fact]
    public void WriteIsByteExactOnASecondPassOverTheGoldenMap()
    {
        List<BspEntity> first = EntityLump.Parse(_fixture.Bsp[BspLump.Entities]);
        BspLumpData once = EntityLump.Write(first);
        BspLumpData twice = EntityLump.Write(EntityLump.Parse(once));

        Assert.True(once.Data.Span.SequenceEqual(twice.Data.Span));
    }

    [Fact]
    public void TheGoldenMapHasExactlyOneWorldspawn()
    {
        List<BspEntity> entities = EntityLump.Parse(_fixture.Bsp[BspLump.Entities]);

        Assert.Single(entities, e => e.ClassName == "worldspawn");
    }

    [Fact]
    public void TheGoldenMapsWorldspawnIsTheFirstEntity()
    {
        // vbsp writes entity 0 as the world. Anything else means the parser
        // lost or reordered entities.
        List<BspEntity> entities = EntityLump.Parse(_fixture.Bsp[BspLump.Entities]);

        Assert.Equal("worldspawn", entities[0].ClassName);
    }

    [Fact]
    public void EveryEntityInTheGoldenMapHasAClassname()
    {
        List<BspEntity> entities = EntityLump.Parse(_fixture.Bsp[BspLump.Entities]);

        Assert.All(entities, e => Assert.NotEqual(string.Empty, e.ClassName));
    }
}
