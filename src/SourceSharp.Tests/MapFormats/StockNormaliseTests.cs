using System.Diagnostics;
using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Geometry;

using Xunit;

namespace SourceSharp.Tests.MapFormats;

/// <summary>
/// <see cref="Vec3.NormaliseLikeStock"/>: reproducing stock's
/// reciprocal-square-root estimate, so normals can be compared with stock
/// exactly rather than through a tolerance.
/// </summary>
public class StockNormaliseTests
{
    [Fact]
    public void ExactAndStockNormalisationDisagree()
    {
        // If these agreed there would be no reason for two methods, and the
        // fact below that compares against the compiled reference would prove nothing. The
        // estimate is accurate to about 12 bits, so it differs from an exact
        // divide in the low mantissa on almost any input.
        Vec3 v = new(3f, 4f, 12f);

        (Vec3 exact, _) = v.Normalise();
        (Vec3 stock, _) = v.NormaliseLikeStock();

        Assert.NotEqual(exact, stock);
    }

    [Fact]
    public void StockNormalisationIsCloseToExact()
    {
        // rsqrtss is specified to a relative error below 1.5 * 2^-12, and the
        // Newton-Raphson step squares that, so the result should be very close
        // indeed. This is the vendor-INDEPENDENT half of the checking: it holds
        // on any CPU, where the bitwise fact below only holds where a compiler
        // is available to compare against.
        Vec3 v = new(-17.25f, 3.5f, 88f);

        (Vec3 exact, _) = v.Normalise();
        (Vec3 stock, _) = v.NormaliseLikeStock();

        Assert.Equal(exact.X, stock.X, 5);
        Assert.Equal(exact.Y, stock.Y, 5);
        Assert.Equal(exact.Z, stock.Z, 5);
    }

    [Fact]
    public void StockNormalisationOfAZeroVectorDoesNotProduceNaN()
    {
        // Stock's +1.0e-10f guard is what makes this finite, and it is part of
        // the ported arithmetic rather than a nicety. The result is a huge
        // reciprocal times zero, so the vector stays zero.
        (Vec3 normalised, float returned) = Vec3.Zero.NormaliseLikeStock();

        Assert.False(float.IsNaN(normalised.X));
        Assert.False(float.IsNaN(returned));
        Assert.Equal(0f, normalised.X);
        Assert.Equal(0f, normalised.Y);
        Assert.Equal(0f, normalised.Z);
    }

    [Fact]
    public void StockNormalisationIsDeterministicOnThisMachine()
    {
        // The estimate may differ BETWEEN CPUs; it does not differ between two
        // calls on one CPU. That distinction is the whole reason this method is
        // opt-in rather than the default.
        Vec3 v = new(1.5f, -2.25f, 0.125f);

        Assert.Equal(v.NormaliseLikeStock(), v.NormaliseLikeStock());
    }

