//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Text;

/// <summary>
/// Resolves a <c>patch</c> material against the file it includes.
/// </summary>
/// <remarks>
/// <para>
/// Takes a LOOKUP DELEGATE rather than a file system, so this assembly still
/// touches no path: the caller resolves <c>include</c> to text through
/// whatever mount stack it has and hands the parsed material back. The
/// delegate is async and takes the token, because it will be doing IO.
/// </para>
/// <para>
/// See <see cref="VmtPatchDialect"/> for why there are two algorithms and
/// where they disagree.
/// </para>
/// </remarks>
public static class VmtPatchResolver
{
    /// <summary>
    /// The nesting limit, shared by both reference implementations.
    /// </summary>
    /// <remarks>
    /// On exhaustion both WARN and carry on with whatever they have -- neither
    /// fails. The warning text is the same in both: "Infinite recursion in
    /// patch file?".
    /// </remarks>
    public const int MaxPatchDepth = 10;

    /// <summary>
    /// The key the engine dialect's recursive insert leaves in a block that
    /// would otherwise be empty.
    /// </summary>
    /// <remarks>
    /// Its purpose there is to stop an empty subkey being pruned. It is
    /// produced only by <see cref="VmtPatchDialect.Engine"/>; the compiler
    /// dialect has no equivalent, so a compiled map never contains one.
    /// </remarks>
    public const string PatchDummyKey = "__vmtpatchdummy";

