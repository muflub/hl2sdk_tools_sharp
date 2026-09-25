using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Geometry;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapTools.Rad.Light;

/// <summary>
/// <c>BuildFacelights</c> for one face, as a small
/// state machine that runs in ROUNDS so its rays can be traced in batches.
/// </summary>
/// <remarks>
/// <para>
/// Stock lights a face in one call: sample the lights for every group of four
/// samples, then up to <c>extrapasses</c> supersampling passes per light style,
/// each pass deciding what to supersample from the previous one's answers.
/// Each of those steps asks for rays, and the batch tracer answers only
/// between steps -- so a face here is a sequence of rounds, and each round is
/// run twice by <see cref="RunRound"/>: collecting (rays recorded into
/// <see cref="Rays"/>, no side effects) and then, after the batch is traced,
/// replaying (the same calls, the real answers, the side effects).
/// </para>
/// <para>
/// Round 0 is the direct gather (<c>GatherSampleLightAt4Points</c>). Round
/// <c>p</c> &gt; 0 is supersampling pass <c>p</c> for every style still going.
/// Stock runs the styles one after another;
/// they share nothing but scratch, so running their passes side by side gives
/// the same numbers.
/// </para>
/// </remarks>
public sealed class FaceLightJob
{
    private readonly FaceLightContext _context;
    private readonly GatherOutput _output = new();
    private readonly SampleGroup _group = new();
    private readonly bool[] _laneNeeded = new bool[SampleGroup.Lanes];
    private readonly Vec3[] _flatBump = new Vec3[BumpBasis.Count];

    private readonly List<(int State, int Sample)> _items = [];
    private readonly List<int> _lightScratch = [];

    private StyleSupersample?[] _supersample = [];
    private int _round;
    private int _itemCount;
    private int _emitCursor;
    private int _resolveCursor;
    private LightRayLog? _rays;

    // Lane 4e: the displacement this face is the base of, when it is one.
    private Displacement.VradDispSurface? _disp;

