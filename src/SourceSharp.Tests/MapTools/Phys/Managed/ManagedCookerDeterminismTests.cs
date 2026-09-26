//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Security.Cryptography;
using Xunit;

namespace SourceSharp.Tests.MapTools.Phys.Managed;

/// <summary>
/// Determinism by construction (plan I4, ruling Q16): no static state, one scratch context per
/// thread, so any thread count gives the bytes one thread gives, and a cook does not depend on
/// what was cooked before it.
/// </summary>
public class ManagedCookerDeterminismTests
{
    [Theory]
    [InlineData(CookMode.Stock)]
    [InlineData(CookMode.Correct)]
    public void ManyThreadsProduceTheSingleThreadBytes(CookMode mode)
    {
        List<CookerFixture.Job> jobs = [.. CookerFixture.Jobs("brushes").Take(1500), .. CookerFixture.Jobs("multi")];
        byte[] serial = Digest(jobs, mode, 1);
        byte[] parallel = Digest(jobs, mode, 16);
        Assert.Equal(serial, parallel);
    }

    [Fact]
    public void ACookDoesNotDependOnThePreviousCook()
    {
        // Spike 0b's check, for the managed cooker: the same shape cooked first, and again after
        // a different shape through the same context, gives the same bytes.
        List<CookerFixture.Job> jobs = CookerFixture.Jobs("clouds");
        var context = CookerFixture.Context(CookMode.Correct);
        byte[] first = CookerFixture.Cook(jobs[0], CookMode.Correct, context)!;
        _ = CookerFixture.Cook(jobs[1], CookMode.Correct, context);
        _ = CookerFixture.Cook(jobs[2], CookMode.Correct, context);
        byte[] again = CookerFixture.Cook(jobs[0], CookMode.Correct, context)!;
        Assert.Equal(first, again);
    }

    [Fact]
    public void TwoContextsGiveTheSameBytes()
    {
        CookerFixture.Job job = CookerFixture.Jobs("multi")[3];
        byte[] a = CookerFixture.Cook(job, CookMode.Stock, CookerFixture.Context(CookMode.Stock))!;
        byte[] b = CookerFixture.Cook(job, CookMode.Stock, CookerFixture.Context(CookMode.Stock))!;
        Assert.Equal(a, b);
    }

    private static byte[] Digest(List<CookerFixture.Job> jobs, CookMode mode, int threads)
    {
        byte[][] blobs = new byte[jobs.Count][];
        using var contexts = new ThreadLocal<SourceSharp.MapTools.Phys.Managed.IvpCookContext>(() => CookerFixture.Context(mode));
        System.Threading.Tasks.Parallel.For(
            0,
            jobs.Count,
            new ParallelOptions { MaxDegreeOfParallelism = threads },
            i => blobs[i] = CookerFixture.Cook(jobs[i], mode, contexts.Value!) ?? []);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (byte[] b in blobs)
        {
            sha.AppendData(BitConverter.GetBytes(b.Length));
            sha.AppendData(b);
        }

        return sha.GetHashAndReset();
    }
}
