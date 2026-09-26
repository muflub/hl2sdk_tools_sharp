//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Numerics;

namespace SourceSharp.MapTools.Phys.Managed;

/// <summary>
/// The IVP builders at one precision, behind one non-generic face so the session code is written
/// once (the arithmetic is still chosen once per cooker: stock float or TF2 double).
/// </summary>
internal interface IIvpBuild
{
    /// <summary>True for TF2's double arithmetic.</summary>
    bool IsDouble { get; }

    /// <summary><c>ConvexFromPlanes</c>.</summary>
    IvpCompactLedge? ConvexFromPlanes(ReadOnlySpan<(float X, float Y, float Z, float Distance)> planes, float mergeDistance);

    /// <summary><c>ConvexFromVerts</c> (with its <c>RebuildConvexFromPlanes</c>).</summary>
    IvpCompactLedge? ConvexFromVerts(ReadOnlySpan<(float X, float Y, float Z)> points);

    /// <summary><c>ConvexFromVertsFast</c>: the point soup alone.</summary>
    IvpCompactLedge? ConvexFromVertsFast(ReadOnlySpan<(float X, float Y, float Z)> points);

    /// <summary>
    /// <c>IVP_SurfaceBuilder_Ledge_Soup::compile</c> plus <c>new CPhysCollideCompactSurface</c>:
    /// the surface with <c>dummy[0]</c> = 0 and <c>dummy[2]</c> = 'IVPS'.
    /// </summary>
    byte[]? Compile(List<IvpCompactLedge> ledges, bool buildRootConvexHull);
}

/// <summary><see cref="IIvpBuild"/> at one precision over one thread's scratch.</summary>
/// <typeparam name="T">float (stock) or double (TF2).</typeparam>
/// <typeparam name="TP">The matching precision policy.</typeparam>
internal sealed class IvpBuild<T, TP>(IvpCookContext context) : IIvpBuild
    where T : unmanaged, IBinaryFloatingPointIeee754<T>
    where TP : struct, IIvpPrecision<T>
{
    /// <inheritdoc/>
    public bool IsDouble => TP.IsDouble;

    /// <inheritdoc/>
    public IvpCompactLedge? ConvexFromPlanes(ReadOnlySpan<(float X, float Y, float Z, float Distance)> planes, float mergeDistance) =>
        IvpCooker<T, TP>.ConvexFromPlanes(planes, mergeDistance, context);

    /// <inheritdoc/>
    public IvpCompactLedge? ConvexFromVerts(ReadOnlySpan<(float X, float Y, float Z)> points) =>
        IvpCooker<T, TP>.ConvexFromVerts(points, context);

    /// <inheritdoc/>
    public IvpCompactLedge? ConvexFromVertsFast(ReadOnlySpan<(float X, float Y, float Z)> points) =>
        IvpCooker<T, TP>.ConvexFromVertsFast(points, context);

    /// <inheritdoc/>
    public byte[]? Compile(List<IvpCompactLedge> ledges, bool buildRootConvexHull)
    {
        byte[]? surface = IvpLedgeSoup<T, TP>.Compile(ledges, buildRootConvexHull, context);
        if (surface is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(surface.AsSpan(0x24), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(surface.AsSpan(0x2c), 0x53505649u);
        }

        return surface;
    }
}
