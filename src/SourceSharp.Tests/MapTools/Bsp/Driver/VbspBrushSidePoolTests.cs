//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Csg;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Options;

using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.Driver;

/// <summary>
/// The brush side pool over whole compiles: it must not change a byte, and it
/// must be empty when the compile ends however it ended.
/// </summary>
public sealed class VbspBrushSidePoolTests
{
    // ---- same bytes ----------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(4242)]
    public async Task ASeededBrushMapCompilesToTheSameBytesWithAndWithoutThePool(int seed)
    {
        VmfDocument document = SeededMap(seed);

        (byte[] off, _) = await CompileAsync(document, BrushSidePooling.Off);
        (byte[] pooled, _) = await CompileAsync(document, BrushSidePooling.Pooled);
        (byte[] checkedPool, long reused) = await CompileAsync(document, BrushSidePooling.Checked);

        Assert.Equal(off, pooled);
        Assert.Equal(off, checkedPool);
        Assert.True(reused > 0, "the map never recycled an array, so it proves nothing");
    }

    [Fact]
    public async Task ASeededBrushMapUnderStockComplianceIsUnchangedToo()
    {
        VbspOptions stock = VbspOptions.Default with { Compliance = ComplianceOptions.Stock };
        VmfDocument document = SeededMap(99);

        (byte[] off, _) = await CompileAsync(document, BrushSidePooling.Off, stock);
        (byte[] checkedPool, _) = await CompileAsync(document, BrushSidePooling.Checked, stock);

        Assert.Equal(off, checkedPool);
    }

    [RepoSourceFact("maps/ss_sandbox.vmf")]
    public async Task TheSandboxCompilesToTheSameBytesWithAndWithoutThePool()
    {
        VmfDocument document = await VmfDocument.ParseAsync(
            await File.ReadAllBytesAsync(RepoSourceFactAttribute.Find("maps/ss_sandbox.vmf")!));

        (byte[] off, _) = await CompileAsync(document, BrushSidePooling.Off);
        (byte[] checkedPool, long reused) = await CompileAsync(document, BrushSidePooling.Checked);

        Assert.Equal(off, checkedPool);
        Assert.True(reused > 1000, $"only {reused} arrays recycled on the sandbox");
    }

    // ---- released at the end -------------------------------------------------

    [Fact]
    public async Task AFinishedCompileLeavesThePoolEmpty()
    {
        (VbspCompilation compilation, RecordingExtension probe) = await PrepareAsync(SeededMap(3), failWith: null);

        await compilation.RunAsync(CancellationToken.None);

        Assert.True(probe.PooledWhenSeen > 0, "the pool was empty before the end; nothing to release");
        Assert.Equal(0, compilation.Build!.SidePool!.PooledArrays);
    }

    [Fact]
    public async Task AFailedCompileLeavesThePoolEmpty()
    {
        (VbspCompilation compilation, RecordingExtension probe) =
            await PrepareAsync(SeededMap(3), failWith: _ => throw new InvalidDataException("extension failed"));

        await Assert.ThrowsAsync<InvalidDataException>(() => compilation.RunAsync(CancellationToken.None));

        Assert.True(probe.PooledWhenSeen > 0);
        Assert.Equal(0, compilation.Build!.SidePool!.PooledArrays);
    }

    [Fact]
    public async Task ACancelledCompileLeavesThePoolEmpty()
    {
        using CancellationTokenSource cancel = new();
        (VbspCompilation compilation, RecordingExtension probe) = await PrepareAsync(
            SeededMap(3),
            failWith: token =>
            {
                cancel.Cancel();
                token.ThrowIfCancellationRequested();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compilation.RunAsync(cancel.Token));

        Assert.True(probe.PooledWhenSeen > 0);
        Assert.Equal(0, compilation.Build!.SidePool!.PooledArrays);
    }

    [Fact]
    public async Task ACompileThatFailsBeforeTheBuildExistsStillUnwinds()
    {
        VbspContext context = await UnitMap.ContextAsync();
        MapFile map = await MapFileLoader.LoadAsync(context, SeededMap(3));
        MapFileReader.TakeBounds(map);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        VbspCompilation compilation = new(map, context, []);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compilation.RunAsync(cancelled.Token));
        Assert.Null(compilation.Build);
    }

    // ---- helpers -------------------------------------------------------------

    private static async Task<(byte[] Bytes, long Reused)> CompileAsync(
        VmfDocument document,
        BrushSidePooling pooling,
        VbspOptions? options = null)
    {
        VbspContext context = await UnitMap.ContextAsync(options);
        context.BrushSidePooling = pooling;
        MapFile map = await MapFileLoader.LoadAsync(context, document);
        MapFileReader.TakeBounds(map);

        VbspCompilation compilation = new(map, context, Vbsp.DefaultExtensions(context));
        VbspResult result = await compilation.RunAsync(CancellationToken.None);

        using MemoryStream stream = new();
        await BspFile.SaveAsync(result.Bsp!, stream, BspWriteMode.Canonical, CancellationToken.None);

        // The portal file is part of the output as much as the BSP is.
        byte[] portals = result.Portals?.ToBytes(PortalLineEnding.Lf) ?? [];

        return ([.. stream.ToArray(), .. portals], compilation.Build!.SidePool?.Reused ?? 0);
    }

