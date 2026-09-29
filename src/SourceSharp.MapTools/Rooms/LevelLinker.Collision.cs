//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

using SourceSharp.MapFormats.Bsp;

using SourceSharp.MapTools.Bsp.Collision;
using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys.Managed;

namespace SourceSharp.MapTools.Rooms;

public static partial class LevelLinker
{
    /// <summary>IVP's length unit is the metre; the map's is the inch.</summary>
    private const float MetersPerInch = 0.0254f;

    /// <summary>A VPHY blob's magic, <c>"VPHY"</c> read little-endian.</summary>
    private const uint VphyMagic = 0x59485056u;

    /// <summary>
    /// The linked world collision: every room's world convexes, moved to the
    /// room's placement, rebuilt into one surface per contents class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A room's <c>PhysCollide</c> lump holds one record for model 0 whose
    /// solids are static compact surfaces, one per contents class vbsp
    /// collects (solid, player clip, monster clip), named by
    /// <c>staticsolid</c> blocks in the keydata, plus a
    /// <c>materialtable</c> naming the surface properties the triangles'
    /// 7-bit material indices refer to. The convexes (IVP ledges) inside a
    /// surface each carry, as their game data, the index of the brush they
    /// were cooked from.
    /// </para>
    /// <para>
    /// The merge takes every leaf ledge out of every room's surface, drops
    /// the ledges of jointed plug brushes (the doorway must be passable to
    /// physics as well as to traces), moves each remaining ledge's points by
    /// the placement, renumbers its game data to the linked brush index (the
    /// stripped plugs are not in the linked brush lump, so each room's kept
    /// brushes are numbered without them: <see cref="KeptBrushes"/>) and
    /// its triangles' materials to the linked material table, and compiles
    /// the ledges of each contents class into one surface — which rebuilds
    /// the ledge tree, bounding radius and mass properties for the level.
    /// The ledges themselves are not re-cooked, so the linked map collides
    /// with exactly the convexes each room's own compile made.
    /// </para>
    /// <para>
    /// The move is in IVP's axes: a map point <c>(X, Y, Z)</c> is the ledge
    /// point <c>(X, -Z, Y)</c> in metres, so a quarter turn about the map's
    /// +z turns IVP's (x, z) the same way and leaves y alone, and the
    /// translation lands on x and z. The rebuild runs at the precision the
    /// context's compliance chooses, as the cooker would have.
    /// </para>
    /// <para>
    /// Rooms compiled without a cooker (<c>-cooker none</c>) have no
    /// collision lump; a level of them links with none. A level that mixes
    /// the two is refused: the rooms without would be walls you can walk
    /// through for physics.
    /// </para>
    /// </remarks>
    /// <returns>The <c>PhysCollide</c> and <c>PhysDisp</c> lumps, each null when no room had one.</returns>
    internal static (byte[]? PhysCollide, byte[]? PhysDisp) MergeCollision(
        RoomPlan[] plans, ComplianceOptions compliance, CancellationToken cancellationToken)
    {
        List<RoomPlan> with = [.. plans.Where(p => p.Bsp[BspLump.PhysCollide].Length > 0)];
        byte[]? physDisp = plans.Any(p => p.Bsp[BspLump.PhysDisp].Length > 0) ? PhysDispLump.Write([]) : null;
        if (with.Count == 0)
        {
            return (null, physDisp);
        }

        if (with.Count != plans.Length)
        {
            RoomPlan without = plans.First(p => p.Bsp[BspLump.PhysCollide].Length == 0);
            throw new LinkException(
                $"room {with[0].Placement.Room.Definition.Name} has world collision and room"
                + $" {without.Placement.Room.Definition.Name} has none (compiled with -cooker none?);"
                + " a linked level is cooked in every room or in none");
        }

        List<string> materials = [];
        List<int> contentsOrder = [];
        Dictionary<int, List<IvpCompactLedge>> groups = [];
        bool virtualTerrain = false;
        foreach (RoomPlan plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Read out and turned at room compile time (or now, for a room
            // without stored link data): what is left is the level's share,
            // which brushes it strips, the cell, the linked brush numbers and
            // material slots.
            // Never null here: this room has a collision lump, so the
            // computed collision is not null, and stored link data holds a
            // collision section only for a room that has one.
            RoomLinkCollision collide = CollisionFor(plan.Placement.Room, plan.Transform.Placement.NormalizedRotation)!;
            virtualTerrain |= collide.VirtualTerrain;

            // Room-local material index m (1-based into its table, 0 = none)
            // to the linked table's, with the cooker's own cap: past 126
            // names a material is 0.
            int[] remap = new int[collide.Materials.Count + 1];
            for (int m = 0; m < collide.Materials.Count; m++)
            {
                remap[m + 1] = MaterialIndex(materials, collide.Materials[m]);
            }

            foreach (RoomLinkSolid solid in collide.Solids)
            {
                if (!groups.TryGetValue(solid.Contents, out List<IvpCompactLedge>? group))
                {
                    groups[solid.Contents] = group = [];
                    contentsOrder.Add(solid.Contents);
                }

                for (int l = 0; l < solid.Starts.Length; l++)
                {
                    // The client data (the room-local brush) is at byte 4,
                    // and turning the points never touches it.
                    ReadOnlySpan<byte> turned = solid.Ledge(l);
                    if (plan.StrippedBrushes.Contains(BinaryPrimitives.ReadInt32LittleEndian(turned[4..])))
                    {
                        continue;
                    }

                    // A copy: the stored ledge is the room's, shared by every
                    // placement at this turn and every later link.
                    IvpCompactLedge ledge = new(turned.ToArray());
                    TranslateLedge(ledge, plan.Transform);
                    ledge.ClientData = plan.LinkedBrush(ledge.ClientData);
                    RemapMaterials(ledge, remap, plan);
                    group.Add(ledge);
                }
            }
        }

        List<byte[]> solids = [];
        CollisionTextBuffer text = new();
        using (ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(compliance))
        {
            foreach (int contents in contentsOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cooker.CompileLedges(groups[contents]) is not { } blob)
                {
                    continue;
                }

                new PhysStaticSolidEntry(blob, contents).WriteText(text, solids.Count);
                solids.Add(blob);
            }
        }

        if (solids.Count == 0)
        {
            return (null, physDisp);
        }

        if (virtualTerrain)
        {
            text.WriteText("virtualterrain {}\n");
        }

        if (materials.Count > 0)
        {
            text.WriteText("materialtable {\n");
            for (int m = 0; m < materials.Count; m++)
            {
                text.WriteIntKey(materials[m], m + 1);
            }

            text.WriteText("}\n");
        }

        text.Terminate();
        return (PhysCollideLump.Write([new PhysCollideModel(0, solids, text.ToArray())]), physDisp);
    }

