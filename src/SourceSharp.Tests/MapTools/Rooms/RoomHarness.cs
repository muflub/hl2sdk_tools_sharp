//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// The room/link tests' compile harness: in-memory materials, a room built by
/// <see cref="RoomModel"/>, and the two compiles a level needs.
/// </summary>
/// <remarks>
/// Same shape as <c>UnitMap</c> — no disk, no game install, milliseconds — with
/// the two materials the room model names plus a trigger, which is what the
/// socket plugs are made of.
/// </remarks>
internal static class RoomHarness
{
    public const string Plain = "unit/plain";
    public const string Trigger = "unit/trigger";

    /// <summary>The standard kit, sized for the test cell so compiles stay small.</summary>
    public const float Cell = 256f;

    public static SocketKit Kit { get; } = new(96f, 96f, 16f);

    public static async Task<VbspContext> ContextAsync(VbspOptions? options = null, int degree = 1)
    {
        InMemoryFileSystem files = new();
        files.AddText(
            $"materials/{Plain}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n}\n");
        files.AddText(
            $"materials/{Trigger}.vmt",
            "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileTrigger\" \"1\"\n}\n");

        DirectoryContentMount mount = await DirectoryContentMount.MountAsync(files, VPath.Empty);
        ContentFileSystem content = new([mount]);

        return new VbspContext(options ?? VbspOptions.Default, content) { MapBase = "roomtest" };
    }

    /// <summary>A room definition for the test cell, with the listed faces open.</summary>
    public static RoomDefinition Room(string name, params RoomFacing[] open)
    {
        List<RoomSocket> sockets = [];
        foreach (RoomFacing facing in open)
        {
            sockets.Add(new RoomSocket(facing, facing.ToString()));
        }

        return new RoomDefinition(name, Cell, Kit, sockets);
    }

    /// <summary>
    /// The room VMF with its plugs written as the harness's trigger material —
    /// the plug material the model names is a game path the in-memory mount
    /// cannot have, and the compile flag is what matters.
    /// </summary>
    public static VmfDocument BuildRoomModel(RoomDefinition definition, string shell = Plain, string plug = Trigger)
    {
        VmfDocument document = RoomModel.Build(definition, Kit.Depth, shell);
        foreach (VmfChunk chunk in document.Chunks)
        {
            RenumberMaterials(chunk, RoomModel.PlugMaterial, plug);
        }

        // The leak reporter walks from a player start; every room's cell center
        // is (cell/2, cell/2, cell/2), room-local.
        VmfChunk start = new(MapFileLoader.EntityChunk);
        start.AddKey("id", "900000");
        start.AddKey("classname", "info_player_start");
        start.AddKey("origin", RoomModel.Tuple(definition.CellSize / 2f, definition.CellSize / 2f, definition.CellSize / 2f + 1f));
        document.Chunks.Add(start);
        return document;
    }

    private static void RenumberMaterials(VmfChunk chunk, string from, string to)
    {
        foreach (VmfKey key in chunk.Keys)
        {
            if (string.Equals(key.Name, "material", StringComparison.Ordinal)
                && string.Equals(key.Value, from, StringComparison.Ordinal))
            {
                key.Value = to;
            }
        }

        foreach (VmfChunk child in chunk.Chunks)
        {
            RenumberMaterials(child, from, to);
        }
    }

    /// <summary>Compiles a document to a BSP with its portals.</summary>
    public static async Task<VbspResult> CompileAsync(VmfDocument document, VbspContext context)
    {
        MapFile map = await MapFileLoader.LoadAsync(context, document, CancellationToken.None);
        MapFileReader.TakeBounds(map);
        return await Vbsp.CompileAsync(map, context, CancellationToken.None);
    }

    /// <summary>The leaf clusters a compiled map's vis produced, in leaf order.</summary>
    public static List<short> Clusters(BspData bsp)
    {
        List<short> result = [];
        foreach (DLeaf leaf in BspStructView.As<DLeaf>(bsp[BspLump.Leafs]))
        {
            result.Add(leaf.Cluster);
        }

        return result;
    }

    public static async Task<VisResult> VisAsync(BspData bsp, PortalSet portals, int degree = 1)
    {
        VisContext context = new()
        {
            Parallelism = new CompileParallelism { MaxDegree = degree },
        };
        return await Vvis.ComputeAsync(bsp, portals, context, CancellationToken.None);
    }
}
