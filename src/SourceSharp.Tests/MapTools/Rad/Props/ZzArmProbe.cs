//========= Copyright Valve Corporation, All rights reserved. ============//
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Rad.Props;
using SourceSharp.Tests.MapTools.Rad.Ambient;
using Xunit;

namespace SourceSharp.Tests.MapTools.Rad.Props;

public sealed class ZzArmProbe(AmbientFixture ambient) : IClassFixture<AmbientFixture>
{
    [Fact]
    public void ContentProbe()
    {
        List<string> lines = [];
        foreach ((string path, byte[] bytes) in SourceSharp.MapGen.Content.SyntheticContent.Build().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            lines.Add(path + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16]);
        }

        Assert.Fail("CONTENT " + lines.Count + "\n" + string.Join("\n", lines));
    }

    [Fact]
    public void TrigProbe()
    {
        List<string> diffs = [];
        foreach (int sides in new[] { 3, 4, 6, 8, 12, 16, 24, 32 })
        {
            for (int i = 0; i <= sides; i++)
            {
                float a = 2 * MathF.PI * i / sides;
                float c = MathF.Cos(a), s = MathF.Sin(a);
                float dc = SourceSharp.MapFormats.Numerics.DetMathF.Cos(a), ds = SourceSharp.MapFormats.Numerics.DetMathF.Sin(a);
                float rc = (float)Math.Cos(a), rs = (float)Math.Sin(a);
                if (c != dc || s != ds || rc != dc || rs != ds)
                {
                    diffs.Add($"{sides}/{i}: a={a:R} cos {c:R} det {dc:R} dbl {rc:R}; sin {s:R} det {ds:R} dbl {rs:R}");
                }
            }
        }

        Assert.Fail("TRIG " + diffs.Count + "\n" + string.Join("\n", diffs));
    }

    [Fact]
    public async Task Probe()
    {
        (StaticPropLump lump, IReadOnlyList<StaticPropModel> models) = await StaticPropChunkingScene.PropsAsync();
        List<string> actual = [];
        foreach ((bool indirect, bool dss) in new[] { (true, false), (true, true), (false, false), (false, true) })
        {
            StaticPropLightingResult r = await StaticPropChunkingScene.LightAsync(
                ambient, lump, models, ComplianceOptions.Correct, indirect, dss, Batching.PropAtATime);
            actual.Add(StaticPropChunkingScene.Digest(r)[..16]);
        }

        Assert.Fail("DIGESTS " + string.Join(" ", actual));
    }
}
