using SourceSharp.MapFormats.Bsp.Structs;

namespace SourceSharp.MapTools.Disp;

/// <summary>
/// Walks the vertices two displacements share along ONE sub-neighbour:
/// <c>CDispSubEdgeIterator</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Call <see cref="Next"/> first.</b> <c>Start</c> positions the iterator
/// one step BEFORE the first vertex, so the loop is
/// <c>while (it.Next())</c> and never a do-while. That is stock's shape and it
/// is the reason the corner-touching mode can be a single subtraction.
/// </para>
/// <para>
/// By default the corners are NOT visited: the walk stops one short at each
/// end, because a corner vertex belongs to up to four displacements and is
/// handled by the corner lists instead.
/// </para>
/// </remarks>
public struct DispSubEdgeIterator
{
    private VertIndex _index;
    private VertIndex _increment;
    private VertIndex _nbIndex;
    private VertIndex _nbIncrement;
    private int _end;
    private int _freeDim;

    /// <summary>The neighbour this walk is along, or null when it is empty.</summary>
    public IDispUtils? Neighbor { get; private set; }

    /// <summary>The current vertex on the displacement the walk started from.</summary>
    public readonly VertIndex VertIndex => _index;

    /// <summary>The matching vertex on <see cref="Neighbor"/>.</summary>
    public readonly VertIndex NeighborVertIndex => _nbIndex;

    /// <summary>
    /// Points the iterator at one sub-neighbour:
    /// <c>CDispSubEdgeIterator::Start</c>.
    /// </summary>
    /// <param name="disp">The displacement to walk from.</param>
    /// <param name="edge">Which of its edges.</param>
    /// <param name="sub">Which sub-neighbour.</param>
    /// <param name="touchCorners">
    /// Extend the walk by one step at each end so the shared corners are
    /// visited too.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="disp"/> is null.</exception>
    public void Start(IDispUtils disp, int edge, int sub, bool touchCorners = false)
    {
        ArgumentNullException.ThrowIfNull(disp);

        Neighbor = DispUtils.SetupEdgeIncrements(disp, edge, sub, out EdgeWalk walk);

        if (Neighbor is null)
        {
            // Stock's "setup so Next returns false": index.x, inc.x, end and
            // the free dimension all zeroed, which makes the first Next compare
            // 0 < 0.
            _index = default;
            _increment = default;
            _nbIndex = default;
            _nbIncrement = default;
            _end = 0;
            _freeDim = 0;
            return;
        }

        _index = walk.Index;
        _increment = walk.Increment;
        _nbIndex = walk.NeighborIndex;
        _nbIncrement = walk.NeighborIncrement;
        _end = walk.End;
        _freeDim = walk.FreeDim;

        if (!touchCorners)
        {
            return;
        }

        _index -= _increment;
        _nbIndex -= _nbIncrement;
        _end += _increment[_freeDim];
    }

    /// <summary>Steps to the next shared vertex.</summary>
    /// <returns>False once the walk is past the end.</returns>
    public bool Next()
    {
        _index += _increment;
        _nbIndex += _nbIncrement;

        return _index[_freeDim] < _end;
    }

    /// <summary>Whether the next <see cref="Next"/> will end the walk.</summary>
    /// <returns>True on the last vertex.</returns>
    public readonly bool IsLastVert() => _index[_freeDim] + _increment[_freeDim] >= _end;
}

/// <summary>
/// Walks every vertex one displacement shares along one whole edge, across
/// both sub-neighbours: <c>CDispEdgeIterator</c>.
/// </summary>
/// <remarks>
/// Corner vertices are never visited, and neither is the edge's own midpoint
/// when two neighbours meet there — that vertex is a corner of both of them.
/// </remarks>
public struct DispEdgeIterator
{
    private readonly IDispUtils _disp;
    private readonly int _edge;
    private int _currentSub;
    private DispSubEdgeIterator _iterator;

    /// <summary>Starts a walk along one edge.</summary>
    /// <param name="disp">The displacement.</param>
    /// <param name="edge">Which of its edges.</param>
    /// <exception cref="ArgumentNullException"><paramref name="disp"/> is null.</exception>
    public DispEdgeIterator(IDispUtils disp, int edge)
    {
        ArgumentNullException.ThrowIfNull(disp);

        _disp = disp;
        _edge = edge;
        _currentSub = -1;
        _iterator = default;
    }

    /// <summary>The current vertex on the displacement.</summary>
    public readonly VertIndex VertIndex => _iterator.VertIndex;

    /// <summary>The matching vertex on the current neighbour.</summary>
    public readonly VertIndex NeighborVertIndex => _iterator.NeighborVertIndex;

    /// <summary>The neighbour the current vertex is shared with.</summary>
    public readonly IDispUtils? CurrentNeighbor => _iterator.Neighbor;

    /// <summary>Steps to the next shared vertex, moving on to sub 1 when sub 0 runs out.</summary>
    /// <returns>False when the edge is done.</returns>
    public bool Next()
    {
        while (!_iterator.Next())
        {
            if (_currentSub == 1)
            {
                return false;
            }

            _currentSub++;
            _iterator.Start(_disp, _edge, _currentSub);
        }

        return true;
    }
}

/// <summary>
/// Walks every vertex on the boundary of a displacement, corners included:
/// <c>CDispCircumferenceIterator</c>.
/// </summary>
/// <remarks>
/// The order is left edge upward, top rightward, right downward, bottom
/// leftward, ending just before it returns to the lower-left corner it started
/// at.
/// </remarks>
public struct DispCircumferenceIterator
{
    private readonly int _sideLengthMinus1;
    private int _currentEdge;
    private VertIndex _vertIndex;

    /// <summary>Starts a walk around a displacement of a given size.</summary>
    /// <param name="sideLength">Its side length in vertices.</param>
    public DispCircumferenceIterator(int sideLength)
    {
        _sideLengthMinus1 = sideLength - 1;
        _currentEdge = -1;
        _vertIndex = default;
    }

    /// <summary>The current vertex.</summary>
    public readonly VertIndex VertIndex => _vertIndex;

    /// <summary>Steps to the next boundary vertex.</summary>
    /// <returns>False once the walk has come back round.</returns>
    public bool Next()
    {
        switch (_currentEdge)
        {
            case -1:
                _currentEdge = (int)DispEdge.Left;
                _vertIndex = new VertIndex(0, 0);
                break;

            case (int)DispEdge.Left:
                _vertIndex = new VertIndex(_vertIndex.X, _vertIndex.Y + 1);
                if (_vertIndex.Y == _sideLengthMinus1)
                {
                    _currentEdge = (int)DispEdge.Top;
                }

                break;

            case (int)DispEdge.Top:
                _vertIndex = new VertIndex(_vertIndex.X + 1, _vertIndex.Y);
                if (_vertIndex.X == _sideLengthMinus1)
                {
                    _currentEdge = (int)DispEdge.Right;
                }

                break;

            case (int)DispEdge.Right:
                _vertIndex = new VertIndex(_vertIndex.X, _vertIndex.Y - 1);
                if (_vertIndex.Y == 0)
                {
                    _currentEdge = (int)DispEdge.Bottom;
                }

                break;

            default:
                _vertIndex = new VertIndex(_vertIndex.X - 1, _vertIndex.Y);
                if (_vertIndex.X == 0)
                {
                    return false;
                }

                break;
        }

        return true;
    }
}
