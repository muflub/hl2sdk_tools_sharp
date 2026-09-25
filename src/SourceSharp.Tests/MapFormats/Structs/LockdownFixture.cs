using SourceSharp.MapFormats.Bsp;

namespace SourceSharp.Tests.MapFormats.Structs;

/// <summary>
/// The committed golden map, loaded once for a whole test class.
/// </summary>
/// <remarks>
/// A fixture rather than a static field. The map is 11 MB and dozens of facts
/// read it, so loading it per fact is wasteful -- but a mutable static holding
/// it would be the very thing this port's rules forbid, and a rule that the
/// tests break is not a rule.
/// </remarks>
public sealed class LockdownFixture
{
    /// <summary>Loads <c>dm_lockdown.bsp</c> from this worktree.</summary>
    public LockdownFixture()
    {
        using FileStream stream = File.OpenRead(GoldenBsp.Lockdown());
        Bsp = BspFile.LoadAsync(stream).GetAwaiter().GetResult();
    }

    /// <summary>The loaded map.</summary>
    public BspData Bsp { get; }
}