    /// <summary>
    /// Resolves a material, following <c>include</c> until a non-patch root is
    /// reached.
    /// </summary>
    /// <param name="material">The material, which may or may not be a patch.</param>
    /// <param name="load">
    /// Resolves an <c>include</c> path to a parsed material, or null when the
    /// file is not there.
    /// </param>
    /// <param name="dialect">Which algorithm to apply.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>
    /// The resolved material. When <paramref name="material"/> was not a patch
    /// it comes back unchanged; otherwise the root is the INCLUDED material's
    /// shader name, because the string <c>patch</c> never survives resolution
    /// (the reference implementation assigns the base wholesale, name included).
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="material"/> or <paramref name="load"/> is null.
    /// </exception>
    /// <exception cref="VmtPatchException">
    /// A patch's <c>include</c> names a file the loader could not produce.
    /// </exception>
    public static async Task<VmtDocument> ResolveAsync(
        VmtDocument material,
        Func<string, CancellationToken, Task<VmtDocument?>> load,
        VmtPatchDialect dialect = VmtPatchDialect.Compiler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(load);

        return dialect == VmtPatchDialect.Engine
            ? await ResolveEngineAsync(material, load, cancellationToken).ConfigureAwait(false)
            : await ResolveCompilerAsync(material, load, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Apply each level's patch to that level's include as the chain is
    /// walked. This is the compiler dialect's order.
    /// </summary>
    private static async Task<VmtDocument> ResolveCompilerAsync(
        VmtDocument material,
        Func<string, CancellationToken, Task<VmtDocument?>> load,
        CancellationToken cancellationToken)
    {
        KeyValuesNode current = material.Root.Clone();
        int count = 0;

        while (count < MaxPatchDepth &&
               string.Equals(current.Name, VmtDocument.PatchKeyword, StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? includePath = current.GetString(VmtDocument.IncludeKey);
            if (string.IsNullOrEmpty(includePath))
            {
                // No include means nothing to load and the reference loop
                // spins. Refusing is the honest answer for a
                // library.
                throw new VmtPatchException("a patch material has no 'include' key");
            }

            VmtDocument? included = await load(includePath, cancellationToken).ConfigureAwait(false);
            if (included is null)
            {
                throw new VmtPatchException(
                    $"failed to load $include VMT file ({includePath})");
            }

            KeyValuesNode includeRoot = included.Root.Clone();

            // The reference order, kept exactly. Both branches
            // apply to includeKeyValues and then reassign `keyValues` FROM it,
            // and that is where the bug lives: after the insert branch runs,
            // `keyValues` IS the included material, so the FindKey("replace")
            // searches the BASE rather than the patch. A patch carrying
            // both sections therefore loses its replace block entirely.
            KeyValuesNode? insert = current.Find(VmtDocument.InsertKey);

            if (insert is not null)
            {
                Apply(includeRoot, insert, checkForExistence: false, recursive: false, allowSections: false);
                current = includeRoot;
            }

            // Deliberately looked up on `current`, which the branch above may
            // just have replaced. That is the reference's own sequencing.
            KeyValuesNode? replace = current.Find(VmtDocument.ReplaceKey);

            if (replace is not null)
            {
                Apply(includeRoot, replace, checkForExistence: true, recursive: false, allowSections: false);
                current = includeRoot;
            }

            // NOT unconditional. When the patch has NEITHER section,
            // `keyValues` is never reassigned (the reference has no
            // else), so the loop spins on the same patch until the counter runs
            // out and then warns. Assigning here would quietly "fix" a patch
            // that stock rejects.
            count++;
        }

        return new VmtDocument(current);
    }

    /// <summary>
    /// Accumulate every level's sections first, then apply them once to the
    /// base. This is the engine dialect's order.
    /// </summary>
    private static async Task<VmtDocument> ResolveEngineAsync(
        VmtDocument material,
        Func<string, CancellationToken, Task<VmtDocument?>> load,
        CancellationToken cancellationToken)
    {
        if (!material.IsPatch)
        {
            // The reference early-out that reports success.
            return material;
        }

        // The accumulator always ends up holding both sections, created empty
        // if absent.
        KeyValuesNode accumulator = new("patch");
        KeyValuesNode accumulatedInsert = accumulator.FindOrCreate(VmtDocument.InsertKey);
        KeyValuesNode accumulatedReplace = accumulator.FindOrCreate(VmtDocument.ReplaceKey);

        KeyValuesNode current = material.Root.Clone();
        int count = 0;

        while (count < MaxPatchDepth &&
               string.Equals(current.Name, VmtDocument.PatchKeyword, StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Accumulate BEFORE loading the include, so
            // the outermost patch is merged first and each deeper level
            // OVERWRITES it -- the reference merge is documented as
            // "overwriting any keys that are already there".
            // The deeper level therefore wins.
            if (current.Find(VmtDocument.InsertKey) is { } insert)
            {
                MergeOverwriting(accumulatedInsert, insert);
            }

            if (current.Find(VmtDocument.ReplaceKey) is { } replace)
            {
                MergeOverwriting(accumulatedReplace, replace);
            }

            string? includePath = current.GetString(VmtDocument.IncludeKey);
            if (string.IsNullOrEmpty(includePath))
            {
                throw new VmtPatchException("VMT patch file has no include key - invalid!");
            }

            VmtDocument? included = await load(includePath, cancellationToken).ConfigureAwait(false);
            if (included is null)
            {
                throw new VmtPatchException(
                    $"failed to load $include VMT file ({includePath})");
            }

            current = included.Root.Clone();
            count++;
        }

        // INSERT first, then REPLACE, and both
        // recurse into subkeys.
        if (accumulatedInsert.Children.Count > 0)
        {
            Apply(current, accumulatedInsert, checkForExistence: false, recursive: false, allowSections: true);
        }

        if (accumulatedReplace.Children.Count > 0)
        {
            Apply(current, accumulatedReplace, checkForExistence: true, recursive: false, allowSections: true);
        }

        return new VmtDocument(current);
    }

    /// <summary>
    /// The one worker behind both <c>insert</c> and
    /// <c>replace</c>.
    /// </summary>
    /// <param name="destination">The material being patched.</param>
    /// <param name="source">The <c>insert</c> or <c>replace</c> section.</param>
    /// <param name="checkForExistence">
    /// <c>bCheckForExistence</c>: false is <c>insert</c>, true is
    /// <c>replace</c>.
    /// </param>
    /// <param name="recursive">
    /// <c>bRecursive</c>, which is true only on the nested calls the reference
    /// merge makes and is what arms the
    /// <see cref="PatchDummyKey"/>.
    /// </param>
    /// <param name="allowSections">
    /// Whether a nested block in the section is applied at all. FALSE for the
    /// compiler dialect, whose type switch has
    /// no subkey case, so such a block is silently dropped.
    /// </param>
    /// <remarks>
    /// <para>
    /// The gate is <c>!bCheckForExistence || dst.FindKey(name)</c>.
    /// So <c>insert</c> is NOT "add if absent": it
    /// SETS, adding missing keys and overwriting present ones. <c>replace</c>
    /// writes only where the key already exists and silently skips the rest.
    /// </para>
    /// <para>
    /// The subkey case belongs to the engine dialect and NOT the
    /// compiler's, which has no such case, so a nested
    /// block is dropped there. That is what
    /// <paramref name="allowSections"/> selects.
    /// </para>
    /// </remarks>
    private static void Apply(
        KeyValuesNode destination,
        KeyValuesNode source,
        bool checkForExistence,
        bool recursive,
        bool allowSections)
    {
        foreach (KeyValuesNode sourceChild in source.Children)
        {
            if (sourceChild.IsSection && !allowSections)
            {
                // The reference switch covers string, int,
                // float and pointer, and TYPE_NONE falls out of it with
                // nothing done.
                continue;
            }

            KeyValuesNode? existing = destination.Find(sourceChild.Name);

            if (checkForExistence && existing is null)
            {
                continue;
            }

            if (sourceChild.IsSection)
            {
                // FindKey(name, true), which CREATES. So
                // a recursive replace against a destination that held a scalar
                // of that name turns it into a section.
                KeyValuesNode target = destination.FindOrCreate(sourceChild.Name);
                Apply(target, sourceChild, checkForExistence, recursive: true, allowSections);
                continue;
            }

            if (existing is not null && !existing.IsSection)
            {
                existing.Value = sourceChild.Value;
            }
            else
            {
                destination.Children.Add(
                    new KeyValuesNode(sourceChild.Name) { Value = sourceChild.Value });
            }
        }

        // A recursive call that left the block with
        // no children stamps a dummy so it is not pruned.
        if (recursive && destination.Children.Count == 0)
        {
            destination.SetInt(PatchDummyKey, 1);
        }
    }

    /// <summary>
    /// Add the source's keys to the destination, OVERWRITING what is there.
    /// </summary>
    private static void MergeOverwriting(KeyValuesNode destination, KeyValuesNode source)
    {
        foreach (KeyValuesNode child in source.Children)
        {
            if (child.IsSection)
            {
                MergeOverwriting(destination.FindOrCreate(child.Name), child);
                continue;
            }

            KeyValuesNode? existing = destination.Find(child.Name);
            if (existing is not null && !existing.IsSection)
            {
                existing.Value = child.Value;
            }
            else
            {
                destination.Children.Add(
                    new KeyValuesNode(child.Name) { Value = child.Value });
            }
        }
    }
}
