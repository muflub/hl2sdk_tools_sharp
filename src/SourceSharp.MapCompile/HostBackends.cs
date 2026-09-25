using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

using SourceSharp.MapTools.Compile.Cache;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;

namespace SourceSharp.MapCompile;

/// <summary>
/// The host's optional-package seams: the SQLite cache store and the Vulkan
/// ray tracer, opened by name at runtime.
/// </summary>
/// <remarks>
/// <para>
/// The SQLite store package is a host reference: ssmap's csproj names it, so
/// the shipped tree can serve <c>-incremental</c> out of the box (P15, after
/// P12 anomaly 5: with the package found-by-name only, the shipped JIT tree
/// carried no <c>Cache.Sqlite.dll</c>, the probe below silently found
/// neither, and <c>-incremental</c> cooked everything on every corpus map).
/// The GPU package stays reflection-only: the port's rule wants a
/// machine without Vulkan to run the flag-free path untouched, and a
/// <c>-gpu</c> without the package is one <c>VRAD0707</c> warning and the CPU
/// tracer, never a load failure of the whole CLI. A missing (or stripped, or
/// unloadable) package is never a crash either way: the cache verbs and
/// <c>WithBackendsAsync</c> print <see cref="MissingReason"/>, and
/// <c>-incremental</c> with no openable store compiles everything and says so.
/// </para>
/// <para>
/// Under <c>PublishAot</c> the GPU package cannot arrive by reflection —
/// NativeAOT has no JIT to bring IL in with — so the csproj links it
/// statically under that condition and roots it through
/// <c>aot-packages*.rd.xml</c>; the loader below then finds it in the default
/// load context by name, and the remaining reflection sits on types the
/// linker can see and keep.
/// </para>
/// <para>
/// Everything the verbs do with an open store is a direct <see
/// cref="ICacheStore"/> call: the interface lives in MapTools, which the host
/// references normally, so once the store object exists no reflection is
/// needed at all (and reflection into framework generic tasks, which AOT
/// trimming makes unreliable, never happens). Only the two constructions —
/// <see cref="Activator.CreateInstance(Type, object[])"/> on the store type
/// and the GPU's <c>TryCreateAsync</c> probe — stay by name.
/// </para>
/// <para>
/// The members are public because the CLI gets no <c>InternalsVisibleTo</c> —
/// a fact that asserts what the host does must be able to call what the host
/// does.
/// </para>
/// </remarks>
public static class HostBackends
{
    private const string SqliteAssembly = "SourceSharp.MapTools.Cache.Sqlite";
    private const string SqliteStoreType = SqliteAssembly + ".SqliteCacheStore";
    private const string GpuAssembly = "SourceSharp.MapTools.Gpu";
    private const string GpuTracerType = GpuAssembly + ".VulkanRayTracer";
    private const string GpuOptionsType = GpuAssembly + ".VulkanRayTracerOptions";

    /// <summary>The store type, or null when the SQLite package is absent.</summary>
    /// <remarks>
    /// The annotation sits on the property (so it covers what the getter hands
    /// out, which is the only way the type leaves this class) rather than on
    /// the backing field: the linker then keeps the constructors the single
    /// <see cref="Activator.CreateInstance(Type, object[])"/> seam needs and
    /// the analyzer proves the call instead of being muted about it.
    /// </remarks>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    public static Type? SqliteStore { get; } = Loaded(SqliteAssembly, SqliteStoreType);

