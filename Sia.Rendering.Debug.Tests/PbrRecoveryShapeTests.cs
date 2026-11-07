using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Sia.Asset;
using Sia.Engine.Mesh;
using Sia.Engine.Rendering;
using Sia.Engine.Rendering.Pbr;
using Sia.Math;
using Xunit;

namespace Sia.Rendering.Debug.Tests;

public sealed class PbrRecoveryShapeTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type OwnerType = typeof(PbrStreamResidency);
    private static Type Nested(string name) => OwnerType.GetNestedType(name, BindingFlags.NonPublic)!;
    private static object New(string name, params object[] args) => Activator.CreateInstance(Nested(name), Instance, null, args, null)!;
    private static object Shape(uint vertices, uint triangles, bool raster) => New("PageShape", vertices, triangles, raster);
    private static object Get(object owner, string field) => owner.GetType().GetField(field, Instance)!.GetValue(owner)!;
    private static void Set(object owner, string field, object? value) => owner.GetType().GetField(field, Instance)!.SetValue(owner, value);
    private static object? Call(object owner, string method, params object?[] args)
    {
        try { return owner.GetType().GetMethod(method, Instance)!.Invoke(owner, args); }
        catch (TargetInvocationException error) when (error.InnerException is { } inner) {
            ExceptionDispatchInfo.Capture(inner).Throw(); throw;
        }
    }

    [Theory]
    [InlineData(true, true, 4, 2, true)]
    [InlineData(true, true, 6, 2, true)]
    [InlineData(true, true, 7, 2, false)]
    [InlineData(true, true, 0, 2, false)]
    [InlineData(true, true, 4, 1, false)]
    [InlineData(true, false, 6, 2, false)]
    [InlineData(false, true, 6, 2, false)]
    [InlineData(false, false, 6, 2, true)]
    [InlineData(false, false, 4, 2, false)]
    public void TypedShapeValidationKeepsVirtualExactAndRasterBounded(bool declaredRaster, bool actualRaster,
        uint vertices, uint triangles, bool valid)
    {
        var method = OwnerType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "ValidPageShape");
        var descriptor = new PbrSceneStream.PageInfo("typed-page", 6 * StreamGeometryPage.VertexBytes + 24, 6, 2, 512);
        Assert.Equal(valid, (bool)method.Invoke(null, [descriptor, declaredRaster, Shape(vertices, triangles, actualRaster)])!);
    }

    [Fact]
    public async Task CompactRasterRecoveryPlansWithdrawsAndReservesTheSameActualShapes()
    {
        await using var fixture = await RecoveryFixture.Create();
        var originalDescriptors = fixture.Source.Pages.ToArray();
        Assert.True(fixture.TargetGeometry.VertexCount < fixture.Source.Pages[fixture.TargetPage].Vertices);
        Assert.True(fixture.VictimGeometry.VertexCount < fixture.Source.Pages[fixture.VictimPage].Vertices);
        Assert.True(fixture.TargetGeometry.VertexCount + fixture.VictimGeometry.VertexCount
            < fixture.Source.Pages[fixture.TargetPage].Vertices + fixture.Source.Pages[fixture.VictimPage].Vertices);

        var victims = Assert.IsAssignableFrom<Array>(Call(fixture.Owner, "PlanRecovery", fixture.Target));
        Assert.Equal(fixture.Victim, Assert.Single(victims.Cast<object>()));
        fixture.Begin();
        var recovery = Get(fixture.Owner, "_recovery");
        Assert.True((bool)Get(recovery, "Started"));
        var reservations = (IDictionary)Get(recovery, "Reservations");
        var shapes = (IDictionary)Get(recovery, "Shapes");
        Assert.Equal(2, reservations.Count);
        Assert.False(fixture.Vertices.Contains(fixture.OriginalVictim.Vertices));
        foreach (var key in new[] { fixture.Target, fixture.Victim }) {
            var allocation = Assert.IsType<StreamPageAllocation>(reservations[key]);
            Assert.True((bool)Call(shapes[key]!, "Matches", allocation)!);
        }
        var held = Assert.IsType<StreamPageAllocation>(reservations[fixture.Target]);
        Assert.Equal((uint)fixture.TargetGeometry.VertexCount, held.Vertices.Count);
        Assert.Equal((uint)fixture.TargetGeometry.TriangleCount, held.Triangles.Count);
        Assert.Equal(fixture.Arena.VertexCapacity, fixture.Vertices.Used);
        Assert.Equal(fixture.Arena.TriangleCapacity, fixture.Triangles.Used);
        Assert.Equal(originalDescriptors, fixture.Source.Pages); // Admission costs were never rewritten.

        var exact = fixture.Reserve(fixture.Target, fixture.TargetGeometry);
        Assert.Equal(held, exact);
        var upload = fixture.Arena.Prepare(fixture.TargetGeometry, exact, float4x4.identity, 0);
        fixture.Arena.Abort(upload); // Real CPU packing and real range validation, no GPU calls.
        Call(fixture.Owner, "AbortRecovery", false);
        fixture.AssertOnlyPinnedRoots();
    }

    [Fact]
    public async Task MissingAcquiredTargetShapeCannotAuthorizeARecoveryPlan()
    {
        await using var fixture = await RecoveryFixture.Create();
        ((IDictionary)Get(Get(fixture.Owner, "_recovery"), "Shapes")).Clear();
        Assert.Null(Call(fixture.Owner, "PlanRecovery", fixture.Target));
        Assert.True(fixture.Vertices.Contains(fixture.OriginalVictim.Vertices));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InsufficientActualRestorationCapacityStillRejectsBeforeWithdrawal(bool vertices)
    {
        await using var fixture = await RecoveryFixture.Create(shortVertices: vertices ? 1u : 0u, shortTriangles: vertices ? 0u : 1u);
        var used = fixture.Arena.UsedBytes;
        Assert.Null(Call(fixture.Owner, "PlanRecovery", fixture.Target));
        Assert.True(fixture.Vertices.Contains(fixture.OriginalVictim.Vertices));
        Assert.True(fixture.Triangles.Contains(fixture.OriginalVictim.Triangles));
        Assert.Equal(used, fixture.Arena.UsedBytes);
    }

    [Fact]
    public async Task NormalReserveCapturesTheAcquiredTargetRatherThanItsAdmissionBound()
    {
        await using var fixture = await RecoveryFixture.Create(shortVertices: 1, shortTriangles: 1);
        Set(fixture.Owner, "_recovery", null);
        Set(fixture.Owner, "_recoveryCpuInput", true);
        object?[] args = [fixture.Target, fixture.TargetGeometry, null];
        Assert.False((bool)Call(fixture.Owner, "Reserve", args)!);
        var recovery = Get(fixture.Owner, "_recovery");
        var captured = ((IDictionary)Get(recovery, "Shapes"))[fixture.Target];
        Assert.Equal(Shape((uint)fixture.TargetGeometry.VertexCount, (uint)fixture.TargetGeometry.TriangleCount, true), captured);
        Assert.Null(Call(fixture.Owner, "PlanRecovery", fixture.Target)); // Capturing never waives real capacity.
        Assert.True(fixture.Vertices.Contains(fixture.OriginalVictim.Vertices));
    }

    [Fact]
    public void WholeRasterMixedRootsKeepTheirSourceAuthenticatedKind()
    {
        var mesh = ProceduralMesh.Cube();
        var raster = MeshRasterData.Build(MeshSource.Create(mesh), MeshRasterBuildSettings.Default with { MaximumLods = 1 });
        var rasterScene = PbrRasterScene.Create([new(raster, 0)], [new(PbrMaterial.Default)], [new(0, 0, float4x4.identity)]);
        var empty = new PbrVirtualScene([], [], [], "empty", []);
        var mixed = new PbrMixedScene(rasterScene, empty, [1]);
        var stream = PbrSceneStream.Create(mixed, _ => throw new InvalidOperationException("No Virtual acquisition"),
            (_, _) => throw new InvalidOperationException("No Virtual preparation"), (_, _) => throw new InvalidOperationException("No Virtual acquisition"));
        Assert.NotEmpty(stream.ResidentRoots);
        foreach (var (id, geometry) in stream.ResidentRoots) {
            Assert.Null(geometry.Data);
            Assert.True(stream.IsRasterPage(id));
        }
        Assert.False(stream.IsRasterPage("unknown-page"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ChangedTargetPromiseWrongKindOrExceededBoundRollsBackEveryHeldRecoveryReservation(int change)
    {
        await using var fixture = await RecoveryFixture.Create();
        fixture.Begin();
        var recovery = Get(fixture.Owner, "_recovery");
        var held = ((IDictionary)Get(recovery, "Reservations")).Values.Cast<StreamPageAllocation>().ToArray();
        StreamGeometryPage actual = fixture.TargetGeometry;
        if (change == 1) {
            var vertices = Enumerable.Range(0, actual.VertexCount).Select(actual.GetVertex).ToArray();
            actual = StreamGeometryPage.Create(MeshVirtualPageData.Create(vertices, actual.Indices, []));
        }
        else if (change == 0) {
            ((IDictionary)Get(recovery, "Shapes"))[fixture.Target] = Shape((uint)actual.VertexCount - 1,
                (uint)actual.TriangleCount, true);
        }
        else {
            var bounds = fixture.Source.Pages.ToArray();
            bounds[fixture.TargetPage] = bounds[fixture.TargetPage] with { Vertices = actual.VertexCount - 1 };
            Set(fixture.Owner, "_pages", bounds);
        }
        Assert.Throws<InvalidDataException>(() => fixture.Reserve(fixture.Target, actual));
        Assert.Null(OwnerType.GetField("_recovery", Instance)!.GetValue(fixture.Owner));
        Assert.All(held, allocation => {
            Assert.False(fixture.Vertices.Contains(allocation.Vertices));
            Assert.False(fixture.Triangles.Contains(allocation.Triangles));
        });
        fixture.AssertOnlyPinnedRoots();
    }

    [Fact]
    public async Task ArenaPrepareStillRejectsConservativeRasterBoundsAsAnExactUploadShape()
    {
        await using var fixture = await RecoveryFixture.Create();
        var declared = fixture.Source.Pages[fixture.TargetPage];
        var arena = CpuArena((uint)declared.Vertices, (uint)declared.Triangles, out _, out _);
        Assert.True(arena.TryReserve((uint)declared.Vertices, (uint)declared.Triangles, out var allocation));
        Assert.Throws<ArgumentException>(() => arena.Prepare(fixture.TargetGeometry, allocation, float4x4.identity, 0));
        arena.Free(allocation);
        Assert.Equal(0ul, arena.UsedBytes);
    }

    [Fact]
    public async Task RelocationCommitsTheActualTargetProofAndRejectsAChangedTargetOnConsumption()
    {
        await using var fixture = await RecoveryFixture.Create(extraTargetCopies: 1);
        var destination = fixture.ReserveRaw(fixture.VictimGeometry);
        var resident = ((IDictionary)Get(fixture.Owner, "_resident"))[fixture.Victim]!;
        var targetShape = Shape((uint)fixture.TargetGeometry.VertexCount, (uint)fixture.TargetGeometry.TriangleCount, true);
        var move = New("Relocation", fixture.Victim, fixture.Target, targetShape, resident, destination);
        var completed = fixture.Arena.Prepare(fixture.VictimGeometry, destination, float4x4.identity, 0);
        Set(completed, "Plane", StreamGeometryPage.VertexPlanes + 1); // Only skip GPU transfer; commit uses real range ledger.
        Set(move, "Upload", completed);
        Set(fixture.Owner, "_relocation", move);
        Set(fixture.Owner, "_recovery", null);
        Call(fixture.Owner, "BeginRelocationFrame");
        var held = Get(fixture.Owner, "_relocationReservation");
        var allocation = Assert.IsType<StreamPageAllocation>(held.GetType().GetField("Item2")!.GetValue(held));
        Assert.Equal((uint)fixture.TargetGeometry.VertexCount, allocation.Vertices.Count);
        Assert.True(allocation.Vertices.Count < fixture.Source.Pages[fixture.TargetPage].Vertices);
        Assert.False(fixture.Vertices.Contains(fixture.OriginalVictim.Vertices));
        Assert.Equal(targetShape, held.GetType().GetField("Item3")!.GetValue(held));

        // Simulate a reread with another exact shape while preserving V/T pool
        // ownership. The promise, not a loose descriptor fit, must decide it.
        var wrong = Shape((uint)fixture.TargetGeometry.VertexCount - 1, (uint)fixture.TargetGeometry.TriangleCount, true);
        var altered = Activator.CreateInstance(held.GetType(), fixture.Target, allocation, wrong)!;
        Set(fixture.Owner, "_relocationReservation", altered);
        Assert.Throws<InvalidDataException>(() => fixture.Reserve(fixture.Target, fixture.TargetGeometry));
        Assert.False(fixture.Vertices.Contains(allocation.Vertices));
        Assert.False(fixture.Triangles.Contains(allocation.Triangles));
        Assert.True(fixture.Vertices.Contains(destination.Vertices)); // Committed replacement remains owned.
    }

    private static StreamPageArena CpuArena(uint vertices, uint triangles, out StreamRangePool v, out StreamRangePool t)
    {
        // Bypass only GPU buffer construction. Reservation, validation, packing,
        // proof, withdrawal and rollback below execute production code.
        var arena = (StreamPageArena)RuntimeHelpers.GetUninitializedObject(typeof(StreamPageArena));
        Set(arena, "_vertices", v = new(vertices)); Set(arena, "_triangles", t = new(triangles));
        Set(arena, "_staging", new byte[StreamGeometryPage.MaximumBytes]);
        return arena;
    }

    private sealed class RecoveryFixture : IAsyncDisposable
    {
        public PbrSceneStream Source = null!;
        public PbrStreamResidency Owner = null!;
        public StreamPageArena Arena = null!;
        public StreamRangePool Vertices = null!, Triangles = null!;
        public StreamGeometryPage TargetGeometry = null!, VictimGeometry = null!;
        public object Target = null!, Victim = null!;
        public int TargetPage, VictimPage;
        public StreamPageAllocation OriginalVictim;
        private ulong _rootBytes;
        private BoundedPageScheduler<PbrSceneStream.PreparedGeometryPage> _requests = null!;

        public static async Task<RecoveryFixture> Create(uint extraTargetCopies = 0, uint shortVertices = 0, uint shortTriangles = 0)
        {
            var mesh = ProceduralMesh.Sphere(latitudeSegments: 12, longitudeSegments: 24);
            var raster = MeshRasterData.Build(MeshSource.Create(mesh),
                MeshRasterBuildSettings.Default with { MaximumLods = 3, MinimumTriangles = 16 });
            var pages = new Dictionary<AssetId, MeshRasterPageData>();
            var product = MeshRasterPagesData.Build(raster, AssetId.New(), AssetId.New(), (id, data) => pages.Add(id, data));
            var scene = new PbrRasterPagesScene([new(product, 0)], [new(PbrMaterial.Default)],
                [new(0, 0, float4x4.identity)], "actual-recovery-shape", [1], CancellationToken.None);
            var source = PbrSceneStream.Create(scene, r => pages[r.Value],
                (r, _) => Task.FromResult(new MeshRasterPageRecord { Data = pages[r.Value] }), (_, record) => record.Data);
            var tree = Assert.Single(source.Hierarchies);
            Assert.Equal(3, tree.Nodes.Length);
            var middle = tree.GetPages(tree.Nodes[1]); var fine = tree.GetPages(tree.Nodes[2]);
            Assert.Equal(1, middle.Length); Assert.Equal(1, fine.Length);
            var fixture = new RecoveryFixture { Source = source, VictimPage = middle[0].Page, TargetPage = fine[0].Page };
            fixture.Target = New("Key", -1, fixture.TargetPage); fixture.Victim = New("Key", -1, fixture.VictimPage);
            async Task<StreamGeometryPage> Read(int index) {
                var id = source.Pages[index].Id;
                return source.UsePreparedPage(id, await source.ReadPreparedPageAsync(id, CancellationToken.None));
            }
            fixture.TargetGeometry = await Read(fixture.TargetPage); fixture.VictimGeometry = await Read(fixture.VictimPage);
            var rootShape = PbrGpuScene.RootShape(source, true);
            fixture.Arena = CpuArena(checked((uint)rootShape.Vertices + (uint)fixture.VictimGeometry.VertexCount
                    + (uint)fixture.TargetGeometry.VertexCount * (1 + extraTargetCopies) - shortVertices),
                checked((uint)rootShape.Triangles + (uint)fixture.VictimGeometry.TriangleCount
                    + (uint)fixture.TargetGeometry.TriangleCount * (1 + extraTargetCopies) - shortTriangles), out fixture.Vertices, out fixture.Triangles);
            fixture.Owner = (PbrStreamResidency)RuntimeHelpers.GetUninitializedObject(OwnerType);
            Set(fixture.Owner, "_source", source); Set(fixture.Owner, "_pages", source.Pages);
            Set(fixture.Owner, "_canonicalPages", Enumerable.Range(0, source.Pages.Length).ToArray());
            Set(fixture.Owner, "_instances", source.Instances.ToArray()); Set(fixture.Owner, "_arena", fixture.Arena);
            Set(fixture.Owner, "<SharedInstances>k__BackingField", true);
            foreach (var name in new[] { "_resident", "_wanted", "_used", "_evictionCandidates", "_dependencies", "_recoveryIncoming", "_recoveryFeedback" }) {
                var type = OwnerType.GetField(name, Instance)!.FieldType;
                Set(fixture.Owner, name, Activator.CreateInstance(type));
            }
            var previous = new Dictionary<PbrStreamResidency.ChildRequest, float> { [new(-1, 0, 1)] = 1, [new(-1, 0, 2)] = 1 };
            Set(fixture.Owner, "_budgetPreviousRequests", previous);
            fixture._requests = new((_, _) => throw new InvalidOperationException("CPU proof must not schedule IO"),
                p => p.DecodedBytes, 1, maximumDecodedBytes: 1);
            Set(fixture.Owner, "_requests", fixture._requests);
            foreach (var (id, geometry) in source.ResidentRoots) {
                var key = New("Key", -1, source.GetPageIndex(id));
                fixture.AddResident(key, geometry, true);
            }
            fixture._rootBytes = fixture.Arena.UsedBytes;
            fixture.OriginalVictim = fixture.AddResident(fixture.Victim, fixture.VictimGeometry, false);
            ((IDictionary)Get(fixture.Owner, "_wanted"))[fixture.Target] = 1f;
            var recovery = New("Recovery", fixture.Target);
            ((IDictionary)Get(recovery, "Shapes"))[fixture.Target] = Shape((uint)fixture.TargetGeometry.VertexCount,
                (uint)fixture.TargetGeometry.TriangleCount, true);
            Set(fixture.Owner, "_recovery", recovery);
            return fixture;
        }

        public void Begin() => Call(Owner, "BeginRecoveryFrame");
        public StreamPageAllocation ReserveRaw(StreamGeometryPage geometry)
        {
            Assert.True(Arena.TryReserve((uint)geometry.VertexCount, (uint)geometry.TriangleCount, out var allocation));
            return allocation;
        }
        private StreamPageAllocation AddResident(object key, StreamGeometryPage geometry, bool root)
        {
            var allocation = ReserveRaw(geometry);
            var shape = Shape((uint)geometry.VertexCount, (uint)geometry.TriangleCount, geometry.Data is null);
            ((IDictionary)Get(Owner, "_resident"))[key] = New("Resident", allocation, shape, root, 0L);
            ((IDictionary)Get(Owner, "_wanted"))[key] = 1f;
            return allocation;
        }
        public StreamPageAllocation Reserve(object key, StreamGeometryPage geometry)
        {
            object?[] args = [key, geometry, null];
            Assert.True((bool)Call(Owner, "Reserve", args)!);
            return Assert.IsType<StreamPageAllocation>(args[2]);
        }
        public void AssertOnlyPinnedRoots() => Assert.Equal(_rootBytes, Arena.UsedBytes);
        public ValueTask DisposeAsync() => _requests.DisposeAsync();
    }
}
