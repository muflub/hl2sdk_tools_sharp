using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Faces;
using SourceSharp.MapTools.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// Face allocation and the <c>c_faces</c> balance.
/// </summary>
public class FaceAllocatorTests
{
    [Fact]
    public void AllocatingAndFreeingBalancesTheLiveCount()
    {
        FaceAllocator faces = new(new WindingArena());

        Face face = faces.Alloc();
        Assert.Equal(1, faces.LiveFaces);

        faces.Free(face);
        Assert.Equal(0, faces.LiveFaces);
    }

    [Fact]
    public void ACopyCarriesTheSourcesIdAndNotAFreshOne()
    {
        // NewFaceFromFace allocates (stamping a new id) and then does
        // *newf = *f, which copies the source's id straight back over it.
        FaceAllocator faces = new(new WindingArena());

        Face first = faces.Alloc();
        Face second = faces.Alloc();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Id, faces.NewFaceFromFace(first).Id);
    }

    [Fact]
    public void TheIdCounterStillRunsAheadOfTheIdsInUse()
    {
        FaceAllocator faces = new(new WindingArena());

        Face first = faces.Alloc();
        faces.NewFaceFromFace(first);
        faces.NewFaceFromFace(first);

        Assert.Equal(3, faces.TotalAllocations);
    }

    [Fact]
    public void ACopyHasNoWindingAndNoMergeOrSplitLinks()
    {
        WindingArena arena = new();
        FaceAllocator faces = new(arena);

        Face source = faces.Alloc();
        source.Winding = arena.Create([new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f)]);
        source.Merged = source;
        source.Split[0] = source;

        Face copy = faces.NewFaceFromFace(source);

        Assert.True(copy.Winding.IsNull);
        Assert.Null(copy.Merged);
        Assert.Null(copy.Split[0]);
        Assert.Null(copy.Split[1]);
        Assert.False(copy.IsDead);
    }

    [Fact]
    public void ACopyCarriesEverythingElseIncludingTheNextLink()
    {
        FaceAllocator faces = new(new WindingArena());

        Face next = faces.Alloc();
        Face source = faces.Alloc();
        source.Next = next;
        source.TexInfo = 5;
        source.PlaneNumber = 9;
        source.Contents = 3;
        source.SmoothingGroups = 12;
        source.NumPoints = 2;
        source.VertexNumbers[0] = 41;
        source.VertexNumbers[1] = 42;

        Face copy = faces.NewFaceFromFace(source);

        Assert.Same(next, copy.Next);
        Assert.Equal(5, copy.TexInfo);
        Assert.Equal(9, copy.PlaneNumber);
        Assert.Equal(3, copy.Contents);
        Assert.Equal(12u, copy.SmoothingGroups);
        Assert.Equal([41, 42], FaceStageFixture.VertexNumbers(copy));
    }

    [Fact]
    public void CopyFaceAlsoCopiesTheWinding()
    {
        WindingArena arena = new();
        FaceAllocator faces = new(arena);

        Face source = faces.Alloc();
        source.Winding = arena.Create([new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f)]);

        Face copy = faces.CopyFace(source);

        Assert.NotEqual(source.Winding, copy.Winding);
        Assert.Equal(
            arena.Points(source.Winding).ToArray(), arena.Points(copy.Winding).ToArray());
    }

    [Fact]
    public void FreeingAFaceFreesItsWinding()
    {
        WindingArena arena = new();
        FaceAllocator faces = new(arena);

        Face face = faces.Alloc();
        face.Winding = arena.Create([new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f)]);

        int before = arena.ActiveWindings;
        faces.Free(face);

        Assert.Equal(before - 1, arena.ActiveWindings);
        Assert.True(face.Winding.IsNull);
    }

    [Fact]
    public void FreeingAListFreesEveryFaceOnIt()
    {
        FaceAllocator faces = new(new WindingArena());

        Face a = faces.Alloc();
        Face b = faces.Alloc();
        Face c = faces.Alloc();
        a.Next = b;
        b.Next = c;

        faces.FreeList(a);

        Assert.Equal(0, faces.LiveFaces);
    }

    [Fact]
    public void AFaceIsDeadAsSoonAsAnyOfTheThreeLinksIsSet()
    {
        FaceAllocator faces = new(new WindingArena());

        Face merged = faces.Alloc();
        merged.Merged = merged;
        Assert.True(merged.IsDead);

        Face front = faces.Alloc();
        front.Split[0] = front;
        Assert.True(front.IsDead);

        Face back = faces.Alloc();
        back.Split[1] = back;
        Assert.True(back.IsDead);
    }
}
