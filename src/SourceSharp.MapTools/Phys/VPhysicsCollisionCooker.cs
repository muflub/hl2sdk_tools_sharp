using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

using SourceSharp.MapTools.Io;

namespace SourceSharp.MapTools.Phys;

/// <summary>
/// The collision cooker over Valve's own 64-bit <c>vphysics.so</c>: every call
/// is marshalled onto ONE dedicated thread.
/// </summary>
/// <remarks>
/// <para>
/// ONE PER PROCESS. The library keeps its state in its own globals and a
/// second <c>dlopen</c> of the same file returns the same object, so two
/// cookers would be two threads in one non-thread-safe library -- the regime
/// spike 0b measured crashing in 11 of 11 runs. Creating a second cooker while
/// one is alive is refused; share the one you have (every compile in a process
/// may, which is what plan §1a documents). Separate PROCESSES are safe: each
/// has its own copy of the globals.
/// </para>
/// <para>
/// THE HOST MUST BE LAUNCHED WITH <c>LD_LIBRARY_PATH</c> naming the library's
/// directory. <c>libtier0.so</c> has no <c>DT_SONAME</c>, so loading it by
/// absolute path does not satisfy the other two libraries' <c>DT_NEEDED</c>,
/// and glibc reads the variable once at start-up. <c>ssmap</c> re-executes
/// itself with it set; a library cannot, so it says so in the
/// <see cref="VPhysicsLoadException"/> instead.
/// </para>
/// <para>
/// The libraries are never unloaded: stock never unloads vphysics either, and a
/// closed library's static destructors are not something to run speculatively.
/// </para>
/// </remarks>
public sealed class VPhysicsCollisionCooker : ICollisionCooker
{
    private const string ProcessOwnerKey = "SourceSharp.MapTools.Phys.VPhysicsCollisionCooker.Owner";

    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _marker = new();
    private NativeCollisionSession? _session;
    private int _disposed;

