//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

using SourceSharp.MapFormats.Bsp;

using SourceSharp.MapTools.Diagnostics;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Parallel;
using SourceSharp.MapTools.Rad;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap vrad</c>: the lighting stage, spelled the way stock spells it.
/// </summary>
/// <remarks>
/// <para>
/// A thin client of <see cref="Vrad.LightAsync(SourceSharp.MapFormats.Bsp.BspData, VradContext, CancellationToken)"/>; everything printed here is a
/// property of the <see cref="RadResult"/> the library returned.
/// </para>
/// <para>
/// Stock reads <c>lights.rad</c> from the game's search path and
/// <c>&lt;map&gt;.rad</c> from beside the <c>.bsp</c>;
/// the library reads both through one <see cref="IContentFileSystem"/>, so this
/// command layers the map's own <c>.rad</c> and the <c>-lights</c> file over
/// the mounted game. Stock's last resort -- <c>lights.rad</c> beside
/// <c>vrad.exe</c> -- has no meaning for a library and is not tried.
/// </para>
/// </remarks>
public static class VradCommand
{
    /// <summary>The exit code for a map that could not be lit.</summary>
    public const int ExitFailed = 1;

    /// <summary>Runs one <c>vrad</c> invocation.</summary>
    /// <param name="fileSystem">Where maps and game content are read and written.</param>
    /// <param name="args">The arguments after <c>vrad</c>.</param>
    /// <param name="output">Where the running commentary goes.</param>
    /// <param name="cancellationToken">Cancels the compile.</param>
    /// <returns>
    /// <see cref="Program.ExitSuccess"/>, <see cref="ExitFailed"/> or
    /// <see cref="Program.ExitUsage"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<int> RunAsync(
        IFileSystem fileSystem,
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        StockArgsResult<VradOptions> parsed = StockArgs.ParseVrad(args);

        if (parsed.ListCompliance && !parsed.HasErrors)
        {
            await output.WriteAsync(ComplianceCatalogue.Format(CompileTools.Vrad)).ConfigureAwait(false);
            return Program.ExitSuccess;
        }