    /// <summary>One room's world collision, read and checked.</summary>
    /// <param name="Solids">Each static solid's contents class and VPHY blob, in solid order.</param>
    /// <param name="Materials">The material table's names, index 1 first.</param>
    /// <param name="VirtualTerrain">Whether the keydata announced virtual terrain.</param>
    internal sealed record RoomCollide(IReadOnlyList<(int Contents, byte[] Blob)> Solids, IReadOnlyList<string> Materials, bool VirtualTerrain);

    /// <summary>Reads a room's <c>PhysCollide</c>, refusing any record or block the merge does not understand.</summary>
    internal static RoomCollide ReadRoomCollide(BspData bsp, string name)
    {
        IReadOnlyList<PhysCollideModel> records;
        try
        {
            records = PhysCollideLump.Read(bsp[BspLump.PhysCollide].Data.Span);
        }
        catch (MapCompileException exception)
        {
            throw new LinkException($"room {name}'s world collision is not a collision lump: {exception.Message}");
        }

        if (records.Count != 1 || records[0].ModelIndex != 0)
        {
            throw new LinkException(
                $"room {name}'s world collision has {records.Count} records; a linkable room has one, for model 0");
        }

        PhysCollideModel record = records[0];
        (List<(int Index, int Contents)> statics, List<string> materials, bool virtualTerrain) =
            ParseKeyData(record.KeyText, name);

        List<(int, byte[])> solids = [];
        for (int i = 0; i < record.Solids.Count; i++)
        {
            int at = statics.FindIndex(s => s.Index == i);
            if (at < 0)
            {
                throw new LinkException($"room {name}'s world collision solid {i} has no staticsolid block");
            }

            byte[] blob = record.Solids[i];
            if (blob.Length < VphyWriter.HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(blob) != VphyMagic
                || BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(6)) != 0)
            {
                throw new LinkException($"room {name}'s world collision solid {i} is not a compact surface");
            }

            solids.Add((statics[at].Contents, blob));
        }

        if (statics.Count != record.Solids.Count)
        {
            throw new LinkException(
                $"room {name}'s world collision names {statics.Count} static solids for {record.Solids.Count} solids");
        }

