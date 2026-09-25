using System.Linq;
using System.Security.Cryptography;
using System.Text;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Disp;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The canonical digest of the PARSED map model — what and its
/// fixups actually consume, not the VMF text (plan_maptools.md 10a: Hammer's
/// save-time churn — <c>editor</c>{f} blocks, camera positions, view settings,
/// renumbered face ids — must not invalidate the key, while anything that
/// changes a computed byte must).
/// </summary>
/// <remarks>
/// <para>
/// Everything below is taken AFTER load fixups (<see cref="MapFile"/> as the
/// load phase returns it is the parse boundary; vbsp's own work products —
/// visclusters, texinfo tables, the winding arena — are re-derived from these
/// inputs by pure functions, so folding the inputs folds the products). The
/// fold is deterministic: fixed field order, index order, so two loads of one
/// map digest identically while any semantic change flips it.
/// </para>
/// <para>
/// What is deliberately EXCLUDED and why each is safe:
/// </para>
/// <list type="bullet">
/// <item>the VMF <c>editor</c>/<c>cameraORIGIN</c>/<c>view</c> blocks — the
/// loader discards them, they never reach any lump.</item>
/// <item><see cref="MapBrushSide.Tested"/> — a scratch flag of winding ops.</item>
/// <item><see cref="MapBrushSide.Winding"/> — DERIVED from the planes and the
/// brush's sides; folding the planes and sides reproduces it.</item>
/// <item><see cref="MapFile.Mins"/>/<see cref="MapFile.Maxs"/> — derived from
/// the brush bounds folded here.</item>
/// </list>
/// </remarks>
public static class ModelDigest
{
    /// <summary>The parsed model's digest.</summary>
    /// <param name="map">The map as the load phase returned it.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
    public static string OfParsed(MapFile map)
    {
        ArgumentNullException.ThrowIfNull(map);

        using CanonicalHash h = new("map-model");

        h.U32((int)map.Planes.Count);
        for (int i = 0; i < map.Planes.Count; i++)
        {
            Plane plane = map.Planes[i];
            h.Vec3(plane.Normal);
            h.F32(plane.Dist);
            h.I32((int)plane.Type);
        }

        h.U32((int)map.Entities.Count);
        foreach (MapEntity entity in map.Entities)
        {
            h.U32((int)entity.Pairs.Count);
            foreach (MapKeyValue pair in entity.Pairs)
            {
                h.Str(pair.Key);
                h.Str(pair.Value);
            }

            h.Vec3(entity.Origin);
            h.F32Opt(entity.FloodOrigin);
            h.I32(entity.FirstBrush);
            h.I32(entity.BrushCount);
            h.I32(entity.AreaPortalNumber);
            h.I32(entity.PortalAreas[0]);
            h.I32(entity.PortalAreas[1]);
        }

        h.U32((int)map.BrushCount);
        foreach (MapBrush brush in map.Brushes)
        {
            h.I32(brush.EntityNumber);
            h.I32(brush.BrushNumber);
            h.I32(brush.Id);
            h.I32(brush.Contents);
            h.Vec3(brush.Mins);
            h.Vec3(brush.Maxs);
            h.I32(brush.FirstSide);
            h.I32(brush.SideCount);
        }

        h.U32((int)map.BrushSideCount);
        for (int i = 0; i < map.BrushSideCount; i++)
        {
            MapBrushSide side = map.BrushSides[i];
            h.I32(side.PlaneNumber);
            h.I32(side.TexInfo);
            h.I32(side.Contents);
            h.I32(side.Surface);
            h.Bool(side.Visible);
            h.Bool(side.Bevel);
            h.I32(side.Id);
            h.U32(unchecked((int)side.SmoothingGroups));
            h.Ints(side.OverlayIds);
            h.Ints(side.WaterOverlayIds);
            h.Bool(side.DynamicShadowsEnabled);
            OfDisplacement(h, side.Displacement);
            OfBrushTexture(h, map.SideBrushTextures[i]);
        }

        h.U32((int)map.ConnectionPairs.Count);
        foreach (MapKeyValue pair in map.ConnectionPairs)
        {
            h.Str(pair.Key);
            h.Str(pair.Value);
        }

        h.Ints(map.VisClusterEntities);
        h.Ints(map.NoDynamicShadowSides);
        h.Ints(map.OverlayEntities);

        h.U32((int)map.WaterOverlayData.Count);
        foreach (VmfChunk chunk in map.WaterOverlayData)
        {
            OfVmf(h, chunk, 0);
        }

        h.I32(map.StartMapOverlays);
        h.I32(map.StartMapWaterOverlays);
        h.I32(map.ClipBrushes);
        h.I32(map.ClipTexInfo);
        h.Vec3(map.Mins);
        h.Vec3(map.Maxs);

        return h.Finish();
    }

    /// <summary>The digest of the finished BSP the chain hands to vvis/vrad — the stage keys' semantic core.</summary>
    /// <param name="bsp">The container as vbsp left it.</param>
    /// <param name="exceptLumps">Slots to skip (the stage's own outputs), or null for all.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    public static string OfBsp(BspData bsp, IReadOnlySet<int>? exceptLumps = null)
    {
        ArgumentNullException.ThrowIfNull(bsp);

        using CanonicalHash h = new("bsp-lumps");
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            if (exceptLumps?.Contains(i) == true)
            {
                h.Raw([0x73]);
                continue;
            }

            BspLumpData lump = bsp[i];
            h.I32(lump.Version);
            h.I32(lump.UncompressedSize);
            h.Bytes(lump.Data.Span);
        }