        foreach (CompileDiagnostic diagnostic in parsed.Diagnostics)
        {
            await output.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Message}").ConfigureAwait(false);
        }

        if (parsed.HasErrors || parsed.MapPath is null)
        {
            await output.WriteLineAsync("usage: ssmap vrad [stock options] <map>").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        // Stock: the extension is stripped, the
        // base name is the level's name, and ".bsp" is put back.
        string full = Path.GetFullPath(parsed.MapPath);
        string source = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full));
        string bspPath = source + ".bsp";
        string mapName = Path.GetFileName(source);

        if (!VPath.TryCreate(bspPath, out VPath bsp))
        {
            await output.WriteLineAsync($"ssmap vrad: \"{bspPath}\" is not a usable path").ConfigureAwait(false);
            return Program.ExitUsage;
        }

        if (!await fileSystem.ExistsAsync(bsp, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync($"ssmap vrad: no such file: {bspPath}").ConfigureAwait(false);
            return ExitFailed;
        }

        Stopwatch clock = Stopwatch.StartNew();

        IContentFileSystem? game = await MountGameAsync(fileSystem, parsed.GameDirectory, source, output, cancellationToken)
            .ConfigureAwait(false);
        LooseFileContent content = new(fileSystem, game);
        content.Add(mapName + ".rad", source + ".rad");
        if (parsed.Options.LightsFile is { Length: > 0 } lights)
        {
            content.Add(lights, Path.GetFullPath(lights));
        }

        await output.WriteLineAsync($"Loading {bspPath}").ConfigureAwait(false);
        BspData map;
        await using (Stream stream = await fileSystem.OpenReadAsync(bsp, cancellationToken).ConfigureAwait(false))
        {
            map = await BspFile.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        VradContext contextBase = new()
        {
            Options = parsed.Options,
            MapName = mapName,
            Content = content,
            Parallelism = parsed.Threads is int degree && degree > 0
                ? new CompileParallelism { MaxDegree = degree }
                : CompileParallelism.Default,
        };

        // The -gpu seam (plan 10c): the same host factory the chain wires. A
        // decline is one VRAD0707 warning in the result and the CPU KD tracer.
        VradContext context = parsed.GpuDeviceMatch is null
            ? contextBase
            : contextBase with
            {
                GpuTracerFactory = new HostBackends.GpuFactory(
                    parsed.GpuDeviceMatch, parsed.GpuRaysPerSlab),
            };

        RadResult result;
        try
        {
            result = await Vrad.LightAsync(map, context, cancellationToken).ConfigureAwait(false);
        }
        catch (MapCompileException exception)
        {
            await output.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return ExitFailed;
        }

        await WriteResultAsync(result, output).ConfigureAwait(false);

        await output.WriteLineAsync($"Writing {bspPath}").ConfigureAwait(false);
        using (MemoryStream buffer = new())
        {
            await BspFile.SaveAsync(map, buffer, BspWriteMode.Canonical, cancellationToken).ConfigureAwait(false);
            byte[] bytes = buffer.ToArray();
            await fileSystem.ReplaceAsync(
                bsp,
                async (stream, token) => await stream.WriteAsync(bytes, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture, $"{clock.Elapsed.TotalSeconds:F1} seconds elapsed")).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    /// <summary>
    /// The game named by <c>-game</c>, or the one two directories above the
    /// map (<c>game/maps/x.bsp</c>) when that has a <c>gameinfo.txt</c>; null,
    /// with a note, when there is none -- a map without props or macro
    /// textures lights the same without it.
    /// </summary>
    private static async Task<IContentFileSystem?> MountGameAsync(
        IFileSystem fileSystem,
        string? gameDirectory,
        string source,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string directory = gameDirectory is null
            ? Path.GetDirectoryName(Path.GetDirectoryName(source)!) ?? "/"
            : Path.GetFullPath(gameDirectory);
        string gameInfo = Path.Combine(directory, "gameinfo.txt");

        if (!VPath.TryCreate(gameInfo, out VPath info)
            || !await fileSystem.ExistsAsync(info, cancellationToken).ConfigureAwait(false))
        {
            if (gameDirectory is not null)
            {
                await output.WriteLineAsync($"ssmap vrad: no gameinfo.txt in {directory}").ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync(
                    $"ssmap vrad: no game content (no gameinfo.txt in {directory}); pass -game to mount one")
                    .ConfigureAwait(false);
            }

            return null;
        }

        try
        {
            GameContentMounter.Result mounted = await GameContentMounter.MountAsync(
                new ReadOnlyFileSystem(fileSystem),
                info,
                VPath.Create(Path.GetDirectoryName(directory)!),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return mounted.Content;
        }
        catch (IOException exception)
        {
            await output.WriteLineAsync($"ssmap vrad: cannot mount {directory}: {exception.Message}").ConfigureAwait(false);
            return null;
        }
    }

    private static async Task WriteResultAsync(RadResult result, TextWriter output)
    {
        foreach (RadPassResult pass in result.Passes)
        {
            string range = pass.Hdr ? "HDR" : "LDR";
            await output.WriteLineAsync($"[{range}] {pass.World.Faces} faces").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"[{range}] {pass.World.Subdivision.PatchesBefore} patches before subdivision").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"[{range}] {pass.World.Subdivision.PatchesAfter} patches after subdivision").ConfigureAwait(false);
            await output.WriteLineAsync($"[{range}] {pass.World.DirectLights} direct lights").ConfigureAwait(false);
            await output.WriteLineAsync($"[{range}] lightdata {pass.LightDataSize} bytes").ConfigureAwait(false);
        }

        foreach (CompileDiagnostic d in result.Diagnostics)
        {
            await output.WriteLineAsync($"{d.Severity} {d.Code}: {d.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A few named files from disk, found by name ahead of the game content:
    /// the level's <c>.rad</c> beside the map, and the <c>-lights</c> file.
    /// </summary>
    internal sealed class LooseFileContent(IFileSystem fileSystem, IContentFileSystem? inner) : IContentFileSystem
    {
        private readonly Dictionary<string, VPath> _files = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Serves <paramref name="name"/> from the disk file <paramref name="path"/>.</summary>
        public void Add(string name, string path)
        {
            if (VPath.TryCreate(name, out VPath key) && VPath.TryCreate(path, out VPath file))
            {
                _files[key.Value] = file;
            }
        }

        public async ValueTask<ContentSource?> ResolveAsync(VPath path, CancellationToken cancellationToken = default)
        {
            if (_files.TryGetValue(path.Value, out VPath file)
                && await fileSystem.ExistsAsync(file, cancellationToken).ConfigureAwait(false))
            {
                return new ContentSource(file, "map");
            }

            return inner is null ? null : await inner.ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<IMemoryOwner<byte>?> ReadAsync(VPath path, CancellationToken cancellationToken = default)
        {
            if (_files.TryGetValue(path.Value, out VPath file)
                && await fileSystem.ExistsAsync(file, cancellationToken).ConfigureAwait(false))
            {
                return await fileSystem.ReadAllAsync(file, cancellationToken).ConfigureAwait(false);
            }

            return inner is null ? null : await inner.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<VPath> EnumerateAsync(
            VPath directory,
            string searchPattern = "*",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (inner is null)
            {
                yield break;
            }

            await foreach (VPath path in inner.EnumerateAsync(directory, searchPattern, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return path;
            }
        }
    }
}
