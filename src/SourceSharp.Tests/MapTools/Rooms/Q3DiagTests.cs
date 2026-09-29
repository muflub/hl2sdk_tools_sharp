//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;

using Xunit;
using Xunit.Abstractions;

namespace SourceSharp.Tests.MapTools.Rooms;

public sealed class Q3DiagTests(Rooms3x3Fixture fixture, ITestOutputHelper output) : IClassFixture<Rooms3x3Fixture>
{
    [Theory]
    [MemberData(nameof(Rooms3x3Fixture.CaseNames), MemberType = typeof(Rooms3x3Fixture))]
    public async Task Diag(string name)
    {
        Rooms3x3Pair pair = await fixture.PairAsync(name);
        List<(Vec3 P, int Mono, int Linked)> samples = [];
        IReadOnlyList<DLeaf> leafs = pair.MonolithicProbe.Leafs;
        for (int l = 0; l < leafs.Count; l++)
        {
            DLeaf leaf = leafs[l];
            if (leaf.Cluster < 0 || (leaf.Contents & (int)BrushContents.Solid) != 0)
            {
                continue;
            }

            Vec3 lo = new(leaf.Mins[0], leaf.Mins[1], leaf.Mins[2]);
            Vec3 hi = new(leaf.Maxs[0], leaf.Maxs[1], leaf.Maxs[2]);
            Vec3 mid = (lo + hi) * 0.5f;
            for (int b = -1; b < 8; b++)
            {
                Vec3 corner = b < 0 ? mid : new Vec3((b & 1) == 0 ? lo.X : hi.X, (b & 2) == 0 ? lo.Y : hi.Y, (b & 4) == 0 ? lo.Z : hi.Z);
                Vec3 sample = mid + ((corner - mid) * (2f / 3f));
                if (pair.MonolithicProbe.Leaf(sample) != l)
                {
                    continue;
                }

                int linked = pair.LinkedProbe.Leafs[pair.LinkedProbe.Leaf(sample)].Cluster;
                if (linked >= 0)
                {
                    samples.Add((sample, leaf.Cluster, linked));
                }
            }
        }

        int linkedPairs = 0, monoPairs = 0, violations = 0, losViolations = 0, losPairs = 0, losMonoMiss = 0;
        foreach (var a in samples)
        {
            foreach (var b in samples)
            {
                bool mono = pair.MonolithicVis.CanSee(a.Mono, b.Mono);
                bool linked = pair.Linked.Vis.CanSee(a.Linked, b.Linked);
                bool los = pair.MonolithicProbe.SightLine(a.P, b.P);
                monoPairs += mono ? 1 : 0; linkedPairs += linked ? 1 : 0;
                losPairs += los ? 1 : 0;
                if (mono && !linked)
                {
                    violations++;
                    if (los)
                    {
                        losViolations++;
                        if (losViolations < 5) output.WriteLine($"LOS violation {a.P} (m{a.Mono} l{a.Linked}) -> {b.P} (m{b.Mono} l{b.Linked})");
                    }
                }

                if (los && !mono) losMonoMiss++;
            }
        }

        int cl = pair.Linked.Vis.ClusterCount, cm = pair.MonolithicVis.ClusterCount;
        output.WriteLine($"{name}: samples {samples.Count} linkedPairs {linkedPairs} monoPairs {monoPairs} violations {violations} losPairs {losPairs} losViolations {losViolations} losNotMono {losMonoMiss}; linked visible {pair.Linked.Vis.TotalVisibleClusters}/{cl * cl}, mono {pair.MonolithicVis.TotalVisibleClusters}/{cm * cm}");
    }
}