        return new RoomCollide(solids, materials, virtualTerrain);
    }

    /// <summary>
    /// The world keydata: <c>staticsolid</c> blocks (index, contents), an
    /// optional empty <c>virtualterrain</c> block, and an optional
    /// <c>materialtable</c>. Any other block — a movable <c>solid</c>, a
    /// <c>fluid</c> — is a room the relocation does not carry.
    /// </summary>
    internal static (List<(int Index, int Contents)> Statics, List<string> Materials, bool VirtualTerrain) ParseKeyData(
        string text, string room)
    {
        List<(int, int)> statics = [];
        List<string> materials = [];
        bool virtualTerrain = false;
        List<string> tokens = Tokenize(text, room);
        int at = 0;
        while (at < tokens.Count)
        {
            string block = tokens[at++];
            if (at >= tokens.Count || tokens[at++] != "{")
            {
                throw new LinkException($"room {room}'s collision keydata has \"{block}\" without a block");
            }

            List<(string Key, string Value)> pairs = [];
            while (at < tokens.Count && tokens[at] != "}")
            {
                if (at + 1 >= tokens.Count || tokens[at + 1] is "{" or "}")
                {
                    throw new LinkException($"room {room}'s collision keydata block \"{block}\" has a key without a value");
                }

                pairs.Add((tokens[at], tokens[at + 1]));
                at += 2;
            }

            if (at >= tokens.Count)
            {
                throw new LinkException($"room {room}'s collision keydata block \"{block}\" is not closed");
            }

            at++; // "}"
            switch (block)
            {
                case "staticsolid":
                    statics.Add((KeyInt(pairs, "index", block, room), KeyInt(pairs, "contents", block, room)));
                    break;
                case "virtualterrain":
                    virtualTerrain = true;
                    break;
                case "materialtable":
                    string[] names = new string[pairs.Count];
                    foreach ((string key, string value) in pairs)
                    {
                        int index = ParseKeyInt(value, key, block, room);
                        if (index < 1 || index > names.Length || names[index - 1] is not null)
                        {
                            throw new LinkException(
                                $"room {room}'s collision material table gives \"{key}\" slot {index} of {names.Length}");
                        }

                        names[index - 1] = key;
                    }

                    materials.AddRange(names);
                    break;
                default:
                    throw new LinkException(
                        $"room {room}'s world collision has a \"{block}\" block; the relocation carries static world solids only");
            }
        }

        return (statics, materials, virtualTerrain);
    }

    private static int KeyInt(List<(string Key, string Value)> pairs, string key, string block, string room)
    {
        foreach ((string k, string v) in pairs)
        {
            if (k == key)
            {
                return ParseKeyInt(v, key, block, room);
            }
        }

        throw new LinkException($"room {room}'s collision keydata block \"{block}\" has no \"{key}\"");
    }

    private static int ParseKeyInt(string value, string key, string block, string room) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : throw new LinkException($"room {room}'s collision keydata \"{block}\" \"{key}\" is \"{value}\", not an integer");

    /// <summary>Quoted strings, bare words and braces, as the keydata writer emits them.</summary>
    private static List<string> Tokenize(string text, string room)
    {
        List<string> tokens = [];
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == '\0')
            {
                i++;
            }
            else if (c is '{' or '}')
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (c == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0)
                {
                    throw new LinkException($"room {room}'s collision keydata has an unclosed quote");
                }

                tokens.Add(text[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"' or '\0'))
                {
                    i++;
                }

                tokens.Add(text[start..i]);
            }
        }

        return tokens;
    }

    /// <summary>A material's 1-based slot in the linked table, added if new; 0 once the table holds 126.</summary>
    internal static int MaterialIndex(List<string> table, string name)
    {
        int at = table.IndexOf(name);
        if (at >= 0)
        {
            return at + 1;
        }

        if (table.Count < 126)
        {
            table.Add(name);
            return table.Count;
        }

        return 0;
    }

    /// <summary>A ledge's points through the placement, in IVP's axes.</summary>
    /// <remarks>
    /// The turn (<see cref="RotateLedge"/>) and then the translation
    /// (<see cref="TranslateLedge"/>); the link stores the first per room and
    /// makes only the second, and each point's value is the same either way,
    /// because the turn is a negation and a swap and rounds nothing.
    /// </remarks>
    internal static void MoveLedge(IvpCompactLedge ledge, RoomTransform transform)
    {
        RotateLedge(ledge, transform.Placement.NormalizedRotation);
        TranslateLedge(ledge, transform);
    }

    /// <summary>
    /// A quarter turn of IVP's horizontal axes: <c>(x, z)</c> is the map's
    /// <c>(X, Y)</c> in metres, turned as the map turns.
    /// </summary>
    private static (float X, float Z) TurnIvp(float x, float z, int turns) => turns switch
    {
        0 => (x, z),
        1 => (-z, x),
        2 => (-x, -z),
        _ => (z, -x),
    };

    /// <summary>The placement's translation on IVP's horizontal axes, in metres.</summary>
    private static Vec3Ivp IvpTranslation(RoomTransform transform) =>
        new(transform.Apply(default).X * MetersPerInch, transform.Apply(default).Y * MetersPerInch);

    /// <summary>The placement's translation on IVP's horizontal axes, x and z.</summary>
    private readonly record struct Vec3Ivp(float X, float Z);

    /// <summary>Each triangle's 7-bit material index through the room's table remap.</summary>
    private static void RemapMaterials(IvpCompactLedge ledge, int[] remap, RoomPlan plan)
    {
        for (int t = 0; t < ledge.TriangleCount; t++)
        {
            uint word = ledge.TriangleWord(t);
            int material = (int)((word >> 24) & 0x7f);
            if (material == 0)
            {
                continue;
            }

            if (material >= remap.Length)
            {
                throw new LinkException(
                    $"room {plan.Placement.Room.Definition.Name}'s world collision uses material {material},"
                    + $" but its table names {remap.Length - 1}");
            }

            ledge.SetTriangleWord(t, (word & ~(0x7fu << 24)) | ((uint)remap[material] << 24));
        }
    }
}
