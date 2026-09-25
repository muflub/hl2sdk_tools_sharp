using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Rad;
using SourceSharp.MapTools.Tracing;
using SourceSharp.Tests.MapFormats;
using SourceSharp.Tests.MapTools.Io;

using Xunit;

namespace SourceSharp.Tests.MapTools.Rad;

/// <summary>
/// <c>dm_lockdown</c>'s static prop lump, read once, plus the two caster
/// passes that need no content at all.
/// </summary>
/// <remarks>
/// The lump is version 5 on disk. Stock's reference numbers were measured on
/// the same map with its <c>sprp</c> upgraded to version 10, which changes
/// nothing this path reads: <c>StaticPropLump</c>'s v5 upgrade
/// copies origin, angles and flags across
/// unchanged, and origin, angles and flags are the only three fields
/// <c>AddPolysForRayTrace</c> looks at.
/// </remarks>
public sealed class StaticPropCasterFixture
{
    /// <summary>Reads the golden map's prop lump and runs the content-free passes.</summary>
    public StaticPropCasterFixture()
    {
        using FileStream stream = File.OpenRead(GoldenBsp.Lockdown());
        BspData bsp = BspFile.LoadAsync(stream).GetAwaiter().GetResult();

        GameLumpEntry sprp = bsp.GameLumps.Single(
            entry => entry.Id == GameLumpEntry.MakeId("sprp"));
        Props = StaticPropLump.Read(sprp);

        NoContentBuilder = new ShadowCasterBuilder();
        NoContentReport = StaticPropShadowCasters.AddAsync(
            Props,
            new ContentFileSystem([]),
            NullPropCollisionSource.Instance,
            new StaticPropShadowCasterOptions(),
            NoContentBuilder).GetAwaiter().GetResult();
        NoContentSet = NoContentBuilder.Build();
    }

    /// <summary>The map's static props.</summary>
    public StaticPropLump Props { get; }

    /// <summary>The builder of the no-content default pass.</summary>
    public ShadowCasterBuilder NoContentBuilder { get; }

    /// <summary>What the no-content default pass reported.</summary>
    public StaticPropShadowCasterReport NoContentReport { get; }

    /// <summary>
    /// The casters of a default pass whose content holds nothing, so every
    /// model is rejected exactly as stock's <c>LoadStudioModel</c> failure is.
    /// </summary>
    public ShadowCasterSet NoContentSet { get; }
}

/// <summary>
/// <c>CVradStaticPropMgr::AddPolysForRayTrace</c>
/// and the model loading behind it.
/// </summary>
/// <remarks>
/// <para>
/// The gate is stock's own measured output on <c>dm_lockdown</c>:
/// <c>-StaticPropPolys</c> emits 92,582 triangles over
/// (-4600.17, 1292.05, -258.15) to (-2544.00, 6400.00, 608.64). That path is
/// entirely managed -- <c>.mdl</c>, <c>.vvd</c> and <c>.dx80.vtx</c> and
/// nothing else -- so the number has to come out exactly.
/// </para>
/// <para>
/// The default path's 17,304 is NOT gated here and cannot be: its triangles
/// come out of vphysics through <c>ICollisionQuery</c>, and there is no
/// managed decoder for a <c>.phy</c> solid in this tree. What IS gated is
/// every decision the default path makes around that hole -- which props are
/// skipped, which fall to the hull box, how many triangles a box is, and that
/// solidity is not consulted.
/// </para>
/// </remarks>
public sealed class StaticPropShadowCasterTests : IClassFixture<StaticPropCasterFixture>
{
    private readonly StaticPropCasterFixture _fixture;