    /// <summary>Creates the job for one face.</summary>
    /// <param name="context">The shared, read-only lighting state.</param>
    /// <param name="faceNum">The face.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public FaceLightJob(FaceLightContext context, int faceNum)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        FaceNum = faceNum;
    }

    /// <summary>The face.</summary>
    public int FaceNum { get; }

    /// <summary>The result, once <see cref="Prepare"/> has run; null for a face that needs no lightmap.</summary>
    public FaceLight? Result { get; private set; }

    /// <summary>The face's lighting frame, once prepared.</summary>
    public FaceLightInfo? Info { get; private set; }

    /// <summary>
    /// The job's own log for <see cref="RunRound"/>, created on first use; the
    /// batch driver brings its own pooled logs instead.
    /// </summary>
    public LightRayLog Rays => _rays ??= new LightRayLog { StockRays = _context.Geometry.StockEstimates, PadCalls = true };

    /// <summary>
    /// The light samples this job has, a cost estimate for scheduling; 0
    /// before <see cref="Prepare"/> or when there is nothing to light.
    /// </summary>
    public int SampleCount => Done || Result is null ? 0 : Result.Samples.Length;

    /// <summary>True when the job has no further rounds.</summary>
    public bool Done { get; private set; }

    /// <summary>Warnings, such as stock's "Too many light styles on a face".</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>How many rounds have been replayed.</summary>
    public int RoundsCompleted => _round;

    /// <summary>
    /// The per-face set-up of <c>BuildFacelights</c>:
    /// decides whether the face is lit at all, then builds its frame, samples
    /// and luxels.
    /// </summary>
    /// <param name="arena">Scratch windings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is null.</exception>
    public void Prepare(WindingArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);

        LightGeometry geometry = _context.Geometry;
        ref readonly DFace face = ref geometry.Faces[FaceNum];
        ref readonly TexInfo tex = ref geometry.TexInfos[face.TexInfo];

        // TEX_SPECIAL is SURF_SKY | SURF_NOLIGHT.
        const int texSpecial = (int)(SurfaceFlags.Sky | SurfaceFlags.NoLight);
        if ((tex.Flags & texSpecial) != 0)
        {
            Done = true;
            return;
        }

        int normalCount = (tex.Flags & (int)SurfaceFlags.BumpLight) != 0 ? BumpBasis.LightmapCount : 1;

        // A displacement face is sampled by the displacement manager
        // (StaticDispMgr->BuildDispSamples/Luxels).
        // Without one in the context it is left unlit and flagged, as before.
        if (face.DispInfo != -1)
        {
            _disp = _context.Displacements?.ForFace(geometry, FaceNum);
            if (_disp is null)
            {
                Result = new FaceLight(FaceNum, normalCount) { IsDisplacementDeferred = true };
                Done = true;
                return;
            }
        }

        // No patch means the face was degenerate.
        if (_context.Patches.FacePatches[FaceNum] == Patch.Invalid)
        {
            Done = true;
            return;
        }

        Vec3 modelOrigin = _context.Patches.FaceOffsets[FaceNum];
        FaceLightInfo info = FaceLightInfo.Build(
            geometry, _context.Neighbours, FaceNum, modelOrigin, _context.Settings.SmoothingThreshold);
        FaceLight faceLight = new(FaceNum, normalCount);

        if (_disp is not null)
        {
            Displacement.DispSampleBuilder.CalcPoints(
                _disp, face, tex, faceLight, _context.Settings.Fast, _context.Settings.Compliance);
        }
        else
        {
            FaceSampleBuilder.CalcPoints(
                geometry, info, faceLight, arena,
                _context.Settings.Fast, _context.Settings.CenterSamples, _context.Settings.Supersample);
        }

        // InitSampleInfo's flat-face normals. A displacement's are
        // replaced per sample in ComputeIlluminationPointAndNormals.
        if (info.IsFlat && normalCount > 1 && _disp is null)
        {
            (Vec3 texS, Vec3 texT) = TextureAxes(tex);
            BumpBasis.Build(texS, texT, info.FaceNormal, info.FaceNormal, _flatBump, _context.Settings.StockNormalise);
        }

        // Style 0 always exists.
        faceLight.Styles[0] = 0;
        faceLight.AllocateStyle(0);

        Info = info;
        Result = faceLight;
        StartRound();
    }

    /// <summary>
    /// Runs the current round in the two-call form: while
    /// <see cref="Rays"/> is collecting, emits every item of the round;
    /// once its answers are in, resolves them and moves
    /// on. The gather arithmetic runs once, in the collecting call.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The job was not prepared, or a replay consumed a different number of
    /// rays than its collection recorded.
    /// </exception>
    public void RunRound()
    {
        if (Done)
        {
            return;
        }

        if (Result is null || Info is null)
        {
            throw new InvalidOperationException("Prepare the job before running it");
        }

        LightRayLog rays = Rays;
        if (rays.Collecting)
        {
            _ = EmitItems(rays, int.MaxValue);
            return;
        }

        int round = _round;
        ResolveItems(rays, _emitCursor - _resolveCursor);
        if (!rays.ReplayComplete)
        {
            throw new InvalidOperationException(
                $"face {FaceNum} round {round}: the replay did not ask for the rays its collection recorded");
        }

        rays.Reset();
    }

    /// <summary>
    /// <c>ComputeIlluminationPointAndNormalsSSE</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ComputeIlluminationPointAndNormals(ReadOnlySpan<Vec3> pos, int count, ReadOnlySpan<Vec3> sampleNormals = default)
    {
        FaceLightInfo l = Info!;
        FaceLight fl = Result!;
        SampleGroup g = _group;
        g.Count = count;
        g.NormalCount = fl.NormalCount;

        // One unit off the face, "so that light sampling will not
        // be affected by a bug where raycasts will intersect with the face
        // being lit".
        for (int i = 0; i < SampleGroup.Lanes; i++)
        {
            g.Points[i] = pos[i] + l.FaceNormal;
        }

        if (_disp is not null)
        {
            // A displacement's point normal is the
            // sample's own blended normal, and its bump basis is always rebuilt
            // from it (computeNormals is true for a displacement).
            (Vec3 texS, Vec3 texT) = TextureAxes(_context.Geometry.TexInfos[_context.Geometry.Faces[FaceNum].TexInfo]);
            Span<Vec3> bump = stackalloc Vec3[BumpBasis.Count];
            for (int i = 0; i < SampleGroup.Lanes; i++)
            {
                g.Normal(0, i) = sampleNormals[i];
                if (fl.NormalCount > 1)
                {
                    BumpBasis.Build(texS, texT, l.FaceNormal, sampleNormals[i], bump, _context.Settings.StockNormalise);
                    for (int b = 0; b < BumpBasis.Count; b++)
                    {
                        g.Normal(b + 1, i) = bump[b];
                    }
                }
            }
        }
        else if (l.IsFlat)
        {
            for (int i = 0; i < SampleGroup.Lanes; i++)
            {
                g.Normal(0, i) = l.FaceNormal;
                for (int b = 0; b < BumpBasis.Count && fl.NormalCount > 1; b++)
                {
                    g.Normal(b + 1, i) = _flatBump[b];
                }
            }
        }
        else
        {
            // The phong normal is taken at the sample position with
            // the model origin removed, in dvertex space.
            Span<Vec3> spots = stackalloc Vec3[SampleGroup.Lanes];
            Span<Vec3> normals = stackalloc Vec3[SampleGroup.Lanes];
            for (int i = 0; i < SampleGroup.Lanes; i++)
            {
                spots[i] = pos[i] - l.ModelOrigin;
            }

            PhongNormals.ComputeFour(
                _context.Geometry, _context.Neighbours, _context.Patches.Centroids, FaceNum,
                spots, normals, _context.Settings.SmoothingThreshold);

            (Vec3 texS, Vec3 texT) = TextureAxes(_context.Geometry.TexInfos[_context.Geometry.Faces[FaceNum].TexInfo]);
            Span<Vec3> bump = stackalloc Vec3[BumpBasis.Count];
            for (int i = 0; i < SampleGroup.Lanes; i++)
            {
                g.Normal(0, i) = normals[i];
                if (fl.NormalCount > 1)
                {
                    BumpBasis.Build(texS, texT, l.FaceNormal, normals[i], bump, _context.Settings.StockNormalise);
                    for (int b = 0; b < BumpBasis.Count; b++)
                    {
                        g.Normal(b + 1, i) = bump[b];
                    }
                }
            }
        }

        // The cluster of the sample position, not of the offset point.
        for (int i = 0; i < SampleGroup.Lanes; i++)
        {
            g.Clusters[i] = _context.Tree.ClusterFromPoint(pos[i]);
        }
    }

    /// <summary>
    /// The emit half of one group of the direct gather:
    /// <c>GatherSampleLightAt4Points</c> up to the
    /// rays. Tape: count, lane 0's point, then per light that survives the PVS
    /// test its index, its lane mask and the gatherer's record; -1 ends it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EmitGroup(int grp, LightRayLog rays)
    {
        FaceLight fl = Result!;
        int numSamples = fl.Samples.Length;
        int nSample = 4 * grp;
        int count = Math.Min(4, numSamples - nSample);
        Span<Vec3> positions = stackalloc Vec3[SampleGroup.Lanes];
        Span<Vec3> sampleNormals = stackalloc Vec3[SampleGroup.Lanes];

        for (int i = 0; i < SampleGroup.Lanes; i++)
        {
            positions[i] = fl.Samples[nSample + Math.Min(i, count - 1)].Position;
            sampleNormals[i] = fl.Samples[nSample + Math.Min(i, count - 1)].Normal;
        }

        ComputeIlluminationPointAndNormals(positions, count, sampleNormals);

        // A smoothed face's samples keep their phong normal. It
        // depends on no ray, so it is written here, once.
        if (!Info!.IsFlat)
        {
            for (int i = 0; i < count; i++)
            {
                fl.Samples[nSample + i].Normal = _group.Normal(0, i);
            }
        }

        GatherTape tape = rays.Tape;
        tape.Int(count);
        tape.Vector(_group.Points[0]);

        IReadOnlyList<DirectLight> lights = _context.Gatherer.Lights;

        // Only the lights some lane's cluster can see: every other light
        // fails the PVS test on all four lanes and is skipped below anyway.
        foreach (int li in _context.Gatherer.LightsReaching(_group.Clusters, _lightScratch))
        {
            DirectLight dl = lights[li];

            int mask = 0;
            for (int s = 0; s < SampleGroup.Lanes; s++)
            {
                _laneNeeded[s] = s < count && LightVisibility.PvsCheck(dl.Pvs, _group.Clusters[s]);
                mask |= _laneNeeded[s] ? 1 << s : 0;
            }

            if (mask == 0)
            {
                continue;
            }

            tape.Int(li);
            tape.Int(mask);
            _context.Gatherer.Emit(dl, _group, _laneNeeded, rays, _output, GatherFlags.None, 0.0f);
        }

        tape.Int(-1);
    }

    /// <summary>The resolve half of <see cref="EmitGroup"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ResolveGroup(int grp, LightRayLog rays)
    {
        FaceLight fl = Result!;
        int normals = fl.NormalCount;
        int sampleIdx = 4 * grp;
        GatherTape tape = rays.Tape;
        int numSamples = tape.ReadInt();
        Vec3 point0 = tape.ReadVector();
        Span<float> fxdot = stackalloc float[SampleGroup.Lanes * BumpBasis.LightmapCount];
        IReadOnlyList<DirectLight> lights = _context.Gatherer.Lights;

        for (int li = tape.ReadInt(); li >= 0; li = tape.ReadInt())
        {
            int mask = tape.ReadInt();
            DirectLight dl = lights[li];
            _context.Gatherer.Resolve(rays, _output);

            // 2515-2526. (dot * mask) * falloff.
            bool skipLight = true;
            for (int b = 0; b < normals; b++)
            {
                for (int lane = 0; lane < SampleGroup.Lanes; lane++)
                {
                    int k = (b * SampleGroup.Lanes) + lane;
                    float masked = (mask & (1 << lane)) != 0 ? _output.Dot[k] : 0.0f;
                    fxdot[k] = masked * _output.Falloff[lane];
                    skipLight &= fxdot[k] == 0.0f;
                }
            }

            if (skipLight)
            {
                continue;
            }

            int styleIndex = fl.FindOrAllocateStyle(dl.Style);
            if (styleIndex < 0)
            {
                if (Warnings.Count == 0)
                {
                    Warnings.Add(
                        $"WARNING: Too many light styles on a face at ({point0.X:F6}, {point0.Y:F6}, {point0.Z:F6})");
                }

                continue;
            }

            for (int n = 0; n < normals; n++)
            {
                LightingValue[] target = fl.LightFor(styleIndex, n)!;
                for (int i = 0; i < numSamples; i++)
                {
                    target[sampleIdx + i].AddLight(
                        fxdot[(n * SampleGroup.Lanes) + i], dl.Intensity, _output.SunAmount[i]);
                }
            }
        }
    }

    /// <summary>
    /// The emit half of <c>ResampleLightAt4Points</c>
    /// for the current group. Tape: per light, index and lane mask then the
    /// gatherer's record; -1 ends it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EmitResample(int styleIndex, bool ambientOnly, LightRayLog rays)
    {
        FaceLight fl = Result!;
        GatherTape tape = rays.Tape;
        IReadOnlyList<DirectLight> lights = _context.Gatherer.Lights;

        foreach (int li in _context.Gatherer.LightsReaching(_group.Clusters, _lightScratch))
        {
            DirectLight dl = lights[li];

            if (ambientOnly && dl.Type != EmitType.SkyAmbient)
            {
                continue;
            }

            if (!ambientOnly && dl.Type == EmitType.SkyAmbient)
            {
                continue;
            }

            if (dl.Style != fl.Styles[styleIndex])
            {
                continue;
            }

            int mask = 0;
            for (int s = 0; s < SampleGroup.Lanes; s++)
            {
                _laneNeeded[s] = LightVisibility.PvsCheck(dl.Pvs, _group.Clusters[s]);
                mask |= _laneNeeded[s] ? 1 << s : 0;
            }

            if (mask == 0)
            {
                continue;
            }

            tape.Int(li);
            tape.Int(mask);
            _context.Gatherer.Emit(dl, _group, _laneNeeded, rays, _output, GatherFlags.None, 0.0f);
        }

        tape.Int(-1);
    }

    /// <summary>
    /// The resolve half of <see cref="EmitResample"/>: the light of one style
    /// at four supersample points, into <paramref name="result"/> indexed
    /// <c>[lane * LightmapCount + normal]</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ResolveResample(LightRayLog rays, Span<LightingValue> result)
    {
        FaceLight fl = Result!;
        int normals = fl.NormalCount;
        GatherTape tape = rays.Tape;
        IReadOnlyList<DirectLight> lights = _context.Gatherer.Lights;
        result.Clear();

        for (int li = tape.ReadInt(); li >= 0; li = tape.ReadInt())
        {
            int mask = tape.ReadInt();
            DirectLight dl = lights[li];
            _context.Gatherer.Resolve(rays, _output);

            // 2616-2637. falloff * dot * mask, and no skip test.
            for (int i = 0; i < SampleGroup.Lanes; i++)
            {
                for (int n = 0; n < normals; n++)
                {
                    float f = _output.Falloff[i] * _output.Dot[(n * SampleGroup.Lanes) + i];
                    f = (mask & (1 << i)) != 0 ? f : 0.0f;
                    result[(i * BumpBasis.LightmapCount) + n].AddLight(f, dl.Intensity, _output.SunAmount[i]);
                }
            }
        }
    }

    // How many items the current round has: groups in round 0, (style,
    // sample) pairs in a supersampling pass.
    private void StartRound()
    {
        while (!Done)
        {
            _emitCursor = 0;
            _resolveCursor = 0;
            if (_round == 0)
            {
                int numSamples = Result!.Samples.Length;

                _itemCount = (numSamples & 0x3) != 0 ? (numSamples / 4) + 1 : numSamples / 4;
            }
            else
            {
                _items.Clear();
                for (int s = 0; s < _supersample.Length; s++)
                {
                    StyleSupersample? state = _supersample[s];
                    if (state is null)
                    {
                        continue;
                    }

                    foreach (int i in state.Selected)
                    {
                        _items.Add((s, i));
                    }
                }

                _itemCount = _items.Count;
            }

            if (_itemCount > 0)
            {
                return;
            }

            // A round with nothing in it completes at once.
            _round++;
            AdvanceAfterRound();
        }
    }

    /// <summary>
    /// Emits the next items of the current round -- at least one, then more
    /// while <paramref name="rays"/> holds fewer than <paramref name="budget"/>
    /// rays and its tape fewer than <paramref name="tapeBudget"/> words --
    /// never past the round's end, whose successor depends on answers.
    /// </summary>
    /// <param name="rays">Where the rays and the tape go.</param>
    /// <param name="budget">Stop once the log holds this many rays.</param>
    /// <param name="tapeBudget">Stop once the log's tape holds this many words.</param>
    /// <returns>How many items were emitted; 0 when the job is done or waiting.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal int EmitItems(LightRayLog rays, int budget, int tapeBudget = int.MaxValue)
    {
        int emitted = 0;
        while (!Done && _emitCursor < _itemCount
               && (emitted == 0 || (rays.TotalCount < budget && rays.Tape.Length < tapeBudget)))
        {
            if (_round == 0)
            {
                EmitGroup(_emitCursor, rays);
            }
            else
            {
                (int s, int i) = _items[_emitCursor];
                EmitSupersampleItem(_supersample[s]!, i, rays);
            }

            // Every item starts a packet of its own (LightRayLog remarks).
            rays.EndItem();
            _emitCursor++;
            emitted++;
        }

        return emitted;
    }

    /// <summary>
    /// Resolves the next <paramref name="count"/> emitted items, in emission
    /// order, and moves to the next round when this one is complete.
    /// </summary>
    /// <param name="rays">The tape and the answers, positioned at the first item.</param>
    /// <param name="count">How many items to resolve.</param>
    /// <exception cref="InvalidOperationException">More items are asked for than were emitted.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void ResolveItems(LightRayLog rays, int count)
    {
        if (_resolveCursor + count > _emitCursor)
        {
            throw new InvalidOperationException($"face {FaceNum}: resolving items that were never emitted");
        }

        for (int k = 0; k < count; k++)
        {
            if (_round == 0)
            {
                ResolveGroup(_resolveCursor, rays);
            }
            else
            {
                (int s, int i) = _items[_resolveCursor];
                ResolveSupersampleItem(_supersample[s]!, i, rays);
            }

            rays.SkipItemPadding();
            _resolveCursor++;
        }

        if (_resolveCursor == _itemCount)
        {
            _round++;
            AdvanceAfterRound();
            StartRound();
        }
    }

    private void AdvanceAfterRound()
    {
        FaceLight fl = Result!;

        if (_round == 1)
        {
            // End of the direct gather: supersample every style
            // that exists, unless -noextra -- and never a displacement
            // ("get rid of the -extra functionality on displacement surfaces").
            if (!_context.Settings.Supersample || _disp is not null)
            {
                Finish();
                return;
            }

            int styles = 0;
            while (styles < LightConstants.MaxLightmaps && fl.Styles[styles] != 255)
            {
                styles++;
            }

            _supersample = new StyleSupersample?[styles];
            for (int s = 0; s < styles; s++)
            {
                StyleSupersample state = new(fl, Info!, s, SupersampleReadsUninitialised(_context.Settings.Compliance));
                state.ComputeSampleIntensities();
                _supersample[s] = state;
            }
        }
        else
        {
            // A supersampling pass finished: advance the pass counter.
            foreach (StyleSupersample? state in _supersample)
            {
                if (state is not null && state.Selected.Count > 0)
                {
                    state.Pass++;
                }
            }
        }

        // 2883 `while (do_anotherpass && pass <= extrapasses)`: choose what
        // the next pass supersamples, from the answers just resolved.
        bool any = false;
        foreach (StyleSupersample? state in _supersample)
        {
            if (state is null)
            {
                continue;
            }

            if (!state.AnotherPass || state.Pass > _context.Settings.ExtraPasses)
            {
                state.Selected.Clear();
                continue;
            }

            state.SelectForPass();
            any |= state.Selected.Count > 0;
        }

        if (!any)
        {
            Finish();
        }
    }

    // One supersampled sample of one style: ambient, then direct.
    private void EmitSupersampleItem(StyleSupersample state, int i, LightRayLog rays)
    {
        EmitSupersamplePoint(i, state.StyleIndex, ambientOnly: true, rays);
        EmitSupersamplePoint(i, state.StyleIndex, ambientOnly: false, rays);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ResolveSupersampleItem(StyleSupersample state, int i, LightRayLog rays)
    {
        Span<LightingValue> ambientLight = stackalloc LightingValue[BumpBasis.LightmapCount];
        Span<LightingValue> directLight = stackalloc LightingValue[BumpBasis.LightmapCount];
        FaceLight fl = Result!;

        int ambientCount = ResolveSupersamplePoint(ambientLight, ambientOnly: true, rays);
        int directCount = ResolveSupersamplePoint(directLight, ambientOnly: false, rays);

        if (_context.Settings.DebugExtra)
        {
            int pass = state.Pass;
            state.Visualize[i] = new Vec3((pass & 1) * 255, (pass & 2) * 128, (pass & 4) * 64);
        }

        if (ambientCount > 0 && directCount > 0)
        {
            for (int n = 0; n < fl.NormalCount; n++)
            {
                ref LightingValue v = ref fl.LightFor(state.StyleIndex, n)![i];
                v = default;
                v.AddWeighted(directLight[n], 1.0f / directCount);
                v.AddWeighted(ambientLight[n], 1.0f / ambientCount);
            }

            state.ComputeLuxelIntensity(i);
        }
    }

    // Tape per row: -1 when all four points are outside the sample's
    // winding (and, for ambient, the end of the point), else the row's
    // invalid bits followed by its resample record.
    private const int RowOutside = -1;

    /// <summary>
    /// The emit half of <c>SupersampleLightAtPoint</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EmitSupersamplePoint(int sampleIndex, int styleIndex, bool ambientOnly, LightRayLog rays)
    {
        FaceLightInfo l = Info!;
        FaceLight fl = Result!;
        ref readonly LightSample sample = ref fl.Samples[sampleIndex];
        GatherTape tape = rays.Tape;

        (float originS, float originT) = l.WorldToLuxel(sample.Position);

        // 2694-2697. 4x4 for direct light, 2x2 for ambient; csshift is a
        // double division narrowed.
        float sampleWidth = ambientOnly ? 2 : 4;
        float cscale = 1.0f / sampleWidth;
        float csshift = (float)(-((sampleWidth - 1) * cscale) / 2.0);

        Span<Vec3> positions = stackalloc Vec3[SampleGroup.Lanes];
        ReadOnlySpan<Vec3> winding = sample.WindingCount > 0
            ? fl.SampleWindingPoints.AsSpan(sample.WindingOffset, sample.WindingCount)
            : [];

        int rows = ambientOnly ? 1 : 4;
        Span<float> aRow = stackalloc float[4];
        for (int c = 0; c < 4; c++)
        {
            aRow[c] = csshift + (c * cscale);
        }

        for (int s = 0; s < rows; s++)
        {
            for (int lane = 0; lane < SampleGroup.Lanes; lane++)
            {
                float cs;
                float ct;
                if (ambientOnly)
                {
                    // LoadAndSwizzle of four offsets.
                    cs = originS + (lane < 2 ? csshift : csshift + cscale);
                    ct = originT + ((lane & 1) == 0 ? csshift : csshift + cscale);
                }
                else
                {
                    cs = originS + aRow[s];
                    ct = originT + aRow[lane];
                }

                positions[lane] = l.LuxelToWorld(cs, ct);
            }

            // Only a partial sample has a winding to test against.
            int invalidBits = 0;
            if (!winding.IsEmpty && !PointsInWinding(positions, winding, out invalidBits))
            {
                tape.Int(RowOutside);
                if (ambientOnly)
                {
                    return;
                }

                continue;
            }

            tape.Int(invalidBits);
            ComputeSupersampleIllumination(positions, sample.Normal);
            EmitResample(styleIndex, ambientOnly, rays);
        }
    }

    /// <summary>The resolve half of <see cref="EmitSupersamplePoint"/>.</summary>
    /// <returns>How many subsamples counted.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int ResolveSupersamplePoint(Span<LightingValue> light, bool ambientOnly, LightRayLog rays)
    {
        int normals = Result!.NormalCount;
        GatherTape tape = rays.Tape;
        light[..normals].Clear();
        int subsampleCount = 0;
        Span<LightingValue> result = stackalloc LightingValue[SampleGroup.Lanes * BumpBasis.LightmapCount];

        int rows = ambientOnly ? 1 : 4;
        for (int s = 0; s < rows; s++)
        {
            int invalidBits = tape.ReadInt();
            if (invalidBits == RowOutside)
            {
                if (ambientOnly)
                {
                    return 0;
                }

                continue;
            }

            ResolveResample(rays, result);
            for (int i = 0; i < SampleGroup.Lanes; i++)
            {
                if (((invalidBits >> i) & 1) == 0)
                {
                    for (int n = 0; n < normals; n++)
                    {
                        light[n].AddLight(result[(i * BumpBasis.LightmapCount) + n]);
                    }

                    subsampleCount++;
                }
            }
        }

        return subsampleCount;
    }

    private void ComputeSupersampleIllumination(ReadOnlySpan<Vec3> positions, Vec3 sampleNormal)
    {
        // The supersample normal matters only to displacements, which never reach here.
        _ = sampleNormal;
        ComputeIlluminationPointAndNormals(positions, SampleGroup.Lanes);
    }

    /// <summary>
    /// <c>PointsInWinding</c>: which of four points
    /// lie inside a convex world-space winding.
    /// </summary>
    /// <param name="points">Four points.</param>
    /// <param name="winding">The winding.</param>
    /// <param name="invalidBits">Bit i set when point i is outside.</param>
    /// <returns>False when all four are outside.</returns>
    /// <remarks>
    /// Each edge's cross product with the point is compared in SIGN against the
    /// first edge's. Stock normalises the crosses with an estimate, which
    /// cannot change a sign; this normalises exactly. Stock also never
    /// initialises <c>invalidMask</c> before OR-ing
    /// into it -- whatever the stack held. Here it starts at zero, which is the
    /// only defined reading.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool PointsInWinding(ReadOnlySpan<Vec3> points, ReadOnlySpan<Vec3> winding, out int invalidBits)
    {
        invalidBits = 0;
        int n = winding.Length;
        Span<Vec3> testCross = stackalloc Vec3[SampleGroup.Lanes];
        Span<bool> invalid = stackalloc bool[SampleGroup.Lanes];

        for (int lane = 0; lane < SampleGroup.Lanes; lane++)
        {
            Vec3 edge = winding[1 % n] - winding[0];
            Vec3 toPt = points[lane] - winding[0];
            testCross[lane] = Vec3.Cross(edge, toPt).Normalise().Normalised;
        }

        for (int ndxPt = 1; ndxPt < n; ndxPt++)
        {
            Vec3 p0 = winding[ndxPt];
            Vec3 p1 = winding[(ndxPt + 1) % n];
            Vec3 edge = p1 - p0;
            invalidBits = 0;
            for (int lane = 0; lane < SampleGroup.Lanes; lane++)
            {
                Vec3 cross = Vec3.Cross(edge, points[lane] - p0).Normalise().Normalised;
                float dot = Vec3.Dot(cross, testCross[lane]);
                invalid[lane] |= dot < 0.0f;
                invalidBits |= invalid[lane] ? 1 << lane : 0;
            }

            if (invalidBits == 0xF)
            {
                return false;
            }
        }

        return true;
    }

    private void Finish()
    {
        FaceLight fl = Result!;

        if (_context.Settings.DebugExtra)
        {
            foreach (StyleSupersample? state in _supersample)
            {
                if (state is null)
                {
                    continue;
                }

                for (int i = 0; i < fl.Samples.Length; i++)
                {
                    for (int j = 0; j < fl.NormalCount; j++)
                    {
                        fl.LightFor(state.StyleIndex, j)![i].Lighting = state.Visualize[i];
                    }
                }
            }
        }

        PatchLighting.BuildPatchLights(_context, FaceNum, fl);

        // The sample windings exist for supersampling only.
        fl.SampleWindingPoints = [];
        for (int i = 0; i < fl.Samples.Length; i++)
        {
            fl.Samples[i].WindingOffset = 0;
            fl.Samples[i].WindingCount = 0;
        }

        _supersample = [];
        Done = true;
    }

    /// <summary>
    /// How <c>ComputeLightmapGradients</c> treats a neighbouring luxel that has
    /// no sample.
    /// </summary>
    /// <param name="compliance">The policy.</param>
    /// <returns>True for stock's reading of uninitialised stack.</returns>
    /// <remarks>
    /// <para>
    /// <c>pSampleIntensity</c> is <c>stackalloc</c>'d and never cleared
    /// Only luxels that HAVE a sample are written
    /// So on any face whose outline does not fill its
    /// lightmap rectangle the gradient of an edge sample is taken against
    /// whatever the stack held. That is undefined and cannot be reproduced
    /// bit for bit.
    /// </para>
    /// <para>
    /// <see cref="StockQuirk.SupersampleGradientReadsUninitialised"/> models it
    /// as garbage that never matches: such a sample is always supersampled.
    /// Measured against stock <c>-bounce 0</c> (p4f-findings.md) that is much
    /// closer than reading zero, which supersampled only lit edge samples.
    /// <see cref="CompliancePolicy.Correct"/> leaves a luxel with no sample out
    /// of the gradient, which is what the test means.
    /// </para>
    /// </remarks>
    public static bool SupersampleReadsUninitialised(ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(compliance);
        return compliance.Emulates(StockQuirk.SupersampleGradientReadsUninitialised);
    }

    /// <summary>The texture s and t axes, xyz only (<c>textureVecsTexelsPerWorldUnits</c>).</summary>
    /// <param name="tex">The texinfo.</param>
    /// <returns>The two axes.</returns>
    public static (Vec3 S, Vec3 T) TextureAxes(in TexInfo tex)
    {
        FloatArray8 v = tex.TextureVecsTexelsPerWorldUnits;
        return (new Vec3(v[0], v[1], v[2]), new Vec3(v[4], v[5], v[6]));
    }

    /// <summary>
    /// The state of <c>BuildSupersampleFaceLights</c>
    /// for one light style.
    /// </summary>
    private sealed class StyleSupersample
    {
        private readonly FaceLight _fl;
        private readonly int _width;
        private readonly int _height;
        private readonly int _size;
        private readonly float[] _intensity;
        private readonly float[] _gradient;
        private readonly bool[] _processed;

        private readonly bool[] _hasSample;
        private readonly bool _uninitialisedIsGarbage;

        public StyleSupersample(FaceLight fl, FaceLightInfo info, int styleIndex, bool uninitialisedIsGarbage)
        {
            _fl = fl;
            StyleIndex = styleIndex;
            _width = info.Width;
            _height = info.Height;
            _size = _width * _height;
            _uninitialisedIsGarbage = uninitialisedIsGarbage;
            _hasSample = new bool[_size];
            foreach (LightSample sample in fl.Samples)
            {
                _hasSample[sample.S + (sample.T * _width)] = true;
            }

            // Stock stackallocs these UNINITIALISED; the intensity
            // of a luxel with no sample is whatever the stack held, and the
            // gradient test reads it for every edge sample. Zero here.
            _processed = new bool[Math.Max(_size, fl.Samples.Length)];
            _gradient = new float[fl.Samples.Length];
            _intensity = new float[fl.NormalCount * _size];
            Visualize = new Vec3[fl.Samples.Length];
        }

        public int StyleIndex { get; }

        public int Pass { get; set; } = 1;

        public bool AnotherPass { get; private set; } = true;

        public List<int> Selected { get; } = [];

        public Vec3[] Visualize { get; }

        public void ComputeSampleIntensities()
        {
            for (int i = 0; i < _fl.Samples.Length; i++)
            {
                ComputeLuxelIntensity(i);
            }
        }

        /// <summary><c>ComputeLuxelIntensity</c>.</summary>
        public void ComputeLuxelIntensity(int sampleIdx)
        {
            ref readonly LightSample sample = ref _fl.Samples[sampleIdx];
            int destIdx = sample.S + (sample.T * _width);
            for (int n = 0; n < _fl.NormalCount; n++)
            {
                float intensity = _fl.LightFor(StyleIndex, n)![sampleIdx].Intensity();

                // "convert to a linear perception space": pow in double.
                _intensity[(n * _size) + destIdx] = (float)Math.Pow(intensity / 256.0, 1.0 / 2.2);
            }
        }

        /// <summary>
        /// One pass's selection: <c>ComputeLightmapGradients</c> then the
        /// 0.0625 threshold.
        /// </summary>
        public void SelectForPass()
        {
            ComputeGradients();
            Selected.Clear();
            for (int i = 0; i < _fl.Samples.Length; i++)
            {
                if (_processed[i])
                {
                    continue;
                }

                if (_gradient[i] < LightConstants.SupersampleGradient)
                {
                    continue;
                }

                _processed[i] = true;
                Selected.Add(i);
            }

            AnotherPass = Selected.Count > 0;
        }

        // One neighbour of the gradient. A luxel with no sample is skipped
        // (correct) or is stack garbage that never matches (stock).
        private float Step(float g, float c, int index, int normal)
        {
            if (!_hasSample[index - (normal * _size)])
            {
                return _uninitialisedIsGarbage ? float.PositiveInfinity : g;
            }

            return MaxMacro(g, MathF.Abs(c - _intensity[index]));
        }

        // The `max` macro, `a > b ? a : b`: the second operand on a tie or a NaN.
        private static float MaxMacro(float a, float b) => a > b ? a : b;

        private void ComputeGradients()
        {
            int w = _width;
            int h = _height;
            for (int i = 0; i < _fl.Samples.Length; i++)
            {
                if (_processed[i])
                {
                    continue;
                }

                float g = 0.0f;
                ref readonly LightSample sample = ref _fl.Samples[i];
                for (int n = 0; n < _fl.NormalCount; n++)
                {
                    int j = (n * _size) + sample.S + (sample.T * w);
                    float c = _intensity[j];

                    if (sample.T > 0)
                    {
                        if (sample.S > 0)
                        {
                            g = Step(g, c, j - 1 - w, n);
                        }

                        g = Step(g, c, j - w, n);
                        if (sample.S < w - 1)
                        {
                            g = Step(g, c, j + 1 - w, n);
                        }
                    }

                    if (sample.T < h - 1)
                    {
                        if (sample.S > 0)
                        {
                            g = Step(g, c, j - 1 + w, n);
                        }

                        g = Step(g, c, j + w, n);
                        if (sample.S < w - 1)
                        {
                            g = Step(g, c, j + 1 + w, n);
                        }
                    }

                    if (sample.S > 0)
                    {
                        g = Step(g, c, j - 1, n);
                    }

                    if (sample.S < w - 1)
                    {
                        g = Step(g, c, j + 1, n);
                    }
                }

                _gradient[i] = g;
            }
        }
    }
}
