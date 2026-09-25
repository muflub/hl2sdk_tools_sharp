using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// The shared, read-only state every <see cref="FaceLightJob"/> reads.
/// </summary>
/// <param name="Geometry">The map.</param>
/// <param name="Neighbours">PairEdges' output.</param>
/// <param name="Patches">
/// The patches. Jobs WRITE their own face's patches (see
/// <see cref="PatchLighting"/>), and only those.
/// </param>
/// <param name="Tree">The BSP.</param>
/// <param name="Settings">The switches.</param>
/// <param name="Gatherer">The lights and how to gather them.</param>
/// <param name="Displacements">
/// The displacements (lane 4e); null lights no displacement face and leaves it
/// flagged <see cref="FaceLight.IsDisplacementDeferred"/>.
/// </param>
public sealed record FaceLightContext(
    LightGeometry Geometry,
    FaceNeighbours Neighbours,
    PatchSet Patches,
    CompiledBspTree Tree,
    DirectLightingSettings Settings,
    DirectLightGatherer Gatherer,
    Displacement.VradDisplacements? Displacements = null);

/// <summary>
/// <c>BuildPatchLights</c> and <c>AddSampleToPatch</c> (<c>,
/// 2060</c>): a face's direct light handed to its radiosity patches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Race-free as stock wrote it, which the plan did not expect.</b> The plan
/// (§8, "correctness fixes") lists <c>AddSampleToPatch</c> as a shared-patch
/// accumulation needing per-worker merge. It is not one: the patch walk starts
/// at <c>g_FacePatches[facenum]</c>, and after <c>SubdividePatches</c> rebuilt
/// that list it holds ONLY that face's patches, whose
/// parents are the same face's too. Each face's job therefore writes a
/// disjoint set of patches, in sample order, and the sums are deterministic at
/// any thread count with no merge at all.
/// </para>
/// </remarks>
public static class PatchLighting
{
    /// <summary>
    /// <c>BuildPatchLights</c>.
    /// </summary>
    /// <param name="context">The lighting state.</param>
    /// <param name="faceNum">The face.</param>
    /// <param name="faceLight">Its facelight.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void BuildPatchLights(FaceLightContext context, int faceNum, FaceLight faceLight)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(faceLight);

        PatchSet patches = context.Patches;
        int bounces = context.Settings.Bounces;

        // The slot holding style 0; none means nothing to send.
        int k;
        for (k = 0; k < LightConstants.MaxLightmaps; k++)
        {
            if (faceLight.Styles[k] == 0)
            {
                break;
            }
        }

        if (k >= LightConstants.MaxLightmaps)
        {
            return;
        }

        LightingValue[] light = faceLight.LightFor(k, 0)!;
        for (int i = 0; i < faceLight.Samples.Length; i++)
        {
            AddSampleToPatch(patches, bounces, faceLight.Samples[i], light[i].Lighting, faceNum);
        }

        int head = patches.FacePatches[faceNum];
        if (head == Patch.Invalid)
        {
            return;
        }

        // Children first in the list, so one forward walk pushes
        // every level up.
        for (int p = head; p != Patch.Invalid; p = patches.At(p).Next)
        {
            ref Patch patch = ref patches.At(p);
            if (patch.Parent == Patch.Invalid)
            {
                continue;
            }

            ref Patch parent = ref patches.At(patch.Parent);
            parent.SampleArea += patch.SampleArea;
            parent.SampleLight += patch.SampleLight;
        }

        if (bounces > 0)
        {
            for (int p = head; p != Patch.Invalid; p = patches.At(p).Next)
            {
                ref Patch patch = ref patches.At(p);
                if (patch.SampleArea != 0f)
                {
                    // `1.0 / samplearea` is double, narrowed into a float.
                    float scale = (float)(1.0 / patch.SampleArea);
                    Vec3 v = patch.SampleLight * scale;
                    patch.TotalLight.Flat += v;
                    patch.DirectLight += v;
                }
            }
        }

