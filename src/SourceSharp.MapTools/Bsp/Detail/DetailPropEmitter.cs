//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Numerics;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp.MaterialPatch;
using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Ambient;

namespace SourceSharp.MapTools.Bsp.Detail;

/// <summary>
/// A displacement's surface as <c>CCoreDispInfo::GetPositionOnSurface</c>
/// answers it, for detail placement on displacement faces
/// Lane 3f's displacement builder
/// implements it.
/// </summary>
public interface IDetailDisplacementSurfaces
{
    /// <summary>The point, normal and alpha (0-255) at a parametric position of one displacement.</summary>
    /// <param name="dispInfo">The face's <c>dispinfo</c> index.</param>
    /// <param name="u">0 to 1.</param>
    /// <param name="v">0 to 1.</param>
    /// <returns>The surface sample.</returns>
    (Vec3 Point, Vec3 Normal, float Alpha) PositionOnSurface(int dispInfo, float u, float v);
}

/// <summary>
/// <c>EmitDetailObjects</c>:
/// detail props scattered on every face whose material has a
/// <c>%detailtype</c>, plus the <c>prop_detail</c> and
/// <c>prop_detail_sprite</c> entities, into the <c>dprp</c> game lump.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stock call order.</b> <c>LoadEmitDetailObjectDictionary</c> after the
/// map is loaded; <c>EmitDetailObjects</c> in
/// <c>EndBSPFile</c> right after <c>EmitStaticProps</c>
/// Over the WRITTEN faces. Also in
/// <c>-onlyprops</c>, never in <c>-onlyents</c>.
/// </para>
/// <para>
/// Placement is a function of each face's Hammer id: <c>srand(hammerfaceid)</c>
/// and <c>RandomSeed(hammerfaceid)</c> per face, the MSVC
/// <c>rand()</c> sequence consumed in face order, then the unstable CRT
/// <c>qsort</c> by leaf. Both are reproduced as the specification. The one
/// piece of state that crosses faces is the Gaussian stream's cached value
/// (<see cref="GaussianRandomStream"/>).
/// </para>
/// </remarks>
public sealed class DetailPropEmitter
{
    /// <summary><c>65535</c>: the most detail props the lump can index.</summary>
    public const int MaxDetailProps = 65535;

    private readonly VbspContext _context;
    private readonly MaterialPatcher _patcher;
    private readonly DetailDictionary _dictionary;
    private readonly Dictionary<string, bool> _validModels = new(StringComparer.Ordinal);
    private readonly GaussianRandomStream _gaussian = new();
    private int _overflow;

    /// <summary>An emitter for one compile.</summary>
    /// <param name="context">The compile.</param>
    /// <param name="patcher">The compile's patcher: materials are looked up by their original name.</param>
    /// <param name="dictionary">The loaded <c>detail.vbsp</c>.</param>
    public DetailPropEmitter(VbspContext context, MaterialPatcher patcher, DetailDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(patcher);
        ArgumentNullException.ThrowIfNull(dictionary);

        _context = context;
        _patcher = patcher;
        _dictionary = dictionary;
    }

    /// <summary>
    /// <c>LoadEmitDetailObjectDictionary</c>: worldspawn's
    /// <c>detailvbsp</c>, else <c>detail.vbsp</c>; a missing or unparseable
    /// file is an EMPTY dictionary, silently.
    /// </summary>
    /// <param name="entities">The map's entities.</param>
    /// <param name="content">Where to read it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The dictionary.</returns>
    public static async Task<DetailDictionary> LoadDictionaryAsync(
        IReadOnlyList<MapEntity> entities,
        IContentFileSystem content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(content);

        string name = "detail.vbsp";
        MapEntity? world = entities.FirstOrDefault(e => string.Equals(e.ValueForKey("classname"), "worldspawn", StringComparison.Ordinal));
        if (world is not null && world.ValueForKey("detailvbsp").Length > 0)
        {
            name = world.ValueForKey("detailvbsp");
        }

        if (!VPath.TryCreate(name, out VPath path) || path.IsEmpty)
        {
            return new DetailDictionary();
        }

        using IMemoryOwner<byte>? owner = await content.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (owner is null)
        {
            return new DetailDictionary();
        }

        KeyValuesDocument document;
        try
        {
            document = await KeyValuesDocument.ParseAsync(owner.Memory, null, cancellationToken).ConfigureAwait(false);
        }
        catch (ChunkFileException)
        {
            return new DetailDictionary();
        }

        return document.Root is null ? new DetailDictionary() : DetailDictionary.Parse(document.Root);
    }