    /// <summary>Takes the shared lump and no-content passes.</summary>
    /// <param name="fixture">The shared fixture.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is null.</exception>
    public StaticPropShadowCasterTests(StaticPropCasterFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    // -----------------------------------------------------------------------
    // The indexing arithmetic, which is the half of this port that fails
    // quietly rather than loudly.
    // -----------------------------------------------------------------------

    /// <summary>The managed vertex struct is the 48 bytes the index divides by.</summary>
    [Fact]
    public void MeshVertexStrideIsFortyEightBytes()
    {
        // -- "48 bytes". mstudiomodel_t::vertexindex is a BYTE
        // offset and the global index is that divided by this number, so a
        // struct that drifted to 52 would shift every model's vertices by a
        // fraction of a mesh and nothing would throw.
        Assert.Equal(48, StaticPropModel.VertexStride);
        Assert.Equal(48, Unsafe.SizeOf<StudioVertex>());
    }

    /// <summary>
    /// The global vertex index is the model's byte offset divided by the
    /// stride, plus the mesh's index offset, plus the strip vertex's id.
    /// </summary>
    [Fact]
    public async Task VertexIndexCombinesTheModelByteOffsetAndTheMeshIndex()
    {
        // The vertex index joins the two halves of
        // mstudio_meshvertexdata_t::Position. The fixture model puts five
        // decoy vertices before the model's run (vertexindex = 5 * 48) and
        // gives the second mesh a vertexoffset of 3, so getting either
        // indirection wrong lands on a decoy or on the other mesh -- and both
        // produce a triangle count that is exactly right.
        SyntheticModel model = SyntheticModel.TwoMeshes();
        ShadowCasterSet set = await model.RunAsync(
            new StaticPropShadowCasterOptions { StaticPropPolys = true });

        Assert.Equal(2, set.Count);
        Assert.Equal(
            new Vec3(100, 0, 0),
            set.Triangles[0].V0);
        Assert.Equal(
            new Vec3(200, 0, 0),
            set.Triangles[1].V0);
    }

    /// <summary>Every vertex of both fixture triangles is the one the file names.</summary>
    [Fact]
    public async Task EveryRenderMeshVertexIsTheOneTheFileNames()
    {
        SyntheticModel model = SyntheticModel.TwoMeshes();
        ShadowCasterSet set = await model.RunAsync(
            new StaticPropShadowCasterOptions { StaticPropPolys = true });

        Assert.Equal(new Vec3(100, 1, 0), set.Triangles[0].V1);
        Assert.Equal(new Vec3(100, 0, 1), set.Triangles[0].V2);
        Assert.Equal(new Vec3(200, 1, 0), set.Triangles[1].V1);
        Assert.Equal(new Vec3(200, 0, 1), set.Triangles[1].V2);
    }

    // -----------------------------------------------------------------------
    // The prop loop's decisions, over the committed golden map. These never
    // skip: the BSP is tracked and the content is deliberately empty.
    // -----------------------------------------------------------------------

    /// <summary>The golden map holds the 261 props stock counted.</summary>
    [Fact]
    public void LockdownHasTwoHundredAndSixtyOneStaticProps()
    {
        Assert.Equal(261, _fixture.Props.Props.Count);
    }

    /// <summary>Thirty-four of them carry <c>STATIC_PROP_NO_SHADOW</c>.</summary>
    [Fact]
    public void ThirtyFourLockdownPropsCarryNoShadow()
    {
        int flagged = _fixture.Props.Props.Count(
            prop => (prop.Flags & StaticPropFlags.NoShadow) != 0);

        Assert.Equal(34, flagged);
    }

    /// <summary>The pass skips exactly those thirty-four.</summary>
    [Fact]
    public void NoShadowPropsAreSkipped()
    {
        //, and it is the FIRST test in the loop.
        // before the model dictionary is consulted at all, which is why a
        // NO_SHADOW prop with an unloadable model produces no warning.
        Assert.Equal(34, _fixture.NoContentReport.PropsSkippedNoShadow);
    }

    /// <summary>No skipped prop contributes a triangle.</summary>
    [Fact]
    public void NoSkippedPropAppearsInTheCasterSet()
    {
        HashSet<int> emitted = [];
        foreach (TracedTriangle triangle in _fixture.NoContentSet.Triangles)
        {
            emitted.Add(TraceId.PropIndex(triangle.Id));
        }

        for (int i = 0; i < _fixture.Props.Props.Count; i++)
        {
            if ((_fixture.Props.Props[i].Flags & StaticPropFlags.NoShadow) == 0)
            {
                continue;
            }

            // i - 1 may legitimately be present: AddQuad's id + 1 defect
            // attributes half of prop i-1's box to prop i. So the test is that
            // prop i's own box is absent, which shows as its triangles never
            // being the FIRST of a quad.
            Assert.DoesNotContain(
                _fixture.NoContentSet.Triangles.ToArray(),
                triangle => triangle.Id == (TraceId.StaticProp | i)
                    && IsFirstTriangleOfAQuad(_fixture.NoContentSet, triangle));
        }
    }

    /// <summary>Every surviving prop contributes exactly the twelve of a box.</summary>
    [Fact]
    public void EachHullBoxPropContributesTwelveTriangles()
    {
        //: six quads, two triangles each. Not eight, not
        // six -- a box that came out as six triangles would be a caster set
        // that traced through half of every prop.
        int casting = _fixture.Props.Props.Count - 34;

        Assert.Equal(casting, _fixture.NoContentReport.PropsFromHullBox);
        Assert.Equal(casting * 12, _fixture.NoContentReport.TrianglesAdded);
        Assert.Equal(casting * 12, _fixture.NoContentSet.Count);
    }

    /// <summary>Not one prop reached the collision branch without a source.</summary>
    [Fact]
    public void NoPropReachesTheCollisionBranchWithoutACollisionSource()
    {
        Assert.Equal(0, _fixture.NoContentReport.PropsFromCollision);
    }

    /// <summary>The pass ran to the end of the prop list.</summary>
    [Fact]
    public void TheDefaultPassNeverAbandonsTheMap()
    {
        // The two early returns are render-mesh only: the default path has no
        // way to trip them, which is worth pinning because it is why the
        // default path is the one that always produces a full caster set.
        Assert.Equal(StaticPropAbandonReason.None, _fixture.NoContentReport.Abandoned);
        Assert.Equal(-1, _fixture.NoContentReport.AbandonedAtProp);
    }

    /// <summary>A <c>SOLID_NONE</c> prop still casts a shadow.</summary>
    [Fact]
    public void SolidNonePropsStillCast()
    {
        // A STOCK BEHAVIOUR pinned rather than fixed. vrad reads m_Solid out
        // of the lump and AddPolysForRayTrace never looks
        // at it, so a prop the player and every trace walk straight through
        // still blocks light. Thirty-two of dm_lockdown's props are
        // SOLID_NONE.
        int index = -1;
        for (int i = 0; i < _fixture.Props.Props.Count; i++)
        {
            StaticProp prop = _fixture.Props.Props[i];
            if (prop.Solid == 0 && (prop.Flags & StaticPropFlags.NoShadow) == 0)
            {
                index = i;
                break;
            }
        }

        Assert.True(index >= 0, "dm_lockdown has a SOLID_NONE prop that casts");
        Assert.Contains(
            _fixture.NoContentSet.Triangles.ToArray(),
            triangle => triangle.Id == (TraceId.StaticProp | index));
    }

    /// <summary>The golden map has the 32 <c>SOLID_NONE</c> props stock counted.</summary>
    [Fact]
    public void ThirtyTwoLockdownPropsAreSolidNone()
    {
        Assert.Equal(32, _fixture.Props.Props.Count(prop => prop.Solid == 0));
    }

    /// <summary>A model that failed to load leaves a degenerate box.</summary>
    [Fact]
    public void AFailedModelLoadLeavesADegenerateBoxAtThePropsOrigin()
    {
        // The dictionary entry's hull corners are
        // set to vec3_origin, not left at the header's -- so mins == maxs ==
        // the prop's origin and the twelve triangles have zero area. This is
        // the ONLY way stock reaches the AABB branch, which is why
        // stock's fallback boxes are points and this port's (with a real model
        // and no collision source) are not.
        ShadowCasterStats stats = _fixture.NoContentSet.Stats(ShadowCasterSource.StaticProp);

        Vec3 minOrigin = new(
            _fixture.Props.Props.Min(p => Casting(p) ? p.Origin.X : float.PositiveInfinity),
            _fixture.Props.Props.Min(p => Casting(p) ? p.Origin.Y : float.PositiveInfinity),
            _fixture.Props.Props.Min(p => Casting(p) ? p.Origin.Z : float.PositiveInfinity));

        Assert.Equal(minOrigin.X, stats.Min.X);
        Assert.Equal(minOrigin.Y, stats.Min.Y);
        Assert.Equal(minOrigin.Z, stats.Min.Z);
    }

    /// <summary>Half of a prop's box is attributed to the next prop.</summary>
    [Fact]
    public void HalfOfAHullBoxIsAttributedToTheNextProp()
    {
        // ShadowCasterBuilder.AddQuad reproduces the reference implementation's id + 1,
        // and the static prop AABB fallback is vrad's only caller. Pinned from
        // this side too, because it is THIS call site that turns a harmless
        // arithmetic quirk into a shadow ray skipping its neighbour's box:
        // TRACE_ID_STATICPROP | nProp plus one is TRACE_ID_STATICPROP |
        // (nProp + 1), a perfectly valid identity for a different prop.
        int first = -1;
        for (int i = 0; i < _fixture.Props.Props.Count; i++)
        {
            if (Casting(_fixture.Props.Props[i]))
            {
                first = i;
                break;
            }
        }

        Assert.True(first >= 0);

        int six = 0;
        foreach (TracedTriangle triangle in _fixture.NoContentSet.Triangles)
        {
            if (triangle.Id == (TraceId.StaticProp | (first + 1)))
            {
                six++;
            }
        }

        // Six from prop `first`'s box, plus prop first+1's own twelve when it
        // also casts. Either way it is more than the twelve one prop owns.
        Assert.True(six >= 6, $"prop {first + 1}'s id carries {six} triangles, not at least 6");
    }

    // -----------------------------------------------------------------------
    // The AABB fallback's own arithmetic, over a synthetic model whose hull is
    // real -- which the golden map with empty content cannot show.
    // -----------------------------------------------------------------------

    /// <summary>The hull box is the model's hull translated by the origin.</summary>
    [Fact]
    public async Task TheHullBoxIsTheModelHullPlusThePropOrigin()
    {
        SyntheticModel model = SyntheticModel.TwoMeshes();
        ShadowCasterSet set = await model.RunAsync(
            new StaticPropShadowCasterOptions(),
            origin: new Vec3(10, 20, 30),
            angles: default);

        ShadowCasterStats stats = set.Stats(ShadowCasterSource.StaticProp);

        Assert.Equal(new Vec3(10 - 16, 20 - 16, 30 - 16), stats.Min);
        Assert.Equal(new Vec3(10 + 16, 20 + 16, 30 + 16), stats.Max);
    }

    /// <summary>The hull box ignores the prop's angles.</summary>
    [Fact]
    public async Task TheHullBoxIgnoresThePropAngles()
    {
        // A STOCK DEFECT, reproduced and pinned. is a
        // plain VectorAdd of the model's hull and the prop's origin: no
        // rotation anywhere. So a prop turned on its side casts the shadow of
        // its upright bounding box, and two props of one model at one origin
        // with different angles cast identical shadows. Fixing it means
        // TransformBounds, and that is a decision about parity, not a typo.
        SyntheticModel model = SyntheticModel.TwoMeshes();

        ShadowCasterSet upright = await model.RunAsync(
            new StaticPropShadowCasterOptions(), new Vec3(10, 20, 30), default);
        ShadowCasterSet turned = await model.RunAsync(
            new StaticPropShadowCasterOptions(), new Vec3(10, 20, 30), new Vec3(30, 45, 60));

        Assert.Equal(
            upright.Stats(ShadowCasterSource.StaticProp),
            turned.Stats(ShadowCasterSource.StaticProp));
    }

    /// <summary>A hull box's triangles all block light completely.</summary>
    [Fact]
    public async Task HullBoxTrianglesCarryFullCoverage()
    {
        // the reference implementation's fullCoverage, whose x is 1 and whose y
        // and z were never written.
        SyntheticModel model = SyntheticModel.TwoMeshes();
        ShadowCasterSet set = await model.RunAsync(new StaticPropShadowCasterOptions());

        Assert.Equal(12, set.Count);
        foreach (float coverage in set.Coverage)
        {
            Assert.Equal(1.0f, coverage);
        }
    }

    // -----------------------------------------------------------------------
    // The render-mesh path's own defects.
    // -----------------------------------------------------------------------

    /// <summary>Render-mesh triangles carry coverage ZERO, not one.</summary>
    [Fact]
    public async Task RenderMeshTrianglesCarryZeroCoverageWithoutTextureShadows()
    {
        // A STOCK ODDITY, reproduced and pinned. The default path passes
        // fullCoverage (x = 1); the -StaticPropPolys path declares
        // `Vector color = vec3_origin` and only
        // ever writes color.x inside the texture-shadow branch. So the same
        // prop casts coverage 1 by default and coverage 0 under the switch.
        // Harmless today -- coverage is read only for a triangle flagged
        // FCACHETRI_TRANSPARENT -- but it is stock's number, and a tracer that
        // started honouring coverage on opaque triangles would turn every
        // -StaticPropPolys prop transparent.
        SyntheticModel model = SyntheticModel.TwoMeshes();
        ShadowCasterSet set = await model.RunAsync(
            new StaticPropShadowCasterOptions { StaticPropPolys = true });

        Assert.Equal(2, set.Count);
        Assert.Equal(0.0f, set.Coverage[0]);
        Assert.Equal(0.0f, set.Coverage[1]);
        Assert.Equal(0, set.TransparentCount);
    }

    /// <summary>A <c>noshadow</c> name silences a whole mesh by substring.</summary>
    [Fact]
    public async Task ANoShadowMaterialSubstringSilencesTheWholeMesh()
    {
        //, Q_stristr: a SUBSTRING match, case
        // insensitive. The fixture's second mesh uses "glasswindow070a", so
        // "GLASS" silences it and leaves the first mesh alone.
        SyntheticModel model = SyntheticModel.TwoMeshes();
        ShadowCasterSet set = await model.RunAsync(new StaticPropShadowCasterOptions
        {
            StaticPropPolys = true,
            NoShadowMaterials = ["GLASS"],
        });

        Assert.Equal(1, set.Count);
        Assert.Equal(new Vec3(100, 0, 0), set.Triangles[0].V0);
    }

    /// <summary>A name that matches nothing silences nothing.</summary>
    [Fact]
    public async Task ANoShadowNameThatMatchesNothingSilencesNothing()
    {
        SyntheticModel model = SyntheticModel.TwoMeshes();
        ShadowCasterSet set = await model.RunAsync(new StaticPropShadowCasterOptions
        {
            StaticPropPolys = true,
            NoShadowMaterials = ["nosuchmaterial"],
        });

        Assert.Equal(2, set.Count);
    }

    /// <summary>A missing VTX abandons the rest of the map, not the prop.</summary>
    [Fact]
    public async Task AMissingVtxAbandonsEveryLaterProp()
    {
        // A STOCK DEFECT, reproduced and pinned.
        // says "must have model and its verts for decoding triangles" -- a
        // reason to skip THIS prop -- and then returns from the whole
        // function. Every prop after it, including props whose models are
        // fine, silently stops casting.
        SyntheticModel model = SyntheticModel.TwoMeshes();
        model.DropVtx();

        (ShadowCasterSet set, StaticPropShadowCasterReport report) = await model.RunWithReportAsync(
            new StaticPropShadowCasterOptions { StaticPropPolys = true },
            propCount: 3);

        Assert.Equal(StaticPropAbandonReason.MissingModelOrVtx, report.Abandoned);
        Assert.Equal(0, report.AbandonedAtProp);
        Assert.Equal(0, set.Count);
    }

    /// <summary>A strip that is not a triangle list abandons every later prop.</summary>
    [Fact]
    public async Task ANonTriangleListStripAbandonsEveryLaterProp()
    {
        // A STOCK DEFECT, reproduced and pinned.
        // prints "unexpected strips found", asserts, and returns. The
        // triangles already added for that prop are kept -- so the fixture's
        // first mesh survives and the map stops there.
        SyntheticModel model = SyntheticModel.TwoMeshes(secondMeshIsTriStrip: true);

        (ShadowCasterSet set, StaticPropShadowCasterReport report) = await model.RunWithReportAsync(
            new StaticPropShadowCasterOptions { StaticPropPolys = true },
            propCount: 3);

        Assert.Equal(StaticPropAbandonReason.NonTriangleListStrip, report.Abandoned);
        Assert.Equal(0, report.AbandonedAtProp);
        Assert.Equal(1, set.Count);
    }

    /// <summary>The transparency hook is consulted once per model, not per prop.</summary>
    [Fact]
    public async Task TheTransparencyHookIsConsultedOncePerModel()
    {
        // The cached dict.m_triangleMaterialIndex drives the replay:
        // the first prop of a model computes the material indices and every
        // later prop replays them.
        SyntheticModel model = SyntheticModel.TwoMeshes();
        int calls = 0;

        await model.RunWithReportAsync(
            new StaticPropShadowCasterOptions
            {
                StaticPropPolys = true,
                ForcedTextureShadowModels = [SyntheticModel.ModelName],
                Transparency = (_, _, _, _, _) =>
                {
                    calls++;
                    return new PropTriangleShadow(0.25f, 7);
                },
            },
            propCount: 3);

        Assert.Equal(2, calls);
    }

    /// <summary>The replay loses the coverage, keeping only the material index.</summary>
    [Fact]
    public async Task ReplayedTransparencyKeepsTheMaterialIndexAndLosesTheCoverage()
    {
        // A STOCK DEFECT, reproduced and pinned. the reference implementation's
        // else-branch restores materialIndex from the cache and never touches
        // `color`, which is still vec3_origin. So the first prop to use a
        // model gets the real coverage and every later prop sharing that model
        // gets zero -- identical geometry shadowing differently depending on
        // which prop index the loop reached first.
        SyntheticModel model = SyntheticModel.TwoMeshes();

        (ShadowCasterSet set, _) = await model.RunWithReportAsync(
            new StaticPropShadowCasterOptions
            {
                StaticPropPolys = true,
                ForcedTextureShadowModels = [SyntheticModel.ModelName],
                Transparency = (_, _, _, _, _) => new PropTriangleShadow(0.25f, 7),
            },
            propCount: 2);

        Assert.Equal(4, set.Count);

        // Prop 0, both triangles: the computed coverage.
        Assert.Equal(0.25f, set.Coverage[0]);
        Assert.Equal(0.25f, set.Coverage[1]);

        // Prop 1, the same two triangles of the same model: zero.
        Assert.Equal(0.0f, set.Coverage[2]);
        Assert.Equal(0.0f, set.Coverage[3]);

        // The material index survives on all four, and so does the flag.
        Assert.Equal<int[]>([7, 7, 7, 7], set.MaterialIndices.ToArray());
        Assert.Equal(4, set.TransparentCount);
    }

    /// <summary>An opaque hook answer leaves the triangle untagged.</summary>
    [Fact]
    public async Task AnOpaqueHookAnswerLeavesTheTriangleUntagged()
    {
        // -- a coverage of 1 means materialIndex = -1
        // and no FCACHETRI_TRANSPARENT, which is what keeps a fully opaque
        // alpha-tested triangle out of the per-ray transparency test.
        SyntheticModel model = SyntheticModel.TwoMeshes();

        (ShadowCasterSet set, _) = await model.RunWithReportAsync(
            new StaticPropShadowCasterOptions
            {
                StaticPropPolys = true,
                ForcedTextureShadowModels = [SyntheticModel.ModelName],
                Transparency = (_, _, _, _, _) => new PropTriangleShadow(1.0f, -1),
            },
            propCount: 1);

        Assert.Equal(0, set.TransparentCount);
        Assert.Equal<int[]>([-1, -1], set.MaterialIndices.ToArray());
    }

    /// <summary>A model that does not opt in never reaches the hook.</summary>
    [Fact]
    public async Task AModelThatDoesNotOptInNeverReachesTheTransparencyHook()
    {
        //: the texture-shadow table is built
        // only for a model carrying STUDIOHDR_FLAGS_CAST_TEXTURE_SHADOWS or
        // named in a lights.rad forcetextureshadow line.
        SyntheticModel model = SyntheticModel.TwoMeshes();
        int calls = 0;

        await model.RunWithReportAsync(
            new StaticPropShadowCasterOptions
            {
                StaticPropPolys = true,
                Transparency = (_, _, _, _, _) =>
                {
                    calls++;
                    return new PropTriangleShadow(0.25f, 7);
                },
            },
            propCount: 2);

        Assert.Equal(0, calls);
    }

    // -----------------------------------------------------------------------
    // The.phy seam.
    // -----------------------------------------------------------------------

    /// <summary>A usable <c>.phy</c> is framed and counted.</summary>
    [Fact]
    public async Task AUsablePhysicsFileIsRecognised()
    {
        // LoadStudioCollisionModel's whole test:
        // header.size == sizeof(phyheader_t) and solidCount > 0. Nothing about
        // the checksum, and nothing about the solids' contents.
        SyntheticModel model = SyntheticModel.TwoMeshes();
        model.AddPhy(solidCount: 3);

        StaticPropModel loaded = await model.LoadModelAsync();

        Assert.True(loaded.HasPhysicsFile);
        Assert.Equal(3, loaded.PhysicsSolidCount);
    }

    /// <summary>Only the first solid of a multi-solid <c>.phy</c> ever casts.</summary>
    [Fact]
    public async Task OnlyTheFirstSolidOfAPhysicsFileIsEverUsed()
    {
        // A STOCK DEFECT, reproduced and pinned. is
        // `m_pModel = m_loadedModel.solids[0]`: the dictionary keeps the whole
        // vcollide_t and the caster path queries only the first solid, so
        // every solid after the first in a multi-solid.phy casts no shadow at
        // all.
        SyntheticModel model = SyntheticModel.TwoMeshes();
        model.AddPhy(solidCount: 3);

        StaticPropModel loaded = await model.LoadModelAsync();

        Assert.Equal(1, loaded.PhysicsSolidsUsedByStock);
    }

    /// <summary>A header whose size is not 16 is not a usable <c>.phy</c>.</summary>
    [Fact]
    public async Task APhysicsFileWithAWrongHeaderSizeIsNotUsable()
    {
        SyntheticModel model = SyntheticModel.TwoMeshes();
        model.AddPhy(solidCount: 1, headerSize: 20);

        StaticPropModel loaded = await model.LoadModelAsync();

        Assert.False(loaded.HasPhysicsFile);
        Assert.Equal(0, loaded.PhysicsSolidCount);
    }

    /// <summary>A <c>.phy</c> with no solids is not usable either.</summary>
    [Fact]
    public async Task APhysicsFileWithNoSolidsIsNotUsable()
    {
        SyntheticModel model = SyntheticModel.TwoMeshes();
        model.AddPhy(solidCount: 0);

        StaticPropModel loaded = await model.LoadModelAsync();

        Assert.False(loaded.HasPhysicsFile);
    }

    /// <summary>A model with no <c>.phy</c> at all is not an error.</summary>
    [Fact]
    public async Task AModelWithNoPhysicsFileIsNotAnError()
    {
        // -- "this is not an error, the model simply
        // has no PHY file". Stock then goes to ComputeConvexHull, which is
        // also vphysics and is also behind IPropCollisionSource.
        SyntheticModel model = SyntheticModel.TwoMeshes();

        StaticPropModel loaded = await model.LoadModelAsync();

        Assert.True(loaded.IsUsable);
        Assert.False(loaded.HasPhysicsFile);
        Assert.Equal(PropCollisionKind.None, loaded.CollisionKind);
    }

    /// <summary>The null collision source has nothing for any model.</summary>
    [Fact]
    public async Task TheNullCollisionSourceHasNothingForAnyModel()
    {
        Assert.Null(await NullPropCollisionSource.Instance.LoadAsync(
            VPath.Create("models/props_c17/oildrum001.mdl")));
    }

    // -----------------------------------------------------------------------
    // The model loader's rejections.
    // -----------------------------------------------------------------------

    /// <summary>A model that is not in the content is a missing file.</summary>
    [Fact]
    public async Task AModelThatIsNotInTheContentIsRejectedAsMissing()
    {
        StaticPropModelLoader loader = new(
            new ContentFileSystem([]), NullPropCollisionSource.Instance);

        StaticPropModel model = await loader.LoadAsync("models/props/nothere.mdl");

        Assert.Equal(StaticPropModelRejection.FileMissing, model.Rejection);
        Assert.False(model.IsUsable);
    }

    /// <summary>A rejected model's hull is zeroed, not left at the header's.</summary>
    [Fact]
    public async Task ARejectedModelsHullIsZeroed()
    {
        StaticPropModelLoader loader = new(
            new ContentFileSystem([]), NullPropCollisionSource.Instance);

        StaticPropModel model = await loader.LoadAsync("models/props/nothere.mdl");

        Assert.Equal(default, model.HullMin);
        Assert.Equal(default, model.HullMax);
    }

    /// <summary>A model without <c>$staticprop</c> is refused.</summary>
    [Fact]
    public async Task AModelWithoutStaticPropIsRefused()
    {
        // IsStaticProp. Stock's warning says why:
        // "as a static prop, it must be compiled with $staticprop!"
        SyntheticModel model = SyntheticModel.TwoMeshes(staticProp: false);

        StaticPropModel loaded = await model.LoadModelAsync();

        Assert.Equal(StaticPropModelRejection.NotAStaticProp, loaded.Rejection);
    }

    /// <summary>A VTX whose checksum disagrees with the MDL is dropped.</summary>
    [Fact]
    public async Task AVtxWithAStaleChecksumIsDropped()
    {
        // A stale VTX indexes vertices that moved, so
        // stock purges the buffer and the model's render mesh is disabled --
        // which then trips the material-index return.
        SyntheticModel model = SyntheticModel.TwoMeshes();
        model.CorruptVtxChecksum();

        StaticPropModel loaded = await model.LoadModelAsync();

        Assert.True(loaded.IsUsable);
        Assert.Null(loaded.Vtx);
    }

    /// <summary>The VTX read is the dx80 one, not dx90 and not the bare name.</summary>
    [Fact]
    public async Task TheVtxReadIsTheDx80One()
    {
        // -- strcat(filename, ".dx80.vtx"). The
        // three VTX flavours of one model differ in strip grouping, so reading
        // dx90 gives a triangle count that is close and not stock's.
        SyntheticModel model = SyntheticModel.TwoMeshes();
        model.RenameVtx("models/test/vertexprobe.dx90.vtx");

        StaticPropModel loaded = await model.LoadModelAsync();

        Assert.Null(loaded.Vtx);
    }

    /// <summary><c>CleanModelName</c> strips the prefix and cuts at the first dot.</summary>
    [Theory]
    [InlineData("models/props_c17/oildrum001.mdl", "props_c17/oildrum001")]
    [InlineData("MODELS/Props/Crate.mdl", "Props/Crate")]
    [InlineData("props/crate.mdl", "props/crate")]
    [InlineData("models/a.b/c.mdl", "a")]
    public void CleanModelNameStripsTheModelsPrefixAndTheExtension(string input, string expected)
    {
        // The last case is stock's strchr, which cuts
        // at the FIRST dot -- so a model under a directory with a dot in its
        // name is truncated. Reproduced because the forcetextureshadow list is
        // matched against exactly these strings.
        Assert.Equal(expected, StaticPropModel.CleanModelName(input));
    }

    // -----------------------------------------------------------------------
    // THE GATE: stock's measured -StaticPropPolys output on dm_lockdown.
    // Needs the HL2 content the props live in, so it skips visibly when the
    // game is not installed.
    // -----------------------------------------------------------------------

    /// <summary>Every prop model of the golden map is studiohdr version 44.</summary>
    [InstalledGameFact]
    public async Task EveryLockdownPropModelIsOlderThanStudioVersion()
    {
        // THE PREMISE OF THE UPGRADE, measured rather than assumed. All 56 of
        // dm_lockdown's prop models are version 44 on disk and STUDIO_VERSION
        // is 48, so LoadStudioModel's version test at
        // passes only because
        // Studio_ConvertStudioHdrToNewVersion ran four lines earlier and
        // slammed the number. A port that took the version check at face value
        // rejects every prop in the map and emits nothing -- which is exactly
        // what this one did until this fact existed.
        IContentFileSystem content = await MountInstalledAsync();
        SortedSet<int> versions = [];

        foreach (string name in _fixture.Props.ModelNames)
        {
            using System.Buffers.IMemoryOwner<byte>? owner =
                await content.ReadAsync(VPath.Create(name));
            versions.Add(owner is null
                ? -1
                : BinaryPrimitives.ReadInt32LittleEndian(owner.Memory.Span[4..]));
        }

        Assert.Equal<int[]>([44], [.. versions]);
    }

    /// <summary>A model older than <c>STUDIO_VERSION</c> is upgraded, not refused.</summary>
    [Fact]
    public async Task AModelOlderThanStudioVersionIsUpgradedInPlace()
    {
        // -- "for now, just slam the version number since
        // they're compatible". The synthetic model is written at version 44 so
        // that this holds without installed content.
        SyntheticModel model = SyntheticModel.TwoMeshes(mdlVersion: 44);

        StaticPropModel loaded = await model.LoadModelAsync();

        Assert.True(loaded.IsUsable);
        Assert.Equal(48, loaded.Mdl!.Header.Version);
    }

    /// <summary>The upgrade does not disturb the geometry it carries.</summary>
    [Fact]
    public async Task AnUpgradedModelEmitsTheSameTrianglesAsACurrentOne()
    {
        // The upgrade touches four bytes of header and nothing else, so a
        // version 44 file and a version 48 one with the same body must produce
        // identical casters. If the 44 layout really had moved a field this
        // would be the fact that said so.
        ShadowCasterSet old = await SyntheticModel.TwoMeshes(mdlVersion: 44).RunAsync(
            new StaticPropShadowCasterOptions { StaticPropPolys = true });
        ShadowCasterSet current = await SyntheticModel.TwoMeshes().RunAsync(
            new StaticPropShadowCasterOptions { StaticPropPolys = true });

        Assert.Equal<TracedTriangle[]>([.. old.Triangles], [.. current.Triangles]);
    }

    /// <summary>Every model the golden map names loads out of installed content.</summary>
    [InstalledGameFact]
    public async Task EveryLockdownPropModelLoads()
    {
        // Checked before the count, because a rejected model is silent: it
        // produces a hull box rather than an error, so a content mount that
        // resolved half the props would give a wrong number with no clue why.
        StaticPropModelLoader loader = new(
            await MountInstalledAsync(), NullPropCollisionSource.Instance);

        IReadOnlyList<StaticPropModel> models =
            await loader.LoadDictionaryAsync(_fixture.Props.ModelNames);

        string[] rejected = [.. models
            .Where(model => !model.IsUsable)
            .Select(model => $"{model.Path.Value}: {model.Rejection}")];

        Assert.Empty(rejected);
    }

    /// <summary>Every model has the <c>.dx80.vtx</c> and <c>.vvd</c> the render path needs.</summary>
    [InstalledGameFact]
    public async Task EveryLockdownPropModelHasItsVtxAndVvd()
    {
        StaticPropModelLoader loader = new(
            await MountInstalledAsync(), NullPropCollisionSource.Instance);

        IReadOnlyList<StaticPropModel> models =
            await loader.LoadDictionaryAsync(_fixture.Props.ModelNames);

        Assert.Empty(models.Where(model => model.Vtx is null).Select(model => model.Path.Value));
        Assert.Empty(models.Where(model => model.Vvd is null).Select(model => model.Path.Value));
    }

    /// <summary>THE GATE: 92,582 triangles, exactly as stock.</summary>
    [InstalledGameFact]
    public async Task StaticPropPolysMatchesStocksTriangleCount()
    {
        // Measured from stock vrad on game/mod_sharp/maps/dm_lockdown.bsp with
        // its sprp lump upgraded v5 -> v10. The render-mesh path is entirely
        // managed, so this number is not an approximation of anything: a wrong
        // strip flag, the dx90 VTX, a missed noshadow list or LOD 1 instead of
        // LOD 0 all move it.
        ShadowCasterSet set = await RunStaticPropPolysAsync();

        Assert.Equal(92582, set.Count);
    }

    /// <summary>THE GATE: the same bounds stock's dumper printed.</summary>
    [InstalledGameFact]
    public async Task StaticPropPolysMatchesStocksBounds()
    {
        // Two decimals, which is what stock's dump prints. Bounds are what
        // catch a caster set with the right COUNT in the wrong place -- a pass
        // that forgot the per-prop origin collapses to the models' own hulls
        // around zero.
        ShadowCasterStats stats =
            (await RunStaticPropPolysAsync()).Stats(ShadowCasterSource.StaticProp);

        Assert.Equal(-4600.17f, stats.Min.X, 0.01);
        Assert.Equal(1292.05f, stats.Min.Y, 0.01);
        Assert.Equal(-258.15f, stats.Min.Z, 0.01);
        Assert.Equal(-2544.00f, stats.Max.X, 0.01);
        Assert.Equal(6400.00f, stats.Max.Y, 0.01);
        Assert.Equal(608.64f, stats.Max.Z, 0.01);
    }

    /// <summary>The render-mesh pass reached the end of the prop list.</summary>
    [InstalledGameFact]
    public async Task StaticPropPolysNeverAbandonsTheGoldenMap()
    {
        // If either of stock's two early returns fired, the count above would
        // be a prefix of the right answer rather than the right answer, and
        // nothing else in the set would say so.
        (ShadowCasterSet _, StaticPropShadowCasterReport report) =
            await RunStaticPropPolysWithReportAsync();

        Assert.Equal(StaticPropAbandonReason.None, report.Abandoned);
        Assert.Equal(261 - 34, report.PropsFromRenderMesh);
    }

    /// <summary>The render-mesh pass still skips the thirty-four.</summary>
    [InstalledGameFact]
    public async Task StaticPropPolysSkipsTheSameThirtyFourProps()
    {
        (ShadowCasterSet _, StaticPropShadowCasterReport report) =
            await RunStaticPropPolysWithReportAsync();

        Assert.Equal(34, report.PropsSkippedNoShadow);
    }

    /// <summary>The render mesh is a different scene from the hull boxes.</summary>
    [InstalledGameFact]
    public async Task StaticPropPolysIsNotTheDefaultPath()
    {
        // 92,582 against 227 * 12 = 2,724. Worth pinning because the two
        // passes share every line of the prop loop above the branch, and a
        // switch that silently did nothing would leave both numbers equal.
        ShadowCasterSet polys = await RunStaticPropPolysAsync();

        Assert.NotEqual(_fixture.NoContentSet.Count, polys.Count);
    }

    // -----------------------------------------------------------------------

    private static bool Casting(StaticProp prop) =>
        (prop.Flags & StaticPropFlags.NoShadow) == 0;

    private static bool IsFirstTriangleOfAQuad(ShadowCasterSet set, TracedTriangle triangle)
    {
        // A quad's two triangles share v0; the first also owns v1 as its own
        // second corner. Enough to tell "this prop emitted a box" from "this
        // prop's id was borrowed by its predecessor's second triangle", which
        // always has its predecessor's V0.
        foreach (TracedTriangle other in set.Triangles)
        {
            if (other.Id == triangle.Id - 1 && other.V0 == triangle.V0)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<ShadowCasterSet> RunStaticPropPolysAsync() =>
        (await RunStaticPropPolysWithReportAsync()).Set;

    private async Task<(ShadowCasterSet Set, StaticPropShadowCasterReport Report)>
        RunStaticPropPolysWithReportAsync()
    {
        ShadowCasterBuilder builder = new();
        StaticPropShadowCasterReport report = await StaticPropShadowCasters.AddAsync(
            _fixture.Props,
            await MountInstalledAsync(),
            NullPropCollisionSource.Instance,
            new StaticPropShadowCasterOptions { StaticPropPolys = true },
            builder);

        return (builder.Build(), report);
    }

    private static async Task<IContentFileSystem> MountInstalledAsync()
    {
        PhysicalFileSystem host = PhysicalFileSystem.AtHostRoot();
        GameContentMounter.Result result = await GameContentMounter.MountAsync(
            new ReadOnlyFileSystem(host),
            host.ToVirtualPath(Path.Combine(InstalledGameContent.BaseDirectory, "hl2mp/gameinfo.txt")),
            host.ToVirtualPath(InstalledGameContent.BaseDirectory));

        return result.Content;
    }
}

/// <summary>
/// A studio model built byte by byte, so the vertex indexing and the strip
/// walk can be pinned without an installed game.
/// </summary>
/// <remarks>
/// <para>
/// WHY SYNTHESISE ONE. The gate against stock's 92,582 needs the installed game's content
/// and therefore skips on a machine without it, and the two things most worth
/// pinning -- that the global vertex index combines a byte offset with an
/// index, and that a non-trilist strip abandons the map -- are both invisible
/// in an aggregate count anyway. A file whose every offset is chosen to
/// punish a wrong one is a better instrument than 261 real props.
/// </para>
/// <para>
/// The model has five decoy vertices ahead of its own run
/// (<c>vertexindex = 5 * 48</c>) and two meshes whose vertex offsets are 0 and
/// 3. Mesh 0's triangle sits at x = 100 and mesh 1's at x = 200, so a reader
/// that dropped either indirection lands somewhere it can be named.
/// </para>
/// </remarks>
internal sealed class SyntheticModel
{
    /// <summary>The model's path in the fixture's content.</summary>
    public const string ModelName = "models/test/vertexprobe.mdl";

    private const int Checksum = 0x0BADF00D;
    private const int DecoyVertices = 5;

    private readonly Dictionary<string, byte[]> _files = [];

    private SyntheticModel(bool staticProp, bool secondMeshIsTriStrip, int mdlVersion)
    {
        _files[ModelName] = BuildMdl(staticProp, mdlVersion);
        _files["models/test/vertexprobe.vvd"] = BuildVvd();
        _files["models/test/vertexprobe.dx80.vtx"] = BuildVtx(secondMeshIsTriStrip);
    }

    /// <summary>A model with two single-triangle meshes.</summary>
    /// <param name="staticProp">Whether to set <c>STUDIOHDR_FLAGS_STATIC_PROP</c>.</param>
    /// <param name="secondMeshIsTriStrip">
    /// Whether the second mesh's strip claims to be a triangle STRIP, which is
    /// what trips the reference implementation's return there.
    /// </param>
    /// <param name="mdlVersion">
    /// What the header claims; anything but 48 exercises
    /// <c>Studio_ConvertStudioHdrToNewVersion</c>.
    /// </param>
    /// <returns>The model.</returns>
    public static SyntheticModel TwoMeshes(
        bool staticProp = true,
        bool secondMeshIsTriStrip = false,
        int mdlVersion = StudioIdents.MdlVersion) =>
        new(staticProp, secondMeshIsTriStrip, mdlVersion);

    /// <summary>Removes the VTX, as a purged <c>m_VtxBuf</c>.</summary>
    public void DropVtx() => _files.Remove("models/test/vertexprobe.dx80.vtx");

    /// <summary>Moves the VTX to another name.</summary>
    /// <param name="path">Where to put it.</param>
    public void RenameVtx(string path)
    {
        byte[] bytes = _files["models/test/vertexprobe.dx80.vtx"];
        _files.Remove("models/test/vertexprobe.dx80.vtx");
        _files[path] = bytes;
    }

    /// <summary>Makes the VTX's checksum disagree with the MDL's.</summary>
    public void CorruptVtxChecksum()
    {
        byte[] bytes = _files["models/test/vertexprobe.dx80.vtx"];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), Checksum + 1);
    }

    /// <summary>Adds a <c>.phy</c> beside the model.</summary>
    /// <param name="solidCount">What the header declares.</param>
    /// <param name="headerSize">
    /// What the header claims its own size is; 16 is the only usable value.
    /// </param>
    public void AddPhy(int solidCount, int headerSize = 16)
    {
        int payload = Math.Max(solidCount, 0) * (sizeof(int) + 8);
        byte[] bytes = new byte[16 + payload];
        Span<byte> span = bytes;

        BinaryPrimitives.WriteInt32LittleEndian(span, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], solidCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], Checksum);

