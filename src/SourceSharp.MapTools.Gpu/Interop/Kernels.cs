//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapTools.Gpu.Interop;

/// <summary>
/// The one piece of logic in this assembly that is not C#.
/// </summary>
/// <remarks>
/// <para>
/// Derived from the Phase 0f spike kernel with three production changes and
/// NO restructuring: <c>tmin</c> and the
/// any-hit tmax scale come from push constants instead of hard-coded
/// <c>1e-3</c>/<c>tmax</c>, and out-of-range tail lanes run no query at all.
/// The SHAPE is the finding: initialize -&gt; <c>while (proceed) {}</c> to
/// exhaustion -&gt; read the COMMITTED state only after convergence. Do not
/// "tidy" the loop.
/// </para>
/// <para>
/// <b>No terminate after a finished loop.</b> <c>rayQueryTerminateEXT</c>
/// (<c>OpRayQueryTerminateKHR</c>) is only defined when the last
/// <c>rayQueryProceedEXT</c> on that query returned true: it is the way to
/// stop a traversal that still has candidates to offer. After
/// <c>while (proceed) {}</c> the last proceed returned false, so a terminate
/// there is undefined behaviour, and a driver is free to do anything with
/// it, including discarding the committed intersection the kernel is about
/// to read. The committed state is readable without a terminate once
/// proceed has returned false, so the closest and any-hit modes have none.
/// The only terminate is in the telemetry mode, on the <c>break</c> that
/// leaves the loop while proceed's last answer was true.
/// <c>KernelRayQueryRuleTests</c> scans <see cref="RayGlsl"/> and fails on
/// a terminate anywhere else, because nothing a device answers would show
/// the mistake reliably: a driver that happens to ignore it gives right
/// answers.
/// </para>
/// <para>
/// Modes kept: 0 = any-hit (TerminateOnFirstHit), 1 = closest, 4 = output
/// readback sanity (no traversal), 5 = traversal telemetry
/// (proceed-iterations / candidates-seen). 4 and 5 exist only for the
/// capability self-test's report. Mode 4 separates "the compute output path
/// is broken" from everything else. Mode 5 can only add detail: with an
/// opaque BLAS a conformant driver offers no candidates, so zero iterations
/// is what a working device reports too, and the telemetry cannot tell a
/// device that never traverses from one that traverses and commits the
/// wrong answer. What it can show is a driver offering opaque triangles as
/// candidates (llvmpipe does), which is worth naming beside a failed
/// known-hit test. The
/// spike's diagnostic modes 2 (no-TOFH) and 3 (in-kernel Möller–Trumbore)
/// were probe scaffolding and are not productized.
/// </para>
/// <para>
/// Output layout is sample-major and deterministic with NO global atomics:
/// one workgroup is 64 rays and owns exactly two <c>uint</c> words of the bit
/// output (<c>word[2g]</c> = rays 64g..64g+31 LSB-first, <c>word[2g+1]</c> =
/// the next 32), which lane 0 and lane 32 each write once after folding the
/// workgroup's shared bits. The shared-memory <c>atomicOr</c> is inside one
/// workgroup behind a barrier, so the bytes a slab produces are a
/// deterministic function of that slab's rays only — the invariant a batch
/// must give the same bits whatever slab size, chunking, or thread count fed
/// it.
/// </para>
/// </remarks>
internal static class Kernels
{
    /// <summary>The any-hit/closest kernel, GLSL 460 + <c>GL_EXT_ray_query</c>.</summary>
    public const string RayGlsl = """
#version 460
#extension GL_EXT_ray_query : require
layout(local_size_x = 64) in;
layout(binding = 0) buffer Rays { vec4 data[]; } RAYS;      // 2 vec4 per ray: (o,0), (d,tmax)
layout(binding = 1) buffer OutU { uint data[]; } OUT;
layout(binding = 2) uniform accelerationStructureEXT BLAS;
layout(push_constant) uniform Args {
    uint mode; uint rayCount; uint triCount; uint reserved;
    uint tminBits; uint tmaxScaleBits; uint reserved2; uint reserved3;
} PC;
shared uint s_bits[2];

void trace_one(bool anyMode, out bool hit, out uint prim, out float t) {
    uint index = (gl_WorkGroupID.x * 64u) + gl_LocalInvocationIndex;
    uint rb = index * 2u;
    vec3 origin = RAYS.data[rb + 0u].xyz;
    vec3 dir = RAYS.data[rb + 1u].xyz;
    float tmax = RAYS.data[rb + 1u].w;

    // SPEC USAGE (the finding this kernel exists for): the committed
    // intersection may only be read once the query is CONSISTENT, i.e. after
    // rayQueryProceedEXT returned false. No rayQueryTerminateEXT follows the
    // loop: terminate is only defined while proceed's last answer was true,
    // and here it was false (the remarks on Kernels say why this matters).
    rayQueryEXT rq;
    float tmin = uintBitsToFloat(PC.tminBits);
    uint qflags = anyMode ? gl_RayFlagsTerminateOnFirstHitEXT : 0u;
    // IRayTracer: a visibility hit must be STRICTLY short of the segment end.
    // The driver culls to t <= tmax, so any-hit scales tmax down one ulp
    // class (host passes 1 - 2^-23) and a t at the boundary falls to the miss
    // side; the parity facts count that eps-band explicitly.
    float tmaxEff = anyMode ? tmax * uintBitsToFloat(PC.tmaxScaleBits) : tmax;
    rayQueryInitializeEXT(rq, BLAS, qflags, 0xFFu, origin, tmin, dir, tmaxEff);
    while (rayQueryProceedEXT(rq)) { }
    hit = rayQueryGetIntersectionTypeEXT(rq, true) != gl_RayQueryCommittedIntersectionNoneEXT;
    prim = 0xFFFFFFFFu;
    t = 0.0;
    if (hit && !anyMode) {
        prim = rayQueryGetIntersectionPrimitiveIndexEXT(rq, true);
        t = rayQueryGetIntersectionTEXT(rq, true);
    }
}

void main() {
    uint lane = gl_LocalInvocationIndex;
    uint index = (gl_WorkGroupID.x * 64u) + lane;
    bool inRange = index < PC.rayCount;

    // Mode 4: write all-ones hit bits with no traversal and no SSBO reads.
    // The self-test's first question — if this reads back zero, the compute
    // write/copy/readback path itself is broken and no ray answer means
    // anything.
    if (PC.mode == 4u) {
        if (lane == 0u || lane == 32u) { OUT.data[(gl_WorkGroupID.x * 2u) + (lane >> 5u)] = 0xFFFFFFFFu; }
        return;
    }

    // Mode 5: per-ray (proceedIterations, candidateHits) telemetry, a
    // diagnostic for the self-test's report. The BLAS is opaque, so a
    // conformant driver commits every triangle inside traversal and proceed
    // returns false at once: iters == 0 is the normal answer, not a fault.
    // Candidates > 0 means the driver offered an opaque triangle as a
    // candidate, which is worth reporting next to a failed known-hit test.
    // The terminate stops the traversal on the break, while proceed's last
    // answer was true, which is the one place terminate is defined.
    if (PC.mode == 5u) {
        uint iters = 0u;
        uint cands = 0u;
        if (inRange) {
            uint rb5 = index * 2u;
            rayQueryEXT rq5;
            rayQueryInitializeEXT(rq5, BLAS, 0u, 0xFFu,
                RAYS.data[rb5 + 0u].xyz, uintBitsToFloat(PC.tminBits),
                RAYS.data[rb5 + 1u].xyz, RAYS.data[rb5 + 1u].w);
            while (rayQueryProceedEXT(rq5)) {
                iters++;
                if (rayQueryGetIntersectionTypeEXT(rq5, false) == gl_RayQueryCandidateIntersectionTriangleEXT) {
                    cands++;
                    rayQueryTerminateEXT(rq5);
                    break;
                }
            }
        }

        OUT.data[(index * 2u) + 0u] = iters;
        OUT.data[(index * 2u) + 1u] = cands;
        return;
    }

    bool anyMode = PC.mode == 0u;
    if (anyMode) {
        if (lane == 0u) { s_bits[0] = 0u; s_bits[1] = 0u; }
        groupMemoryBarrier(); barrier();
    }

    bool hit = false;
    uint prim = 0xFFFFFFFFu;
    float t = 0.0;
    if (inRange) { trace_one(anyMode, hit, prim, t); }

    if (anyMode) {
        if (hit) { atomicOr(s_bits[lane >> 5u], 1u << (lane & 31u)); }
        groupMemoryBarrier(); barrier();
        if (lane == 0u || lane == 32u) {
            OUT.data[((gl_WorkGroupID.x * 2u) + (lane >> 5u))] = s_bits[lane >> 5u];
        }
    } else {
        OUT.data[(index * 2u) + 0u] = prim;
        OUT.data[(index * 2u) + 1u] = floatBitsToUint(t);
    }
}
""";
}
