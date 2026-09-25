namespace SourceSharp.MapTools.Io;

/// <summary>
/// Which failure a <see cref="FaultInjectingFileSystem"/> injects, and after how
/// many bytes.
/// </summary>
/// <remarks>
/// <para>
/// The four failures named here are the ones this project has to be able to
/// survive and cannot otherwise provoke: a disk that fills part-way through a
/// 300 MB <c>.bsp</c>, a read that comes up short because a file was truncated,
/// an <see cref="IOException"/> arriving mid-write, and a cancellation arriving
/// mid-read. Each of them is a one-line fixture here and a filled partition,
/// a corrupted file or a race otherwise.
/// </para>
/// <para>
/// A record with <c>init</c> properties rather than a builder, so a fact reads
/// as the sentence it is testing: <c>FaultPlan.DiskFullAfter(1024)</c>.
/// </para>
/// </remarks>
public sealed record FaultPlan
{
    /// <summary>The default message a full disk reports.</summary>
    public const string DiskFullMessage = "There is not enough space on the disk.";

    /// <summary>Injects nothing; every operation behaves as the inner file system does.</summary>
    public static FaultPlan None { get; } = new();

    /// <summary>
    /// Writes fail once this many bytes have been written, or never when null.
    /// </summary>
    public long? FailWriteAfterBytes { get; init; }

    /// <summary>The message the injected <see cref="IOException"/> carries.</summary>
    public string WriteFailureMessage { get; init; } = "the write failed";

    /// <summary>
    /// Reads report end-of-stream once this many bytes have been read, or never
    /// when null.
    /// </summary>
    /// <remarks>
    /// A TRUNCATION, not an error: the stream simply says there is no more. That
    /// is the shape a real short read takes, and the one that gets mistaken for
    /// a valid small file if a reader does not check how much it got.
    /// </remarks>
    public long? ShortReadAfterBytes { get; init; }

    /// <summary>
    /// Reads throw <see cref="OperationCanceledException"/> once this many bytes
    /// have been read, or never when null.
    /// </summary>
    public long? CancelReadAfterBytes { get; init; }

    /// <summary>A plan whose writes fail with a disk-full message.</summary>
    /// <param name="bytes">How many bytes are written before it fails.</param>
    /// <returns>The plan.</returns>
    public static FaultPlan DiskFullAfter(long bytes) => new()
    {
        FailWriteAfterBytes = Check(bytes),
        WriteFailureMessage = DiskFullMessage,
    };

    /// <summary>A plan whose writes fail with an <see cref="IOException"/>.</summary>
    /// <param name="bytes">How many bytes are written before it fails.</param>
    /// <returns>The plan.</returns>
    public static FaultPlan FailWriteAfter(long bytes) => new()
    {
        FailWriteAfterBytes = Check(bytes),
    };

    /// <summary>A plan whose reads stop short.</summary>
    /// <param name="bytes">How many bytes are read before end-of-stream.</param>
    /// <returns>The plan.</returns>
    public static FaultPlan ShortReadAfter(long bytes) => new()
    {
        ShortReadAfterBytes = Check(bytes),
    };

    /// <summary>A plan whose reads are cancelled part-way.</summary>
    /// <param name="bytes">How many bytes are read before the cancellation.</param>
    /// <returns>The plan.</returns>
    public static FaultPlan CancelReadAfter(long bytes) => new()
    {
        CancelReadAfterBytes = Check(bytes),
    };

    private static long Check(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        return bytes;
    }
}
