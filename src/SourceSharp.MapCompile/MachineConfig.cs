//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Rad.Light;

namespace SourceSharp.MapCompile;

/// <summary>
/// <c>ssmap</c>'s per-machine defaults: a small file of <c>key = value</c>
/// lines, grouped by verb, that fill in what a command line leaves out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a machine file.</b> Some choices belong to the machine rather than
/// to the map or the person: how many threads this box should give a compile,
/// which vvis separator path is faster on this CPU, which GPU vrad should
/// trace on. Typed on every command line they are forgotten; baked into the
/// libraries they would be a guess about every machine at once. The libraries
/// only ever receive options (<see cref="VvisOptions"/> and the rest); this
/// file is the CLI host's, read through the host's own <see cref="IFileSystem"/>.
/// </para>
/// <para>
/// <b>The format.</b>
/// </para>
/// <code>
/// # ~/.config/ssmap/config
/// # every verb: sixteen threads unless the command line says otherwise
/// threads = 16
///
/// [vvis]
/// # auto | 256 | 512
/// separator = auto
///
/// [vrad]
/// gpu = auto
/// gpu_depth = 3
/// </code>
/// <para>
/// A line is blank, a comment (first non-blank character <c>#</c> or
/// <c>;</c>), a section header <c>[vbsp]</c>, <c>[vvis]</c>, <c>[vrad]</c> or
/// <c>[all]</c>, or <c>key = value</c>. Keys before the first header apply to
/// every verb that has them. Keys and section names are case-insensitive;
/// surrounding blanks are trimmed. A <c>#</c> after a value is part of the
/// value (a GPU name may contain one), so comments take a line of their own.
/// </para>
/// <para>
/// <b>Keys.</b> Each is the default for one command-line flag, and only the
/// verbs that take that flag accept it: <c>threads</c> (<c>-threads</c>; every
/// verb), <c>separator</c> (<c>-separator</c>; vvis and all), <c>gpu</c>
/// (<c>-gpu</c>; vrad and all) and <c>gpu_depth</c> (<c>-gpu_depth</c>; vrad
/// and all). Another default -- <c>-fastflow</c>, say -- is one more row in
/// <see cref="Keys"/>, and nothing else changes.
/// </para>
/// <para>
/// <b>Which value a verb gets.</b> Its own section's, else -- for <c>all</c>
/// -- the section of the stage the key belongs to (<c>separator</c> from
/// <c>[vvis]</c>, <c>gpu</c> and <c>gpu_depth</c> from <c>[vrad]</c>; the
/// chain's <c>threads</c> is its own, never a stage's), else the unsectioned
/// value, else nothing.
/// </para>
/// <para>
/// <b>Precedence: flag, then config, then the built-in default.</b> A value
/// is applied only when the command line does not give its flag anywhere, by
/// adding the flag to the command line (<see cref="Apply"/>). So a flag
/// always wins, and <c>all</c>'s rule that the chain has one <c>-threads</c>
/// never sees a config value beside a typed one. And because the config
/// becomes arguments, a verb that relaunches itself (the native cooker) hands
/// the child the same effective command line.
/// </para>
/// <para>
/// <b>Errors, not warnings.</b> An unknown key or section, a key in a section
/// that does not take it, a key given twice in one section, or a value that
/// does not parse is an error naming the file and line, and the command does
/// not run. A machine file is written once and then forgotten; a typo that
/// only warned would scroll past and leave the default silently in place on
/// every compile after it. The cost is that an older <c>ssmap</c> refuses a
/// newer key -- a clear message where the alternative is a silent one.
/// </para>
/// </remarks>
public sealed class MachineConfig
{
    /// <summary>The config's directory name under the user's config directory.</summary>
    public const string DirectoryName = "ssmap";

    /// <summary>The config's file name.</summary>
    public const string FileName = "config";

    /// <summary>Names a config file instead of the default location.</summary>
    public const string ConfigSwitch = "--config";

    /// <summary>Reads no config file at all.</summary>
    public const string NoConfigSwitch = "--no-config";

    private readonly Dictionary<(string Section, string Key), string> _values;

    private MachineConfig(string source, Dictionary<(string Section, string Key), string> values)
    {
        Source = source;
        _values = values;
    }

