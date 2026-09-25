using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapTools.Rad.Final;

/// <summary>What <see cref="FinalLightFace.Run"/> did with a face.</summary>
public enum FinalFaceOutcome
{
    /// <summary>
    /// Nothing to write: a <c>TEX_SPECIAL</c> face, or one with no styles
    /// (the two early returns at <c>, 670</c>).
    /// </summary>
    NotLit,

    /// <summary>Every style's luxels and average colour were written.</summary>
    Written,

    /// <summary>
    /// A displacement face, left unwritten because the context has no
    /// displacement radials (the hash was not built).
    /// </summary>
    DeferredDisplacement,
}

/// <summary>
/// Per-worker buffers for <see cref="FinalLightFace"/>: the two radial grids,
/// the per-luxel light arrays and the median lists.
/// </summary>
public sealed class FinalLightScratch
{
    /// <summary>The direct grid (<c>rad</c>).</summary>
    public LuxelRadial Radial { get; } = new();

    /// <summary>The bounce grid (<c>prad</c>).</summary>
    public LuxelRadial PatchRadial { get; } = new();

    internal LightingValue[] Lb { get; } = new LightingValue[BumpBasis.LightmapCount];

    internal LightingValue[] V { get; } = new LightingValue[BumpBasis.LightmapCount];

    internal LightingValue[] Sample { get; } = new LightingValue[BumpBasis.LightmapCount];

    internal List<float> Red { get; } = new(256);

    internal List<float> Green { get; } = new(256);

    internal List<float> Blue { get; } = new(256);

    /// <summary>Faces whose style slot had no light array (stock reads a null pointer there).</summary>
    public int MissingStyleSlots { get; internal set; }
}