    private VPhysicsCollisionCooker(string identity)
    {
        CookerIdentity = identity;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "vphysics cooker",
        };
    }

    /// <inheritdoc />
    public string CookerIdentity { get; }

    /// <summary>
    /// Whether loading the library switched its thread to flush denormals
    /// (FTZ/DAZ) -- the environment every native call then runs under.
    /// </summary>
    /// <remarks>
    /// The SDK build of <c>vphysics.so</c> links <c>crtfastmath</c>, whose
    /// constructor sets FTZ and DAZ in MXCSR on the thread that loads it
    /// (Ghidra comparison, re-vphys-findings §5). MXCSR is per thread but is
    /// INHERITED by threads that thread creates, and the .NET thread pool can
    /// create workers from the cooker thread; the first binding leaked FTZ onto
    /// a pool thread that way (measured). So the cooker captures the library's
    /// environment, restores IEEE on its own thread straight after the load,
    /// and enters the library's environment only for the duration of each
    /// native call (<c>NativeScope</c>). Managed code in a
    /// <see cref="RunAsync{T}"/> callback, and every other thread, stays IEEE.
    /// </remarks>
    public bool LibraryFlushesDenormals { get; private set; }

    /// <summary>Whether the cooker thread itself flushes denormals between native calls (it must not).</summary>
    public bool CookerThreadFlushesDenormals { get; private set; }

    /// <summary>
    /// Whether this thread flushes a denormal: the smallest float times one is
    /// zero under DAZ (the input is read as zero) or FTZ (the result is).
    /// </summary>
    /// <param name="tiny">A denormal, loaded by <see cref="DenormalProbe.Tiny"/> at runtime.</param>
    /// <returns>True when denormals are flushed.</returns>
    /// <remarks>
    /// The multiply runs under the thread's MXCSR at the call site: the only
    /// constant is 1.0, and the comparison is against the same value the
    /// multiply produced, so neither folds and both keep runtime semantics.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static bool FlushesDenormals(float tiny) => tiny * 1.0f == 0.0f;

    /// <summary>Loads the denormal's bits from the live heap: see <see cref="DenormalProbe.Tiny"/>.</summary>
    private readonly DenormalProbe _denormalProbe = new();

    /// <summary>
    /// Asks the probe whether this thread flushes a denormal.
    /// </summary>
    /// <returns>True when denormals are flushed on this thread.</returns>
    public bool ProbeFlushesDenormals() => FlushesDenormals(_denormalProbe.Tiny());

    /// <summary>
    /// Loads the library and starts the cooker thread.
    /// </summary>
    /// <param name="fileSystem">
    /// The physical filesystem the library lives on: it is read through it for
    /// its identity, and mapped to an OS path for the loader.
    /// </param>
    /// <param name="libraryPath">The <c>vphysics.so</c> to load, in a <c>bin/linux64</c> beside <c>libtier0.so</c> and <c>libvstdlib.so</c>.</param>
    /// <param name="cancellationToken">Cancels before the load.</param>
    /// <returns>The running cooker.</returns>
    /// <exception cref="VPhysicsLoadException">The library could not be loaded.</exception>
    /// <exception cref="InvalidOperationException">Another cooker is alive in this process.</exception>
    public static async Task<VPhysicsCollisionCooker> CreateAsync(
        PhysicalFileSystem fileSystem,
        VPath libraryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        cancellationToken.ThrowIfCancellationRequested();

        ElfIdentity elf;
        await using (Stream stream = await fileSystem.OpenReadAsync(libraryPath, cancellationToken).ConfigureAwait(false))
        {
            elf = await ElfIdentityReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        string md5;
        using (IMemoryOwner<byte> bytes = await fileSystem.ReadAllAsync(libraryPath, cancellationToken).ConfigureAwait(false))
        {
            md5 = Convert.ToHexStringLower(MD5.HashData(bytes.Memory.Span));
        }

        if (!elf.IsLoadableHere)
        {
            throw new VPhysicsLoadException(
                $"{libraryPath} is {elf.Architecture}, not a 64-bit x86 library this process can load.");
        }

        string identity = string.Create(
            CultureInfo.InvariantCulture,
            $"vphysics.so build-id={elf.BuildId ?? "none"} md5={md5}");

        string hostDirectory = fileSystem.ToHostPath(libraryPath.Directory);
        VPhysicsCollisionCooker cooker = new(identity);
        cooker.ClaimProcess();

        try
        {
            cooker._thread.Start();
            await cooker.EnqueueAsync<int>(
                _ =>
                {
                    FloatEnv ieee = FloatEnvironment.Get();
                    VPhysicsModule module = VPhysicsModule.Load(hostDirectory);

                    // The load may have set FTZ/DAZ on this thread (crtfastmath).
                    // Keep that environment for native calls only: a thread this
                    // one creates -- a thread-pool worker, say -- inherits it.
                    FloatEnv library = FloatEnvironment.Get();
                    cooker.LibraryFlushesDenormals = cooker.ProbeFlushesDenormals();
                    FloatEnvironment.Set(ieee);
                    cooker.CookerThreadFlushesDenormals = cooker.ProbeFlushesDenormals();

                    cooker._session = new NativeCollisionSession(module, Environment.CurrentManagedThreadId, library);
                    return 0;
                },
                beforeLoad: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await cooker.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return cooker;
    }


    /// <inheritdoc />
    public Task<T> RunAsync<T>(Func<ICollisionSession, T> work, CancellationToken cancellationToken = default) =>
        EnqueueAsync(work, beforeLoad: false, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_thread.IsAlive)
        {
            _queue.Add(() => _session?.ReleaseMeshEvents());
            _queue.CompleteAdding();
            await _stopped.Task.ConfigureAwait(false);
        }
        else
        {
            _queue.CompleteAdding();
        }

        _queue.Dispose();
        ReleaseProcess();
    }

    private Task<T> EnqueueAsync<T>(Func<ICollisionSession, T> work, bool beforeLoad, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void Item()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                ICollisionSession session = beforeLoad
                    ? null!
                    : _session ?? throw new InvalidOperationException("the cooker never loaded its library.");
                completion.TrySetResult(work(session));
            }
            catch (Exception e)
            {
                completion.TrySetException(e);
            }
        }

        try
        {
            _queue.Add(Item, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(VPhysicsCollisionCooker));
        }

        return completion.Task;
    }

    private void Loop()
    {
        try
        {
            foreach (Action item in _queue.GetConsumingEnumerable())
            {
                item();
            }
        }
        finally
        {
            _stopped.TrySetResult();
        }
    }

    private void ClaimProcess()
    {
        lock (typeof(VPhysicsCollisionCooker))
        {
            if (AppContext.GetData(ProcessOwnerKey) is not null)
            {
                throw new InvalidOperationException(
                    "a VPhysicsCollisionCooker is already alive in this process. vphysics keeps its "
                    + "state in process-wide globals and is not thread-safe, so a second cooker would "
                    + "be a second thread in the same library. Share the existing cooker.");
            }

            AppContext.SetData(ProcessOwnerKey, _marker);
        }
    }

    private void ReleaseProcess()
    {
        lock (typeof(VPhysicsCollisionCooker))
        {
            if (ReferenceEquals(AppContext.GetData(ProcessOwnerKey), _marker))
            {
                AppContext.SetData(ProcessOwnerKey, null);
            }
        }
    }
}
