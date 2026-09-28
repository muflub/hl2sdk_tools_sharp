//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Text;

namespace SourceSharp.MapGen.Rooms;

/// <summary>
/// The checked-in 3x3 rooms sample, as files: everything under
/// <c>samples/rooms-3x3/</c> except its hand-written README.
/// </summary>
/// <remarks>
/// <para>
/// The folder is a game folder of its own (a <c>gameinfo.txt</c> that mounts
/// only itself, and the kit's materials), so <c>ssmap room maps/&lt;kind&gt;.vmf</c>
/// finds its game by the usual rule — the map folder's parent — and the
/// sample compiles with no Steam install.
/// </para>
/// <list type="bullet">
/// <item><c>maps/&lt;kind&gt;.vmf</c> and <c>maps/&lt;kind&gt;.vmf.roomdef.json</c>: the five rooms and their definitions;</item>
/// <item><c>layouts/rooms3x3*.json</c>: the sample level and its three whole-level quarter turns;</item>
/// <item><c>maps/rooms3x3*.vmf</c>: each of those as one monolithic VMF, the reference the linked map is checked against.</item>
/// </list>
/// <para>
/// Deterministic: the same bytes on every run and every host (fixed order,
/// invariant number formatting, LF line ends). A fact holds the checked-in
/// files to exactly what this returns.
/// </para>
/// </remarks>
public static class Rooms3x3Sample
{
    /// <summary>The sample's folder, repository-relative.</summary>
    public const string Folder = "samples/rooms-3x3";

    /// <summary>Every generated file: its path under <see cref="Folder"/> and its bytes, in ordinal path order.</summary>
    public static IReadOnlyDictionary<string, byte[]> Build()
    {
        SortedDictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["gameinfo.txt"] = Encoding.UTF8.GetBytes(Rooms3x3Kit.GameInfo),
        };

        foreach ((string path, string vmt) in Rooms3x3Kit.Materials())
        {
            files[path] = Encoding.UTF8.GetBytes(vmt);
        }

        foreach (RoomKind kind in Rooms3x3Kit.Kinds)
        {
            files[$"maps/{kind.Name}.vmf"] = Encoding.UTF8.GetBytes(Rooms3x3Kit.RoomVmf(kind));
            files[$"maps/{kind.Name}.vmf.roomdef.json"] = Encoding.UTF8.GetBytes(Rooms3x3Kit.RoomDefinitionJson(kind));
        }

        Rooms3x3Arrangement level = Rooms3x3Permutations.Canonical;
        for (int turns = 0; turns < 4; turns++)
        {
            string name = Rooms3x3Permutations.TurnName(turns);
            files[$"layouts/{name}.json"] = Encoding.UTF8.GetBytes(level.LayoutJson(name));
            files[$"maps/{name}.vmf"] = Encoding.UTF8.GetBytes(level.MonolithicVmf());
            level = level.Turned();
        }

        return files;
    }
}