/// <summary>
/// <c>FinalLightFace</c>: filters one face's samples
/// onto its luxel grid, adds the bounced light, clamps to the entity's
/// <c>_minlight</c>, applies macro textures, and writes the face's luxels and
/// per-style median colours as <c>ColorRGBExp32</c> into the lighting lump.
/// </summary>
/// <remarks>
/// <para>
/// Faces write disjoint byte ranges (the layout gives each face its own), and
/// every read is of state that is final before this stage starts, so faces can
/// run on any number of workers in any order and the bytes are the same.
/// </para>
/// <para>
/// The encoding is <see cref="StockLightColor.Encode"/>: the shipping,
/// bit-twiddling <c>VectorToColorRGBExp32</c>. The gamma tables
/// <c>MathLib_Init(2.2, 2.2, 0, 2.0)</c> builds are NOT on this path -- they
/// are read only by the decoders (<c>TexLightToLinear</c>), which leaf ambient
/// and prop lighting use to read this stage's output back.
/// </para>
/// </remarks>
public static class FinalLightFace
{
    /// <summary>Runs <c>FinalLightFace</c> for one face.</summary>
    /// <param name="context">The pass.</param>
    /// <param name="faceNum">The face.</param>
    /// <param name="lightData">The whole lighting lump, <see cref="LightmapLayout.LightDataSize"/> bytes.</param>
    /// <param name="scratch">This worker's buffers.</param>
    /// <returns>What was done.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static FinalFaceOutcome Run(
        FinalLightContext context,
        int faceNum,
        Span<byte> lightData,
        FinalLightScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scratch);

        RadWorld world = context.World;
        LightGeometry geometry = world.Geometry;
        ref readonly DFace face = ref geometry.Faces[faceNum];
        int flags = geometry.TexInfos[face.TexInfo].Flags;

        // TEX_SPECIAL.
        const int texSpecial = (int)(SurfaceFlags.Sky | SurfaceFlags.NoLight);
        if ((flags & texSpecial) != 0)
        {
            return FinalFaceOutcome.NotLit;
        }

        ReadOnlySpan<byte> styles = FaceStyles(context, faceNum);

        int lightstyles;
        for (lightstyles = 0; lightstyles < LightConstants.MaxLightmaps; lightstyles++)
        {
            if (styles[lightstyles] == 255)
            {
                break;
            }
        }

        if (lightstyles == 0)
        {
            return FinalFaceOutcome.NotLit;
        }

        bool disp = face.DispInfo != -1;
        Displacement.DispRadialContext? disps = context.Displacements;
        if (disp && disps is null)
        {
            return FinalFaceOutcome.DeferredDisplacement;
        }

        FaceLight fl = world.FaceLights[faceNum]
            ?? throw new InvalidOperationException($"face {faceNum} has styles but no facelight");

        // FloatForKey(face_entity) * 128, a float.
        int entity = world.Patches.FaceEntities[faceNum];
        float minlight = entity >= 0 && entity < world.Entities.Count
            ? EntityKeys.FloatForKey(world.Entities[entity], "_minlight") * 128
            : 0f;

        bool needsBumpmap = (flags & (int)SurfaceFlags.BumpLight) != 0;
        int bumpSampleCount = needsBumpmap ? BumpBasis.LightmapCount : 1;

        FaceLightInfo info = context.Info(faceNum);
        Span<LightingValue> lb = scratch.Lb.AsSpan(0, bumpSampleCount);
        Span<LightingValue> v = scratch.V.AsSpan(0, bumpSampleCount);
        int numLuxels = fl.Luxels.Length;
        int lightOfs = context.Layout.LightOffsets[faceNum];

        for (int k = 0; k < lightstyles; k++)
        {
            scratch.Red.Clear();
            scratch.Green.Clear();
            scratch.Blue.Clear();

            LuxelRadial? rad = null;
            LuxelRadial? prad = null;
            Displacement.DispRadialMap? drad = null;
            Displacement.DispRadialMap? dprad = null;

            // A displacement's radials come from the sample and
            // patch hashes(:1047) instead.
            if (!context.Fast)
            {
                if (!disp)
                {
                    rad = scratch.Radial;
                    rad.Reset(info);
                    BuildLuxelRadial(context, faceNum, k, rad, scratch);
                }
                else
                {
                    drad = Displacement.DispRadial.BuildLuxelRadial(disps!, faceNum, k, needsBumpmap);
                }
            }

            // Bounced light goes into style 0 only.
            if (context.Bounces > 0 && k == 0)
            {
                if (!disp)
                {
                    prad = scratch.PatchRadial;
                    prad.Reset(info);
                    BuildPatchRadial(context, faceNum, prad);
                }
                else
                {
                    dprad = Displacement.DispRadial.BuildPatchRadial(disps!, faceNum, needsBumpmap);
                }
            }

            int avgCount = 0;

            for (int j = 0; j < numLuxels; j++)
            {
                bool baseSampleOk = true;
                Vec3 luxel = fl.Luxels[j];

                if (rad is not null)
                {
                    baseSampleOk = rad.Sample(luxel, lb, context.RedErrors, context.Compliance);
                }
                else if (drad is not null)
                {
                    baseSampleOk = Displacement.DispRadial.SampleRadial(drad, j, lb, bumpSampleCount, patch: false);
                }
                else
                {
                    FastLuxel(context, fl, k, j, lb);
                }

                if (prad is not null || dprad is not null)
                {
                    // The return value is ignored.
                    if (prad is not null)
                    {
                        _ = prad.Sample(luxel, v, context.RedErrors, context.Compliance);
                    }
                    else
                    {
                        _ = Displacement.DispRadial.SampleRadial(dprad!, j, v, bumpSampleCount, patch: true);
                    }

                    for (int b = 0; b < bumpSampleCount; b++)
                    {
                        lb[b].AddLight(v[b]);
                    }
                }

                // A face with no samples is deliberately red.
                if (fl.Samples.Length == 0)
                {
                    for (int b = 0; b < bumpSampleCount; b++)
                    {
                        lb[b] = new LightingValue(new Vec3(255f, 0f, 0f), 0f);
                    }

                    baseSampleOk = false;
                }

                for (int b = 0; b < bumpSampleCount; b++)
                {
                    // 811-814. max(value, minlight): the value when greater, else minlight.
                    Vec3 c = lb[b].Lighting;
                    lb[b].Lighting = new Vec3(
                        c.X > minlight ? c.X : minlight,
                        c.Y > minlight ? c.Y : minlight,
                        c.Z > minlight ? c.Z : minlight);

                    // The macro texture and the median take the flat
                    // map of a luxel that had a sample, after minlight.
                    if (b == 0 && baseSampleOk)
                    {
                        ++avgCount;
                        Vec3 flat = lb[0].Lighting;
                        context.MacroTextures.Apply(faceNum, luxel, ref flat);
                        lb[0].Lighting = flat;

                        scratch.Red.Add(flat.X);
                        scratch.Green.Add(flat.Y);
                        scratch.Blue.Add(flat.Z);
                    }

                    int offset = lightOfs + ((((k * bumpSampleCount) + b) * numLuxels) + j) * 4;
                    Write(lightData, offset, StockLightColor.Encode(lb[b].Lighting));
                }
            }

            // Stored BEFORE lightofs, in reverse style order.
            Vec3 median = avgCount == 0
                ? Vec3.Zero
                : new Vec3(Median(scratch.Red), Median(scratch.Green), Median(scratch.Blue));
            Write(lightData, lightOfs - ((k + 1) * 4), StockLightColor.Encode(median));
        }

        return FinalFaceOutcome.Written;
    }

    /// <summary>
    /// <c>BuildLuxelRadial</c>: the face's own samples
    /// of one style, then each neighbour's samples of the SAME light style
    /// re-projected into this face's luxel space.
    /// </summary>
    /// <param name="context">The pass.</param>
    /// <param name="faceNum">The face.</param>
    /// <param name="styleIndex">The style slot on this face.</param>
    /// <param name="rad">The grid, already reset to the face.</param>
    /// <param name="scratch">Buffers; counts a missing style slot.</param>
    /// <remarks>
    /// A neighbour's style is matched by the light style NUMBER, not the slot
 /// And a neighbour without it contributes nothing. A
    /// neighbour's sample bounds are carried corner by corner: its luxel space
    /// to world, world to this face's luxel space.
    /// </remarks>
    public static void BuildLuxelRadial(
        FinalLightContext context,
        int faceNum,
        int styleIndex,
        LuxelRadial rad,
        FinalLightScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rad);
        ArgumentNullException.ThrowIfNull(scratch);

        RadWorld world = context.World;
        LightGeometry geometry = world.Geometry;
        Span<LightingValue> light = scratch.Sample;

        bool needsBumpmap = IsBumped(geometry, faceNum);
        FaceLight? fl = world.FaceLights[faceNum];

        if (fl is not null)
        {
            if (!HasStyle(fl, styleIndex))
            {
                scratch.MissingStyleSlots++;
            }
            else
            {
                for (int k = 0; k < fl.Samples.Length; k++)
                {
                    GatherSample(fl, styleIndex, k, needsBumpmap, light);
                    ref readonly LightSample sample = ref fl.Samples[k];
                    rad.AddDirect(
                        sample.Position, sample.MinS, sample.MinT, sample.MaxS, sample.MaxT,
                        light, needsBumpmap, needsBumpmap);
                }
            }
        }

        ReadOnlySpan<byte> styles = FaceStyles(context, faceNum);
        byte wanted = styles[styleIndex];

        foreach (int neighbour in world.Neighbours.Neighbours(faceNum))
        {
            FaceLight? nfl = world.FaceLights[neighbour];
            bool neighbourHasBumpmap = IsBumped(geometry, neighbour);

            ReadOnlySpan<byte> nstyles = FaceStyles(context, neighbour);
            int nstyle = 0;
            if (nstyles[nstyle] != wanted)
            {
                for (nstyle = 1; nstyle < LightConstants.MaxLightmaps; nstyle++)
                {
                    if (nstyles[nstyle] == wanted)
                    {
                        break;
                    }
                }

                if (nstyle >= LightConstants.MaxLightmaps)
                {
                    continue;
                }
            }

            if (nfl is null || nfl.Samples.Length == 0)
            {
                continue;
            }

            if (!HasStyle(nfl, nstyle))
            {
                scratch.MissingStyleSlots++;
                continue;
            }

            FaceLightInfo l = context.Info(neighbour);

            for (int k = 0; k < nfl.Samples.Length; k++)
            {
                GatherSample(nfl, nstyle, k, neighbourHasBumpmap, light);
                ref readonly LightSample sample = ref nfl.Samples[k];

                Vec3 tmp = l.LuxelToWorld(sample.MinS, sample.MinT);
                (float minS, float minT) = rad.Info.WorldToLuxel(tmp);
                tmp = l.LuxelToWorld(sample.MaxS, sample.MaxT);
                (float maxS, float maxT) = rad.Info.WorldToLuxel(tmp);

                rad.AddDirect(sample.Position, minS, minT, maxS, maxT, light, needsBumpmap, neighbourHasBumpmap);
            }
        }
    }

    /// <summary>
    /// <c>BuildPatchRadial</c>: every leaf patch of the
    /// face and of its neighbours, splatted with its total (bounced) light.
    /// </summary>
    /// <param name="context">The pass.</param>
    /// <param name="faceNum">The face.</param>
    /// <param name="rad">The grid, already reset to the face.</param>
    /// <remarks>
    /// A displacement patch's origin was moved onto the displaced surface, so
 /// stock uses the centre of its (flat) winding instead.
    /// </remarks>
    public static void BuildPatchRadial(FinalLightContext context, int faceNum, LuxelRadial rad)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rad);

        RadWorld world = context.World;
        bool needsBumpmap = IsBumped(world.Geometry, faceNum);

        AddFacePatches(context, faceNum, rad, needsBumpmap, needsBumpmap);

        foreach (int neighbour in world.Neighbours.Neighbours(faceNum))
        {
            AddFacePatches(context, neighbour, rad, needsBumpmap, NeighbourPatchBumpmap(context, faceNum, neighbour));
        }
    }

    /// <summary>
    /// Whether a neighbour's patches are treated as bumped in
    /// <see cref="BuildPatchRadial"/>.
    /// </summary>
    /// <param name="context">The pass.</param>
    /// <param name="faceNum">The face being filtered.</param>
    /// <param name="neighbour">The neighbour.</param>
    /// <returns>The <c>neighborHasBumpmap</c> argument.</returns>
    /// <remarks>
    /// Stock computes <c>neighborNeedsBumpmap</c> from <c>facenum</c> rather
    /// than the neighbour and then passes
 /// <c>needsBumpmap</c> twice anyway. So a bumped face
    /// takes an unbumped neighbour's zero bump-direction light at full weight,
    /// darkening its bump maps near the seam.
    /// <see cref="StockQuirk.PatchRadialNeighbourBumpFromSelf"/> keeps that;
    /// correct asks the neighbour.
    /// </remarks>
    public static bool NeighbourPatchBumpmap(FinalLightContext context, int faceNum, int neighbour)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Compliance.Emulates(StockQuirk.PatchRadialNeighbourBumpFromSelf)
            ? IsBumped(context.World.Geometry, faceNum)
            : IsBumped(context.World.Geometry, neighbour);
    }

    /// <summary>
    /// The <c>-fast</c> branch: each luxel takes
    /// the sample of the same index, unfiltered.
    /// </summary>
    /// <param name="context">The pass.</param>
    /// <param name="fl">The facelight.</param>
    /// <param name="styleIndex">The style slot being written.</param>
    /// <param name="luxel">The luxel.</param>
    /// <param name="lb">Receives the light.</param>
    /// <remarks>
    /// Stock reads <c>fl-&gt;light[0]</c> -- style slot ZERO -- whatever style
    /// it is writing, so every extra style of a <c>-fast</c> face is a copy of
    /// its base light. <see cref="StockQuirk.FastFinalLightStyleZero"/> keeps
    /// that; correct reads the style being written.
    /// </remarks>
    public static void FastLuxel(
        FinalLightContext context, FaceLight fl, int styleIndex, int luxel, Span<LightingValue> lb)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fl);

        int slot = context.Compliance.Emulates(StockQuirk.FastFinalLightStyleZero) ? 0 : styleIndex;
        for (int b = 0; b < lb.Length; b++)
        {
            LightingValue[]? values = fl.LightFor(slot, b);
            lb[b] = values is not null && luxel < values.Length ? values[luxel] : default;
        }
    }

    /// <summary>
 /// The median of the values, as the RB-tree walk takes
    /// it: element <c>count / 2</c> of the ascending order.
    /// </summary>
    /// <param name="values">The values; sorted in place.</param>
    /// <returns>The median.</returns>
    public static float Median(List<float> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        values.Sort();
        return values[values.Count >> 1];
    }

    private static void AddFacePatches(
        FinalLightContext context, int faceNum, LuxelRadial rad, bool hasBumpmap, bool neighbourHasBumpmap)
    {
        RadWorld world = context.World;
        PatchSet patches = world.Patches;
        bool dispFace = world.Geometry.IsValidDispFace(faceNum);
        Span<Vec3> light = stackalloc Vec3[BumpBasis.LightmapCount];

        for (int p = patches.FacePatches[faceNum]; p != Patch.Invalid; p = patches.At(p).Next)
        {
            ref Patch patch = ref patches.At(p);

            // Only leaf patches.
            if (patch.Child1 != Patch.Invalid)
            {
                continue;
            }

            (float minS, float minT, float maxS, float maxT) = PatchLightmapCoordRange(rad, patches, patch);

            Vec3 origin = dispFace ? patches.Arena.Center(patch.Winding) : patch.Origin;
            for (int b = 0; b < light.Length; b++)
            {
                light[b] = patch.TotalLight[b];
            }

            rad.AddBounced(origin, minS, minT, maxS, maxT, light, hasBumpmap, neighbourHasBumpmap);
        }
    }

    /// <summary><c>PatchLightmapCoordRange</c>.</summary>
    private static (float MinS, float MinT, float MaxS, float MaxT) PatchLightmapCoordRange(
        LuxelRadial rad, PatchSet patches, in Patch patch)
    {
        float minS = 1E30f;
        float minT = 1E30f;
        float maxS = -1E30f;
        float maxT = -1E30f;

        foreach (Vec3 point in patches.Arena.Points(patch.Winding))
        {
            (float s, float t) = rad.Info.WorldToLuxel(point);
            minS = minS < s ? minS : s;
            maxS = maxS > s ? maxS : s;
            minT = minT < t ? minT : t;
            maxT = maxT > t ? maxT : t;
        }

        return (minS, minT, maxS, maxT);
    }

    private static void GatherSample(FaceLight fl, int styleIndex, int sample, bool bumped, Span<LightingValue> light)
    {
        int count = bumped ? BumpBasis.LightmapCount : 1;
        for (int b = 0; b < count; b++)
        {
            LightingValue[]? values = fl.LightFor(styleIndex, b);
            light[b] = values is null ? default : values[sample];
        }
    }

    private static bool HasStyle(FaceLight fl, int styleIndex) => fl.LightFor(styleIndex, 0) is not null;

    private static bool IsBumped(LightGeometry geometry, int faceNum) =>
        (geometry.TexInfos[geometry.Faces[faceNum].TexInfo].Flags & (int)SurfaceFlags.BumpLight) != 0;

    private static ReadOnlySpan<byte> FaceStyles(FinalLightContext context, int faceNum) =>
        context.Layout.Styles.AsSpan(faceNum * LightConstants.MaxLightmaps, LightConstants.MaxLightmaps);

    private static void Write(Span<byte> data, int offset, ColorRgbExp32 c)
    {
        data[offset] = c.R;
        data[offset + 1] = c.G;
        data[offset + 2] = c.B;
        data[offset + 3] = unchecked((byte)c.Exponent);
    }
}
