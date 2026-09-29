//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Rooms;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

public sealed class Q3DiagTests(Rooms3x3Fixture fixture, ITestOutputHelper output) : IClassFixture<Rooms3x3Fixture>
{
    [Fact]
    public async Task Diag()
    {
        var (layout, linked, mono, monoVis) = await fixture.GeneratedAsync(5, 5, 3, 0.2, 4);
        LevelProbe lp = new(linked.Bsp), mp = new(mono.Bsp!);
        List<(Vec3 P, int Mono, int Linked, int Cell)> samples = [];
        IReadOnlyList<DLeaf> leafs = mp.Leafs;
        for (int l = 0; l < leafs.Count; l++)
        {
            DLeaf leaf = leafs[l];
            if (leaf.Cluster < 0 || (leaf.Contents & (int)BrushContents.Solid) != 0) continue;
            Vec3 lo = new(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]);
            Vec3 hi = new(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]);
            Vec3 mid = (lo + hi) * 0.5f;
            for (int b = -1; b < 8; b++)
            {
                Vec3 corner = b < 0 ? mid : new Vec3((b & 1) == 0 ? lo.X : hi.X, (b & 2) == 0 ? lo.Y : hi.Y, (b & 4) == 0 ? lo.Z : hi.Z);
                Vec3 sample = mid + ((corner - mid) * (2f / 3f));
                if (mp.Leaf(sample) != l) continue;
                int c = lp.Leafs[lp.Leaf(sample)].Cluster;
                if (c >= 0) samples.Add((sample, leaf.Cluster, c, (int)(sample.X / 256) * 100 + (int)(sample.Y / 256)));
            }
        }

        long sameL = 0, sameM = 0, crossL = 0, crossM = 0, crossBoth = 0, crossLos = 0, adjL = 0, adjM = 0;
        foreach (var a in samples)
        foreach (var b in samples)
        {
            bool l = linked.Vis.CanSee(a.Linked, b.Linked), m = monoVis.CanSee(a.Mono, b.Mono);
            if (a.Cell == b.Cell) { sameL += l ? 1 : 0; sameM += m ? 1 : 0; continue; }
            int dx = Math.Abs(a.Cell / 100 - b.Cell / 100), dy = Math.Abs(a.Cell % 100 - b.Cell % 100);
            if (dx + dy == 1) { adjL += l ? 1 : 0; adjM += m ? 1 : 0; continue; }
            crossL += l ? 1 : 0; crossM += m ? 1 : 0; crossBoth += l && m ? 1 : 0;
        }

        output.WriteLine($"same-room linked {sameL} mono {sameM}; adjacent linked {adjL} mono {adjM}; farther linked {crossL} mono {crossM} both {crossBoth}");
    }
}
