using SourceSharp.MapFormats.Geometry;
using System.Reflection;
using System.Globalization;
using System.Collections;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;

namespace SourceSharp.MapTools.Compile.Cache;

/// <summary>
/// The tool identity a cache row is pinned to (assembly
/// version + commit, per row so a prior worktree's rows stay reachable for
/// their own build only).
/// </summary>
public static class ToolIdentity
{
    private static readonly string Lazy = Compute();

    /// <summary>The current build's identity string.</summary>
    public static string Current => Lazy;

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "SingleFile", "IL3000:AssemblyLocationAsLabelOnly",
        Justification = "Location is read only as the product's display label inside the identity string, never to find a file (the warning's subject): non-empty under the dotnet host, where the label stays the assembly file name and every cache row keeps its t13 lineage; an embedded AOT assembly reports it empty and the label falls back to the native binary's own file name below.")]
    private static string Compute()
    {
        Assembly assembly = typeof(ToolIdentity).Assembly;
        string version =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        // The informational version may carry "+<sha>" from the build-stamp
        // target; the whole string is what distinguishes two worktrees that
        // share an assembly version.
        string location = assembly.Location;
        string product = location.Length > 0
            ? Path.GetFileName(location)
            : Path.GetFileName(Environment.ProcessPath ?? "ssmap");
        return string.Create(
            CultureInfo.InvariantCulture,
            $"MapTools {version} ({product})");
    }

    /// <summary>The identity a run pins its rows to, cooker included where the product came from one.</summary>
    /// <param name="cookerIdentity">The cooker's identity, or null when nothing was cooked.</param>
    /// <returns>The composed identity.</returns>
    public static string Of(string? cookerIdentity) =>
        cookerIdentity is null ? Current : Current + " | " + cookerIdentity;
}

