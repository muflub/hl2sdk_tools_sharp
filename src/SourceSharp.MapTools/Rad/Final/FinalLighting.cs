using SourceSharp.MapTools.Parallel;

namespace SourceSharp.MapTools.Rad.Final;

/// <summary>Counts from one <see cref="FinalLighting"/> run.</summary>
/// <param name="WrittenFaces">Faces whose luxels were written.</param>
/// <param name="DeferredDisplacementFaces">
/// Displacement faces left unwritten because the context had no displacement
/// radials (the hashes were not built). Their bytes are zero.
/// </param>
/// <param name="OffGridSamples">
/// <c>SampleRadial</c> calls that fell off the grid -- stock's once-only
/// "SampleRadial: Punting, Waiting for fix" warning, counted.
/// </param>
/// <param name="MissingStyleSlots">
/// Reads of a style slot that had no light array, where stock dereferences a
/// null pointer (<c>-dlightmap</c> on a face with only style 0).
/// </param>
public sealed record FinalLightingStatistics(
    int WrittenFaces,
    int DeferredDisplacementFaces,
    int OffGridSamples,
    int MissingStyleSlots);

/// <summary>The lighting lump and its counts.</summary>
/// <param name="LightData">
/// <c>pdlightdata</c>: the whole <c>LUMP_LIGHTING</c> (or <c>_HDR</c>), sized by
/// the layout.
/// </param>
/// <param name="Statistics">The counts.</param>
public sealed record FinalLightingResult(byte[] LightData, FinalLightingStatistics Statistics);

/// <summary>
/// <c>RunThreadsOnIndividual(numfaces, true, FinalLightFace)</c>
/// Every face, on the work queue.
/// </summary>
public static class FinalLighting
{
    /// <summary>The stage name progress is reported under.</summary>
    public const string Stage = "FinalLightFace";

    /// <summary>Finishes every face of a pass.</summary>
    /// <param name="context">The pass.</param>
    /// <param name="parallelism">How many workers.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>The lighting lump.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// Faces are items, and each writes only its own range of the lump, so the
    /// result is byte-identical at any worker count. Partitioning is the
    /// queue's dynamic chunking; faces vary a lot in cost (luxels times styles
    /// times neighbours), which is what dynamic claiming is for.
    /// </remarks>
    public static async Task<FinalLightingResult> RunAsync(
        FinalLightContext context,
        CompileParallelism parallelism,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parallelism);
        cancellationToken.ThrowIfCancellationRequested();

        int faceCount = context.World.Geometry.Faces.Length;
        byte[] data = new byte[context.Layout.LightDataSize];

        using WorkQueue queue = new(parallelism);
        FinalLightScratch[] scratches = new FinalLightScratch[queue.Degree];

        FinalFaceOutcome[] outcomes = await queue.RunAsync(
            faceCount,
            (face, scratch, _) => FinalLightFace.Run(context, face, data, scratch),
            worker => scratches[worker] = new FinalLightScratch(),
            new WorkQueueOptions { Stage = Stage },
            cancellationToken).ConfigureAwait(false);

        int written = 0;
        int deferred = 0;
        foreach (FinalFaceOutcome outcome in outcomes)
        {
            if (outcome == FinalFaceOutcome.Written)
            {
                written++;
            }
            else if (outcome == FinalFaceOutcome.DeferredDisplacement)
            {
                deferred++;
            }
        }

        int offGrid = 0;
        int missing = 0;
        foreach (FinalLightScratch? s in scratches)
        {
            if (s is not null)
            {
                offGrid += s.Radial.OffGrid + s.PatchRadial.OffGrid;
                missing += s.MissingStyleSlots;
            }
        }

        return new FinalLightingResult(data, new FinalLightingStatistics(written, deferred, offGrid, missing));
    }
}
