using SourceSharp.MapTools.Phys;

using Xunit;

namespace SourceSharp.Tests.MapTools.Phys;

/// <summary>
/// The managed <see cref="SurfacePropertyTable"/> against the library's own
/// <c>IPhysicsSurfaceProps</c>, fed the same SDK Base 2013 scripts in the
/// same order: the indices are output, so they must agree exactly.
/// </summary>
[Collection(VPhysicsCollection.Name)]
[Trait("Tier", PinnedVPhysics.Tier)]
[VPhysicsJournal]
public class SurfacePropsNativeEqualityTests
{
    private static readonly string[] Files = ["scripts/surfaceproperties.txt", "scripts/surfaceproperties_hl2.txt"];

    // The library's database is a process-wide singleton that refuses a file
    // name twice, so it is parsed ONCE per child process and shared.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Snapshot? _snapshot;

    private readonly VPhysicsCookerFixture _fixture;

    public SurfacePropsNativeEqualityTests(VPhysicsCookerFixture fixture) => _fixture = fixture;

    private sealed record Snapshot(
        SurfacePropertyTable Managed,
        int NativeCount,
        string?[] NativeNames,
        int[] NativeIndexOfManagedName,
        SurfacePhysics[] NativePhysics);

    private async Task<Snapshot> TakeAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_snapshot is not null)
            {
                return _snapshot;
            }

            string root = Path.GetFullPath(Path.Combine(PinnedVPhysics.Directory, "..", "..", "hl2"));
            (string Name, string Text)[] files =
                [.. Files.Select(f => (f, File.ReadAllText(Path.Combine(root, f), System.Text.Encoding.Latin1)))];

            SurfacePropertyTable managed = new();
            foreach ((string name, string text) in files)
            {
                managed.ParseSurfaceData(name, text);
            }

            _snapshot = await _fixture.Cooker.RunAsync(s =>
            {
                ISurfacePropertySession native = s.SurfaceProps;
                foreach ((string name, string text) in files)
                {
                    native.ParseSurfaceData(name, text);
                }

                int count = native.Count;
                string?[] names = [.. Enumerable.Range(0, count).Select(native.GetPropName)];
                int[] indices = [.. Enumerable.Range(0, managed.Count).Select(i => native.GetSurfaceIndex(managed.GetPropName(i)!))];
                SurfacePhysics[] physics = [.. Enumerable.Range(0, count).Select(native.GetPhysicsProperties)];
                return new Snapshot(managed, count, names, indices, physics);
            });

            return _snapshot;
        }
        finally
        {
            Gate.Release();
        }
    }

    [VPhysicsNativeFact]
    public async Task TheCountsAgree()
    {
        Snapshot s = await TakeAsync();

        Assert.True(s.NativeCount > 50, s.NativeCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(s.NativeCount, s.Managed.Count);
    }

    [VPhysicsNativeFact]
    public async Task EveryIndexHasTheSameName()
    {
        Snapshot s = await TakeAsync();

        Assert.Equal(s.NativeNames, Enumerable.Range(0, s.Managed.Count).Select(s.Managed.GetPropName));
    }

    [VPhysicsNativeFact]
    public async Task EveryNameResolvesToTheSameIndex()
    {
        Snapshot s = await TakeAsync();

        Assert.Equal(
            Enumerable.Range(0, s.Managed.Count).Select(i => s.Managed.GetSurfaceIndex(s.Managed.GetPropName(i)!)),
            s.NativeIndexOfManagedName);
    }

    [VPhysicsNativeFact]
    public async Task EveryIndexHasTheSamePhysics()
    {
        Snapshot s = await TakeAsync();

        Assert.Equal(s.NativePhysics, Enumerable.Range(0, s.Managed.Count).Select(s.Managed.GetPhysicsProperties));
    }
}