        // A parent's light is the area-weighted blend of its two
        // children's -- overwriting what the loop above averaged into it.
        for (int p = head; p != Patch.Invalid; p = patches.At(p).Next)
        {
            ref Patch patch = ref patches.At(p);
            if (patch.Child1 == Patch.Invalid)
            {
                continue;
            }

            ref Patch child1 = ref patches.At(patch.Child1);
            ref Patch child2 = ref patches.At(patch.Child2);
            float s1 = child1.Area / (child1.Area + child2.Area);
            float s2 = child2.Area / (child1.Area + child2.Area);

            Vec3 total = child1.TotalLight.Flat * s1;
            total += child2.TotalLight.Flat * s2;
            patch.TotalLight.Flat = total;
            patch.DirectLight = total;
        }

        // 3301-3320. -ambient, into every normal of the style-0 slot.
        Vec3 ambient = context.Settings.Ambient;
        if (ambient.X != 0f || ambient.Y != 0f || ambient.Z != 0f)
        {
            for (int j = 0; j < LightConstants.MaxLightmaps && faceLight.Styles[j] != 255; j++)
            {
                if (faceLight.Styles[j] != 0)
                {
                    continue;
                }

                for (int n = 0; n < faceLight.NormalCount; n++)
                {
                    LightingValue[] values = faceLight.LightFor(j, n)!;
                    for (int i = 0; i < values.Length; i++)
                    {
                        values[i].Lighting += ambient;
                    }
                }

                break;
            }
        }
    }

    /// <summary>
    /// <c>AddSampleToPatch</c>: credits one sample's
    /// light to every leaf patch of the face whose bounds it roughly overlaps.
    /// </summary>
    /// <param name="patches">The patches.</param>
    /// <param name="bounces"><c>numbounce</c>; zero does nothing.</param>
    /// <param name="sample">The sample.</param>
    /// <param name="light">Its flat, style-0 light.</param>
    /// <param name="faceNum">Its face.</param>
    /// <exception cref="ArgumentNullException"><paramref name="patches"/> is null.</exception>
    /// <remarks>
    /// "Roughly": the test is the sample's position grown by half the square
    /// root of its AREA against the patch winding's bounds, so one sample can
    /// land in several patches (and in none -- "don't worry if some samples
    /// don't find a patch"). A sample averaging under 1 is dropped outright.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void AddSampleToPatch(PatchSet patches, int bounces, in LightSample sample, Vec3 light, int faceNum)
    {
        ArgumentNullException.ThrowIfNull(patches);

        if (bounces == 0)
        {
            return;
        }

        // VectorAvg is a float divide by 3.
        if ((light.X + light.Y + light.Z) / 3 < 1)
        {
            return;
        }

        int head = patches.FacePatches[faceNum];
        if (head == Patch.Invalid)
        {
            return;
        }

        // 2081. sqrt of a float, then a DOUBLE halving narrowed.
        float radius = (float)(MathF.Sqrt(sample.Area) / 2.0);

        for (int p = head; p != Patch.Invalid; p = patches.At(p).Next)
        {
            ref Patch patch = ref patches.At(p);
            if (patch.Sky || patch.HasChildren)
            {
                continue;
            }

            patches.Arena.Bounds(patch.Winding, out Vec3 mins, out Vec3 maxs);

            bool inside = true;
            for (int i = 0; i < 3 && inside; i++)
            {
                if (mins[i] > sample.Position[i] + radius || maxs[i] < sample.Position[i] - radius)
                {
                    inside = false;
                }
            }

            if (!inside)
            {
                continue;
            }

            patch.SampleArea += sample.Area;
            patch.SampleLight = new Vec3(
                patch.SampleLight.X + (sample.Area * light.X),
                patch.SampleLight.Y + (sample.Area * light.Y),
                patch.SampleLight.Z + (sample.Area * light.Z));
        }
    }
}