    private static async Task<(VbspCompilation, RecordingExtension)> PrepareAsync(
        VmfDocument document,
        Action<CancellationToken>? failWith)
    {
        VbspContext context = await UnitMap.ContextAsync();
        MapFile map = await MapFileLoader.LoadAsync(context, document);
        MapFileReader.TakeBounds(map);

        RecordingExtension probe = new(failWith);
        VbspCompilation compilation = new(map, context, [probe]);
        probe.Compilation = compilation;
        return (compilation, probe);
    }

    /// <summary>
    /// Looks at the pool once every tree has been built, and optionally fails
    /// or cancels the compile there.
    /// </summary>
    private sealed class RecordingExtension(Action<CancellationToken>? failWith) : IVbspExtension
    {
        public VbspCompilation? Compilation { get; set; }

        public int PooledWhenSeen { get; private set; }

        public ValueTask RunAsync(VbspExtensionPoint point, VbspStageContext stage, CancellationToken cancellationToken)
        {
            if (point == VbspExtensionPoint.DefaultCubemaps)
            {
                PooledWhenSeen = Compilation!.Build!.SidePool!.PooledArrays;
                failWith?.Invoke(cancellationToken);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A sealed room full of seeded clutter: overlapping boxes that CSG has to
    /// carve, chamfered boxes with a non-axial side, water, hint, detail and a
    /// brush entity, so every path that allocates and frees brushes runs.
    /// </summary>
    private static VmfDocument SeededMap(int seed)
    {
        Random random = new(seed);
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        document.Chunks.Add(world);

        int id = 10;
        const int Size = 1024;

        void Slab(VmfChunk into, string material, (float, float, float) mins, (float, float, float) maxs) =>
            into.Children.Add(UnitMap.Box(material, mins, maxs, id++));

        Slab(world, UnitMap.Plain, (-16, -16, -16), (Size + 16, Size + 16, 0));
        Slab(world, UnitMap.Plain, (-16, -16, Size), (Size + 16, Size + 16, Size + 16));
        Slab(world, UnitMap.Plain, (-16, -16, 0), (0, Size + 16, Size));
        Slab(world, UnitMap.Plain, (Size, -16, 0), (Size + 16, Size + 16, Size));
        Slab(world, UnitMap.Plain, (0, -16, 0), (Size, 0, Size));
        Slab(world, UnitMap.Plain, (0, Size, 0), (Size, Size + 16, Size));

        (int, int, int, int, int, int) RandomBox()
        {
            int x = random.Next(0, Size - 64) & ~7;
            int y = random.Next(0, Size - 64) & ~7;
            int z = random.Next(0, Size - 64) & ~7;
            int w = random.Next(16, 256) & ~7;
            int d = random.Next(16, 256) & ~7;
            int h = random.Next(16, 256) & ~7;
            return (x, y, z, Math.Min(x + w, Size), Math.Min(y + d, Size), Math.Min(z + h, Size));
        }

        for (int i = 0; i < 40; i++)
        {
            (int x0, int y0, int z0, int x1, int y1, int z1) = RandomBox();
            int kind = random.Next(10);
            string material = kind switch
            {
                0 => UnitMap.Water,
                1 => UnitMap.Hint,
                2 => UnitMap.NoDraw,
                _ => UnitMap.Plain,
            };

            VmfChunk solid = UnitMap.Box(material, (x0, y0, z0), (x1, y1, z1), id++);
            if (kind >= 7 && x1 - x0 > 16 && y1 - y0 > 16)
            {
                // Cut the +X+Y vertical edge off with a 45-degree plane.
                int c = Math.Min(x1 - x0, y1 - y0) / 2;
                UnitMap.Add(solid, material, (x1 - c, y1, z0), (x1, y1 - c, z0), (x1, y1 - c, z1));
            }

            world.Children.Add(solid);
        }

        VmfChunk detail = Entity(document, "func_detail");
        for (int i = 0; i < 12; i++)
        {
            (int x0, int y0, int z0, int x1, int y1, int z1) = RandomBox();
            Slab(detail, UnitMap.Plain, (x0, y0, z0), (x1, y1, z1));
        }

        VmfChunk brush = Entity(document, "func_brush");
        for (int i = 0; i < 4; i++)
        {
            (int x0, int y0, int z0, int x1, int y1, int z1) = RandomBox();
            Slab(brush, UnitMap.Plain, (x0, y0, z0), (x1, y1, z1));
        }

        VmfChunk start = Entity(document, "info_player_start");
        start.AddKey("origin", "8 8 8");

        return document;
    }

    private static VmfChunk Entity(VmfDocument document, string className)
    {
        VmfChunk e = new(MapFileLoader.EntityChunk);
        e.AddKey("id", (document.Chunks.Count + 100000).ToString(CultureInfo.InvariantCulture));
        e.AddKey("classname", className);
        document.Chunks.Add(e);
        return e;
    }
}