    /// <summary>The tracer type, or null when the GPU package is absent.</summary>
    [DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicConstructors
        | DynamicallyAccessedMemberTypes.PublicMethods
        | DynamicallyAccessedMemberTypes.PublicProperties)]
    public static Type? VulkanTracer { get; } = Loaded(GpuAssembly, GpuTracerType);

    /// <summary>The reason a package could not be loaded, or null.</summary>
    public static string? MissingReason { get; private set; }

    /// <summary>The cache file ruling Q7 names for a map.</summary>
    /// <param name="cacheDir">The <c>-cache-dir</c> directory, or null for the map's.</param>
    /// <param name="mapDirectory">The directory holding the <c>.vmf</c>.</param>
    /// <param name="mapName">The level's base name.</param>
    /// <returns>The absolute store path: <c>&lt;dir&gt;/&lt;map&gt;.sscache.db</c>.</returns>
    public static string CachePathFor(string? cacheDir, string mapDirectory, string mapName) =>
        Path.Combine(Path.GetFullPath(cacheDir ?? mapDirectory), mapName + ".sscache.db");

    /// <summary>
    /// The opaque context tags the chain folds into every cache key: the
    /// resolved preset and the cooker whose bytes the store may hold. The
    /// CONTRACT (the T2 seam) is distinct selections → distinct tags, so a
    /// preset swap can never read another preset's cooked bytes.
    /// </summary>
    /// <param name="presetName">The resolved format preset, or null for the default bucket.</param>
    /// <param name="cooker">The cooker the run cooks with, or null.</param>
    /// <returns>The tags, in order.</returns>
    public static IReadOnlyList<string> ContextTagsFor(string? presetName, ICollisionCooker? cooker) =>
    [
        "preset=" + (presetName ?? "(default)"),
        "cooker=" + (cooker?.CookerIdentity ?? "(none)"),
    ];

    /// <summary>
    /// Opens the store <c>-incremental</c> asks for, or returns null: the
    /// flag-free posture, no cache. The caller owns and disposes the store.
    /// </summary>
    /// <param name="cacheDir">The <c>-cache-dir</c> directory, or null for the map's.</param>
    /// <param name="mapDirectory">The <c>.vmf</c>'s directory.</param>
    /// <param name="mapName">The level's base name.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open store, or null when caching is off or the package is absent.</returns>
    public static async Task<ICacheStore?> OpenCacheAsync(
        string? cacheDir,
        string mapDirectory,
        string mapName,
        CancellationToken cancellationToken = default)
    {
        if (SqliteStore is null)
        {
            return null;
        }

        try
        {
            ICacheStore store = Instantiate();
            await store.OpenAsync(
                Path.GetFullPath(CachePathFor(cacheDir, mapDirectory, mapName)), cancellationToken)
                .ConfigureAwait(false);
            return store;
        }
        catch (Exception failure) when (failure is TargetInvocationException or TypeInitializationException
            or DllNotFoundException or BadImageFormatException or FileLoadException
            or MissingMethodException or MissingMemberException or InvalidOperationException
            or PlatformNotSupportedException or System.Data.Common.DbException)
        {
            // A package that is present but cannot serve (no native provider,
            // a mismatched build) is the same posture as none: the run
            // compiles everything, and the reason rides the missing-reason
            // line the verbs print. A cache miss never kills a compile.
            MissingReason = RootMessage(failure);
            return null;
        }
    }

    /// <summary>
    /// The <c>-gpu</c> factory handed to vrad: the pinned device plus slab
    /// size the flags named, asking <see cref="TryOfferGpuAsync"/> per compile.
    /// </summary>
    /// <param name="deviceMatch">The <c>-gpu</c> device pin.</param>
    /// <param name="raysPerSlab">The <c>-gpu_slabs</c> ray count, or null.</param>
    public sealed class GpuFactory(string deviceMatch, int? raysPerSlab) : IGpuTracerFactory
    {
        /// <inheritdoc/>
        public ValueTask<GpuTracerOffer> TryCreateAsync(ShadowCasterSet casters, CancellationToken cancellationToken) =>
            TryOfferGpuAsync(casters, deviceMatch, raysPerSlab, cancellationToken);
    }

    /// <summary>
    /// The <c>-gpu</c> factory vrad asks when the casters exist: open the
    /// pinned device, pass the known-hit self-test, or decline with the
    /// backend's own reason. Never throws a driver failure.
    /// </summary>
    /// <param name="casters">The map's shadow casters, as vrad loaded them.</param>
    /// <param name="deviceMatch">The <c>-gpu</c> device pin (empty = any capable device).</param>
    /// <param name="raysPerSlab">The <c>-gpu_slabs</c> ray count, or null for the default.</param>
    /// <param name="cancellationToken">The compile's token; honoured before the attempt.</param>
    /// <returns>The offer: a hybrid-ready tracer, or the reason for CPU.</returns>
    public static async ValueTask<GpuTracerOffer> TryOfferGpuAsync(
        ShadowCasterSet casters,
        string? deviceMatch,
        int? raysPerSlab,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(casters);

        if (VulkanTracer is null)
        {
            return new GpuTracerOffer(null, MissingReason ?? $"{GpuAssembly} is not installed here");
        }

        try
        {
            object options = MakeGpuOptions(deviceMatch, raysPerSlab);
            object? task = CallStatic(
                VulkanTracer, "TryCreateAsync",
                new ReadOnlyMemory<TracedTriangle>(casters.Triangles.ToArray()), options, cancellationToken);
            if (task is not Task awaitable)
            {
                return new GpuTracerOffer(null, "VulkanRayTracer.TryCreateAsync returned no task");
            }

            object? attempt = await AwaitResultAsync(awaitable, cancellationToken).ConfigureAwait(false);
            if (attempt is null)
            {
                return new GpuTracerOffer(null, "the GPU attempt reported nothing");
            }

            bool success = Property(attempt, "Success") as bool? ?? false;
            if (success && Property(attempt, "Tracer") is IRayTracer gpu)
            {
                return new GpuTracerOffer(gpu, null);
            }

            return new GpuTracerOffer(null, DeclineReason(attempt));
        }
        catch (OperationCanceledException)
        {
            throw; // the compile's own cancellation, never a decline.
        }
        catch (Exception failure) when (failure is TargetInvocationException or MissingMethodException
            or MissingMemberException or InvalidOperationException or NotSupportedException
            or ArgumentException or BadImageFormatException or FileNotFoundException
            or FileLoadException)
        {
            return new GpuTracerOffer(null, RootMessage(failure));
        }
    }

    // -- verbs ----------------------------------------------------------------

    /// <summary>
    /// Reads a cache file's store-wide counters for <c>cache stats</c>.
    /// </summary>
    /// <param name="path">The store file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stats, or null when the package is absent or the file cannot open.</returns>
    public static async Task<CacheStats?> ReadStatsAsync(string path, CancellationToken cancellationToken = default)
    {
        await using ICacheStore? store = await OpenExistingAsync(path, cancellationToken).ConfigureAwait(false);
        return store is null
            ? null
            : await store.ReadStatsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Looks one key up in a cache file for <c>cache explain</c>.
    /// </summary>
    /// <param name="path">The store file.</param>
    /// <param name="key">The model key, lowercase hex.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The row, or null: absent, or the package is missing.</returns>
    public static async Task<CacheRecord?> ExplainAsync(string path, string key, CancellationToken cancellationToken = default)
    {
        await using ICacheStore? store = await OpenExistingAsync(path, cancellationToken).ConfigureAwait(false);
        return store is null
            ? null
            : await store.LookupAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the keys of a cache file for <c>cache explain</c> without a key.
    /// </summary>
    /// <param name="path">The store file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The keys, or null when the store cannot open.</returns>
    public static async Task<IReadOnlyList<string>?> ListKeysAsync(string path, CancellationToken cancellationToken = default)
    {
        await using ICacheStore? store = await OpenExistingAsync(path, cancellationToken).ConfigureAwait(false);
        return store is null
            ? null
            : await store.KeysAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops the blobs no row names and reclaims the file
    /// (<c>cache gc</c>).
    /// </summary>
    /// <param name="path">The store file.</param>
    /// <param name="maxCount">The most blobs one sweep may reclaim.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>The blob keys deleted, or null when the store cannot open.</returns>
    public static async Task<IReadOnlyList<string>?> GcAsync(string path, int maxCount, CancellationToken cancellationToken = default)
    {
        await using ICacheStore? store = await OpenExistingAsync(path, cancellationToken).ConfigureAwait(false);
        if (store is null)
        {
            return null;
        }

        IReadOnlyList<string> blobKeys = await store.CollectGarbageAsync(maxCount, cancellationToken).ConfigureAwait(false);
        if (blobKeys.Count > 0)
        {
            await store.DeleteBlobsAsync(blobKeys, cancellationToken).ConfigureAwait(false);
        }

        await store.VacuumAsync(cancellationToken).ConfigureAwait(false);
        return blobKeys;
    }

    /// <summary>
    /// Empties a cache file (<c>cache clear</c>).
    /// </summary>
    /// <param name="path">The store file.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>True when the store opened and emptied; false otherwise.</returns>
    public static async Task<bool> ClearAsync(string path, CancellationToken cancellationToken = default)
    {
        await using ICacheStore? store = await OpenExistingAsync(path, cancellationToken).ConfigureAwait(false);
        if (store is null)
        {
            return false;
        }

        await store.ClearAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }


    // THE ROOTING THE RUNTIME CANNOT DO FOR ITSELF (AOT arm only).
    //
    // AwaitResultAsync closes the generic helper below over the awaitable's
    // element type at runtime, and NativeAOT only executes a closed generic
    // whose native code and generic dictionary already exist at link time.
    // This field is the compile-time mention that creates them: converting the
    // closed method to a delegate materialises
    // HostBackends.UnboxAsync<VulkanTracerAttempt> and the
    // Task<VulkanTracerAttempt> it unwraps. It sits behind the symbol the
    // AOT-only csproj ItemGroup defines, so the JIT build — where the Gpu
    // types do not exist to be named — still compiles.
#if AOT_LINKED_PACKAGES
    private static readonly Func<Task<SourceSharp.MapTools.Gpu.VulkanTracerAttempt>, Task<object?>>? RootedUnbox =
        UnboxAsync<SourceSharp.MapTools.Gpu.VulkanTracerAttempt>;
#endif
    /// <summary>
    /// SQLite's own verdict on a cache file: the corrupt-reporting answer the
    /// verbs owe (a corrupt store degrades to no-cache in the compiler; this
    /// says so out loud).
    /// </summary>
    /// <param name="path">The store file.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The integrity verdict, or null when the store cannot open.</returns>
    public static async Task<bool?> CheckIntegrityAsync(string path, CancellationToken cancellationToken = default)
    {
        await using ICacheStore? store = await OpenExistingAsync(path, cancellationToken).ConfigureAwait(false);
        if (store is null)
        {
            return null;
        }

        return await store.CheckIntegrityAsync(cancellationToken).ConfigureAwait(false);
    }

    // -- reflection plumbing ---------------------------------------------------
    //
    // Only three seams left: find the type, construct the store, probe the GPU.
    // Everything past a constructed store is a direct ICacheStore call, so no
    // reflection ever touches a framework generic (whose AOT-trimmed metadata
    // the earlier probes showed unreliable) or a store method by name.

    [return: DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicConstructors
        | DynamicallyAccessedMemberTypes.PublicMethods
        | DynamicallyAccessedMemberTypes.PublicProperties)]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "The JIT posture opens the optional package with LoadFrom precisely because the csproj must stay package-free for that posture; the linker never sees those types, so it can neither strip nor warn about them — the analyzer's RequiresUnreferencedCode on LoadFrom/GetType speaks of app code it can see, and there is none here. The AOT posture links the package statically under PublishAot and roots it in aot-packages*.rd.xml; this call then resolves through the default load-context scan below.")]
    [UnconditionalSuppressMessage("Trimming", "IL2073:UnannotatedValueDoesNotSatisfyReturnValueRequirement",
        Justification = "The only values this returns are the two package types its callers name by const string — Cache.Sqlite.SqliteCacheStore and Gpu.VulkanRayTracer — and each package is linked under PublishAot and rooted whole by the rd.xml named in the csproj (Dynamic=Required All), so the constructors/methods/properties the callers' annotations demand are kept by that root. Assembly.GetType cannot carry the annotation and the analyzer cannot see through it.")]
    private static Type? Loaded(string assembly, string type)
    {
        try
        {
            // The package sits beside ssmap.dll when installed, but is not in
            // the entry's deps.json (the csproj is frozen), so the name probe
            // goes to the app's own directory first.
            string dll = Path.Combine(AppContext.BaseDirectory, assembly + ".dll");
            Assembly? loaded = File.Exists(dll)
                ? Assembly.LoadFrom(dll)
                : AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == assembly);
            Type? found = loaded?.GetType(type, throwOnError: false);
            if (found is null)
            {
                MissingReason = $"neither {assembly}.dll nor its provider is installed here";
            }

            return found;
        }
        catch (Exception failure) when (failure is BadImageFormatException or FileLoadException
            or InvalidOperationException or PlatformNotSupportedException)
        {
            // PlatformNotSupportedException: an AOT binary does not JIT, so an
            // IL package dll dropped beside it cannot serve — same posture as
            // absent, stated by the reason line rather than a crash.
            MissingReason = failure.Message;
            return null;
        }
    }

    // A store opened readOnly fails its own identity check (the file is WAL,
    // and a read-only WAL open cannot seed the -shm), so the verbs — reader
    // and writer alike — use the normal open. The store itself is still
    // never corrupted by a verb: they only read, clear, or vacuum.
    private static async Task<ICacheStore?> OpenExistingAsync(string path, CancellationToken cancellationToken)
    {
        if (SqliteStore is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            ICacheStore store = Instantiate();
            await store.OpenAsync(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
            return store;
        }
        catch (Exception failure) when (failure is TargetInvocationException or TypeInitializationException
            or DllNotFoundException or BadImageFormatException or FileLoadException
            or MissingMethodException or MissingMemberException or InvalidOperationException
            or PlatformNotSupportedException or System.Data.Common.DbException)
        {
            MissingReason = RootMessage(failure);
            return null; // the verb prints its unavailable line
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "VulkanRayTracerOptions lives in the Gpu package: absent in the JIT posture (the linker has nothing to strip), statically linked and rooted by aot-packages-gpu.rd.xml in the AOT posture, where a name-resolved GetType on a rooted assembly is trim-safe.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072:NullabilityMismatchOnParameter",
        Justification = "The options type flows from the Assembly.GetType above, which the analyzer cannot see through; the value is the rooted VulkanRayTracerOptions type named at the top of this line, and its public ctor is exactly what the root keeps.")]
    private static object MakeGpuOptions(string? deviceMatch, int? raysPerSlab)
    {
        Type options = VulkanTracer!.Assembly.GetType(GpuOptionsType, throwOnError: true)!;
        object?[] arguments =
        [
            string.IsNullOrEmpty(deviceMatch) ? null : deviceMatch,
            -1,
            raysPerSlab ?? 4_194_304,
            120,
        ];
        return Activator.CreateInstance(options, arguments)!;
    }

    private static string? DeclineReason(object attempt)
    {
        object? report = Property(attempt, "Report");
        string? failure = Property(report!, "Failure") as string;
        object? selected = Property(report!, "Selected");
        string? selfTest = selected is null ? null : Property(selected, "Reason") as string;
        string? device = selected is null ? null : Property(selected, "DeviceName") as string;
        string reason = failure ?? selfTest ?? "the self-test did not clear";
        return device is null ? reason : $"{device}: {reason}";
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075:NullabilityMismatchOnThis",
        Justification = "The targets are the Gpu package's own public records (VulkanTracerAttempt, VulkanDeviceReport, SelfTestRecord) — trim-immune in the JIT posture (the linker never sees the package), rooted by name in aot-packages-gpu.rd.xml in the AOT posture, whose Dynamic=Required All keeps exactly this property metadata.")]
    private static object? Property(object? target, string name) =>
        target?.GetType().GetProperty(name)?.GetValue(target);

    private static object? CallStatic(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        string name,
        params object?[] arguments) =>
        Resolve(type, name, arguments) is not { } method
            ? throw new MissingMethodException(type.Name, name)
            : method.Invoke(null, arguments);

    private static MethodInfo? Resolve(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        string name,
        object?[] arguments) =>
        (from m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
         where m.Name == name && m.GetParameters().Length == arguments.Length
         select m).FirstOrDefault();

    // Awaiting a Task the static type of this assembly has never seen. The
    // result comes through a generic helper of THIS class instantiated at the
    // awaitable's element type — reflection on a framework Task<T>'s Result
    // property is exactly what an earlier probe showed AOT trimming can make
    // return null, so it is never asked for.
    [UnconditionalSuppressMessage("Execution", "IL3050:RequiresDynamicCode",
        Justification = "MakeGenericMethod closes UnboxAsync over the one element type this seam ever sees, VulkanTracerAttempt, whose instantiation the RootedUnbox field above creates at link time (the AOT posture links the Gpu package, so the type is static there and the mention compiles); the JIT posture JITs the instantiation on demand. No other type argument is reachable: the only caller passes the task returned by the package's TryCreateAsync, declared Task<VulkanTracerAttempt>.")]
    private static async Task<object?> AwaitResultAsync(Task awaitable, CancellationToken cancellationToken)
    {
        await awaitable.ConfigureAwait(false);
        Type type = awaitable.GetType();
        if (!type.IsGenericType)
        {
            return null;
        }

        MethodInfo unbox = typeof(HostBackends)
            .GetMethod(nameof(UnboxAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type.GetGenericArguments()[0]);
        var boxed = (Task<object?>)unbox.Invoke(null, [awaitable])!;
        return await boxed.ConfigureAwait(false);
    }

    private static async Task<object?> UnboxAsync<T>(Task<T> task) => await task.ConfigureAwait(false);

    // The store's only ctor takes `bool readOnly = false`; Activator needs it
    // spelled, and a defaulted parameterless call is not a parameterless ctor.
    // The SqliteStore field carries the PublicConstructors annotation, so the
    // linker keeps it and the analyzer is satisfied without a suppression.
    private static ICacheStore Instantiate() =>
        (ICacheStore)Activator.CreateInstance(SqliteStore!, [false])!;

    // The reflection seam wraps the real failure (TargetInvocationException
    // over TypeInitializationException over the native-load one): quote the
    // deepest message, which is the diagnosis.
    private static string RootMessage(Exception failure)
    {
        for (Exception? e = failure; e.InnerException is not null; e = e.InnerException)
        {
            failure = e.InnerException;
        }

        return failure.Message;
    }
}
