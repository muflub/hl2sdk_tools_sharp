//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Bsp.Tree;

/// <summary>
/// The six numbers <c>BrushBSP</c> prints under <c>-v</c>,
/// </summary>
/// <param name="Brushes">"%5i brushes": how many entered the build.</param>
/// <param name="VisibleFaces">
/// "%5i visible faces": sides that are not bevels, have a winding, are not
/// already on a node, and are visible.
/// </param>
/// <param name="NonVisibleFaces">"%5i nonvisible faces": the same, not visible.</param>
/// <param name="VisibleNodes">"%5i visible nodes": <c>c_nodes/2 - c_nonvis</c>.</param>
/// <param name="NonVisibleNodes">"%5i nonvis nodes": <c>c_nonvis</c>.</param>
/// <param name="Leaves">"%5i leafs": <c>(c_nodes+1)/2</c>.</param>
/// <remarks>
/// <para>
/// <b>These are the only window into the tree's shape that a stock compile
/// leaves behind</b>, short of reading the NODES and LEAFS lumps of a finished
/// BSP — which are Phase 3e's to write and carry 3c's and 3d's decisions as
/// well as this lane's. Recording them makes the tree build gateable against
/// a stock <c>-v</c> log on its own, a block at a time, before any of the later
/// stages exist.
/// </para>
/// <para>
/// <see cref="VisibleNodes"/> and <see cref="Leaves"/> are derived from one
/// counter with integer division, so they are not independent: a tree of
/// <c>n</c> internal nodes and <c>n+1</c> leaves has <c>c_nodes = 2n+1</c>,
/// and stock's arithmetic recovers <c>n</c> and <c>n+1</c> from it. A leaf
/// count is therefore exactly one more than an internal node count, always,
/// and the pair says nothing the single number does not.
/// </para>
/// </remarks>
public readonly record struct BspTreeStatistics(
    int Brushes,
    int VisibleFaces,
    int NonVisibleFaces,
    int VisibleNodes,
    int NonVisibleNodes,
    int Leaves);

/// <summary>
/// The two numbers <c>ChopBrushes</c> prints under <c>-v</c>,
/// </summary>
/// <param name="Input">"original brushes: %i".</param>
/// <param name="Output">"output brushes: %i".</param>
/// <remarks>
/// A carve that fragments has more output than input; one that swallows has
/// fewer. On a map with no overlapping brushes at all the two are equal, which
/// is what most of the catalogue looks like and is why the pair is only
/// interesting on the maps that overlap.
/// </remarks>
public readonly record struct ChopStatistics(int Input, int Output);

/// <summary>
/// What one block of the world grid produced.
/// </summary>
/// <param name="BlockX">The block's X coordinate.</param>
/// <param name="BlockY">The block's Y coordinate.</param>
/// <param name="Chop">
/// The CSG counts, or null when the block held no brushes or
/// <c>-nocsg</c> was given.
/// </param>
/// <param name="Tree">The tree counts, or null when the block was empty.</param>
/// <remarks>
/// An empty block becomes a solid leaf with no tree at all
/// Which is why both members are optional and why a
/// count of these records is not a count of trees.
/// </remarks>
public readonly record struct BlockBuildStatistics(
    int BlockX,
    int BlockY,
    ChopStatistics? Chop,
    BspTreeStatistics? Tree);
