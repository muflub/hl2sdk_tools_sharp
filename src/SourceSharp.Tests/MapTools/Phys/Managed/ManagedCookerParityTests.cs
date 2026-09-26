//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// The managed cooker against the reference collision cooker builds, over the committed goldens
/// (plan ruling Q18: byte-exact per build on simple shapes; general brushes graded by
/// <see cref="VphyCompare"/>; here every group turned out byte-exact).
/// </summary>
public class ManagedCookerParityTests
{
    public static TheoryData<string> Groups => [.. CookerFixture.Groups];

    [Theory]
    [MemberData(nameof(Groups))]
    public void StockIsByteExactAgainstTheEarlierReferenceBuild(string group)
    {
        // Stock = the earlier reference build's float arithmetic with its rsqrt estimate.
        // The goldens were cut on an AMD Ryzen 9 9950X; rsqrtss is implementation-defined,
        // so a different CPU vendor may
        // legitimately disagree with both the goldens and this port in the same places.
        AssertExact(group, CookMode.Stock, "sdk");
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void Tf2ParityModeIsByteExactAgainstTf2(string group)
    {
        AssertExact(group, CookMode.Tf2, "tf2");
    }

    [Fact]
    public void CorrectModeDiffersFromTf2OnlyByTheNaNInertiaItFixes()
    {
        // StockQuirk.CollisionInertiaZeroLengthEdge: Tf2 mode writes NaN rotation_inertia for six
        // dm_lockdown brushes; correct mode writes finite values and is otherwise identical.
        List<CookerFixture.Job> jobs = CookerFixture.Jobs("brushes");
        List<CookerFixture.Answer> answers = CookerFixture.Answers("brushes", "tf2");
        var context = CookerFixture.Context(CookMode.Correct);
        int differing = 0;
        for (int i = 0; i < jobs.Count; i++)
        {
            byte[]? blob = CookerFixture.Cook(jobs[i], CookMode.Correct, context);
            if (CookerFixture.Matches(answers[i], blob))
            {
                continue;
            }

            differing++;
            Assert.NotNull(blob);
            for (int k = 0; k < 3; k++)
            {
                Assert.True(float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(28 + 12 + (4 * k)))));
            }
        }

        Assert.Equal(6, differing);
    }

    [Fact]
    public void TheCubeCookHashesAreReproduced()
    {
        // A 32-unit cube through ConvexFromPlanes + ConvertConvexToCollide + CollideWrite;
        // the asserted digests are the two reference builds' answers for it.
        CookerFixture.Job cube = CookerFixture.Jobs("shapes")[0];
        byte[] sdk = CookerFixture.Cook(cube, CookMode.Stock, CookerFixture.Context(CookMode.Stock))!;
        byte[] tf2 = CookerFixture.Cook(cube, CookMode.Tf2, CookerFixture.Context(CookMode.Tf2))!;
        Assert.StartsWith("1cbd34585bd45c26", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(sdk)), StringComparison.Ordinal);
        Assert.StartsWith("00a6c6952a21edb1", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(tf2)), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public void EveryWholeGoldenPassesTheStructuralGrading(string group)
    {
        // The grading instrument itself, run over every whole blob in the goldens: a managed blob
        // that is byte-exact must also grade as identical, so a comparator that miscounts cannot
        // hide behind the byte gate.
        List<CookerFixture.Job> jobs = CookerFixture.Jobs(group);
        List<CookerFixture.Answer> answers = CookerFixture.Answers(group, "tf2");
        var context = CookerFixture.Context(CookMode.Tf2);
        int graded = 0;
        for (int i = 0; i < jobs.Count; i++)
        {
            if (answers[i].Blob is not { } native)
            {
                continue;
            }

            byte[] managed = CookerFixture.Cook(jobs[i], CookMode.Tf2, context)!;
            VphyCompare.Result r = VphyCompare.Compare(native, managed);
            Assert.True(r.Identical, $"{group} #{i}: {r.Detail}");
            graded++;
        }

        Assert.True(graded > 0);
    }

    private static void AssertExact(string group, CookMode mode, string build)
    {
        List<CookerFixture.Job> jobs = CookerFixture.Jobs(group);
        List<CookerFixture.Answer> answers = CookerFixture.Answers(group, build);
        Assert.Equal(jobs.Count, answers.Count);
        var context = CookerFixture.Context(mode);
        var failures = new List<int>();
        for (int i = 0; i < jobs.Count; i++)
        {
            if (!CookerFixture.Matches(answers[i], CookerFixture.Cook(jobs[i], mode, context)))
            {
                failures.Add(i);
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{group} vs {build}: {failures.Count} of {jobs.Count} blobs differ, first {string.Join(",", failures.Take(10))}");
    }
}