    /// <summary>The verbs a config applies to, which are also its section names.</summary>
    public static IReadOnlyList<string> Verbs { get; } = ["vbsp", "vvis", "vrad", "all"];

    /// <summary>
    /// The keys: each one's command-line flag, the verbs that take it, and
    /// for <c>all</c> the stage section it falls back to and where its flag
    /// goes (<c>null</c> for the chain options before the map, otherwise the
    /// section switch it follows).
    /// </summary>
    public static IReadOnlyList<MachineConfigKey> Keys { get; } =
    [
        new("threads", "-threads", ["vbsp", "vvis", "vrad", "all"], Stage: null, AllSection: null, ValidateThreads),
        new("separator", StockArgs.SeparatorFlag, ["vvis", "all"], Stage: "vvis", AllSection: AllCommand.VvisSection, ValidateSeparator),
        new("gpu", "-gpu", ["vrad", "all"], Stage: "vrad", AllSection: null, ValidateGpu),
        new("gpu_depth", "-gpu_depth", ["vrad", "all"], Stage: "vrad", AllSection: null, ValidateGpuDepth),
    ];

    /// <summary>The file the config was read from, as the user would name it.</summary>
    public string Source { get; }

    /// <summary>
    /// Where the config lives when neither <see cref="ConfigSwitch"/> nor
    /// <see cref="NoConfigSwitch"/> is given.
    /// </summary>
    /// <param name="environment">Reads an environment variable; the host passes <see cref="Environment.GetEnvironmentVariable(string)"/>.</param>
    /// <param name="windows">Whether the host is Windows.</param>
    /// <param name="home">The user's home directory, or null or empty when there is none.</param>
    /// <returns>
    /// <c>%APPDATA%\ssmap\config</c> on Windows;
    /// <c>$XDG_CONFIG_HOME/ssmap/config</c> elsewhere when that variable is
    /// an absolute path, else <c>~/.config/ssmap/config</c>. Null when the
    /// variable or home it needs is missing.
    /// </returns>
    /// <remarks>
    /// The XDG rule is the base-directory specification's: a relative
    /// <c>XDG_CONFIG_HOME</c> is invalid and ignored. macOS follows it too,
    /// rather than <c>~/Library/Application Support</c>, because the tool is
    /// a command-line one and that is where its users look. The environment
    /// is a parameter so the facts can hand it any machine.
    /// </remarks>
    public static string? DefaultPath(Func<string, string?> environment, bool windows, string? home)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (windows)
        {
            string? appData = environment("APPDATA");
            return string.IsNullOrEmpty(appData) ? null : Path.Join(appData, DirectoryName, FileName);
        }

        string? xdg = environment("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg) && xdg.StartsWith('/'))
        {
            return Path.Join(xdg, DirectoryName, FileName);
        }

        return string.IsNullOrEmpty(home) ? null : Path.Join(home, ".config", DirectoryName, FileName);
    }

    /// <summary>Reads a config's text.</summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="source">The file's name, for messages.</param>
    /// <returns>The config.</returns>
    /// <exception cref="MachineConfigException">A line is malformed; the message names the file and line.</exception>
    public static MachineConfig Parse(string text, string source)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(source);

        Dictionary<(string, string), string> values = [];
        string section = string.Empty;
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int number = i + 1;
            string line = lines[i].Trim();
            if (i == 0 && line.StartsWith('﻿'))
            {
                line = line[1..].TrimStart();
            }

            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                if (line[^1] != ']')
                {
                    throw new MachineConfigException(source, number, $"a section header is [name], not \"{line}\"");
                }

                string name = line[1..^1].Trim().ToLowerInvariant();
                if (!Verbs.Contains(name))
                {
                    throw new MachineConfigException(
                        source, number, $"unknown section [{name}]: the sections are {string.Join(", ", Verbs.Select(v => $"[{v}]"))}");
                }

