//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Faces;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Faces;

/// <summary>
/// The primitive tables a face can point into
///(<c>g_primitives</c> and friends).
/// </summary>
public class PrimitiveTableTests
{
    [Fact]
    public void ATriangleListPrimitiveRecordsItsIndexRunAndNoVertices()
    {
        PrimitiveTable table = new();

        int id = table.AddTriangleList([0, 1, 2, 0, 2, 3]);

        DPrimitive primitive = table.Primitives[id];

        Assert.Equal((byte)PrimitiveType.TriList, primitive.Type);
        Assert.Equal(0, primitive.FirstIndex);
        Assert.Equal(6, primitive.IndexCount);
        Assert.Equal(0, primitive.VertCount);
        Assert.Equal(6, table.Indices.Count);
    }

    [Fact]
    public void TwoTriangleListsGetConsecutiveIndexRuns()
    {
        PrimitiveTable table = new();

        table.AddTriangleList([0, 1, 2]);
        int second = table.AddTriangleList([3, 4, 5]);

        Assert.Equal(3, table.Primitives[second].FirstIndex);
        Assert.Equal(6, table.Indices.Count);
    }

    [Fact]
    public void AWindingsPointsBecomeNewPrimitiveVertices()
    {
        PrimitiveTable table = new();
        PrimitiveBuilder builder = table.Begin(PrimitiveType.TriStrip);

        ushort[] indices = new ushort[3];
        builder.AddWinding(
            [new Vec3(0f, 0f, 0f), new Vec3(64f, 0f, 0f), new Vec3(0f, 64f, 0f)], indices);

        Assert.Equal([0, 1, 2], indices);
        Assert.Equal(3, builder.VertCount);
    }

    [Fact]
    public void ASecondWindingWeldsToThePointsTheFirstAlreadyPutThere()
    {
        PrimitiveTable table = new();
        PrimitiveBuilder builder = table.Begin(PrimitiveType.TriStrip);

        ushort[] first = new ushort[3];
        builder.AddWinding(
            [new Vec3(0f, 0f, 0f), new Vec3(64f, 0f, 0f), new Vec3(0f, 64f, 0f)], first);

        ushort[] second = new ushort[3];
        builder.AddWinding(
            [new Vec3(64f, 0f, 0f), new Vec3(64f, 64f, 0f), new Vec3(0f, 64f, 0f)], second);

        // The two shared corners reuse indices 1 and 2; only one vertex is new.
        Assert.Equal(1, second[0]);
        Assert.Equal(2, second[2]);
        Assert.Equal(4, builder.VertCount);
    }

    [Fact]
    public void AnUncommittedPrimitiveIsNotInTheTable()
    {
        PrimitiveTable table = new();
        PrimitiveBuilder builder = table.Begin(PrimitiveType.TriStrip);

        builder.AddIndex(0);

        // Stock writes the struct into the array and only bumps the count when
        // the primitive turns out to be real; the indices it wrote stay.
        Assert.Empty(table.Primitives);
        Assert.Single(table.Indices);
    }

    [Fact]
    public void ACommittedPrimitiveCarriesTheRunItBuilt()
    {
        PrimitiveTable table = new();
        PrimitiveBuilder builder = table.Begin(PrimitiveType.TriStrip);

        ushort[] indices = new ushort[3];
        builder.AddWinding(
            [new Vec3(0f, 0f, 0f), new Vec3(64f, 0f, 0f), new Vec3(0f, 64f, 0f)], indices);

        foreach (ushort index in indices)
        {
            builder.AddIndex(index);
        }

        int id = builder.Commit();
        DPrimitive primitive = table.Primitives[id];

        Assert.Equal((byte)PrimitiveType.TriStrip, primitive.Type);
        Assert.Equal(3, primitive.IndexCount);
        Assert.Equal(3, primitive.VertCount);
    }

    [Fact]
    public void TheBuildersIdIsTheSlotItWillTake()
    {
        PrimitiveTable table = new();
        table.AddTriangleList([0, 1, 2]);

        PrimitiveBuilder builder = table.Begin(PrimitiveType.TriStrip);

        Assert.Equal(1, builder.Id);
    }
}
