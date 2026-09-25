using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp.Detail;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary> and the two CRT behaviours it rests on.</summary>
public class DetailPropTests
{
    [Fact]
    public void TheCrtRandSequenceFromSeedOneIsMicrosofts()
    {
        // The documented MSVC sequence for srand(1).
        MsvcRandom random = new(1);

        Assert.Equal([41, 18467, 6334, 26500, 19169], new[] { random.Next(), random.Next(), random.Next(), random.Next(), random.Next() });
    }

    [Fact]
    public void RandNeverExceedsRandMax()
    {
        MsvcRandom random = new(12345);
        for (int i = 0; i < 10000; i++)
        {
            Assert.InRange(random.Next(), 0, MsvcRandom.RandMax);
        }
    }

    [Fact]
    public void AUnitDrawIsTheReciprocalMultiply()
    {
        // Stock's binary multiplies by 1/32767 (measured, see NextUnit).
        MsvcRandom a = new(7), b = new(7);

        Assert.Equal(a.Next() * (1.0f / 32767), b.NextUnit());
    }

    [Fact]
    public void TheGaussianStreamReturnsItsSecondValueOnTheNextCall()
    {
        //: one pair per two calls; the second call draws nothing.
        GaussianRandomStream gaussian = new();
        StockRandomStream uniform = new(3);
        gaussian.RandomFloat(ref uniform, 0f, 1f);
        StockRandomStream copy = uniform;

        gaussian.RandomFloat(ref uniform, 0f, 1f);

        Assert.Equal(copy.GenerateRandomNumber(), uniform.GenerateRandomNumber());
    }

    [Fact]
    public void TheCrtQsortSortsByKey()
    {
        int[] items = [5, 3, 9, 1, 7, 3, 8, 2, 6, 4, 0, 9, 1];

        MsvcQsort.Sort<int>(items, (a, b) => a.CompareTo(b));

        Assert.Equal([0, 1, 1, 2, 3, 3, 4, 5, 6, 7, 8, 9, 9], items);
    }

    [Fact]
    public void TheCrtQsortIsNotStable()
    {
        // Eight or fewer elements go to shortsort, which swaps the FIRST
        // maximum to the end: equal keys come out reordered.
        (int Key, int Id)[] items = [(1, 0), (1, 1), (1, 2)];

        MsvcQsort.Sort<(int Key, int Id)>(items, (a, b) => a.Key.CompareTo(b.Key));

        Assert.Equal([1, 2, 0], items.Select(i => i.Id));
    }

    [Fact]
    public void AnEqualAlphaGroupGoesBeforeTheExistingOne()
    {
        DetailDictionary dictionary = Parse("\"t\" { \"g1\" { \"alpha\" \"1\" \"m\" { \"model\" \"a\" } } \"g2\" { \"alpha\" \"1\" \"m\" { \"model\" \"b\" } } }");

        Assert.Equal(["b", "a"], dictionary.Types[0].Groups.Select(g => g.Models[0].ModelName));
    }

    [Fact]
    public void GroupsAreSortedByAscendingAlpha()
    {
        DetailDictionary dictionary = Parse("\"t\" { \"hi\" { \"alpha\" \"1\" \"m\" { \"model\" \"a\" } } \"lo\" { \"alpha\" \"0\" \"m\" { \"model\" \"b\" } } }");

        Assert.Equal([0f, 1f], dictionary.Types[0].Groups.Select(g => g.Alpha));
    }

    [Fact]
    public void AMinAngleBelowTheMaxIsRaisedToIt()
    {
        DetailModel model = OneModel("\"model\" \"a\" \"minAngle\" \"60\" \"maxAngle\" \"30\"");

        Assert.Equal(model.MaxCosAngle, model.MinCosAngle);
    }

    [Fact]
    public void AmountsAreARunningTotalRenormalisedPastOne()
    {
        //,241-247.
        DetailDictionary dictionary = Parse("\"t\" { \"g\" { \"a\" { \"model\" \"a\" \"amount\" \"1\" } \"b\" { \"model\" \"b\" \"amount\" \"3\" } } }");

        Assert.Equal([0.25f, 1f], dictionary.Types[0].Groups[0].Models.Select(m => m.Amount));
    }

    [Fact]
    public void AmountsUnderOneAreLeftAlone()
    {
        DetailDictionary dictionary = Parse("\"t\" { \"g\" { \"a\" { \"model\" \"a\" \"amount\" \"0.25\" } \"b\" { \"model\" \"b\" \"amount\" \"0.25\" } } }");

        Assert.Equal([0.25f, 0.5f], dictionary.Types[0].Groups[0].Models.Select(m => m.Amount));
    }

    [Fact]
    public void ASpriteTakesItsTextureRectangleHalfATexelIn()
    {
        DetailModel model = OneModel("\"sprite\" \"0 0 64 64 512\"");

        Assert.Equal((0.5f / 512, 0.5f / 512), model.Tex[0]);
        Assert.Equal((63.5f / 512, 63.5f / 512), model.Tex[1]);
        Assert.Equal(DetailModelType.Sprite, model.Type);
    }

    [Fact]
    public void ASpriteWithoutSpriteSizeIsTheDefaultCard()
    {
        DetailModel model = OneModel("\"sprite\" \"0 0 64 64 512\"");

        Assert.Equal([(-10f, 20f), (10f, 0f)], model.Pos);
    }