    /// <summary>Places every detail prop and builds the lump.</summary>
    /// <param name="entities">The map's entities; <c>prop_detail</c>s are cleared.</param>
    /// <param name="bsp">The written faces, edges, vertices, texinfo, texdata and face ids.</param>
    /// <param name="tree">The written tree, for each prop's leaf.</param>
    /// <param name="displacements">Displacement surfaces, or null when the map has none.</param>
    /// <param name="cancellationToken">Cancels the material and model reads.</param>
    /// <returns>The lump.</returns>
    public async Task<DetailPropLump> EmitAsync(
        IReadOnlyList<MapEntity> entities,
        BspData bsp,
        BspTreeView tree,
        IDetailDisplacementSurfaces? displacements = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(tree);

        return await EmitAsync(entities, FaceGeometry.FromBsp(bsp), tree, displacements, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The same over faces not yet assembled into a file: the driver's
    /// <c>d*</c> arrays at <c>EmitDetailObjects</c>, before texinfo compaction.
    /// </summary>
    /// <param name="entities">The map's entities.</param>
    /// <param name="geometry">The written faces.</param>
    /// <param name="tree">The written tree.</param>
    /// <param name="displacements">Displacement surfaces, or null.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The lump.</returns>
    internal async Task<DetailPropLump> EmitAsync(
        IReadOnlyList<MapEntity> entities,
        FaceGeometry geometry,
        BspTreeView tree,
        IDetailDisplacementSurfaces? displacements,
        CancellationToken cancellationToken)
    {
        List<DetailObjectLump> props = [];
        DetailPropLump lump = new();

        for (int j = 0; j < geometry.Faces.Length; j++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DFace face = geometry.Faces[j];
            string name = geometry.MaterialOf(face);

            MaterialFacts facts = await _context.Materials.GetAsync(_patcher.OriginalNameFor(name), cancellationToken)
                .ConfigureAwait(false);
            if (!facts.Found)
            {
                continue;
            }

            string? detailType = facts.Material()?.GetString("%detailtype");
            if (detailType is null)
            {
                continue;
            }

            int typeIndex = _dictionary.Find(detailType);
            if (typeIndex < 0)
            {
                _context.Diagnostics.Add(new CompileDiagnostic(
                    SurfaceContentDiagnostics.DetailUnknownType,
                    DiagnosticSeverity.Warning,
                    $"Material {name} uses unknown detail object type {detailType}!"));
                continue;
            }

            DetailType detail = _dictionary.Types[typeIndex];
            int seed = geometry.HammerFaceId(j);
            FaceRandom random = new(seed);

            if (face.DispInfo < 0)
            {
                await OnFaceAsync(face, geometry, detail, random, props, lump, tree, cancellationToken).ConfigureAwait(false);
            }
            else if (displacements is not null)
            {
                await OnDisplacementAsync(face, geometry, detail, random, displacements, props, lump, tree, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await EmitEntitiesAsync(entities, props, lump, tree, cancellationToken).ConfigureAwait(false);

        // SetLumpData: sorted by leaf with the CRT's qsort.
        DetailObjectLump[] sorted = [.. props];
        MsvcQsort.Sort<DetailObjectLump>(sorted, static (a, b) => Math.Sign(a.Leaf - b.Leaf));
        lump.Props.AddRange(sorted);

        if (_overflow != 0)
        {
            _context.Diagnostics.Add(new CompileDiagnostic(
                SurfaceContentDiagnostics.DetailOverflow,
                DiagnosticSeverity.Warning,
                $"Error! Too many detail props on this map. {_overflow} were not emitted!"));
        }

        return lump;
    }

    // EmitDetailObjectsOnFace.
    private async Task OnFaceAsync(
        DFace face, FaceGeometry geometry, DetailType detail, FaceRandom random,
        List<DetailObjectLump> props, DetailPropLump lump, BspTreeView tree, CancellationToken cancellationToken)
    {
        if (face.NumEdges < 3)
        {
            return;
        }

        Vec3 first = geometry.StartVertex(face, 0);
        for (int i = 1; i < face.NumEdges - 1; i++)
        {
            (Vec3 a, Vec3 b) = geometry.EdgeEnds(face, i);
            Vec3 e1 = a - first;
            Vec3 e2 = b - first;

            Vec3 areaVec = Vec3.Cross(e1, e2);
            float normalLength = areaVec.Length();
            float area = 0.5f * normalLength;
            int numSamples = (int)((area * detail.Density) * 0.000001);

            for (int s = 0; s < numSamples; s++)
            {
                float u = random.Crt.NextUnit();
                float v = random.Crt.NextUnit();
                if (v > 1.0f - u)
                {
                    u = 1.0f - u;
                    v = 1.0f - v;
                }

                int group = SelectGroup(detail, 1.0f, ref random.Crt);
                int model = SelectDetail(detail.Groups[group], ref random.Crt);
                if (model < 0)
                {
                    continue;
                }

                Vec3 pt = Ma(first, u, e1);
                pt = Ma(pt, v, e2);

                // VectorDivide(areaVec, -normalLength): 1/b then a multiply.
                float oob = 1.0f / -normalLength;
                Vec3 normal = new(areaVec.X * oob, areaVec.Y * oob, areaVec.Z * oob);

                await PlaceAsync(detail.Groups[group].Models[model], pt, normal, random, props, lump, tree, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    // EmitDetailObjectsOnDisplacementFace, with the base face's
    // area from its first two triangles (ComputeDisplacementFaceArea).
    private async Task OnDisplacementAsync(
        DFace face, FaceGeometry geometry, DetailType detail, FaceRandom random, IDetailDisplacementSurfaces displacements,
        List<DetailObjectLump> props, DetailPropLump lump, BspTreeView tree, CancellationToken cancellationToken)
    {
        Vec3 first = geometry.StartVertex(face, 0);
        float area = 0.0f;
        for (int i = 1; i <= 2; i++)
        {
            (Vec3 a, Vec3 b) = geometry.EdgeEnds(face, i);
            area += 0.5f * Vec3.Cross(a - first, b - first).Length();
        }

        int numSamples = (int)((area * detail.Density) * 0.000001);
        for (int s = 0; s < numSamples; s++)
        {
            float u = random.Crt.NextUnit();
            float v = random.Crt.NextUnit();

            (Vec3 pt, Vec3 normal, float alpha) = displacements.PositionOnSurface(face.DispInfo, u, v);
            alpha /= 255.0f;

            int group = SelectGroup(detail, alpha, ref random.Crt);
            int model = SelectDetail(detail.Groups[group], ref random.Crt);
            if (model < 0)
            {
                continue;
            }

            await PlaceAsync(detail.Groups[group].Models[model], pt, normal, random, props, lump, tree, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary><c>SelectGroup</c>.</summary>
    /// <param name="detail">The type.</param>
    /// <param name="alpha">The surface alpha, 0 to 1.</param>
    /// <param name="random">The face's CRT stream; one draw only when two groups bracket the alpha.</param>
    /// <returns>The group index.</returns>
    public static int SelectGroup(DetailType detail, float alpha, ref MsvcRandom random)
    {
        ArgumentNullException.ThrowIfNull(detail);

        int start;
        for (start = 0; start < detail.Groups.Count - 1; ++start)
        {
            if (alpha < detail.Groups[start + 1].Alpha)
            {
                break;
            }
        }

        int end = start + 1;
        if (end >= detail.Groups.Count)
        {
            --end;
        }

        if (start == end)
        {
            return start;
        }

        float dist = 0.0f;
        float dAlpha = detail.Groups[end].Alpha - detail.Groups[start].Alpha;
        if (dAlpha != 0.0f)
        {
            dist = (alpha - detail.Groups[start].Alpha) / dAlpha;
        }

        float r = random.NextUnit();
        return r > dist ? start : end;
    }

    /// <summary><c>SelectDetail</c>: -1 when the draw falls past the last cumulative amount.</summary>
    /// <param name="group">The group.</param>
    /// <param name="random">The face's CRT stream; one draw.</param>
    /// <returns>The model index, or -1.</returns>
    public static int SelectDetail(DetailGroup group, ref MsvcRandom random)
    {
        ArgumentNullException.ThrowIfNull(group);

        float r = random.NextUnit();
        for (int i = 0; i < group.Models.Count; i++)
        {
            if (r <= group.Models[i].Amount)
            {
                return i;
            }
        }

        return -1;
    }

    // PlaceDetail.
    private async Task PlaceAsync(
        DetailModel model, Vec3 pt, Vec3 normal, FaceRandom random,
        List<DetailObjectLump> props, DetailPropLump lump, BspTreeView tree, CancellationToken cancellationToken)
    {
        float cosAngle = normal.Z;
        if (cosAngle < model.MaxCosAngle)
        {
            return;
        }

        if (cosAngle < model.MinCosAngle)
        {
            float probability = (cosAngle - model.MaxCosAngle) / (model.MinCosAngle - model.MaxCosAngle);
            float t = random.Crt.NextUnit();
            if (t > probability)
            {
                return;
            }
        }

        Vec3 angles = model.Upright
            ? new Vec3(0f, 360.0f * random.Crt.Next() / MsvcRandom.RandMax, 0f)
            : ConformingAngles(normal, random.Crt.Next(), _context.Options.Compliance);

        if (model.Type == DetailModelType.Model)
        {
            await AddModelAsync(model.ModelName ?? string.Empty, pt, angles, model.Orientation, props, lump, tree, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        float scale = 1.0f;
        if (model.RandomScaleStdDev != 0.0f)
        {
            scale = Math.Abs(_gaussian.RandomFloat(ref random.Vstdlib, 1.0f, model.RandomScaleStdDev));
        }

        AddSprite(pt, angles, model.Orientation, model.Pos, model.Tex, scale, model.Type,
                  model.ShapeAngle, model.ShapeSize, model.SwayAmount, props, lump, tree);
    }

    /// <summary>
    /// The orientation of a detail that conforms to its surface
    /// A basis around the normal, a random
    /// spin about it, and <c>MatrixToAngles</c>.
    /// </summary>
    /// <param name="normal">The surface normal.</param>
    /// <param name="rand">The <c>rand()</c> value that picks the spin.</param>
    /// <param name="compliance">Selects the normalise (see <see cref="StockQuirk.DetailOrientationNormalise"/>).</param>
    /// <returns>Pitch, yaw, roll.</returns>
    public static Vec3 ConformingAngles(Vec3 normal, int rand, ComplianceOptions compliance)
    {
        ArgumentNullException.ThrowIfNull(compliance);

        bool stock = compliance.Emulates(StockQuirk.DetailOrientationNormalise);
        Vec3 Normalise(Vec3 v) => stock ? v.NormaliseLikeStock().Normalised : v.Normalise().Normalised;

        Vec3 zaxis = Normalise(normal);
        Vec3 xaxis = new(1f, 0f, 0f);
        if (Math.Abs(Vec3.Dot(xaxis, zaxis)) - 1.0 > -1e-3)
        {
            xaxis = new Vec3(0f, 1f, 0f);
        }

        Vec3 yaxis = Normalise(Vec3.Cross(zaxis, xaxis));
        xaxis = Normalise(Vec3.Cross(yaxis, zaxis));

        // VMatrix::SetBasisVectors: columns x, y, z; SetTranslation zero.
        float[,] m =
        {
            { xaxis.X, yaxis.X, zaxis.X, 0f },
            { xaxis.Y, yaxis.Y, zaxis.Y, 0f },
            { xaxis.Z, yaxis.Z, zaxis.Z, 0f },
        };

        float rotAngle = 360.0f * rand / MsvcRandom.RandMax;
        float[,] r = AxisRotZ(rotAngle);

        // VMatrix::MatrixMul, rows 0-2; the fourth
        // term's m[i][3] * r[3][j] is 0 * 0 and is kept because adding +0
        // turns a -0 sum into +0.
        float[,] p = new float[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                p[i, j] = (m[i, 0] * r[0, j]) + (m[i, 1] * r[1, j]) + (m[i, 2] * r[2, j]) + (m[i, 3] * r[3, j]);
            }
        }

        return MatrixToAngles(p);
    }

    // SetupMatrixAxisRot(Vector(0, 0, 1), degrees),
    // written with the axis components so every product and its sign is
    // stock's.
    private static float[,] AxisRotZ(float degrees)
    {
        const float ax = 0f, ay = 0f, az = 1f;
        float radians = (float)(degrees * (Math.PI / 180.0f));
        float s = DetMathF.Sin(radians);
        float c = DetMathF.Cos(radians);
        float t = 1.0f - c;

        float tx = t * ax, ty = t * ay, tz = t * az;
        float sx = s * ax, sy = s * ay, sz = s * az;

        return new float[,]
        {
            { (tx * ax) + c, (tx * ay) - sz, (tx * az) + sy, 0f },
            { (tx * ay) + sz, (ty * ay) + c, (ty * az) - sx, 0f },
            { (tx * az) - sy, (ty * az) + sx, (tz * az) + c, 0f },
            { 0f, 0f, 0f, 1f },
        };
    }

    // MatrixToAngles(const VMatrix&).
    private static Vec3 MatrixToAngles(float[,] m)
    {
        float f0 = m[0, 0], f1 = m[1, 0], f2 = m[2, 0];
        float l0 = m[0, 1], l1 = m[1, 1], l2 = m[2, 1];
        float u2 = m[2, 2];

        float xyDist = MathF.Sqrt((f0 * f0) + (f1 * f1));
        if (xyDist > 0.001f)
        {
            return new Vec3(Rad2Deg(Atan2F(-f2, xyDist)), Rad2Deg(Atan2F(f1, f0)), Rad2Deg(Atan2F(l2, u2)));
        }

        return new Vec3(Rad2Deg(Atan2F(-f2, xyDist)), Rad2Deg(Atan2F(-l0, l1)), 0f);
    }

    // RAD2DEG: (float)x * (float)(180.f / M_PI_F).
    private static float Rad2Deg(float x) => x * (180.0f / (float)Math.PI);

    // atan2f. Stock's float trig (sinf, cosf, atan2f) is its own C runtime's,
    // which is not correctly rounded; this is the correctly rounded one, the
    // same on every host. No reading of the reference source -- double or
    // float evaluation, reciprocal or divide -- closes the last-bit gap to
    // stock: the detail gate holds angles to a MEASURED 2^-15 degrees for
    // that reason.
    private static float Atan2F(float y, float x) => DetMathF.Atan2(y, x);

    // AddDetailToLump.
    private async Task AddModelAsync(
        string modelName, Vec3 pt, Vec3 angles, int orientation,
        List<DetailObjectLump> props, DetailPropLump lump, BspTreeView tree, CancellationToken cancellationToken)
    {
        if (!await IsModelValidAsync(modelName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (props.Count == MaxDetailProps)
        {
            ++_overflow;
            return;
        }

        DetailObjectLump record = Common(pt, angles, orientation, tree);
        record.DetailModel = (ushort)AddModelName(lump, modelName);
        record.Type = (byte)DetailModelType.Model;
        props.Add(record);
    }

    // AddDetailSpriteToLump.
    private static void AddSprite(
        Vec3 pt, Vec3 angles, int orientation, (float X, float Y)[] pos, (float X, float Y)[] tex, float scale,
        DetailModelType type, byte shapeAngle, byte shapeSize, byte sway,
        List<DetailObjectLump> props, DetailPropLump lump, BspTreeView tree)
    {
        if (props.Count >= MaxDetailProps)
        {
            throw new MapCompileException("Error! Too many detail props emitted on this map! (64K max!)");
        }

        DetailObjectLump record = Common(pt, angles, orientation, tree);
        record.DetailModel = (ushort)AddSpriteDict(lump, pos, tex);
        record.Type = (byte)type;
        record.Scale = scale;
        record.ShapeAngle = shapeAngle;
        record.ShapeSize = shapeSize;
        record.SwayAmount = sway;
        props.Add(record);
    }

    private static DetailObjectLump Common(Vec3 pt, Vec3 angles, int orientation, BspTreeView tree)
    {
        DetailObjectLump record = default;
        record.Origin = pt;
        record.Angles = angles;
        record.Leaf = (ushort)tree.LeafOf(pt);
        record.Lighting = new ColorRgbExp32 { R = 255, G = 255, B = 255, Exponent = 0 };
        record.LightStyles = 0;
        record.LightStyleCount = 0;
        record.Orientation = unchecked((byte)orientation);
        return record;
    }

    // AddDetailDictLump: strncpy then a memcmp from the end.
    private static int AddModelName(DetailPropLump lump, string modelName)
    {
        for (int i = lump.ModelNames.Count - 1; i >= 0; i--)
        {
            if (string.Equals(lump.ModelNames[i], modelName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        lump.ModelNames.Add(modelName);
        return lump.ModelNames.Count - 1;
    }

    // AddDetailSpriteDictLump: memcmp, so bitwise.
    private static int AddSpriteDict(DetailPropLump lump, (float X, float Y)[] pos, (float X, float Y)[] tex)
    {
        DetailSpriteDictLump entry = default;
        entry.UpperLeft[0] = pos[0].X;
        entry.UpperLeft[1] = pos[0].Y;
        entry.LowerRight[0] = pos[1].X;
        entry.LowerRight[1] = pos[1].Y;
        entry.TexUpperLeft[0] = tex[0].X;
        entry.TexUpperLeft[1] = tex[0].Y;
        entry.TexLowerRight[0] = tex[1].X;
        entry.TexLowerRight[1] = tex[1].Y;

        for (int i = lump.Sprites.Count - 1; i >= 0; i--)
        {
            if (Same(lump.Sprites[i], entry))
            {
                return i;
            }
        }

        lump.Sprites.Add(entry);
        return lump.Sprites.Count - 1;
    }

    private static bool Same(DetailSpriteDictLump a, DetailSpriteDictLump b)
    {
        ReadOnlySpan<byte> x = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<DetailSpriteDictLump>(in a));
        ReadOnlySpan<byte> y = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<DetailSpriteDictLump>(in b));
        return x.SequenceEqual(y);
    }

    // IsModelValid: one LoadStudioModel per exact name.
    private async ValueTask<bool> IsModelValidAsync(string modelName, CancellationToken cancellationToken)
    {
        if (_validModels.TryGetValue(modelName, out bool known))
        {
            return known;
        }

        StudioModelLoad load = await StudioModelCheck.LoadAsync(
            _context.Content, modelName, "detail_prop", _context.Diagnostics, _context.Options.Compliance, cancellationToken).ConfigureAwait(false);

        if (!load.IsValid)
        {
            _context.Diagnostics.Add(new CompileDiagnostic(
                SurfaceContentDiagnostics.StudioModelLoadFailed,
                DiagnosticSeverity.Warning,
                $"Error loading studio model \"{modelName}\"!"));
        }

        _validModels.Add(modelName, load.IsValid);
        return load.IsValid;
    }

    // The entity half of EmitDetailModels.
    private async Task EmitEntitiesAsync(
        IReadOnlyList<MapEntity> entities, List<DetailObjectLump> props, DetailPropLump lump, BspTreeView tree,
        CancellationToken cancellationToken)
    {
        foreach (MapEntity entity in entities)
        {
            string className = entity.ValueForKey("classname");

            if (string.Equals(className, "detail_prop", StringComparison.Ordinal) ||
                string.Equals(className, "prop_detail", StringComparison.Ordinal))
            {
                await AddModelAsync(
                        entity.ValueForKey("model"), entity.GetVectorForKey("origin"), entity.GetVectorForKey("angles"),
                        entity.IntForKey("detailOrientation"), props, lump, tree, cancellationToken)
                    .ConfigureAwait(false);
                entity.Clear();
                continue;
            }

            if (string.Equals(className, "prop_detail_sprite", StringComparison.Ordinal))
            {
                Vec3 ul = entity.GetVectorForKey("position_ul");
                Vec3 lr = entity.GetVectorForKey("position_lr");
                Vec3 texUl = entity.GetVectorForKey("tex_ul");
                Vec3 texSize = entity.GetVectorForKey("tex_size");
                float total = entity.FloatForKey("tex_total_size");

                float t1x = texSize.X + (texUl.X - 0.5f);
                float t1y = texSize.Y + (texUl.Y - 0.5f);
                float t0x = texUl.X + 0.5f;
                float t0y = texUl.Y + 0.5f;

                // Vector2D::operator/=: a reciprocal, then multiplies.
                float oofl = 1.0f / total;

                AddSprite(
                    entity.GetVectorForKey("origin"), entity.GetVectorForKey("angles"), entity.IntForKey("detailOrientation"),
                    [(ul.X, ul.Y), (lr.X, lr.Y)],
                    [(t0x * oofl, t0y * oofl), (t1x * oofl, t1y * oofl)],
                    1.0f, DetailModelType.Sprite, 0, 0, 0, props, lump, tree);
                entity.Clear();
            }
        }
    }

    private static Vec3 Ma(Vec3 start, float scale, Vec3 direction) =>
        new(start.X + (scale * direction.X), start.Y + (scale * direction.Y), start.Z + (scale * direction.Z));

    // The two streams stock reseeds for every face: the CRT's (srand) and
    // vstdlib's (RandomSeed), held per face so faces are independent.
    private sealed class FaceRandom(int seed)
    {
        public MsvcRandom Crt = new(seed);

        public StockRandomStream Vstdlib = new(seed);
    }

    /// <summary>The written faces and the lumps needed to walk them.</summary>
    /// <param name="faces">LUMP_FACES.</param>
    /// <param name="surfEdges">LUMP_SURFEDGES.</param>
    /// <param name="edges">LUMP_EDGES.</param>
    /// <param name="vertices">LUMP_VERTEXES.</param>
    /// <param name="faceIds">LUMP_FACEIDS.</param>
    /// <param name="materialOfTexInfo">A texinfo's material name.</param>
    internal sealed class FaceGeometry(
        DFace[] faces, int[] surfEdges, DEdge[] edges, Vec3[] vertices, DFaceId[] faceIds, Func<int, string> materialOfTexInfo)
    {
        private readonly int[] _surfEdges = surfEdges;
        private readonly DEdge[] _edges = edges;
        private readonly Vec3[] _vertices = vertices;
        private readonly DFaceId[] _faceIds = faceIds;
        private readonly Func<int, string> _materialOf = materialOfTexInfo;

        public static FaceGeometry FromBsp(BspData bsp)
        {
            TexInfo[] texInfos = BspStructView.As<TexInfo>(bsp[BspLump.TexInfo]).ToArray();
            DTexData[] texData = BspStructView.As<DTexData>(bsp[BspLump.TexData]).ToArray();
            int[] stringTable = BspStructView.As<int>(bsp[BspLump.TexDataStringTable]).ToArray();
            byte[] strings = bsp[BspLump.TexDataStringData].Data.ToArray();

            return new FaceGeometry(
                BspStructView.As<DFace>(bsp[BspLump.Faces]).ToArray(),
                BspStructView.As<int>(bsp[BspLump.SurfEdges]).ToArray(),
                BspStructView.As<DEdge>(bsp[BspLump.Edges]).ToArray(),
                BspStructView.As<Vec3>(bsp[BspLump.Vertexes]).ToArray(),
                BspStructView.As<DFaceId>(bsp[BspLump.FaceIds]).ToArray(),
                texInfo =>
                {
                    int start = stringTable[texData[texInfos[texInfo].TexData].NameStringTableId];
                    int end = Array.IndexOf(strings, (byte)0, start);
                    return System.Text.Encoding.Latin1.GetString(strings, start, (end < 0 ? strings.Length : end) - start);
                });
        }

        public DFace[] Faces { get; } = faces;

        public int HammerFaceId(int face) => face < _faceIds.Length ? _faceIds[face].HammerFaceId : 0;

        public string MaterialOf(DFace face) => _materialOf(face.TexInfo);

        // pSurfEdges[0]: the vertex dedges[abs(se)].v[se < 0].
        public Vec3 StartVertex(DFace face, int k)
        {
            int se = _surfEdges[face.FirstEdge + k];
            return _vertices[_edges[Math.Abs(se)].V[se < 0 ? 1 : 0]];
        }

        // Edge i of the face, as (v[vertexIdx], v[1 - vertexIdx]).
        public (Vec3 A, Vec3 B) EdgeEnds(DFace face, int i)
        {
            int se = _surfEdges[face.FirstEdge + i];
            int vertexIdx = se < 0 ? 1 : 0;
            DEdge edge = _edges[Math.Abs(se)];
            return (_vertices[edge.V[vertexIdx]], _vertices[edge.V[1 - vertexIdx]]);
        }
    }
}
