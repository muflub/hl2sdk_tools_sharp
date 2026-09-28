//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapGen.Rooms;

/// <summary>
/// The checked-in 3x3 rooms sample, as files: everything under
/// <c>samples/rooms-3x3/</c> except its hand-written README.
/// </summary>
/// <remarks>
/// <para>
/// The folder is a game folder of its own (a <c>gameinfo.txt</c> that mounts
/// only itself, and the kit's materials), so <c>ssmap room rooms.vmf -game .</c>
/// compiles the rooms with no Steam install.
/// </para>
/// <list type="bullet">
/// <item><c>rooms.vmf</c>: the room library, the five kinds in a line, each marked by an <c>info_room</c>;</item>
/// <item><c>levels/rooms3x3*.yaml</c>: the sample level and its three whole-level quarter turns;</item>
/// <item><c>levels/seed_*.yaml</c>: some of the seeded levels the tests check, as <c>ssmap layout</c> writes them.</item>
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

    /// <summary>How a level file in <c>levels/</c> names the library.</summary>
    public const string LibraryFromLevels = "../" + Rooms3x3Kit.LibraryFile;

    /// <summary>Every generated file: its path under <see cref="Folder"/> and its bytes, in ordinal path order.</summary>
    public static IReadOnlyDictionary<string, byte[]> Build()
    {
        SortedDictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["gameinfo.txt"] = Encoding.UTF8.GetBytes(Rooms3x3Kit.GameInfo),
            [Rooms3x3Kit.LibraryFile] = Encoding.UTF8.GetBytes(Rooms3x3Kit.LibraryVmf()),
        };

        foreach ((string path, string vmt) in Rooms3x3Kit.Materials())
        {
            files[path] = Encoding.UTF8.GetBytes(vmt);
        }

        Rooms3x3Arrangement level = Rooms3x3Permutations.Canonical;
        for (int turns = 0; turns < 4; turns++)
        {
            string name = Rooms3x3Permutations.TurnName(turns);
            files[$"levels/{name}.yaml"] = Encoding.UTF8.GetBytes(level.LevelYaml(name, LibraryFromLevels,
            [
                turns == 0
                    ? "the 3x3 rooms sample level"
                    : string.Create(CultureInfo.InvariantCulture, $"the 3x3 rooms sample level turned {turns * 90} degrees"),
                "the grid's first line is the north row; each line runs west to east",
            ]));
            level = level.Turned();
        }

        // Exactly what `ssmap layout ../rooms.vmf -rows 3 -columns 3 -seed N
        // [-empty R] -out levels/<name>.yaml` writes; a fact runs the verb
        // and compares.
        foreach (string name in Rooms3x3Permutations.SampleSeeds)
        {
            (_, ulong seed, double empty) = Rooms3x3Permutations.Seeds.Single(s => s.Name == name);
            LevelGrid seeded = Rooms3x3Permutations.Seeded(name, LibraryFromLevels);
            LevelGeneratorOptions options = new(Rooms3x3Arrangement.Size, Rooms3x3Arrangement.Size, seed, empty);
            files[$"levels/{name}.yaml"] = Encoding.UTF8.GetBytes(
                LevelYaml.Write(seeded, LevelGenerator.Header(options, seeded)));
        }

        return files;
    }
}
