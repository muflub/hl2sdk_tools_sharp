//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapGen.Rooms;

namespace RoomsSampleTool;

/// <summary>
/// <c>RoomsSample --out &lt;dir&gt; | --check &lt;dir&gt; | --list | --stress &lt;dir&gt; [rooms]</c>
/// </summary>
/// <remarks>
/// <c>--out</c> writes every generated file (the README is hand-written and
/// left alone); <c>--check</c> reports files that differ from, or are missing
/// against, the generator and exits 1 if any do; <c>--list</c> prints how many
/// valid arrangements there are and which ones the default test run checks;
/// <c>--stress</c> writes a game folder whose library holds <c>rooms</c>
/// distinct rooms (1024 by default) for stress and performance runs
/// (<see cref="RoomsStressLibrary"/>). The stress library is tens of
/// megabytes, so it is written to a scratch folder, not checked in.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--out")
        {
            string root = Path.GetFullPath(args[1]);
            IReadOnlyDictionary<string, byte[]> files = Rooms3x3Sample.Build();
            foreach ((string path, byte[] bytes) in files)
            {
                string file = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, bytes);
            }

            Console.WriteLine($"rooms sample: {files.Count} files written to {root}");
            return 0;
        }

        if (args.Length == 2 && args[0] == "--check")
        {
            string root = Path.GetFullPath(args[1]);
            int bad = 0;
            foreach ((string path, byte[] bytes) in Rooms3x3Sample.Build())
            {
                string file = Path.Combine(root, path);
                if (!File.Exists(file) || !(await File.ReadAllBytesAsync(file)).AsSpan().SequenceEqual(bytes))
                {
                    Console.WriteLine($"differs: {path}");
                    bad++;
                }
            }

            Console.WriteLine(bad == 0 ? "rooms sample: up to date" : $"rooms sample: {bad} file(s) differ");
            return bad == 0 ? 0 : 1;
        }

        if (args.Length == 1 && args[0] == "--list")
        {
            IReadOnlyList<Rooms3x3Arrangement> all = Rooms3x3Permutations.All();
            Console.WriteLine($"{all.Count} valid arrangements of the rooms of {Rooms3x3Permutations.Canonical}");
            foreach (Rooms3x3Case c in Rooms3x3Permutations.DefaultCases())
            {
                Console.WriteLine($"{c.Name,-18} {c.Arrangement}  ({c.Reason})");
            }

            return 0;
        }

        if (args.Length is 2 or 3 && args[0] == "--stress")
        {
            int count = RoomsStressLibrary.DefaultCount;
            if (args.Length == 3 && (!int.TryParse(args[2], out count) || count < 1 || count > RoomsStressLibrary.MaxCount))
            {
                Console.Error.WriteLine($"RoomsSample --stress: the room count is a whole number from 1 to {RoomsStressLibrary.MaxCount}");
                return 2;
            }

            string root = Path.GetFullPath(args[1]);
            IReadOnlyDictionary<string, byte[]> files = RoomsStressLibrary.Build(count);
            foreach ((string path, byte[] bytes) in files)
            {
                string file = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, bytes);
            }

            Console.WriteLine($"rooms stress library: {count} rooms, {files.Count} files written to {root}");
            return 0;
        }

        Console.Error.WriteLine("usage: RoomsSample --out <dir> | --check <dir> | --list | --stress <dir> [rooms]");
        return 2;
    }
}