        int at = 16;
        for (int i = 0; i < solidCount; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[at..], 8);
            at += sizeof(int) + 8;
        }

        _files["models/test/vertexprobe.phy"] = bytes;
    }

    /// <summary>Loads the model through the real loader.</summary>
    /// <returns>The loaded dictionary entry.</returns>
    public async Task<StaticPropModel> LoadModelAsync()
    {
        await using ContentFileSystem content = await MountAsync();
        StaticPropModelLoader loader = new(content, NullPropCollisionSource.Instance);
        return await loader.LoadAsync(ModelName);
    }

    /// <summary>Runs one prop of this model through the caster pass.</summary>
    /// <param name="options">The switches.</param>
    /// <param name="origin">The prop's origin.</param>
    /// <param name="angles">The prop's angles.</param>
    /// <returns>The caster set.</returns>
    public async Task<ShadowCasterSet> RunAsync(
        StaticPropShadowCasterOptions options,
        Vec3 origin = default,
        Vec3 angles = default)
    {
        (ShadowCasterSet set, _) = await RunWithReportAsync(options, 1, origin, angles);
        return set;
    }

    /// <summary>Runs several props of this model through the caster pass.</summary>
    /// <param name="options">The switches.</param>
    /// <param name="propCount">How many props to place, all of this model.</param>
    /// <param name="origin">Every prop's origin.</param>
    /// <param name="angles">Every prop's angles.</param>
    /// <returns>The caster set and the report.</returns>
    public async Task<(ShadowCasterSet Set, StaticPropShadowCasterReport Report)>
        RunWithReportAsync(
            StaticPropShadowCasterOptions options,
            int propCount = 1,
            Vec3 origin = default,
            Vec3 angles = default)
    {
        StaticPropLump lump = new();
        lump.ModelNames.Add(ModelName);
        for (int i = 0; i < propCount; i++)
        {
            lump.Props.Add(new StaticProp { Origin = origin, Angles = angles, PropType = 0 });
        }

        await using ContentFileSystem content = await MountAsync();
        ShadowCasterBuilder builder = new();
        StaticPropShadowCasterReport report = await StaticPropShadowCasters.AddAsync(
            lump, content, NullPropCollisionSource.Instance, options, builder);

        return (builder.Build(), report);
    }

    private async Task<ContentFileSystem> MountAsync()
    {
        InMemoryFileSystem disk = new();
        foreach ((string path, byte[] bytes) in _files)
        {
            disk.AddFile("game/" + path, bytes);
        }

        DirectoryContentMount mount =
            await DirectoryContentMount.MountAsync(disk, VPath.Create("game"));
        return new ContentFileSystem([mount]);
    }

    /// <summary>
    /// One body part, one model, two meshes, two textures.
    /// </summary>
    private static byte[] BuildMdl(bool staticProp, int mdlVersion)
    {
        int headerSize = Unsafe.SizeOf<StudioHeader>();
        int textureSize = Unsafe.SizeOf<StudioTexture>();
        int bodyPartSize = Unsafe.SizeOf<StudioBodyParts>();
        int modelSize = Unsafe.SizeOf<StudioModel>();
        int meshSize = Unsafe.SizeOf<StudioMesh>();

        int textureAt = headerSize;
        int textureNamesAt = textureAt + (2 * textureSize);
        byte[] firstName = Encoding.Latin1.GetBytes("metalwall048a\0");
        byte[] secondName = Encoding.Latin1.GetBytes("glasswindow070a\0");

        int bodyPartAt = textureNamesAt + firstName.Length + secondName.Length;
        int modelAt = bodyPartAt + bodyPartSize;
        int meshAt = modelAt + modelSize;
        int total = meshAt + (2 * meshSize);

        byte[] bytes = new byte[total];
        Span<byte> span = bytes;

        StudioHeader header = default;
        header.Id = StudioIdents.Mdl;
        header.Version = mdlVersion;
        header.Checksum = Checksum;
        header.Length = total;
        header.HullMin = new Vec3(-16, -16, -16);
        header.HullMax = new Vec3(16, 16, 16);
        header.Flags = staticProp ? StaticPropModel.StudioFlagStaticProp : 0;
        header.NumTextures = 2;
        header.TextureIndex = textureAt;
        header.NumBodyParts = 1;
        header.BodyPartIndex = bodyPartAt;
        MemoryMarshal.Write(span, in header);

        StudioTexture first = default;
        first.NameIndex = textureNamesAt - textureAt;
        MemoryMarshal.Write(span[textureAt..], in first);

        StudioTexture second = default;
        second.NameIndex =
            (textureNamesAt + firstName.Length) - (textureAt + textureSize);
        MemoryMarshal.Write(span[(textureAt + textureSize)..], in second);

        firstName.CopyTo(span[textureNamesAt..]);
        secondName.CopyTo(span[(textureNamesAt + firstName.Length)..]);

        StudioBodyParts part = default;
        part.NumModels = 1;
        part.ModelIndex = modelAt - bodyPartAt;
        MemoryMarshal.Write(span[bodyPartAt..], in part);

        StudioModel model = default;
        model.NumMeshes = 2;
        model.MeshIndex = meshAt - modelAt;
        model.NumVertices = 6;

        // A BYTE offset, which is the whole point of the fixture: five decoy
        // vertices ahead of this model's own run.
        model.VertexIndex = DecoyVertices * 48;
        MemoryMarshal.Write(span[modelAt..], in model);

        StudioMesh meshOne = default;
        meshOne.Material = 0;
        meshOne.ModelIndex = modelAt - meshAt;
        meshOne.NumVertices = 3;
        meshOne.VertexOffset = 0;
        MemoryMarshal.Write(span[meshAt..], in meshOne);

        StudioMesh meshTwo = default;
        meshTwo.Material = 1;
        meshTwo.ModelIndex = modelAt - (meshAt + meshSize);
        meshTwo.NumVertices = 3;

        // An INDEX, not a byte offset: the second mesh starts three vertices
        // into the model's run.
        meshTwo.VertexOffset = 3;
        MemoryMarshal.Write(span[(meshAt + meshSize)..], in meshTwo);

        return bytes;
    }

    /// <summary>
    /// Eleven vertices: five decoys, then mesh 0's three at x = 100 and
    /// mesh 1's three at x = 200.
    /// </summary>
    private static byte[] BuildVvd()
    {
        const int count = DecoyVertices + 6;
        int headerSize = Unsafe.SizeOf<VertexFileHeader>();
        byte[] bytes = new byte[headerSize + (count * 48)];
        Span<byte> span = bytes;

        VertexFileHeader header = default;
        header.Id = StudioIdents.Vvd;
        header.Version = StudioIdents.VvdVersion;
        header.Checksum = Checksum;
        header.NumLods = 1;
        header.NumLodVertexes[0] = count;
        header.NumFixups = 0;
        header.FixupTableStart = 0;
        header.VertexDataStart = headerSize;
        header.TangentDataStart = 0;
        MemoryMarshal.Write(span, in header);

        for (int i = 0; i < count; i++)
        {
            StudioVertex vertex = default;
            vertex.Position = i < DecoyVertices
                ? new Vec3(-999 - i, -999, -999)
                : MeshVertex(i - DecoyVertices);
            MemoryMarshal.Write(span[(headerSize + (i * 48))..], in vertex);
        }

        return bytes;
    }

    private static Vec3 MeshVertex(int i) => (i / 3, i % 3) switch
    {
        (0, 0) => new Vec3(100, 0, 0),
        (0, 1) => new Vec3(100, 1, 0),
        (0, 2) => new Vec3(100, 0, 1),
        (1, 0) => new Vec3(200, 0, 0),
        (1, 1) => new Vec3(200, 1, 0),
        _ => new Vec3(200, 0, 1),
    };

    /// <summary>
    /// One body part, one model, one LOD, two meshes, one strip group and one
    /// three-index strip each.
    /// </summary>
    private static byte[] BuildVtx(bool secondMeshIsTriStrip)
    {
        int headerSize = Unsafe.SizeOf<VtxFileHeader>();
        int partSize = Unsafe.SizeOf<VtxBodyPartHeader>();
        int modelSize = Unsafe.SizeOf<VtxModelHeader>();
        int lodSize = Unsafe.SizeOf<VtxModelLodHeader>();
        int meshSize = Unsafe.SizeOf<VtxMeshHeader>();
        int groupSize = Unsafe.SizeOf<VtxStripGroupHeader>();
        int vertexSize = Unsafe.SizeOf<VtxVertex>();
        int stripSize = Unsafe.SizeOf<VtxStripHeader>();

        int partAt = headerSize;
        int modelAt = partAt + partSize;
        int lodAt = modelAt + modelSize;
        int meshAt = lodAt + lodSize;
        int blockSize = groupSize + (3 * sizeof(ushort)) + (3 * vertexSize) + stripSize;
        int blocksAt = meshAt + (2 * meshSize);

        byte[] bytes = new byte[blocksAt + (2 * blockSize)];
        Span<byte> span = bytes;

        VtxFileHeader header = default;
        header.Version = StudioIdents.VtxVersion;
        header.CheckSum = Checksum;
        header.NumLods = 1;
        header.NumBodyParts = 1;
        header.BodyPartOffset = partAt;
        MemoryMarshal.Write(span, in header);

        VtxBodyPartHeader part = default;
        part.NumModels = 1;
        part.ModelOffset = modelAt - partAt;
        MemoryMarshal.Write(span[partAt..], in part);

        VtxModelHeader model = default;
        model.NumLods = 1;
        model.LodOffset = lodAt - modelAt;
        MemoryMarshal.Write(span[modelAt..], in model);

        VtxModelLodHeader lod = default;
        lod.NumMeshes = 2;
        lod.MeshOffset = meshAt - lodAt;
        MemoryMarshal.Write(span[lodAt..], in lod);

        for (int i = 0; i < 2; i++)
        {
            int meshHeaderAt = meshAt + (i * meshSize);
            int blockAt = blocksAt + (i * blockSize);
            int indicesAt = blockAt + groupSize;
            int verticesAt = indicesAt + (3 * sizeof(ushort));
            int stripAt = verticesAt + (3 * vertexSize);

            VtxMeshHeader mesh = default;
            mesh.NumStripGroups = 1;
            mesh.StripGroupHeaderOffset = blockAt - meshHeaderAt;
            MemoryMarshal.Write(span[meshHeaderAt..], in mesh);

            VtxStripGroupHeader group = default;
            group.NumVerts = 3;
            group.VertOffset = verticesAt - blockAt;
            group.NumIndices = 3;
            group.IndexOffset = indicesAt - blockAt;
            group.NumStrips = 1;
            group.StripOffset = stripAt - blockAt;
            MemoryMarshal.Write(span[blockAt..], in group);

            for (int index = 0; index < 3; index++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    span[(indicesAt + (index * sizeof(ushort)))..], (ushort)index);

                VtxVertex vertex = default;
                vertex.OrigMeshVertId = (ushort)index;
                vertex.NumBones = 1;
                MemoryMarshal.Write(span[(verticesAt + (index * vertexSize))..], in vertex);
            }

            VtxStripHeader strip = default;
            strip.NumIndices = 3;
            strip.IndexOffset = 0;
            strip.NumVerts = 3;
            strip.VertOffset = 0;
            strip.Flags = i == 1 && secondMeshIsTriStrip
                ? (byte)VtxStripFlags.IsTriStrip
                : (byte)VtxStripFlags.IsTriList;
            MemoryMarshal.Write(span[stripAt..], in strip);
        }

        return bytes;
    }
}
