using System.Buffers.Binary;

namespace SourceSharp.MapFormats.Bsp;

/// <summary>
/// Reads and writes the BSP container: the header, the 64 lump slots, and the
/// game lump's nested directory.
/// </summary>
/// <remarks>
/// <para>
/// Both directions work on a <see cref="Stream"/> and never on a path, because
/// every file this project opens goes through the <c>IFileSystem</c> seam one
/// level up (plan_maptools.md 1a, ruling Q14). That is also what lets the whole
/// of this type be exercised by facts over a <see cref="MemoryStream"/>, with
/// no disk involved.
/// </para>
/// <para>
/// What it does NOT do is interpret lump payloads. A lump is bytes here; the
/// typed readers live beside their consumers. That split is what makes the
/// round-trip gate meaningful -- the writer cannot accidentally "fix" a lump it
/// does not understand.
/// </para>
/// </remarks>
public static class BspFile
{
    /// <summary>
    /// Reads a whole BSP into memory.
    /// </summary>
    /// <param name="stream">
    /// A readable, seekable stream positioned anywhere; the reader seeks
    /// absolutely and does not restore the position.
    /// </param>
    /// <param name="cancellationToken">Cancels the read between lumps.</param>
    /// <returns>The parsed container.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot seek.</exception>
    /// <exception cref="InvalidBspException">The file is not a BSP this engine loads.</exception>
    public static async Task<BspData> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException(
                "a BSP is read by absolute lump offset, so the stream must be seekable",
                nameof(stream));
        }

        byte[] header = new byte[BspData.HeaderSize];
        stream.Seek(0, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        int ident = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (ident != BspData.Ident)
        {
            throw new InvalidBspException(
                $"not a BSP: ident is 0x{ident:X8}, expected 0x{BspData.Ident:X8} (\"VBSP\")");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (version is < BspData.MinVersion or > BspData.MaxVersion)
        {
            throw new InvalidBspException(
                $"BSP version {version} is outside the {BspData.MinVersion}..{BspData.MaxVersion} " +
                "range the engine loads");
        }

        // The L4D2 lump-directory re-layout has no flag of its own; the ++
        // reader's only detection is this parity check
        // (dumps/vbsp/_gameflag/140054040_writebsp.c:21): at version 21 the
        // first lump entry's first dword is the planes lump's fileofs, 1036,
        // in a standard file and the planes version, 0, in a re-laid-out one.
        // A malformed v21 standard file with a zero planes offset is misread
        // exactly as the ++ tool misreads it.
        bool l4d2Layout = version == BspData.MaxVersion
            && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) == 0;

        BspData bsp = new()
        {
            FileVersion = version,
            MapRevision = BinaryPrimitives.ReadInt32LittleEndian(
                header.AsSpan(8 + (BspData.HeaderLumps * 16))),
            SourceLumpsUseL4d2Layout = l4d2Layout,
        };

        BspLumpPlacement[] layout = new BspLumpPlacement[BspData.HeaderLumps];

        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReadOnlySpan<byte> entry = header.AsSpan(8 + (i * 16), 16);

            // The re-layout is the same sixteen bytes shifted one dword
            // right: version leads, and fileofs/filelen slide behind it
            // (140054040_writebsp.c:28-38). The uncompressed size is last in
            // both layouts and untouched by the shift.
            int fileofs = BinaryPrimitives.ReadInt32LittleEndian(entry[l4d2Layout ? 4..8 : 0..4]);
            int filelen = BinaryPrimitives.ReadInt32LittleEndian(entry[l4d2Layout ? 8..12 : 4..8]);
            int lumpVersion = BinaryPrimitives.ReadInt32LittleEndian(entry[l4d2Layout ? 0..4 : 8..12]);
            int uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);

            if (filelen < 0 || fileofs < 0)
            {
                throw new InvalidBspException(
                    $"lump {i} has a negative offset or length ({fileofs}, {filelen})");
            }

            // A slot whose entry is entirely zero was never written by the tool
            // that produced the file. That is not the same as an empty lump
            // that WAS written, which carries the writer's position, and only
            // the header can tell them apart.
            bool written = fileofs != 0 || filelen != 0 || lumpVersion != 0 || uncompressedSize != 0;
            layout[i] = new BspLumpPlacement(fileofs, filelen, lumpVersion, written);

            if (filelen == 0)
            {
                bsp[i] = new BspLumpData(ReadOnlyMemory<byte>.Empty, lumpVersion, uncompressedSize);
                continue;
            }

            if (fileofs + (long)filelen > stream.Length)
            {
                throw new InvalidBspException(
                    $"lump {i} runs past the end of the file: offset {fileofs} + length {filelen} " +
                    $"exceeds {stream.Length} bytes");
            }

            byte[] data = new byte[filelen];
            stream.Seek(fileofs, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);

            bsp[i] = new BspLumpData(data, lumpVersion, uncompressedSize);
        }

        bsp.SourceLayout = layout;
        ParseGameLumps(bsp);
        return bsp;
    }

    /// <summary>
    /// Writes a BSP in the order and with the padding <c>WriteBSPFile</c> uses.
    /// </summary>
    /// <param name="bsp">The container to write.</param>
    /// <param name="stream">A writable, seekable stream; writing starts at zero.</param>
    /// <param name="cancellationToken">Cancels the write between lumps.</param>
    /// <returns>A task that completes when every byte has been written.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot seek.</exception>
    /// <remarks>
    /// The header is written twice, as stock does: once as zeroes to reserve
    /// its 1036 bytes, and again at the end once every lump's offset is known.
    /// </remarks>
    public static Task SaveAsync(
        BspData bsp,
        Stream stream,
        CancellationToken cancellationToken = default) =>
        SaveAsync(bsp, stream, BspWriteMode.Canonical, cancellationToken);

    /// <summary>
    /// Writes a BSP in the requested lump order.
    /// </summary>
    /// <param name="bsp">The container to write.</param>
    /// <param name="stream">A writable, seekable stream; writing starts at zero.</param>
    /// <param name="mode">
    /// <see cref="BspWriteMode.Canonical"/> for compiler output;
    /// <see cref="BspWriteMode.PreserveSourceLayout"/> to re-emit a loaded file
    /// in the order it arrived in.
    /// </param>
    /// <param name="cancellationToken">Cancels the write between lumps.</param>
    /// <returns>A task that completes when every byte has been written.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot seek.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="BspWriteMode.PreserveSourceLayout"/> was asked for on data
    /// that was not loaded from a file.
    /// </exception>
    public static Task SaveAsync(
        BspData bsp,
        Stream stream,
        BspWriteMode mode,
        CancellationToken cancellationToken = default) =>
        SaveAsyncCore(bsp, stream, mode, format: null, cancellationToken);

    /// <summary>
    /// Writes a BSP in the requested lump order, stamping the versions a
    /// <see cref="BspWriteFormat"/> names.
    /// </summary>
    /// <param name="bsp">The container to write.</param>
    /// <param name="stream">A writable, seekable stream; writing starts at zero.</param>
    /// <param name="mode">
    /// <see cref="BspWriteMode.Canonical"/> for compiler output;
    /// <see cref="BspWriteMode.PreserveSourceLayout"/> to re-emit a loaded file
    /// in the order it arrived in.
    /// </param>
    /// <param name="format">
    /// The versions to stamp and the lump-directory layout to emit. Honoured
    /// by <see cref="BspWriteMode.Canonical"/> only: a preserve write exists to
    /// reproduce its source file, so there the header version and the lump
    /// versions come from the source and the directory layout comes from
    /// <see cref="BspData.SourceLumpsUseL4d2Layout"/>. An explicit format is
    /// also authoritative for the header version in canonical mode -- it
    /// stamps <see cref="BspWriteFormat.Version"/> rather than
    /// <see cref="BspData.FileVersion"/>, so the caller resolves the version,
    /// not the loader.
    /// </param>
    /// <param name="cancellationToken">Cancels the write between lumps.</param>
    /// <returns>A task that completes when every byte has been written.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot seek.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="BspWriteMode.PreserveSourceLayout"/> was asked for on data
    /// that was not loaded from a file.
    /// </exception>
    public static Task SaveAsync(
        BspData bsp,
        Stream stream,
        BspWriteMode mode,
        BspWriteFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        return SaveAsyncCore(bsp, stream, mode, format, cancellationToken);
    }

    /// <summary>
    /// The shared writer. <paramref name="format"/> null means "today's
    /// bytes": the header version is <see cref="BspData.FileVersion"/> as
    /// loaded or set, lump versions come from the canonical table or the
    /// source layout as the mode dictates, game-lump versions are untouched,
    /// and only a preserve write can emit the L4D2 re-layout (following its
    /// source).
    /// </summary>
    private static async Task SaveAsyncCore(
        BspData bsp,
        Stream stream,
        BspWriteMode mode,
        BspWriteFormat? format,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException(
                "the header is rewritten once lump offsets are known, so the stream must be seekable",
                nameof(stream));
        }

        if (mode == BspWriteMode.PreserveSourceLayout && bsp.SourceLayout is null)
        {
            throw new InvalidOperationException(
                "there is no source layout to preserve: this BspData was built rather than "
                + "loaded. Write it canonically, which is what a compiled map wants anyway.");
        }

        // fileofs, filelen, version, uncompressedSize per slot. Left at zero
        // for any lump that is never written, which is what stock's memset of
        // the header achieves.
        int[] ofs = new int[BspData.HeaderLumps];
        int[] len = new int[BspData.HeaderLumps];
        int[] ver = new int[BspData.HeaderLumps];

        byte[] headerBytes = new byte[BspData.HeaderSize];
        stream.Seek(0, SeekOrigin.Begin);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        long position = BspData.HeaderSize;

        foreach (BspWriteOrder.Step step in BuildPlan(bsp, mode))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (step.Lump == BspLump.GameLump)
            {
                position = await WriteGameLumpsAsync(
                        bsp, stream, position, ofs, len, StampGameLumpVersion(mode, format),
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            BspLumpData lump = bsp[step.Lump];
            int index = (int)step.Lump;

            if (step.Lump == BspLump.PakFile)
            {
                // The pak lump is aligned before it is written, and the engine
                // expects it last. Alignment is 0 (that is, none) on PC; the
                // parameter exists for the console builds this port does not
                // target, and AlignValue treats anything below 2 as none.
                position = await PadToAsync(stream, position, PakFileAlignment, cancellationToken)
                    .ConfigureAwait(false);
            }

            ofs[index] = checked((int)position);
            len[index] = lump.Length;
            ver[index] = step.Lump is BspLump.WorldLights or BspLump.WorldLightsHdr
                && mode == BspWriteMode.Canonical && format is not null
                ? format.WorldLightVersion
                : step.Version;

            if (!lump.IsEmpty)
            {
                await stream.WriteAsync(lump.Data, cancellationToken).ConfigureAwait(false);
                position += lump.Length;
            }

            if (step.Lump != BspLump.Occlusion || BspWriteOrder.OcclusionPadsToFour)
            {
                position = await PadToAsync(stream, position, 4, cancellationToken).ConfigureAwait(false);
            }
        }

        BinaryPrimitives.WriteInt32LittleEndian(headerBytes, BspData.Ident);
        BinaryPrimitives.WriteInt32LittleEndian(
            headerBytes.AsSpan(4),
            mode == BspWriteMode.Canonical && format is not null ? format.Version : bsp.FileVersion);

        // The writer-side mirror of the reader's re-layout (see LoadAsync):
        // the same sixteen bytes, fields shifted one dword right. Canonical
        // mode takes the layout from the format; preserve mode follows the
        // source file so a v21 L4D2 map round-trips byte-exact even through
        // the legacy overloads.
        bool relayout = mode == BspWriteMode.Canonical
            ? format?.L4d2LumpDirLayout ?? false
            : bsp.SourceLumpsUseL4d2Layout;

        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            Span<byte> entry = headerBytes.AsSpan(8 + (i * 16), 16);
            if (relayout)
            {
                BinaryPrimitives.WriteInt32LittleEndian(entry, ver[i]);
                BinaryPrimitives.WriteInt32LittleEndian(entry[4..], ofs[i]);
                BinaryPrimitives.WriteInt32LittleEndian(entry[8..], len[i]);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(entry, ofs[i]);
                BinaryPrimitives.WriteInt32LittleEndian(entry[4..], len[i]);
                BinaryPrimitives.WriteInt32LittleEndian(entry[8..], ver[i]);
            }

            // uncompressedSize stays zero: the compilers never emit a
            // compressed lump, so a round trip decompresses and writes plain.
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], 0);
        }

        BinaryPrimitives.WriteInt32LittleEndian(
            headerBytes.AsSpan(8 + (BspData.HeaderLumps * 16)), bsp.MapRevision);

        stream.Seek(0, SeekOrigin.Begin);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The alignment the pak lump is padded to before it is written. Zero, and
    /// so no padding, on every platform this port targets.
    /// </summary>
    public const int PakFileAlignment = 0;

    /// <summary>The packed <c>'sprp'</c> code, the one game lump id the writer
    /// recognises. Built through the shared helper so the byte order cannot
    /// drift from the one the parser and the golden map agree on.</summary>
    private static readonly int StaticPropsId = GameLumpEntry.MakeId("sprp");

    /// <summary>
    /// The version to force on the <c>sprp</c> game-lump entry, or null for
    /// "keep each entry's own". Only a canonical write with an explicit format
    /// ever names one.
    /// </summary>
    private static ushort? StampGameLumpVersion(BspWriteMode mode, BspWriteFormat? format) =>
        mode == BspWriteMode.Canonical && format?.StaticPropsFormat is { } props
            ? checked((ushort)BspStaticPropsFormatInfo.GameLumpVersion(props))
            : null;

    /// <summary>
    /// The sequence of lumps to write, and the version to stamp on each.
    /// </summary>
    private static List<BspWriteOrder.Step> BuildPlan(BspData bsp, BspWriteMode mode)
    {
        List<BspWriteOrder.Step> plan = [];

        if (mode == BspWriteMode.Canonical)
        {
            foreach (BspWriteOrder.Step step in BspWriteOrder.Steps)
            {
                if (step.SkipWhenEmpty && bsp[step.Lump].IsEmpty)
                {
                    continue;
                }

                plan.Add(step);
            }

            // AddGameLumps() and WritePakFileLump(), in that order.
            plan.Add(new BspWriteOrder.Step(BspLump.GameLump, 0, false));
            plan.Add(new BspWriteOrder.Step(BspLump.PakFile, 0, false));

            // Lumps_Write(): anything loaded that nothing above claimed goes
            // out now, in slot order. On a fresh compile there is nothing here;
            // on a round trip it is how an unrecognised lump survives.
            for (int i = 0; i < BspData.HeaderLumps; i++)
            {
                BspLump lump = (BspLump)i;
                if (BspWriteOrder.IsOrdered(lump) || lump is BspLump.GameLump or BspLump.PakFile)
                {
                    continue;
                }

                if (!bsp[i].IsEmpty)
                {
                    plan.Add(new BspWriteOrder.Step(lump, bsp[i].Version, false));
                }
            }

            return plan;
        }

        IReadOnlyList<BspLumpPlacement> layout = bsp.SourceLayout!;
        List<int> order = [];
        for (int i = 0; i < BspData.HeaderLumps; i++)
        {
            if (layout[i].Written)
            {
                order.Add(i);
            }
        }

        // Sorted by the offset the source file recorded. Several lumps can
        // share one offset -- every empty lump records the position the writer
        // had reached without advancing it -- so empties sort before the
        // non-empty lump that actually occupies that position. Among several
        // empties the order cannot be recovered and does not matter: they write
        // no bytes, and each records the same offset whichever way round they
        // go. Slot number breaks the remaining tie so the result is stable.
        order.Sort((a, b) =>
        {
            int byOffset = layout[a].Offset.CompareTo(layout[b].Offset);
            if (byOffset != 0)
            {
                return byOffset;
            }

            int byEmptiness = (layout[a].Length == 0 ? 0 : 1).CompareTo(layout[b].Length == 0 ? 0 : 1);
            return byEmptiness != 0 ? byEmptiness : a.CompareTo(b);
        });

        foreach (int i in order)
        {
            // The version comes from the source header, not from the canonical
            // table: an older bsplib stamped versions this one no longer uses.
            plan.Add(new BspWriteOrder.Step((BspLump)i, layout[i].Version, false));
        }

        return plan;
    }

    private static async Task<long> PadToAsync(
        Stream stream,
        long position,
        int alignment,
        CancellationToken cancellationToken)
    {
        if (alignment < 2)
        {
            return position;
        }

        int slack = (int)(position % alignment);
        if (slack == 0)
        {
            return position;
        }

        int count = alignment - slack;
        byte[] padding = new byte[count];
        await stream.WriteAsync(padding, cancellationToken).ConfigureAwait(false);
        return position + count;
    }

    private static void ParseGameLumps(BspData bsp)
    {
        ReadOnlyMemory<byte> raw = bsp[BspLump.GameLump].Data;
        if (raw.Length < 4)
        {
            return;
        }

        ReadOnlySpan<byte> span = raw.Span;
        int count = BinaryPrimitives.ReadInt32LittleEndian(span);
        if (count < 0 || 4 + (count * 16) > raw.Length)
        {
            throw new InvalidBspException(
                $"the game lump's directory claims {count} entries, which does not fit in its " +
                $"{raw.Length} bytes");
        }

        // The game lump's own base offset. Directory entries store ABSOLUTE
        // file offsets, so they are rebased against where the lump itself
        // starts to become offsets within the lump's bytes.
        int lumpBase = 0;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = span.Slice(4 + (i * 16), 16);
            int fileofs = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            if (i == 0)
            {
                // The first payload sits immediately after the directory, so
                // its absolute offset minus that fixed size is where the lump
                // begins in the file.
                lumpBase = fileofs - (4 + (count * 16));
            }
        }

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = span.Slice(4 + (i * 16), 16);
            int id = BinaryPrimitives.ReadInt32LittleEndian(entry);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            int fileofs = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            int filelen = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);

            int start = fileofs - lumpBase;
            if (start < 0 || filelen < 0 || start + filelen > raw.Length)
            {
                throw new InvalidBspException(
                    $"game lump {i} (\"{new GameLumpEntry(id, flags, version, default).IdString()}\") " +
                    $"spans {start}..{start + filelen} of a {raw.Length}-byte lump");
            }

            bsp.GameLumps.Add(new GameLumpEntry(id, flags, version, raw.Slice(start, filelen)));
        }
    }

    /// <summary>
    /// Writes the game lump's nested directory and payloads.
    /// <paramref name="staticPropsVersion"/> is the version to force on the
    /// <c>sprp</c> entry, or null to write every entry's own version -- which
    /// is what the legacy path and preserve mode always do.
    /// </summary>
    private static async Task<long> WriteGameLumpsAsync(
        BspData bsp,
        Stream stream,
        long position,
        int[] ofs,
        int[] len,
        ushort? staticPropsVersion,
        CancellationToken cancellationToken)
    {
        int index = (int)BspLump.GameLump;
        int count = bsp.GameLumps.Count;

        // AddGameLumps records the offset and length unconditionally, and
        // stamps neither a version nor an uncompressed size -- both stay at the
        // zero the header was memset to. A map with no game lumps still gets a
        // four-byte lump holding a count of zero.
        ofs[index] = checked((int)position);

        int directorySize = 4 + (count * 16);
        byte[] directory = new byte[directorySize];
        BinaryPrimitives.WriteInt32LittleEndian(directory, count);

        int payloadOffset = checked((int)position) + directorySize;
        int total = directorySize;
        for (int i = 0; i < count; i++)
        {
            GameLumpEntry entry = bsp.GameLumps[i];

            // The -staticpropformat stamp applies to the static-props lump
            // alone: the ++ preset's prop version is a statement about the
            // prop wire format, and dprp/dplt/dplh carry versions of their
            // own the flag says nothing about.
            ushort version = entry.Id == StaticPropsId && staticPropsVersion is { } stamped
                ? stamped
                : entry.Version;

            Span<byte> slot = directory.AsSpan(4 + (i * 16), 16);
            BinaryPrimitives.WriteInt32LittleEndian(slot, entry.Id);
            BinaryPrimitives.WriteUInt16LittleEndian(slot[4..], entry.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(slot[6..], version);
            BinaryPrimitives.WriteInt32LittleEndian(slot[8..], payloadOffset);
            BinaryPrimitives.WriteInt32LittleEndian(slot[12..], entry.Data.Length);
            payloadOffset += entry.Data.Length;
            total += entry.Data.Length;
        }

        len[index] = total;

        await stream.WriteAsync(directory, cancellationToken).ConfigureAwait(false);
        position += directorySize;

        foreach (GameLumpEntry entry in bsp.GameLumps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteAsync(entry.Data, cancellationToken).ConfigureAwait(false);
            position += entry.Data.Length;
        }

        return await PadToAsync(stream, position, 4, cancellationToken).ConfigureAwait(false);
    }
}
