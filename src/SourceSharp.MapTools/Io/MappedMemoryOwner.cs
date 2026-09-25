using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace SourceSharp.MapTools.Io;

/// <summary>
/// An <see cref="IMemoryOwner{T}"/> over a read-only memory-mapped view of a
/// file, so a 200 MB BSP does not become 200 MB of garbage.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="PhysicalFileSystem"/> creates one, and nothing outside this
/// file can tell that it did: the caller sees a <see cref="Memory{T}"/> it reads
/// and an owner it disposes, exactly as for the pooled case.
/// </para>
/// <para>
/// A <see cref="MemoryManager{T}"/> rather than a plain owner because that is
/// the only way to hand out a <see cref="Memory{T}"/> backed by a pointer. The
/// view handle is acquired for the owner's whole life and released on dispose;
/// <see cref="Pin"/> hands back the same pointer because the mapping already
/// cannot move.
/// </para>
/// </remarks>
internal sealed unsafe class MappedMemoryOwner : MemoryManager<byte>
{
    private readonly int _length;
    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private byte* _pointer;
    private bool _acquired;

    private MappedMemoryOwner(MemoryMappedFile file, MemoryMappedViewAccessor view, int length)
    {
        _file = file;
        _view = view;
        _length = length;

        view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
        _acquired = true;
        _pointer += view.PointerOffset;
    }

    /// <summary>Maps <paramref name="hostPath"/> read-only.</summary>
    /// <param name="hostPath">The host path of an existing file.</param>
    /// <param name="length">The file's length, which must fit an <see cref="int"/>.</param>
    /// <returns>The owner; the caller disposes it.</returns>
    public static MappedMemoryOwner Map(string hostPath, long length)
    {
        MemoryMappedFile file = MemoryMappedFile.CreateFromFile(
            hostPath,
            FileMode.Open,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.Read);

        try
        {
            MemoryMappedViewAccessor view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            try
            {
                return new MappedMemoryOwner(file, view, checked((int)length));
            }
            catch
            {
                view.Dispose();
                throw;
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_view is null, this);
        return new Span<byte>(_pointer, _length);
    }

    /// <inheritdoc />
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_view is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, _length);
        return new MemoryHandle(_pointer + elementIndex);
    }

    /// <inheritdoc />
    public override void Unpin()
    {
        // The mapping cannot move, so a pin never had to do anything.
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        MemoryMappedViewAccessor? view = _view;
        _view = null;

        if (view is not null)
        {
            if (_acquired)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                _acquired = false;
            }

            _pointer = null;
            view.Dispose();
        }

        _file?.Dispose();
        _file = null;
    }
}
