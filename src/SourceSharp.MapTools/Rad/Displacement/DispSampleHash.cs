using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Displacement;

/// <summary>
/// A light sample by face and index: stock's <c>SampleHandle_t</c>
/// (<c>vrad.h:552</c>), which packs the face into the upper 16 bits.
/// </summary>
/// <param name="Face">The face.</param>
/// <param name="Sample">The sample's index in the face's facelight.</param>
public readonly record struct SampleHandle(int Face, int Sample);

/// <summary>
/// The two voxel hashes the displacement radial filter reads:
/// <c>InsertSamplesDataIntoHashTable</c> and
/// <c>InsertPatchSampleDataIntoHashTable</c> (<c>vraddisps.cpp:1425, 1489</c>),
/// built between bounce and <c>FinalLightFace</c> (<c>vrad.cpp:2076-2082</c>).
/// </summary>
/// <remarks>
/// <para>
/// The SAMPLE hash holds every light sample of every lit face -- brush faces
/// too, since a displacement's luxel blends its neighbours' samples -- at the
/// voxel of its position. The PATCH hash holds every leaf patch at the voxel of
/// its ORIGIN (<c>SAMPLEHASH_USE_AREA_PATCHES</c> is commented out,
/// <c>vrad.h:52</c>), and only when light bounces. Faces with
/// <c>TEX_SPECIAL</c> are skipped in both. Insertion is face order, then sample
/// (or patch-list) order; see <see cref="VoxelTable{T}"/> for how that order is
/// kept while building in parallel.
/// </para>
/// </remarks>
public sealed class DispSampleHash
{
    private DispSampleHash(VoxelTable<SampleHandle> samples, VoxelTable<int> patches)
    {
        Samples = samples;
        Patches = patches;
    }

    /// <summary><c>g_SampleHashTable</c>.</summary>
    public VoxelTable<SampleHandle> Samples { get; }

    /// <summary><c>g_PatchSampleHashTable</c>: patch indices.</summary>
    public VoxelTable<int> Patches { get; }

    /// <summary>Builds both hashes.</summary>
    /// <param name="geometry">The map.</param>
    /// <param name="faceLights">Every face's facelight (null where stock lit nothing).</param>
    /// <param name="patches">The patches, lit.</param>
    /// <param name="bounces"><c>numbounce</c>; the patch hash is empty when it is not positive.</param>
    /// <param name="queue">The workers.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The hashes.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<DispSampleHash> BuildAsync(
        LightGeometry geometry,
        IReadOnlyList<FaceLight?> faceLights,
        PatchSet patches,
        int bounces,
        WorkQueue queue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(faceLights);
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(queue);

        int faceCount = geometry.Faces.Length;

        // Sample entries: offsets by face in face order, then keys per face in parallel.
        int[] sampleOffset = new int[faceCount + 1];
        for (int f = 0; f < faceCount; f++)
        {
            FaceLight? fl = f < faceLights.Count ? faceLights[f] : null;
            int n = fl is not null && !IsSpecial(geometry, f) ? fl.Samples.Length : 0;
            sampleOffset[f + 1] = sampleOffset[f] + n;
        }

        VoxelKey[] sampleKeys = new VoxelKey[sampleOffset[faceCount]];
        SampleHandle[] sampleItems = new SampleHandle[sampleKeys.Length];
        await queue.RunAsync(
            faceCount,
            (f, _) =>
            {
                int at = sampleOffset[f];
                int n = sampleOffset[f + 1] - at;
                if (n == 0)
                {
                    return;
                }

                LightSample[] samples = faceLights[f]!.Samples;
                for (int i = 0; i < n; i++)
                {
                    MapFormats.Geometry.Vec3 p = samples[i].Position;
                    sampleKeys[at + i] = VoxelKey.Of(p.X, p.Y, p.Z);
                    sampleItems[at + i] = new SampleHandle(f, i);
                }
            },
            new WorkQueueOptions { Stage = "Build Patch/Sample Hash Table(s)" },
            cancellationToken).ConfigureAwait(false);

        VoxelTable<SampleHandle> sampleTable =
            await VoxelTable<SampleHandle>.BuildAsync(sampleKeys, sampleItems, queue, cancellationToken).ConfigureAwait(false);

        // :1492. "don't insert patch samples if we are not bouncing light".
        if (bounces <= 0)
        {
            return new DispSampleHash(sampleTable, VoxelTable<int>.CreateEmpty());
        }

        // Patch entries: leaf patches of each face, in face-list order. The
        // list walk is cheap and serial; the grouping is parallel.
        List<VoxelKey> patchKeys = [];
        List<int> patchItems = [];
        for (int f = 0; f < faceCount; f++)
        {
            if (IsSpecial(geometry, f))
            {
                continue;
            }

            for (int p = patches.FacePatches[f]; p != Patch.Invalid; p = patches.At(p).Next)
            {
                ref readonly Patch patch = ref patches.At(p);
                if (patch.HasChildren)
                {
                    continue;
                }

                patchKeys.Add(VoxelKey.Of(patch.Origin.X, patch.Origin.Y, patch.Origin.Z));
                patchItems.Add(p);
            }
        }

        VoxelTable<int> patchTable =
            await VoxelTable<int>.BuildAsync([.. patchKeys], [.. patchItems], queue, cancellationToken).ConfigureAwait(false);
        return new DispSampleHash(sampleTable, patchTable);
    }

    /// <summary><c>TEX_SPECIAL</c> (<c>vrad.h:432</c>): SURF_SKY | SURF_NOLIGHT.</summary>
    /// <param name="geometry">The map.</param>
    /// <param name="face">The face.</param>
    /// <returns>True when the face's texinfo carries either flag.</returns>
    public static bool IsSpecial(LightGeometry geometry, int face)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        const int texSpecial = (int)(SurfaceFlags.Sky | SurfaceFlags.NoLight);
        return (geometry.TexInfos[geometry.Faces[face].TexInfo].Flags & texSpecial) != 0;
    }
}
