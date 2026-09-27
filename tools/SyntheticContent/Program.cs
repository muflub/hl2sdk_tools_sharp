//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Text;
using SourceSharp.MapGen.Content;

namespace SyntheticContentTool;

/// <summary>
/// <c>SyntheticContent [--content &lt;game dir&gt; [--force]] [--props-map &lt;in.vmf&gt; &lt;out.vmf&gt;]</c>
/// </summary>
/// <remarks>
/// Existing files are left alone unless <c>--force</c> is given, so pointing
/// this at a real game folder never replaces real content by accident.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        int content = Array.IndexOf(args, "--content");
        int at = Array.IndexOf(args, "--props-map");
        if ((content < 0 && at < 0) || args.Contains("-h") || args.Contains("--help")
            || (content >= 0 && content + 1 >= args.Length) || (at >= 0 && at + 2 >= args.Length))
        {
            Console.Error.WriteLine("usage: SyntheticContent [--content <game dir> [--force]] [--props-map <in.vmf> <out.vmf>]");
            return 2;
        }

        if (content >= 0)
        {
            string game = Path.GetFullPath(args[content + 1]);
            bool force = args.Contains("--force");
            int wrote = 0, kept = 0;
            foreach ((string path, byte[] bytes) in SyntheticContent.Build())
            {
                string file = Path.Combine(game, path);
                if (File.Exists(file) && !force)
                {
                    kept++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, bytes);
                wrote++;
            }

            Console.WriteLine($"synthetic content: {wrote} files written to {game}, {kept} existing files kept");
        }

        if (at >= 0)
        {
            VmfDocument map = await VmfDocument.ParseAsync(await File.ReadAllBytesAsync(args[at + 1]));
            (VmfDocument variant, int added) = await SyntheticContent.WithStaticPropsAsync(map);
            await File.WriteAllBytesAsync(args[at + 2], variant.ToBytes());
            Console.WriteLine($"static-prop map: {added} prop_static added, written to {args[at + 2]}");
        }

        return 0;
    }
}