/// <summary>
/// Canonical digests over the option records and the parsed map model
/// (the vbsp key hashes the PARSED map model, the option
/// subset the stage reads, never the VMF text — Hammer's save-time churn must
/// not invalidate, and an option the stage never looks at must not either).
/// </summary>
/// <remarks>
/// <para>
/// The option fold is reflection over the record's instance properties in
/// metadata order — stable within one assembly build, which is all a cache
/// key needs, because the tool identity in the same key already changes when
/// the assembly changes. Floats fold through their raw bits
/// (<see cref="BitConverter.SingleToInt32Bits(float)"/>) so the digest never
/// depends on culture float formatting; enums fold numerically; nested
/// records recurse; sequences fold ordered with their count, sets sorted.
/// </para>
/// <para>
/// Null-vs-absent, empty-collection-vs-null and both NaN bit patterns are all
/// distinguished — a poisoned digest that conflates two settings is exactly
/// the cache-poisoning failure mode §10a tests for.
/// </para>
/// </remarks>
public static class OptionsDigest
{
    /// <summary>Folds any immutable options object (record, tuple, struct) canonically.</summary>
    /// <param name="options">The options; null digests as the literal <c>&lt;null&gt;</c>.</param>
    /// <returns>Lower-case hex SHA-256.</returns>
    // WHAT THE FOLD MUST BE ABLE TO SEE, ROOTED BY HAND.
    //
    // The fold below reads property accessors by reflection, and a stripped
    // accessor is not an error — `property.GetMethod is null` makes the fold
    // skip the member, every option variant of the affected type digests
    // alike, and the cache starts serving one run's cooked bytes to another.
    // That is §10a's poisoning failure mode, so the types the product code
    // digests are rooted here rather than left to the linker's guess: the
    // property getters survive because this attribute is a use, and a future
    // digest call site on a new type must add its name here (the runtime
    // guard in the fold turns a missed entry into a loud failure, not a
    // silent collision).
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods,
        typeof(CollisionModelCache.CollisionCookingOptions))]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods,
        typeof(SourceSharp.MapTools.Options.ComplianceOptions))]
    public static string Of(object? options)
    {
        using IncrementalHashHasher hasher = new();
        hasher.AppendValue(options, 0);
        return hasher.Finish();
    }

    private sealed class IncrementalHashHasher : IDisposable
    {
        private readonly System.Security.Cryptography.IncrementalHash _hash =
            System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);

        public void FinishInto() => _ = _hash.GetCurrentHash();

        public string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();

        public void Raw(ReadOnlySpan<byte> bytes) => _hash.AppendData(bytes);

        public void Tag(ReadOnlySpan<byte> tag) => _hash.AppendData(tag);

        [UnconditionalSuppressMessage("Trimming", "IL2075:UnannotatedTypeOrMemberDefinitionReturnsAnnotatedType",
            Justification = "The fold walks the runtime type of a caller-supplied options object, which the analyzer cannot see through. Every type the product digests is rooted by the DynamicDependency list on Of, and a type that lost its accessors fails loudly in VerifyDigestible instead of digesting silently, so the metadata the analyzer cannot prove is present by construction.")]
        [UnconditionalSuppressMessage("Trimming", "IL2070:UnannotatedTypeOrMemberDefinitionTarget",
            Justification = "Same untracked flow as the IL2075 above: IsSetLike's candidate is that same rooted runtime type, so GetInterfaces keeps the interface map the set-detection needs to fold a set ordered rather than as an ordered sequence.")]
        public void AppendValue(object? value, int depth)
        {
            _hash.AppendData([(byte)ValueTag(depth)]);
            switch (value)
            {
                case null:
                    return;
                case string s:
                    Tag(0x73u);
                    _hash.AppendData(System.Text.Encoding.UTF8.GetBytes(s));
                    _hash.AppendData([0]);
                    return;
                case bool b:
                    Tag(0x62u);
                    _hash.AppendData([b ? (byte)1 : (byte)0]);
                    return;
                case float f:
                    Tag(0x66u);
                    { Span<byte> b = stackalloc byte[4]; BitConverter.TryWriteBytes(b, f); Write(b); }
                    return;
                case double d:
                    Tag(0x64u);
                    { Span<byte> b = stackalloc byte[8]; BitConverter.TryWriteBytes(b, d); Write(b); }
                    return;
                case int i:
                    Tag(0x69u);
                    { Span<byte> b = stackalloc byte[4]; BitConverter.TryWriteBytes(b, i); Write(b); }
                    return;
                case long l:
                    Tag(0x6cu);
                    { Span<byte> b = stackalloc byte[8]; BitConverter.TryWriteBytes(b, l); Write(b); }
                    return;
                case uint u:
                    Tag(0x55u);
                    { Span<byte> b = stackalloc byte[4]; BitConverter.TryWriteBytes(b, u); Write(b); }
                    return;
                case ulong ul:
                    Tag(0x4cu);
                    { Span<byte> b = stackalloc byte[8]; BitConverter.TryWriteBytes(b, ul); Write(b); }
                    return;
                case short sh:
                    Tag(0x73u);
                    { Span<byte> b = stackalloc byte[2]; BitConverter.TryWriteBytes(b, sh); Write(b); }
                    return;
                case ushort us:
                    Tag(0x53u);
                    { Span<byte> b = stackalloc byte[2]; BitConverter.TryWriteBytes(b, us); Write(b); }
                    return;
                case byte by:
                    Tag(0x42u);
                    Write([by]);
                    return;
                case byte[] bytes:
                    Tag(0x42u);
                    WriteLength(bytes.Length);
                    Write(bytes);
                    return;
                case Enum e:
                    Tag(0x65u);
                    { Span<byte> b = stackalloc byte[8]; BitConverter.TryWriteBytes(b, Convert.ToInt64(e, CultureInfo.InvariantCulture)); Write(b); }
                    return;
                case Vec3 v:
                    Tag(0x76u);
                    { Span<byte> b = stackalloc byte[4]; BitConverter.TryWriteBytes(b, v.X); Write(b); }
                    { Span<byte> b = stackalloc byte[4]; BitConverter.TryWriteBytes(b, v.Y); Write(b); }
                    { Span<byte> b = stackalloc byte[4]; BitConverter.TryWriteBytes(b, v.Z); Write(b); }
                    return;
            }

            if (depth >= 8)
            {
                throw new InvalidOperationException("options digest: nesting deeper than 8 — flatten the option type");
            }

            Type type = value.GetType();
            if (value is IDictionary dictionary)
            {
                Tag(0x44u);
                List<(string Key, object? Value)> entries = [];
                foreach (DictionaryEntry entry in dictionary)
                {
                    entries.Add((Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "<null>", entry.Value));
                }

                entries.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                WriteLength(entries.Count);
                foreach ((string key, object? item) in entries)
                {
                    AppendValue(key, depth + 1);
                    AppendValue(item, depth + 1);
                }

                return;
            }

            if (value is not string && value is IEnumerable sequence and not IComparable)
            {
                // Sets (unordered) sort their folded element strings; ordered
                // sequences fold as they enumerate. ISet is detected first.
                Tag(0x73u);
                List<object?> items = [];
                foreach (object? item in sequence)
                {
                    items.Add(item);
                }

                WriteLength(items.Count);
                if (type.GetInterface(nameof(ISet<object>)) is not null || IsSetLike(type))
                {
                    items.Sort(static (a, b) => string.CompareOrdinal(Stable(a), Stable(b)));
                }

                foreach (object? item in items)
                {
                    AppendValue(item, depth + 1);
                }

                return;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                Tag(0x70u);
                PropertyInfo key = type.GetProperty("Key")!;
                PropertyInfo val = type.GetProperty("Value")!;
                AppendValue(key.GetValue(value), depth + 1);
                AppendValue(val.GetValue(value), depth + 1);
                return;
            }

            // Record / POCO: fold instance properties in metadata order.
            Tag(0x72u);
            WriteName(type.FullName ?? type.Name);
            PropertyInfo[] properties = type.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            Array.Sort(properties, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            int folded = 0;
            foreach (PropertyInfo property in properties)
            {
                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                RequireReadable(property);
                folded++;
                WriteName(property.Name);
                AppendValue(ReadSafe(property, value), depth + 1);
            }

            // Base-declared properties (records chain: options derive from object only,
            // but nested model types may declare bases).
            for (Type? baseType = type.BaseType;
                 baseType is not null && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum);
                 baseType = baseType.BaseType)
            {
                foreach (PropertyInfo property in baseType.GetProperties(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (property.GetIndexParameters().Length != 0)
                    {
                        continue;
                    }

                    RequireReadable(property);
                    folded++;
                    WriteName(property.Name);
                    AppendValue(ReadSafe(property, value), depth + 1);
                }
            }

            if (folded == 0 && HasUnfoldableState(type))
            {
                // Nothing was read from a type the fold was asked to digest:
                // every value of that type would then share one digest, which
                // is §10a's poisoning mode rather than a harmless shortcut.
                // Either trimming took the accessors the DynamicDependency
                // list on Of exists to keep, or the type carries state the
                // fold cannot see. Say so instead of poisoning the store.
                throw new InvalidOperationException(
                    $"options digest: {type.FullName} folds to its name alone — root its accessors with [DynamicDependency] on OptionsDigest.Of, or fold it explicitly");
            }

            return;
            static void RequireReadable(PropertyInfo property)
            {
                // A C# property always declares a getter, so a missing one is
                // trimming and not authorship. Skipping it silently is what
                // turns a trimmed binary's digest into a cache-poisoning key,
                // so the fold fails the run instead.
                if (!property.CanRead || property.GetMethod is null)
                {
                    throw new InvalidOperationException(
                        $"options digest: {property.DeclaringType?.Name}.{property.Name} has no readable getter — root its accessors with [DynamicDependency] on OptionsDigest.Of");
                }
            }

            static bool HasUnfoldableState(Type candidate) =>
                candidate.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length != 0 ||
                candidate.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length != 0;

            object? ReadSafe(PropertyInfo property, object target)
            {
                try
                {
                    return property.GetValue(target);
                }
                catch (TargetInvocationException)
                {
                    // A property that throws is not foldable; fail the digest,
                    // never silently skip — a skipped member is a poisoning hole.
                    throw new InvalidOperationException(
                        $"options digest: {type.Name}.{property.Name} threw; make it a plain field or fold it explicitly");
                }
            }

            static bool IsSetLike(Type candidate)
            {
                // Advance constructed generics to their open definition, then
                // up the base chain. An open generic definition is ITSELF
                // IsGenericType and GetGenericTypeDefinition() returns it
                // unchanged, so stepping without the definition check pins
                // every non-HashSet generic (List<T>, the collection-
                // expression IEnumerable<T>) in this loop forever — a hang,
                // not a wrong digest.
                for (Type? t = candidate; t is not null; t = Advance(t))
                {
                    if (t == typeof(HashSet<>) || t == typeof(SortedSet<>) ||
                        t.FullName?.Contains("IReadOnlySet", StringComparison.Ordinal) == true)
                    {
                        return true;
                    }
                }

                return candidate.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlySet<>));

                static Type? Advance(Type t) =>
                    t is { IsGenericType: true, IsGenericTypeDefinition: false }
                        ? t.GetGenericTypeDefinition()
                        : t.BaseType;
            }

            static string Stable(object? item)
            {
                using IncrementalHashHasher inner = new();
                inner.AppendValue(item, 8);
                return inner.Finish();
            }
        }

        private int ValueTag(int depth) => 0x30 + Math.Min(depth, 9);

        private void Tag(uint marker) => Write([(byte)marker]);

        private void WriteLength(int length)
        {
            Span<byte> buffer = stackalloc byte[8];
            BitConverter.TryWriteBytes(buffer, length);
            Write(buffer);
        }

        private void WriteName(string name)
        {
            _hash.AppendData(System.Text.Encoding.UTF8.GetBytes(name));
            _hash.AppendData([0]);
        }

        private void Write(ReadOnlySpan<byte> bytes) => _hash.AppendData(bytes);
    }
}
