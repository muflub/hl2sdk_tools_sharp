//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Io;

namespace SourceSharp.Tests.MapTools.Bsp.SurfaceContent;

/// <summary>
/// Synthetic studio models for the unit tier: an MDL header with no geometry
/// (and optionally a keyvalue block), and a matching empty VVD.
/// </summary>
internal static class StudioFixture
{
    public static void AddModel(
        InMemoryFileSystem files,
        string path,
        int version = 48,
        int flags = StudioModelCheck.StaticPropFlag,
        string? keyValues = null,
        bool withVvd = true,
        int vvdChecksum = 1234)
    {
        int headerSize = Unsafe.SizeOf<StudioHeader>();
        byte[] kv = keyValues is null ? [] : Encoding.Latin1.GetBytes(keyValues + "\0");
        byte[] mdl = new byte[headerSize + kv.Length];

        StudioHeader header = default;
        header.Id = StudioIdents.Mdl;
        header.Version = version;
        header.Checksum = 1234;
        header.Flags = flags;
        header.Length = mdl.Length;
        string relative = path.StartsWith("models/", StringComparison.Ordinal) ? path["models/".Length..] : path;
        Encoding.Latin1.GetBytes(relative).CopyTo((Span<byte>)header.Name);
        if (kv.Length > 0)
        {
            header.KeyValueIndex = headerSize;
            header.KeyValueSize = kv.Length;
        }

        MemoryMarshal.Write(mdl, in header);
        kv.CopyTo(mdl, headerSize);
        files.AddFile(path, mdl);

        if (withVvd)
        {
            VertexFileHeader vvd = default;
            vvd.Id = StudioIdents.Vvd;
            vvd.Version = StudioIdents.VvdVersion;
            vvd.Checksum = vvdChecksum;
            vvd.NumLods = 1;
            vvd.VertexDataStart = Unsafe.SizeOf<VertexFileHeader>();
            byte[] bytes = new byte[Unsafe.SizeOf<VertexFileHeader>()];
            MemoryMarshal.Write(bytes, in vvd);
            files.AddFile(Path.ChangeExtension(path, ".vvd"), bytes);
        }
    }
}

/// <summary>
/// A collision seam that answers from a box: every model is the same
/// axis-aligned cube, and a leaf holds the prop when the cube's centre
/// satisfies every plane. Records every query.
/// </summary>
internal sealed class BoxCollision(float half) : IStaticPropCollision
{
    public List<(Vec3 Normal, float Dist)[]> Queries { get; } = [];

    private int _builds;

    // The emitter cooks and traces in parallel (plan 3p), so the records are locked.
    public int Builds => Volatile.Read(ref _builds);

    public ValueTask<IStaticPropHull?> BuildHullAsync(IReadOnlyList<Vec3[]> meshes, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _builds);
        return ValueTask.FromResult<IStaticPropHull?>(new Hull(this, half));
    }

    private sealed class Hull(BoxCollision owner, float half) : IStaticPropHull
    {
        public ValueTask<(Vec3 Mins, Vec3 Maxs)> GetAabbAsync(Vec3 origin, Vec3 angles, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult((new Vec3(origin.X - half, origin.Y - half, origin.Z - half),
                                  new Vec3(origin.X + half, origin.Y + half, origin.Z + half)));

        public ValueTask<bool> IntersectsAsync(ReadOnlyMemory<(Vec3 Normal, float Dist)> planes, Vec3 origin, Vec3 angles, CancellationToken cancellationToken = default)
        {
            lock (owner.Queries)
            {
                owner.Queries.Add(planes.ToArray());
            }

            bool inside = true;
            foreach ((Vec3 n, float d) in planes.Span)
            {
                inside &= Vec3.Dot(n, origin) <= d + half;
            }

            return ValueTask.FromResult(inside);
        }
    }
}