    [NativeCompilerFact]
    public void StockNormalisationMatchesRealCompiledCppBitForBit()
    {
        // THE fact that matters, and the only honest way to assert "this
        // matches stock": compile stock's own code and compare the bits.
        //
        // It is vendor-independent despite testing a vendor-dependent
        // instruction, because both sides execute that instruction on the SAME
        // CPU. Whatever this machine's rsqrtss produces, both get it.
        string directory = Path.Combine(Path.GetTempPath(), "ss-rsqrt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string source = Path.Combine(directory, "stock.cpp");
            string binary = Path.Combine(directory, "stock");
            File.WriteAllText(source, CppSource);

            Assert.True(
                NativeCompiler.Compile(source, binary, out string compileError),
                $"g++ could not build the reference: {compileError}");

            Vec3[] vectors = SeededVectors(20_000);

            StringBuilder input = new();
            foreach (Vec3 v in vectors)
            {
                input.Append(CultureInfo.InvariantCulture, $"{Bits(v.X)} {Bits(v.Y)} {Bits(v.Z)}\n");
            }

            string[] lines = NativeCompiler.Run(binary, input.ToString())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(vectors.Length, lines.Length);

            for (int i = 0; i < vectors.Length; i++)
            {
                string[] parts = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                (Vec3 managed, float returned) = vectors[i].NormaliseLikeStock();

                Assert.True(
                    uint.Parse(parts[0], CultureInfo.InvariantCulture) == Bits(managed.X)
                    && uint.Parse(parts[1], CultureInfo.InvariantCulture) == Bits(managed.Y)
                    && uint.Parse(parts[2], CultureInfo.InvariantCulture) == Bits(managed.Z)
                    && uint.Parse(parts[3], CultureInfo.InvariantCulture) == Bits(returned),
                    $"vector {i} ({vectors[i]}) differs: the reference gave [{lines[i]}], managed gave "
                    + $"[{Bits(managed.X)} {Bits(managed.Y)} {Bits(managed.Z)} {Bits(returned)}]");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    private static Vec3[] SeededVectors(int count)
    {
        // Mixed on purpose: map-scale coordinates, near-unit vectors, tiny ones
        // where stock's +1e-10 guard actually moves the estimate's input, and
        // exact axials, where the true answer is representable and the
        // estimate's error is therefore most visible.
        Random random = new(20260920);
        Vec3[] vectors = new Vec3[count];

        for (int i = 0; i < count; i++)
        {
            vectors[i] = (i % 4) switch
            {
                0 => new Vec3(Spread(random, 16384f), Spread(random, 16384f), Spread(random, 16384f)),
                1 => new Vec3(Spread(random, 1f), Spread(random, 1f), Spread(random, 1f)),
                2 => new Vec3(Spread(random, 1e-3f), Spread(random, 1e-3f), Spread(random, 1e-3f)),
                _ => (i / 4 % 6) switch
                {
                    0 => new Vec3(1f, 0f, 0f),
                    1 => new Vec3(0f, 1f, 0f),
                    2 => new Vec3(0f, 0f, 1f),
                    3 => new Vec3(-1f, 0f, 0f),
                    4 => new Vec3(0f, -1f, 0f),
                    _ => new Vec3(0f, 0f, -1f),
                },
            };
        }

        return vectors;
    }

    private static float Spread(Random random, float scale) =>
        (float)((random.NextDouble() * 2.0 - 1.0) * scale);

    /// <summary>
    /// The stock <c>VectorNormalize</c> x86 path, the reference implementation's
    /// arithmetic with nothing around it.
    /// </summary>
    private const string CppSource = """
        #include <xmmintrin.h>
        #include <cstdio>
        #include <cstdint>
        #include <cstring>

        static inline void _SSE_RSqrtInline(float a, float *out)
        {
            __m128 xx = _mm_load_ss(&a);
            __m128 xr = _mm_rsqrt_ss(xx);
            __m128 xt;
            xt = _mm_mul_ss(xr, xr);
            xt = _mm_mul_ss(xt, xx);
            xt = _mm_sub_ss(_mm_set_ss(3.f), xt);
            xt = _mm_mul_ss(xt, _mm_set_ss(0.5f));
            xr = _mm_mul_ss(xr, xt);
            _mm_store_ss(out, xr);
        }

        struct Vec { float x, y, z; };

        static inline float VectorNormalize(Vec &vec)
        {
            float sqrlen = (vec.x * vec.x + vec.y * vec.y + vec.z * vec.z) + 1.0e-10f, invlen;
            _SSE_RSqrtInline(sqrlen, &invlen);
            vec.x *= invlen;
            vec.y *= invlen;
            vec.z *= invlen;
            return sqrlen * invlen;
        }

        static uint32_t bits(float f) { uint32_t u; std::memcpy(&u, &f, 4); return u; }
        static float fromBits(uint32_t u) { float f; std::memcpy(&f, &u, 4); return f; }

        int main()
        {
            uint32_t bx, by, bz;
            while (std::scanf("%u %u %u", &bx, &by, &bz) == 3)
            {
                Vec v{fromBits(bx), fromBits(by), fromBits(bz)};
                float ret = VectorNormalize(v);
                std::printf("%u %u %u %u\n", bits(v.x), bits(v.y), bits(v.z), bits(ret));
            }
            return 0;
        }
        """;
}

/// <summary>
/// Compiling and running a small C++ reference, so a managed port can be
/// checked against the code it was ported from.
/// </summary>
internal static class NativeCompiler
{
    /// <summary>The C++ compiler on this machine, or null when there is none.</summary>
    public static string? Path64()
    {
        foreach (string name in new[] { "g++", "clang++" })
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = System.IO.Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Compiles one source file to one binary.</summary>
    /// <param name="source">The .cpp to compile.</param>
    /// <param name="binary">Where to write the executable.</param>
    /// <param name="error">The compiler's diagnostics when it fails.</param>
    /// <returns>True when the compile succeeded.</returns>
    public static bool Compile(string source, string binary, out string error)
    {
        ProcessStartInfo start = new(Path64()!)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-O2");
        start.ArgumentList.Add("-msse");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(binary);
        start.ArgumentList.Add(source);

        using Process process = Process.Start(start)!;
        error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    /// <summary>Runs a binary with the given standard input.</summary>
    /// <param name="binary">The executable.</param>
    /// <param name="input">What to write to its standard input.</param>
    /// <returns>Everything it wrote to standard output.</returns>
    /// <remarks>
    /// <para>
    /// Standard output is drained CONCURRENTLY with writing standard input, and
    /// that is not defensive style -- the obvious version of this deadlocks.
    /// Writing the whole input before reading a byte works only while the
    /// child's output fits in the pipe buffer, which is about 64 KB on Linux.
    /// This harness feeds 20,000 vectors and gets roughly 800 KB back, so the
    /// child blocks writing output, the parent blocks writing input, and
    /// neither ever moves. That is exactly what happened the first time this
    /// ran: the test hung until it was killed.
    /// </para>
    /// <para>
    /// A timeout as well, so a future change that reintroduces a stall fails
    /// the fact instead of hanging the suite.
    /// </para>
    /// </remarks>
    public static string Run(string binary, string input)
    {
        ProcessStartInfo start = new(binary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };

        using Process process = Process.Start(start)!;

        Task<string> output = process.StandardOutput.ReadToEndAsync();

        process.StandardInput.Write(input);
        process.StandardInput.Close();

        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"the C++ reference did not finish within 60 s. If it is blocked writing output, "
                + $"the parent is not draining it concurrently.");
        }

        return output.GetAwaiter().GetResult();
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for facts that compile a C++ reference,
/// skipping visibly where no compiler is installed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class NativeCompilerFactAttribute : FactAttribute
{
    /// <summary>Skips unless a C++ compiler is on PATH.</summary>
    public NativeCompilerFactAttribute()
    {
        if (NativeCompiler.Path64() is null)
        {
            Skip = "no g++ or clang++ on PATH, so the managed port cannot be compared against the "
                + "reference arithmetic here. The property facts beside it still run, but the "
                + "bit-exactness claim is unchecked in this tree.";
        }
    }
}
