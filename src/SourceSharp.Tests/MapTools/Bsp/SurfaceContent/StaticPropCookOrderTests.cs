//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using Xunit;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary>
/// The order the static prop emitter starts its hull cooks in: the most expensive first, so a
/// heavy hull never starts last and finishes alone; and, whatever order the cooks start and
/// finish in, the same lump.
/// </summary>
public class StaticPropCookOrderTests
{
    // Entity order: a one-box model, then ten boxes (240 vertices), then one 24-sided cylinder
    // (100 vertices). By cost the cylinder is the heaviest (100^3 against 10 * 24^3), although
    // it has fewer vertices than the ten boxes.
    private static readonly string[] Models = ["models/t/one_box.mdl", "models/t/ten_boxes.mdl", "models/t/cylinder.mdl"];

    [Fact]
    public void TheCostIsTheSumOfEachMeshsVertexCountCubed()
    {
        Assert.Equal(0, StaticPropEmitter.CookCost([]));
        Assert.Equal((10L * 24 * 24 * 24) + 1, StaticPropEmitter.CookCost([.. Enumerable.Repeat(new Vec3[24], 10), new Vec3[1]]));

        // Clamped, so a huge mesh cannot overflow the sum.
        Assert.Equal(1L << 48, StaticPropEmitter.CookCost([new Vec3[(1 << 16) + 5]]));
    }

    [Fact]
    public async Task AtOneThreadTheCooksStartMostExpensiveFirst()
    {
        // Red before the cost was cubed: by vertex total the ten boxes (240) went before the
        // cylinder (100).
        var collision = new RecordingCollision(reverseFinish: false);
        await EmitAsync(1, collision, pool: null);

        Assert.Equal([100, 240, 24], collision.Started);
    }

    [Fact]
    public async Task TheLumpDoesNotDependOnTheOrderTheCooksFinishIn()
    {
        // One thread, cooks finishing as they start; eight threads with the cooks finishing in
        // the reverse of the order they started; and a shared pool the same way.
        StaticPropLump serial = await EmitAsync(1, new RecordingCollision(reverseFinish: false), pool: null);
        StaticPropLump reversed = await EmitAsync(8, new RecordingCollision(reverseFinish: true), pool: null);
        using CompilePool pool = new(4);
        StaticPropLump pooled = await EmitAsync(4, new RecordingCollision(reverseFinish: true), pool);

        Assert.Equal(Models, serial.ModelNames);
        foreach (StaticPropLump other in new[] { reversed, pooled })
        {
            Assert.Equal(serial.ModelNames, other.ModelNames);
            Assert.Equal(serial.LeafEntries, other.LeafEntries);
            Assert.Equal(serial.Props.Select(Describe), other.Props.Select(Describe));
        }
    }

    private static async Task<StaticPropLump> EmitAsync(int degree, RecordingCollision collision, CompilePool? pool)
    {
        InMemoryFileSystem files = new();
        Add(files, "t/one_box.mdl", [Primitives.Box(0, new Vec3(-4, -4, 0), new Vec3(4, 4, 8))]);
        Add(files, "t/ten_boxes.mdl", [.. Enumerable.Range(0, 10).Select(i => Primitives.Box(0, new Vec3(i, 0, 0), new Vec3(i + 1, 1, 1)))]);
        Add(files, "t/cylinder.mdl", [Primitives.Cylinder(0, 8, 0, 16, 24)]);
        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        VbspContext context = new(VbspOptions.Default with { Compliance = ComplianceOptions.Correct }, new ContentFileSystem([mount]))
        {
            Parallelism = new CompileParallelism { MaxDegree = degree, Pool = pool },
        };

        List<MapEntity> entities = [.. Enumerable.Range(0, 6).Select(i => Prop(Models[i % 3], -50f + (20f * i)))];
        return await new StaticPropEmitter(context, collision).EmitAsync(entities, OneNode());
    }

    private static void Add(InMemoryFileSystem files, string name, IReadOnlyList<MeshSpec> meshes)
    {
        StudioModelSpec spec = new()
        {
            Name = name,
            Materials = ["m"],
            MaterialSearchPaths = ["models/t/"],
            Meshes = meshes,
        };

        foreach ((string path, byte[] bytes) in StudioModelWriter.Write(spec))
        {
            files.AddFile(path, bytes);
        }
    }

    private static string Describe(StaticProp p) =>
        $"{p.PropType} {p.Origin} {p.Angles} {p.FirstLeaf} {p.LeafCount} {p.Flags} {p.Skin} {p.Solid}";

    private static BspTreeView OneNode()
    {
        DNode node = default;
        node.PlaneNum = 0;
        node.Children[0] = -1;
        node.Children[1] = -2;
        return new([node], [new DPlane { Normal = new Vec3(1f, 0f, 0f), Dist = 0f }], [0, 0]);
    }

    private static MapEntity Prop(string model, float x)
    {
        MapEntity entity = new();
        entity.SetKeyValue("classname", "prop_static");
        entity.SetKeyValue("model", model);
        entity.SetKeyValue("origin", FormattableString.Invariant($"{x} 0 0"));
        entity.SetKeyValue("angles", "0 0 0");
        return entity;
    }

    // Records each cook's total vertex count as it starts. With reverseFinish, a cook that starts
    // earlier waits longer, so at more than one thread the cooks finish in the reverse of the
    // order they started in.
    private sealed class RecordingCollision(bool reverseFinish) : IStaticPropCollision
    {
        private readonly List<int> _started = [];
        private int _cooks;

        public List<int> Started
        {
            get
            {
                lock (_started)
                {
                    return [.. _started];
                }
            }
        }

        public async ValueTask<IStaticPropHull?> BuildHullAsync(IReadOnlyList<Vec3[]> meshes, CancellationToken cancellationToken = default)
        {
            lock (_started)
            {
                _started.Add(meshes.Sum(m => m.Length));
            }

            int order = Interlocked.Increment(ref _cooks);
            if (reverseFinish)
            {
                await Task.Delay(Math.Max(1, 60 - (20 * order)), cancellationToken);
            }

            return new BoxHull();
        }

        private sealed class BoxHull : IStaticPropHull
        {
            private const float Half = 8f;

            public ValueTask<(Vec3 Mins, Vec3 Maxs)> GetAabbAsync(Vec3 origin, Vec3 angles, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult((new Vec3(origin.X - Half, origin.Y - Half, origin.Z - Half),
                                      new Vec3(origin.X + Half, origin.Y + Half, origin.Z + Half)));

            public ValueTask<bool> IntersectsAsync(
                ReadOnlyMemory<(Vec3 Normal, float Dist)> planes, Vec3 origin, Vec3 angles, CancellationToken cancellationToken = default)
            {
                bool inside = true;
                foreach ((Vec3 n, float d) in planes.Span)
                {
                    inside &= Vec3.Dot(n, origin) <= d + Half;
                }

                return ValueTask.FromResult(inside);
            }
        }
    }
}
