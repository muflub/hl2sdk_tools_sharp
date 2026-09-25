using System.IO.Compression;
using SourceSharp.MapTools.Phys.Managed;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// The managed collide queries vbsp uses (volume, AABB, extent) against the reference
/// collision cooker's answers for the first 150 whole blobs of the brush and cloud
/// goldens (&lt;group&gt;.queries.sdk.txt.gz: each line is the query followed by its answer).
/// </summary>
public class ManagedCookerQueryTests
{
    public static TheoryData<string> Groups => ["brushes", "clouds"];

    private sealed record Query(char Kind, int Blob, (float X, float Y, float Z) Direction, float[] Answer);

    private static List<byte[]> Blobs(string group)
    {
        var blobs = new List<byte[]>();
        foreach (CookerFixture.Answer a in CookerFixture.Answers(group, "sdk"))
        {
            if (a.Blob is not null)
            {
                blobs.Add(a.Blob);
            }
        }

        return blobs;
    }

    private static List<Query> Queries(string group)
    {
        var list = new List<Query>();
        string path = Path.Combine(CookerFixture.FixtureDirectory(), group + ".queries.sdk.txt.gz");
        using var r = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int blob = int.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture) - 1;
            if (t[0] == "Q")
            {
                list.Add(new Query('Q', blob, default, t[3..].Select(s => (float)CookerFixture.ParseC99(s)).ToArray()));
            }
            else
            {
                var d = ((float)CookerFixture.ParseC99(t[2]), (float)CookerFixture.ParseC99(t[3]), (float)CookerFixture.ParseC99(t[4]));
                list.Add(new Query('E', blob, d, t[6..].Select(s => (float)CookerFixture.ParseC99(s)).ToArray()));
            }
        }

        return list;
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void TheGoldenCoversOneHundredFiftyBlobs(string group)
    {
        Assert.Equal(150, Blobs(group).Count);
        Assert.Equal(150 * 15, Queries(group).Count);
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void VolumeIsBitExact(string group)
    {
        List<byte[]> blobs = Blobs(group);
        foreach (Query q in Queries(group).Where(q => q.Kind == 'Q'))
        {
            float v = IvpCollideQueries.CollideVolume(blobs[q.Blob]);
            Assert.Equal(BitConverter.SingleToInt32Bits(q.Answer[0]), BitConverter.SingleToInt32Bits(v));
        }
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void AabbIsNumericallyEqual(string group)
    {
        // Numerically, not bitwise: the reference reports -0 where the managed min is +0.
        List<byte[]> blobs = Blobs(group);
        foreach (Query q in Queries(group).Where(q => q.Kind == 'Q'))
        {
            var (mins, maxs) = IvpCollideQueries.CollideGetAabb(blobs[q.Blob]);
            Assert.Equal([q.Answer[2], q.Answer[3], q.Answer[4], q.Answer[5], q.Answer[6], q.Answer[7]], new[] { mins.X, mins.Y, mins.Z, maxs.X, maxs.Y, maxs.Z });
        }
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void ExtentDotAlongEveryDirectionIsEqual(string group)
    {
        // The point may differ on an exact tie; vbsp only uses its dot with the direction.
        List<byte[]> blobs = Blobs(group);
        foreach (Query q in Queries(group).Where(q => q.Kind == 'E'))
        {
            var d = q.Direction;
            var p = IvpCollideQueries.CollideGetExtent(blobs[q.Blob], d);
            float managed = ((p.X * d.X) + (p.Y * d.Y)) + (p.Z * d.Z);
            float native = ((q.Answer[0] * d.X) + (q.Answer[1] * d.Y)) + (q.Answer[2] * d.Z);
            Assert.Equal(native, managed);
        }
    }
}
