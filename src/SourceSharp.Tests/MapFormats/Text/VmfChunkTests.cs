//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Text;

/// <summary>
/// <see cref="VmfChunk.GetChunk"/>, rewritten from a LINQ chain to a loop for
/// the loader's once-per-side <c>dispinfo</c> question: it must still answer
/// exactly what <c>GetChunks(name).FirstOrDefault()</c> answered.
/// </summary>
public class VmfChunkTests
{
    [Fact]
    public void GetChunkFindsTheFirstChunkWithTheName()
    {
        VmfChunk side = new("side");
        VmfChunk first = side.AddChunk("dispinfo");
        side.AddChunk("dispinfo");

        Assert.Same(first, side.GetChunk("dispinfo"));
        Assert.Same(side.GetChunks("dispinfo").FirstOrDefault(), side.GetChunk("dispinfo"));
    }

    [Fact]
    public void GetChunkIgnoresCase()
    {
        VmfChunk side = new("side");
        VmfChunk disp = side.AddChunk("DispInfo");

        Assert.Same(disp, side.GetChunk("dispinfo"));
    }

    [Fact]
    public void GetChunkSkipsAKeyWithTheSameName()
    {
        VmfChunk side = new("side");
        side.AddKey("dispinfo", "not a chunk");
        VmfChunk disp = side.AddChunk("dispinfo");

        Assert.Same(disp, side.GetChunk("dispinfo"));
    }

    [Fact]
    public void GetChunkIsNullWhenThereIsNone()
    {
        VmfChunk side = new("side");
        side.AddKey("plane", "(0 0 0) (1 0 0) (0 1 0)");
        side.AddChunk("other");

        Assert.Null(side.GetChunk("dispinfo"));
    }

    [Fact]
    public void GetChunkRefusesANullName()
    {
        Assert.Throws<ArgumentNullException>(() => new VmfChunk("side").GetChunk(null!));
    }
}