        h.U32((int)bsp.GameLumps.Count);
        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            h.I32(entry.Id);
            h.U16(entry.Flags);
            h.U16(entry.Version);
            h.Bytes(entry.Data.Span);
        }

        h.I32(bsp.FileVersion);
        return h.Finish();
    }

    private static void OfDisplacement(CanonicalHash h, IMapDisplacement? displacement)
    {
        if (displacement is null)
        {
            h.Bool(false);
            return;
        }

        h.Bool(true);
        h.I32(displacement.EntityNumber);
        h.I32(displacement.BrushSideId);

        // The built surface, when present: the collision and virtual-mesh
        // roads read exactly Verts/FlatVerts/Alphas/TriIndices/TriTags/
        // AllowedVerts/TexCoords, so exactly those arrays are hashed — and the
        // power that sizes them.
        if (displacement is MapDisplacement surf)
        {
            h.Bool(true);
            h.I32(surf.Power);
            h.Vec3Span(surf.FieldVectors);
            h.F32Span(surf.FieldDistances);
            h.Vec3Span(surf.VectorOffsets);
            h.Ints(surf.TriangleTags.Select(static t => (int)t));
        }
        else
        {
            h.Bool(false);
        }
    }

    private static void OfBrushTexture(CanonicalHash h, in BrushTexture texture)
    {
        h.Vec3(texture.UAxis);
        h.Vec3(texture.VAxis);
        h.F32(texture.ShiftU);
        h.F32(texture.ShiftV);
        h.F32(texture.Rotate);
        h.F32(texture.TextureWorldUnitsPerTexelU);
        h.F32(texture.TextureWorldUnitsPerTexelV);
        h.F32(texture.LightmapWorldUnitsPerLuxel);
        h.Str(texture.Name);
        h.I32(texture.Flags);
    }

    private static void OfVmf(CanonicalHash h, VmfNode node, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidOperationException("vmf overlay data nested deeper than 64");
        }

        h.Str(node.Name);
        switch (node)
        {
            case VmfKey key:
                h.Str(key.Value);
                break;
            case VmfChunk chunk:
                h.U32((int)chunk.Children.Count);
                foreach (VmfNode child in chunk.Children)
                {
                    OfVmf(h, child, depth + 1);
                }

                break;
        }
    }

    /// <summary>
    /// The fold: domain-tagged SHA-256 over a fixed-order field sequence.
    /// Every primitive write is fixed-width (length-implicit); strings are
    /// NUL-terminated explicitly, so the stream is prefix-free.
    /// </summary>
    internal sealed class CanonicalHash : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public CanonicalHash(string domain)
        {
            _hash.AppendData(Encoding.UTF8.GetBytes(domain));
            _hash.AppendData([0]);
        }

        public string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();

        public void Raw(ReadOnlySpan<byte> bytes) => _hash.AppendData(bytes);

        public void Bytes(ReadOnlySpan<byte> bytes)
        {
            U32(bytes.Length);
            _hash.AppendData(bytes);
        }

        public void Str(string? value)
        {
            if (value is null)
            {
                _hash.AppendData([0xFF]);
                return;
            }

            _hash.AppendData(Encoding.UTF8.GetBytes(value));
            _hash.AppendData([0]);
        }

        public void I32(int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BitConverter.TryWriteBytes(buffer, value);
            _hash.AppendData(buffer);
        }

        public void U32(int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BitConverter.TryWriteBytes(buffer, value);
            _hash.AppendData(buffer);
        }

        public void I16(short value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BitConverter.TryWriteBytes(buffer, value);
            _hash.AppendData(buffer);
        }

        public void U16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BitConverter.TryWriteBytes(buffer, value);
            _hash.AppendData(buffer);
        }

        public void F32(float value) => I32(BitConverter.SingleToInt32Bits(value));

        public void Bool(bool value) => _hash.AppendData([value ? (byte)1 : (byte)0]);

        public void Vec3(Vec3 v)
        {
            F32(v.X);
            F32(v.Y);
            F32(v.Z);
        }

        public void F32Opt(Vec3? v)
        {
            if (v is null)
            {
                Bool(false);
                return;
            }

            Bool(true);
            Vec3(v.Value);
        }

        public void Ints(IEnumerable<int> values)
        {
            int count = 0;
            foreach (int value in values)
            {
                I32(value);
                count++;
            }

            U32(count);
        }

        public void Vec3Span(ReadOnlySpan<Vec3> values)
        {
            U32(values.Length);
            foreach (Vec3 v in values)
            {
                Vec3(v);
            }
        }

        public void F32Span(ReadOnlySpan<float> values)
        {
            U32(values.Length);
            foreach (float v in values)
            {
                F32(v);
            }
        }

        public void U16Span(ReadOnlySpan<ushort> values)
        {
            U32(values.Length);
            foreach (ushort v in values)
            {
                U16(v);
            }
        }

        public void U32Span(ReadOnlySpan<uint> values)
        {
            U32(values.Length);
            foreach (uint v in values)
            {
                I32(unchecked((int)v));
            }
        }
    }
}