    [Fact]
    public void SpriteSizeCentresTheCardOnItsOrigin()
    {
        //: "0.5 0 32 32" -> (-16, 32), (16, -0).
        DetailModel model = OneModel("\"sprite\" \"0 0 64 64 512\" \"spritesize\" \"0.5 0 32 32\"");

        Assert.Equal((-16f, 32f), model.Pos[0]);
        Assert.Equal(16f, model.Pos[1].X);
        Assert.True(float.IsNegative(model.Pos[1].Y), "-oy of zero is -0");
    }

    [Fact]
    public void ACrossShapeIsRecognisedIgnoringCase()
    {
        Assert.Equal(DetailModelType.ShapeCross, OneModel("\"sprite\" \"0 0 64 64 512\" \"sprite_shape\" \"CROSS\"").Type);
    }

    [Fact]
    public void SwayIsClampedAndQuantised()
    {
        //: clamp to 1, then (uchar)(255.0 * x).
        Assert.Equal(255, OneModel("\"sprite\" \"0 0 64 64 512\" \"sway\" \"4\"").SwayAmount);
        Assert.Equal(127, OneModel("\"sprite\" \"0 0 64 64 512\" \"sway\" \"0.5\"").SwayAmount);
    }

    [Fact]
    public void ShapeAngleWrapsAtAByte()
    {
        //, GetInt into an unsigned char.
        Assert.Equal(104, OneModel("\"sprite\" \"0 0 64 64 512\" \"shape_angle\" \"360\"").ShapeAngle);
    }

    [Fact]
    public void AShortSpriteKeyIsFatal()
    {
        Assert.Throws<MapCompileException>(() => OneModel("\"sprite\" \"0 0 64 64\""));
    }

    [Fact]
    public void AModelBlockWithNoKeysIsNotAModel()
    {
        //, GetFirstSubKey.
        DetailDictionary dictionary = Parse("\"t\" { \"g\" { \"empty\" { } \"m\" { \"model\" \"a\" } } }");

        Assert.Single(dictionary.Types[0].Groups[0].Models);
    }

    [Fact]
    public void TypeNamesMatchCaseSensitively()
    {
        // A CUtlSymbol compare; the symbol table is case-sensitive.
        DetailDictionary dictionary = Parse("\"Grass\" { \"g\" { \"m\" { \"model\" \"a\" } } }");

        Assert.Equal(0, dictionary.Find("Grass"));
        Assert.Equal(-1, dictionary.Find("grass"));
    }

    [Fact]
    public void AFlatFaceWithTwoGroupsTakesTheLastWithoutADraw()
    {
        // SelectGroup: alpha 1 walks past every group.
        DetailDictionary dictionary = Parse("\"t\" { \"lo\" { \"alpha\" \"0\" \"m\" { \"model\" \"a\" } } \"hi\" { \"alpha\" \"1\" \"m\" { \"model\" \"b\" } } }");
        MsvcRandom random = new(1);

        int group = DetailPropEmitter.SelectGroup(dictionary.Types[0], 1.0f, ref random);

        Assert.Equal(1, group);
        Assert.Equal(41, random.Next());
    }

    [Fact]
    public void AnAlphaBetweenTwoGroupsDraws()
    {
        DetailDictionary dictionary = Parse("\"t\" { \"lo\" { \"alpha\" \"0\" \"m\" { \"model\" \"a\" } } \"hi\" { \"alpha\" \"1\" \"m\" { \"model\" \"b\" } } }");
        MsvcRandom random = new(1);

        DetailPropEmitter.SelectGroup(dictionary.Types[0], 0.5f, ref random);

        Assert.Equal(18467, random.Next());
    }

    [Fact]
    public void ADrawPastTheLastAmountSelectsNothing()
    {
        // SelectDetail.
        DetailGroup group = new();
        group.Models.Add(new DetailModel { Amount = 0f });
        MsvcRandom random = new(1);

        Assert.Equal(-1, DetailPropEmitter.SelectDetail(group, ref random));
    }

    [Fact]
    public void AFloorDetailConformsWithNoPitchOrRoll()
    {
        Vec3 angles = DetailPropEmitter.ConformingAngles(new Vec3(0f, 0f, 1f), 0, ComplianceOptions.Correct);

        Assert.Equal(0f, angles.Z);
        Assert.Equal(0f, Math.Abs(angles.X));
    }

    [Fact]
    public void AConformingDetailsYawIsItsSpin()
    {
        Vec3 angles = DetailPropEmitter.ConformingAngles(new Vec3(0f, 0f, 1f), 2399, ComplianceOptions.Correct);

        Assert.InRange(angles.Y, 26.357f - 1e-4f, 26.357f + 1e-4f);
    }

    [Fact]
    public void AWallDetailIsRolledOntoTheWall()
    {
        // A +X facing wall: x is along the normal, so the roll swaps in +Y;
        // forward becomes +Y (yaw 90) and up +X, a roll of 90.
        Vec3 angles = DetailPropEmitter.ConformingAngles(new Vec3(1f, 0f, 0f), 0, ComplianceOptions.Correct);

        Assert.InRange(angles.Y, 89.99f, 90.01f);
        Assert.InRange(angles.Z, 89.99f, 90.01f);
    }

    private static DetailModel OneModel(string keys) =>
        Parse($"\"t\" {{ \"g\" {{ \"m\" {{ {keys} }} }} }}").Types[0].Groups[0].Models[0];

    private static DetailDictionary Parse(string types) =>
        DetailDictionary.Parse(KeyValuesDocument.ParseAsync($"\"detail\" {{ {types} }}").AsTask().Result.Root!);
}