                section = name;
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                throw new MachineConfigException(source, number, $"expected key = value, not \"{line}\"");
            }

            string key = line[..equals].Trim().ToLowerInvariant();
            string value = line[(equals + 1)..].Trim();
            MachineConfigKey? known = Keys.FirstOrDefault(k => k.Name == key);
            if (known is null)
            {
                throw new MachineConfigException(
                    source, number, $"unknown key \"{key}\": the keys are {string.Join(", ", Keys.Select(k => k.Name))}");
            }

            if (section.Length > 0 && !known.Verbs.Contains(section))
            {
                throw new MachineConfigException(
                    source, number, $"\"{key}\" is not a {section} setting: it belongs in {string.Join(", ", known.Verbs.Select(v => $"[{v}]"))}");
            }

            if (value.Length == 0)
            {
                throw new MachineConfigException(source, number, $"\"{key}\" has no value");
            }

            if (known.Validate(value) is string problem)
            {
                throw new MachineConfigException(source, number, $"{key} = {value}: {problem}");
            }

            if (!values.TryAdd((section, key), value))
            {
                string where = section.Length == 0 ? "before the first section" : $"in [{section}]";
                throw new MachineConfigException(source, number, $"\"{key}\" is given twice {where}");
            }
        }

        return new MachineConfig(source, values);
    }

    /// <summary>
    /// Reads the config a command line asks for: the one it names with
    /// <see cref="ConfigSwitch"/>, none for <see cref="NoConfigSwitch"/>, else
    /// the one at <paramref name="defaultPath"/> if there is one.
    /// </summary>
    /// <param name="fileSystem">The host's disk, rooted at the filesystem root.</param>
    /// <param name="args">The whole command line; the two switches are taken out of it.</param>
    /// <param name="defaultPath">Where the config lives by default, or null for nowhere.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The config (null when none is read) and the command line without the
    /// two switches.
    /// </returns>
    /// <exception cref="MachineConfigException">
    /// The switches are misused, the named file does not exist, or the file
    /// read is malformed.
    /// </exception>
    /// <remarks>
    /// A missing file at the default location is the ordinary case and reads
    /// as no config; a missing file the user NAMED is an error, because they
    /// asked for it.
    /// </remarks>
    public static async Task<(MachineConfig? Config, string[] Args)> LoadAsync(
        IFileSystem fileSystem,
        IReadOnlyList<string> args,
        string? defaultPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(args);

        List<string> rest = [];
        string? named = null;
        bool none = false;
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], NoConfigSwitch, StringComparison.Ordinal))
            {
                none = true;
            }
            else if (string.Equals(args[i], ConfigSwitch, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count || args[i + 1].Length == 0)
                {
                    throw new MachineConfigException($"{ConfigSwitch} needs a file");
                }

                if (named is not null)
                {
                    throw new MachineConfigException($"{ConfigSwitch} is given twice");
                }

                named = args[++i];
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        if (none && named is not null)
        {
            throw new MachineConfigException($"give {ConfigSwitch} or {NoConfigSwitch}, not both");
        }

        if (none || (named is null && defaultPath is null))
        {
            return (null, [.. rest]);
        }

        string path = Path.GetFullPath(named ?? defaultPath!);
        if (!VPath.TryCreate(path, out VPath file))
        {
            throw new MachineConfigException($"\"{path}\" is not a usable config path");
        }

        if (!await fileSystem.ExistsAsync(file, cancellationToken).ConfigureAwait(false))
        {
            if (named is not null)
            {
                throw new MachineConfigException($"{ConfigSwitch} {path}: no such file");
            }

            return (null, [.. rest]);
        }

        using System.Buffers.IMemoryOwner<byte> bytes =
            await fileSystem.ReadAllAsync(file, cancellationToken).ConfigureAwait(false);
        return (Parse(Encoding.UTF8.GetString(bytes.Memory.Span), path), [.. rest]);
    }

    /// <summary>The value a verb gets for a key, or null.</summary>
    /// <param name="verb">One of <see cref="Verbs"/>.</param>
    /// <param name="key">One of <see cref="Keys"/>' names.</param>
    /// <returns>The value, by the lookup order the type's remarks give.</returns>
    public string? Get(string verb, string key)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(key);

        MachineConfigKey? known = Keys.FirstOrDefault(k => k.Name == key);
        if (known is null || !known.Verbs.Contains(verb))
        {
            return null;
        }

        if (_values.TryGetValue((verb, key), out string? own))
        {
            return own;
        }

        if (verb == "all" && known.Stage is string stage && _values.TryGetValue((stage, key), out string? staged))
        {
            return staged;
        }

        return _values.TryGetValue((string.Empty, key), out string? global) ? global : null;
    }

    /// <summary>
    /// A verb's command line with this config's defaults added for every
    /// flag the line does not already give.
    /// </summary>
    /// <param name="verb">The verb.</param>
    /// <param name="args">The arguments after the verb.</param>
    /// <returns>
    /// The arguments with the defaults in, and the defaults alone (empty when
    /// none applied), for the one line <c>ssmap</c> prints.
    /// </returns>
    /// <remarks>
    /// For a single stage the defaults go first, before everything the user
    /// typed. For <c>all</c>, chain options go first too and a stage's option
    /// goes at the end after its section switch; <c>all</c> collects a
    /// section from every place it appears, so that is the same as writing it
    /// there. "Gives the flag" means any argument equal to it, in any section,
    /// ignoring case, as the parsers match flags.
    /// </remarks>
    public (string[] Args, string[] Added) Apply(string verb, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(args);

        List<string> front = [];
        List<string> back = [];
        foreach (MachineConfigKey key in Keys)
        {
            if (Get(verb, key.Name) is not string value
                || args.Any(a => string.Equals(a, key.Flag, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (verb == "all" && key.AllSection is string section)
            {
                back.AddRange([section, key.Flag, value]);
            }
            else
            {
                front.AddRange([key.Flag, value]);
            }
        }

        string[] added = [.. front, .. back];
        return ([.. front, .. args, .. back], added);
    }

    private static string? ValidateThreads(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0
            ? null
            : "threads is a whole number of at least 1";

    private static string? ValidateSeparator(string value) =>
        StockArgs.TryParseSeparatorPath(value, out _) ? null : "separator is auto, 256 or 512";

    private static string? ValidateGpu(string value) => null;

    private static string? ValidateGpuDepth(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            && n >= 1 && n <= RadWorld.MaxFacelightPipelineDepth
            ? null
            : $"gpu_depth is a whole number from 1 to {RadWorld.MaxFacelightPipelineDepth}";
}

/// <summary>One key a <see cref="MachineConfig"/> may set.</summary>
/// <param name="Name">The key as the file spells it.</param>
/// <param name="Flag">The command-line flag it is the default for.</param>
/// <param name="Verbs">The verbs, and so the sections, that take it.</param>
/// <param name="Stage">For <c>all</c>: the stage section it falls back to, or null.</param>
/// <param name="AllSection">For <c>all</c>: the section switch its flag follows, or null for a chain option.</param>
/// <param name="Validate">Returns what is wrong with a value, or null when it is fine.</param>
public sealed record MachineConfigKey(
    string Name,
    string Flag,
    IReadOnlyList<string> Verbs,
    string? Stage,
    string? AllSection,
    Func<string, string?> Validate);

/// <summary>A config file or switch <c>ssmap</c> cannot act on.</summary>
public sealed class MachineConfigException : Exception
{
    /// <summary>A problem with the switches rather than a file's contents.</summary>
    /// <param name="message">What is wrong.</param>
    public MachineConfigException(string message)
        : base(message)
    {
    }

    /// <summary>A problem on one line of a file.</summary>
    /// <param name="source">The file.</param>
    /// <param name="line">The line, from one.</param>
    /// <param name="message">What is wrong with it.</param>
    public MachineConfigException(string source, int line, string message)
        : base($"{source}:{line.ToString(CultureInfo.InvariantCulture)}: {message}")
    {
        ConfigFile = source;
        Line = line;
    }

    /// <summary>An empty exception, for serializers.</summary>
    public MachineConfigException()
    {
    }

    /// <summary>A message and the exception it wraps.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">The cause.</param>
    public MachineConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The file the problem is in, or null when it is not in a file.</summary>
    public string? ConfigFile { get; }

    /// <summary>The line the problem is on, or zero when it is not in a file.</summary>
    public int Line { get; }
}

/// <summary>Where <c>ssmap</c> looks for its <see cref="MachineConfig"/>.</summary>
/// <param name="FileSystem">The disk, rooted at the host's filesystem root.</param>
/// <param name="DefaultPath">
/// The file read when the command line names none
/// (<see cref="MachineConfig.DefaultPath"/>), or null to read none unless one
/// is named.
/// </param>
public sealed record MachineConfigLocation(IFileSystem FileSystem, string? DefaultPath);
