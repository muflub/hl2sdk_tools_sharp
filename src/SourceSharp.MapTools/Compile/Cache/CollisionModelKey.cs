//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Phys;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The canonical digest of everything one model's collision cook reads
/// (the cache rules: keys hash content; a stale entry is caught at
/// insertion AND at every hit).
/// </summary>
/// <remarks>
/// <para>
/// The cook reads: the model record; the leaves its head node reaches and
/// their brush lists; the referenced brushes with their sides, planes,
/// texinfos and per-side visibility; the faces slice (per-triangle material
/// and area, for a brush entity's mass); the water volumes (world only, with
/// their leaves and surface texinfo); the displacements; and the
/// surface-property tables. The fold is:
/// </para>
/// <list type="bullet">
/// <item>model record;</item>
/// <item>the leaf-reachable brush set, walked exactly as the cook walks it,
/// then each referenced brush's record, its sides' records, planes, texinfos
/// and visibility, in reference order;</item>
/// <item>the faces slice props/areas with their texinfos;</item>
/// <item>world only: the water volumes, the displacement list and the
/// virtual-mesh steering bit.</item>
/// </list>
/// <para>
/// The option bits (compliance, no-virtual-mesh, shrink/merge constants, the
/// cooker identity) fold separately via <see cref="OptionsDigest"/> in
/// <see cref="CollisionModelCache"/>; the surface-property NAME table rides as
/// its identity string so a renamed material invalidates.
/// </para>
/// <para>
/// A brush model (every model but the world) folds content instead of
/// table indices: its brushes relative to its lowest one, planes by value,
/// texinfos by the surface property they resolve to. A world brush added
/// or removed renumbers every later brush, plane, node and face, and an
/// index fold would miss every entity model on that edit.
/// </para>
/// <para>
/// Consequence: a brush edit re-cooks the world model (stock recomputes it
/// too) and that brush's own entity model, but not the other entity models;
/// a light move or a non-brush entity edit changes no fold at all — the
/// edit-loop win §10a is for.
/// </para>
/// </remarks>
public static class CollisionModelKey
{
    /// <summary>Digests one model's cooking inputs.</summary>
    /// <param name="input">The emitter input.</param>
    /// <param name="modelIndex">Which model is being cooked.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">No such model.</exception>
    public static string OfModel(PhysCollisionInput input, int modelIndex)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (modelIndex < 0 || modelIndex >= input.Models.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(modelIndex));
        }

        return modelIndex == 0 ? OfWorld(input) : OfBrushModel(input, modelIndex);
    }

    /// <summary>
    /// The brushes one model's cook reads, walked from its head node exactly
    /// as the cook walks it, in the index order the cook adds them.
    /// </summary>
    /// <param name="input">The emitter input.</param>
    /// <param name="modelIndex">The model.</param>
    /// <returns>The brush indices, ascending.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">No such model.</exception>
    public static SortedSet<int> BrushesOf(PhysCollisionInput input, int modelIndex)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (modelIndex < 0 || modelIndex >= input.Models.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(modelIndex));
        }

        SortedSet<int> brushes = [];
        Walk(input, input.Models[modelIndex].HeadNode, brushes);
        return brushes;
    }

    /// <summary>
    /// A brush model's fold: content only, no global table index. Brush
    /// numbers fold relative to the model's lowest brush (the cook stores
    /// the absolute number in each convex, which the cache rebases on
    /// replay), planes fold by value, and texinfos by the surface property
    /// they resolve to. So an edit that renumbers the BSP around this model
    /// (a world brush added, a material added) leaves its key alone.
    /// </summary>
    private static string OfBrushModel(PhysCollisionInput input, int modelIndex)
    {
        using ModelDigest.CanonicalHash h = new("collision-brush-model");
        DModel m = input.Models[modelIndex];

        // Mins/maxs size the drag-area epsilon.
        h.Vec3(m.Mins);
        h.Vec3(m.Maxs);

        SortedSet<int> brushes = BrushesOf(input, modelIndex);
        int baseBrush = brushes.Count > 0 ? brushes.Min : 0;
        h.U32(brushes.Count);
        foreach (int brush in brushes)
        {
            h.I32(brush - baseBrush);
            OfBrushContent(h, input, brush);
        }

        // The faces slice: per-face surface property and area (the mass).
        h.U32(m.NumFaces);
        for (int i = 0; i < m.NumFaces; i++)
        {
            int faceIndex = m.FirstFace + i;
            if (faceIndex < 0 || faceIndex >= input.Faces.Count)
            {
                h.Bool(false);
                continue;
            }

            h.Bool(true);
            DFace face = input.Faces[faceIndex];
            h.F32(face.Area);
            OfProp(h, input, PropOfTexInfo(input, face.TexInfo));
        }

        // MassAndMaterial falls back to property 0 for "no property".
        OfProp(h, input, 0);
        return h.Finish();
    }

    private static void OfBrushContent(ModelDigest.CanonicalHash h, PhysCollisionInput input, int brush)
    {
        if (brush < 0 || brush >= input.Brushes.Count)
        {
            h.Bool(false);
            return;
        }

        h.Bool(true);
        DBrush b = input.Brushes[brush];
        h.I32(b.Contents);
        h.I32(b.NumSides);
        for (int s = 0; s < b.NumSides; s++)
        {
            int sideIndex = b.FirstSide + s;
            if (sideIndex < 0 || sideIndex >= input.BrushSides.Count)
            {
                h.Bool(false);
                continue;
            }

            h.Bool(true);
            DBrushSide side = input.BrushSides[sideIndex];
            h.I16(side.Bevel);
            if (side.PlaneNum < input.Planes.Count)
            {
                DPlane plane = input.Planes[side.PlaneNum];
                h.Bool(true);
                h.Vec3(plane.Normal);
                h.F32(plane.Dist);
            }
            else
            {
                h.Bool(false);
            }

            // The NODRAW path reads the first non-bevel side's property.
            OfProp(h, input, PropOfTexInfo(input, side.TexInfo));
            h.Bool(SideVisible(input, brush, s) ?? true);
        }
    }

    private static int PropOfTexInfo(PhysCollisionInput input, int texInfo)
    {
        if (texInfo < 0 || texInfo >= input.TexInfos.Count)
        {
            return -1;
        }

        int texData = input.TexInfos[texInfo].TexData;
        return texData >= 0 && texData < input.SurfaceProperties.Count ? input.SurfaceProperties[texData] : -1;
    }

    // A surface property as the cook sees it: its database slot (the mass
    // vote compares slots), its name and the physics the mass reads.
    private static void OfProp(ModelDigest.CanonicalHash h, PhysCollisionInput input, int prop)
    {
        h.I32(prop);
        if (prop < 0 || prop >= input.SurfaceProps.Count)
        {
            return;
        }

        h.Str(input.SurfaceProps.GetPropName(prop));
        SurfacePhysics physics = input.SurfaceProps.GetPhysicsProperties(prop);
        h.F32(physics.Density);
        h.F32(physics.Thickness);
        h.F32(physics.Friction);
        h.F32(physics.Elasticity);
    }

    // The world's fold keeps the table indices: the world is compiled first,
    // so edits elsewhere do not renumber it, and its cook writes per-leaf and
    // per-brush data back by index.
    private static string OfWorld(PhysCollisionInput input)
    {
        const int modelIndex = 0;
        using ModelDigest.CanonicalHash h = new("collision-model");
        DModel m = input.Models[modelIndex];

        h.I32(modelIndex);
        h.Vec3(m.Mins);
        h.Vec3(m.Maxs);
        h.Vec3(m.Origin);
        h.I32(m.HeadNode);
        h.I32(m.FirstFace);
        h.I32(m.NumFaces);

        // The walk the cook performs(VisitLeaves_r), recorded
        // as the brush reference set — so the key's brush set IS the cook's.
        SortedSet<int> brushes = [];
        Walk(input, m.HeadNode, brushes);

        h.U32(brushes.Count);
        foreach (int brush in brushes)
        {
            OfBrush(h, input, brush);
        }

        // The faces slice: per-triangle material and area (mass/volume path).
        for (int i = 0; i < m.NumFaces; i++)
        {
            int faceIndex = m.FirstFace + i;
            if (faceIndex < 0 || faceIndex >= input.Faces.Count)
            {
                h.Bool(false);
                continue;
            }

            h.Bool(true);
            DFace face = input.Faces[faceIndex];
            h.I32(face.TexInfo);
            h.F32(face.Area);
            OfTexInfo(h, input, face.TexInfo);
        }

        // The NODRAW path reads the first referenced brush's first side.
        if (m.NumFaces == 0 && brushes.Count > 0)
        {
            int firstBrush = brushes.Min;
            if (firstBrush >= 0 && firstBrush < input.Brushes.Count)
            {
                OfBrush(h, input, firstBrush);
            }
        }

        h.Bool(true);
        h.Bool(input.NoVirtualMesh);

        h.U32(input.WaterModels.Count);
        foreach (WaterModel water in input.WaterModels)
        {
            if (water.ModelIndex != 0)
            {
                continue;
            }

            h.I32(water.Contents);
            h.Bool(water.HasSurface);
            h.Vec3(water.SurfaceNormal);
            h.F32(water.SurfaceDist);
            h.I32(water.FogVolumeIndex);
            h.I32(water.SurfaceTexInfo);
            OfTexInfo(h, input, water.SurfaceTexInfo);
            h.U32(water.Leaves.Count);
            foreach (int leaf in water.Leaves)
            {
                h.I32(leaf);
                if (leaf >= 0 && leaf < input.Leafs.Count)
                {
                    OfLeaf(h, input, leaf);
                }
            }
        }

        h.U32(input.Displacements.Count);
        foreach (CollisionDisplacement disp in input.Displacements)
        {
            h.I32(disp.Contents);
            h.I32(disp.TexInfo);
            h.I32(disp.SurfaceProp2);
            OfTexInfo(h, input, disp.TexInfo);
            h.I32(disp.Core.Power);
            h.Vec3Span(disp.Core.Verts);
            h.Vec3Span(disp.Core.FlatVerts);
            h.F32Span(disp.Core.Alphas);
            h.U16Span(disp.Core.TriIndices);
        }

        // The surface-property index table.
        h.U32(input.SurfaceProperties.Count);
        foreach (int prop in input.SurfaceProperties)
        {
            h.I32(prop);
        }

        // The named database: names and physics the text blocks print.
        h.U32(input.SurfaceProps.Count);
        for (int p = 0; p < input.SurfaceProps.Count; p++)
        {
            h.Str(input.SurfaceProps.GetPropName(p));
            SurfacePhysics physics = input.SurfaceProps.GetPhysicsProperties(p);
            h.F32(physics.Density);
            h.F32(physics.Thickness);
            h.F32(physics.Friction);
            h.F32(physics.Elasticity);
        }
        return h.Finish();
    }

    private static void Walk(PhysCollisionInput input, int node, SortedSet<int> outBrushes)
    {
        if (node < 0)
        {
            int leafIndex = -1 - node;
            if (leafIndex < 0 || leafIndex >= input.Leafs.Count)
            {
                return;
            }

            DLeaf leaf = input.Leafs[leafIndex];
            for (int i = 0; i < leaf.NumLeafBrushes; i++)
            {
                int slot = leaf.FirstLeafBrush + i;
                if (slot >= 0 && slot < input.LeafBrushes.Count)
                {
                    outBrushes.Add(input.LeafBrushes[slot]);
                }
            }

            return;
        }

        if (node >= input.Nodes.Count)
        {
            return;
        }

        DNode n = input.Nodes[node];
        Walk(input, n.Children[0], outBrushes);
        Walk(input, n.Children[1], outBrushes);
    }

    private static void OfBrush(ModelDigest.CanonicalHash h, PhysCollisionInput input, int brush)
    {
        h.I32(brush);
        if (brush >= input.Brushes.Count)
        {
            h.Bool(false);
            return;
        }

        h.Bool(true);
        DBrush b = input.Brushes[brush];
        h.I32(b.FirstSide);
        h.I32(b.NumSides);
        h.I32(b.Contents);

        for (int s = 0; s < b.NumSides; s++)
        {
            int sideIndex = b.FirstSide + s;
            if (sideIndex >= input.BrushSides.Count)
            {
                h.Bool(false);
                continue;
            }

            h.Bool(true);
            DBrushSide side = input.BrushSides[sideIndex];
            h.U16(side.PlaneNum);
            h.I16(side.TexInfo);
            h.I16(side.DispInfo);
            h.I16(side.Bevel);

            if (side.PlaneNum < input.Planes.Count)
            {
                DPlane plane = input.Planes[side.PlaneNum];
                h.Vec3(plane.Normal);
                h.F32(plane.Dist);
            }

            OfTexInfo(h, input, side.TexInfo);

            // Per-side visibility as MarkVisibleSides left it (the shrink).
            h.Bool(SideVisible(input, brush, s) ?? true);
        }
    }

    private static void OfLeaf(ModelDigest.CanonicalHash h, PhysCollisionInput input, int leaf)
    {
        DLeaf l = input.Leafs[leaf];
        h.I32(l.Contents);
        h.I16(l.Cluster);
        h.U16(l.AreaFlags);
        h.U16(l.FirstLeafBrush);
        h.U16(l.NumLeafBrushes);
        h.I16(l.LeafWaterDataId);
    }

    private static void OfTexInfo(ModelDigest.CanonicalHash h, PhysCollisionInput input, int texInfo)
    {
        h.I32(texInfo);
        if (texInfo < 0 || texInfo >= input.TexInfos.Count)
        {
            return;
        }

        TexInfo t = input.TexInfos[texInfo];
        for (int k = 0; k < 8; k++)
        {
            h.F32(t.TextureVecsTexelsPerWorldUnits[k]);
            h.F32(t.LightmapVecsLuxelsPerWorldUnits[k]);
        }

        h.I32(t.Flags);
        h.I32(t.TexData);
        if (t.TexData >= 0 && t.TexData < input.SurfaceProperties.Count)
        {
            h.I32(input.SurfaceProperties[t.TexData]);
        }
    }

    private static bool? SideVisible(PhysCollisionInput input, int brush, int sideOffset)
    {
        if (input.SideVisible is not { } table)
        {
            return null; // "every side visible"
        }

        if (brush < 0 || brush >= table.Count)
        {
            return null;
        }

        IReadOnlyList<bool> sides = table[brush];
        return sideOffset < sides.Count && sides[sideOffset]; // stock: i >= numsides => shrunk
    }
}
