using System.Runtime.CompilerServices;
using Sia.Math;

namespace Sia.Engine.Mesh;

/// <summary>A disjoint chart rectangle including its producer-owned filtering border.</summary>
public readonly record struct MeshBakeChart(int X, int Y, int Width, int Height, int MaterialSlot);

/// <summary>Offline derived source and chart ownership in original primitive order; no IO or scene ownership.</summary>
public sealed class MeshBakeLayout
{
    private readonly MeshBakeChart[] _charts;
    private readonly int[] _triangleCharts;
    public MeshSource Source { get; }
    public string InputHash { get; }
    public int Resolution { get; }
    public int Padding { get; }
    public ReadOnlySpan<MeshBakeChart> Charts => _charts;
    /// <summary>Degenerate source primitives have chart -1 and remain in the derived source.</summary>
    public ReadOnlySpan<int> TriangleCharts => _triangleCharts;

    private MeshBakeLayout(MeshSource source, string inputHash, int resolution, int padding, MeshBakeChart[] charts, int[] triangleCharts)
    {
        Source = source; InputHash = inputHash; Resolution = resolution; Padding = padding;
        _charts = charts; _triangleCharts = triangleCharts;
    }

    private sealed class Chart(double3 normal, double3 origin, int material, bool sourceUv, int sign)
    {
        internal readonly List<int> Triangles = [];
        internal readonly double3 Normal = normal, Origin = origin;
        internal readonly int Material = material, Sign = sign;
        internal bool SourceUv = sourceUv;
        internal double2 Minimum, Size;
        internal MeshBakeChart Rectangle;
        private readonly double3 _u = math.normalize(math.cross(
            System.Math.Abs(normal.x) <= System.Math.Abs(normal.y) && System.Math.Abs(normal.x) <= System.Math.Abs(normal.z) ? new double3(1,0,0)
                : System.Math.Abs(normal.y) <= System.Math.Abs(normal.z) ? new double3(0,1,0) : new double3(0,0,1), normal));

        internal double2 Point(MeshSource source, uint vertex)
        {
            // A thin oblique primitive can lose its area when an orthogonal
            // projection is rounded to Float32. Its own affine triangle domain
            // remains exact; the surface sampler still uses the original geometry.
            if (Triangles.Count == 1) {
                var first = Triangles[0] * 3;
                if (vertex == source.Indices[first]) return new(0, 0);
                if (vertex == source.Indices[first + 1]) return new(1, 0);
                if (vertex == source.Indices[first + 2]) return new(0, 1);
                throw new InvalidOperationException("Chart point is outside its source primitive.");
            }
            var value = source.Vertices[(int)vertex];
            if (SourceUv) {
                var uv = InputUv(source,vertex);
                return new(uv.x,(double)uv.y*Sign);
            }
            var position = (double3)value.Position - Origin;
            return new(math.dot(position, _u), math.dot(position, math.cross(Normal, _u)));
        }

        internal void Measure(MeshSource source)
        {
            var lo = new double2(double.PositiveInfinity); var hi = new double2(double.NegativeInfinity);
            foreach (var triangle in Triangles)
                for (var corner = 0; corner < 3; corner++) {
                    var uv = Point(source, source.Indices[triangle * 3 + corner]); lo = math.min(lo,uv); hi = math.max(hi,uv);
                }
            Minimum = lo; Size = hi - lo;
        }

        internal float2 Coordinates(MeshSource source, uint vertex, int resolution, int padding)
        {
            var point = (Point(source,vertex)-Minimum)/Size;
            return new((float)((Rectangle.X+padding+.5 + point.x*(Rectangle.Width-2*padding-1))/resolution),
                (float)((Rectangle.Y+padding+.5 + point.y*(Rectangle.Height-2*padding-1))/resolution));
        }
    }

    /// <summary>
    /// Packs source UV or planar connected charts. Overlap/work-budget failures subdivide bounded regions;
    /// no vertex channel or source primitive is discarded. The budget admits known array element capacities,
    /// not process memory, dictionary overhead or temporary list growth.
    /// </summary>
    public static MeshBakeLayout Generate(MeshSource source, int resolution = 256, int padding = 2,
        ulong maximumArrayBytes = 512ul*1024*1024, CancellationToken cancellationToken = default,
        int? maximumResolution = null)
    {
        ArgumentNullException.ThrowIfNull(source); cancellationToken.ThrowIfCancellationRequested();
        if (resolution is < 8 or > 8192 || (resolution & (resolution-1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(resolution));
        var limit = maximumResolution ?? resolution;
        if (limit < resolution || limit > 8192 || (limit & (limit-1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(maximumResolution));
        if (padding < 1 || padding > (resolution-2)/2) throw new ArgumentOutOfRangeException(nameof(padding));
        var count = source.Indices.Length/3;
        if (count > 1_000_000 || source.Vertices.Length > 3_000_000)
            throw new ArgumentException("Bake layout source exceeds its topology work limit.",nameof(source));
        var capacity = (ulong)count*3+(ulong)source.Vertices.Length;
        var arrays = capacity*72 + (ulong)count*(24+12+4+4+20) + (ulong)source.Vertices.Length
            + (ulong)source.Sections.Length*12 + (ulong)count*(ulong)Unsafe.SizeOf<ProjectedTriangle>()
            + (ulong)source.Vertices.Length*sizeof(uint);
        if (arrays > maximumArrayBytes) throw new ArgumentException("Bake layout exceeds known-array admission.",nameof(maximumArrayBytes));
        var normals = new double3[count]; var adjacency = new int[count*3]; Array.Fill(adjacency,-1);
        var slots = new int[count]; var ownership = new int[count]; Array.Fill(ownership,-1);
        foreach (var section in source.Sections)
            slots.AsSpan(section.FirstIndex/3,section.IndexCount/3).Fill(section.MaterialSlot);
        // Bake topology follows exact geometric edges and authored Bake UV seams,
        // independently of normal/tangent/material-UV vertex splits.
        var positions = new Dictionary<(float3 Position,float2 BakeUv),uint>(); var topology = new uint[source.Vertices.Length];
        for (var vertex=0;vertex<topology.Length;vertex++) {
            if ((vertex&4095)==0) cancellationToken.ThrowIfCancellationRequested();
            var position=(source.Vertices[vertex].Position,source.HasBakeUvs?source.BakeUvs[vertex]:default);
            if (!positions.TryGetValue(position,out var representative)) {
                representative=(uint)vertex; positions.Add(position,representative);
            }
            topology[vertex]=representative;
        }
        var edges = new Dictionary<(uint,uint),(int First,int Second,uint From)>();
        for (var triangle = 0; triangle < count; triangle++) {
            if ((triangle&1023)==0) cancellationToken.ThrowIfCancellationRequested();
            var a = (double3)source.Vertices[(int)source.Indices[triangle*3]].Position;
            var b = (double3)source.Vertices[(int)source.Indices[triangle*3+1]].Position;
            var c = (double3)source.Vertices[(int)source.Indices[triangle*3+2]].Position;
            var cross = math.cross(b-a,c-a); var length = math.length(cross);
            if (length == 0) continue;
            normals[triangle] = cross/length;
            for (var corner = 0; corner < 3; corner++) {
                var index = triangle*3+corner; var from = topology[source.Indices[index]]; var to = topology[source.Indices[triangle*3+(corner+1)%3]];
                var key = from < to ? (from,to) : (to,from);
                if (!edges.TryGetValue(key,out var edge)) { edges.Add(key,(index,-1,from)); continue; }
                if (edge.Second != -1 || edge.From == from) {
                    if (edge.Second >= 0) { adjacency[edge.First]=-1; adjacency[edge.Second]=-1; }
                    edges[key]=(edge.First,-2,edge.From); continue;
                }
                adjacency[index]=edge.First/3; adjacency[edge.First]=triangle; edges[key]=(edge.First,index,edge.From);
            }
        }
        double Area(int triangle)
        {
            var a = (double2)InputUv(source,source.Indices[triangle*3]);
            var b = (double2)InputUv(source,source.Indices[triangle*3+1]);
            var c = (double2)InputUv(source,source.Indices[triangle*3+2]);
            return Cross(b-a,c-a);
        }
        var tolerance = math.length((double3)source.Bounds.Max-(double3)source.Bounds.Min)*1e-7;
        bool Planar(int triangle,Chart chart)
        {
            if (math.dot(normals[triangle],chart.Normal)<.999999) return false;
            for (var corner=0;corner<3;corner++)
                if (System.Math.Abs(math.dot((double3)source.Vertices[(int)source.Indices[triangle*3+corner]].Position-chart.Origin,chart.Normal))>tolerance) return false;
            return true;
        }
        Chart Single(int triangle)
        {
            var chart = new Chart(normals[triangle],(double3)source.Vertices[(int)source.Indices[triangle*3]].Position,slots[triangle],false,1);
            chart.Triangles.Add(triangle); chart.Measure(source); return chart;
        }
        var charts = new List<Chart>(); var assigned = new bool[count]; var queue = new Queue<int>();
        void AddRegion(Chart chart)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chart.SourceUv && chart.Triangles.All(t=>Planar(t,chart))) chart.SourceUv=false;
            chart.Measure(source);
            if (!Overlaps(source,chart,cancellationToken)) { charts.Add(chart); return; }
            // Preserve nonoverlapping regions instead of turning a single fold or
            // overlap-check budget into one chart per source primitive.
            var half = chart.Triangles.Count / 2;
            for (var part = 0; part < 2; part++) {
                var child = new Chart(chart.Normal,chart.Origin,chart.Material,chart.SourceUv,chart.Sign);
                var first = part == 0 ? 0 : half; var end = part == 0 ? half : chart.Triangles.Count;
                for (var at=first;at<end;at++) child.Triangles.Add(chart.Triangles[at]);
                AddRegion(child);
            }
        }
        for (var seed=0;seed<count;seed++) {
            if (assigned[seed] || math.all(normals[seed]==0)) continue;
            cancellationToken.ThrowIfCancellationRequested(); var area = Area(seed);
            var chart = new Chart(normals[seed],(double3)source.Vertices[(int)source.Indices[seed*3]].Position,slots[seed],area!=0,area<0?-1:1);
            assigned[seed]=true; queue.Enqueue(seed);
            while (queue.TryDequeue(out var triangle)) {
                if ((chart.Triangles.Count&1023)==0) cancellationToken.ThrowIfCancellationRequested();
                chart.Triangles.Add(triangle);
                for (var corner=0;corner<3;corner++) {
                    var other = adjacency[triangle*3+corner];
                    if (other<0 || assigned[other] || slots[other]!=chart.Material) continue;
                    if (chart.SourceUv ? Area(other)*chart.Sign<=0 : !Planar(other,chart)) continue;
                    assigned[other]=true; queue.Enqueue(other);
                }
            }
            AddRegion(chart);
        }
        if (charts.Count==0) throw new ArgumentException("Bake layout needs at least one nondegenerate primitive.",nameof(source));
        for (var attempt=0;;attempt++) {
            while (!Pack(charts,resolution,padding,cancellationToken)) {
                if (resolution == limit) throw new InvalidOperationException("Bake resolution cannot contain every padded chart rectangle.");
                resolution *= 2;
            }
            var bad = new HashSet<Chart>();
            foreach (var chart in charts) {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var triangle in chart.Triangles) {
                    var a = (double2)chart.Coordinates(source,source.Indices[triangle*3],resolution,padding);
                    var b = (double2)chart.Coordinates(source,source.Indices[triangle*3+1],resolution,padding);
                    var c = (double2)chart.Coordinates(source,source.Indices[triangle*3+2],resolution,padding);
                    if (Cross(b-a,c-a)<=0) { bad.Add(chart); break; }
                }
            }
            if (bad.Count==0) break;
            if (bad.Any(static chart=>chart.Triangles.Count==1))
                throw new InvalidOperationException("Bake triangle cannot retain positive coordinates at the selected precision.");
            charts = charts.SelectMany(chart=>bad.Contains(chart) || attempt>=7 ? chart.Triangles.Select(Single) : [chart]).ToList();
        }
        for (var index=0;index<charts.Count;index++) foreach (var triangle in charts[index].Triangles) ownership[triangle]=index;
        var vertices = new List<MeshVertex>(); var coordinates = new List<float2>(); var indices = new uint[source.Indices.Length];
        var remap = new Dictionary<(int,uint),uint>(); var used = new bool[source.Vertices.Length];
        for (var at=0;at<indices.Length;at++) {
            if ((at&4095)==0) cancellationToken.ThrowIfCancellationRequested();
            var chart=ownership[at/3]; var original=source.Indices[at]; used[(int)original]=true;
            if (!remap.TryGetValue((chart,original),out var vertex)) {
                vertex=(uint)vertices.Count; remap.Add((chart,original),vertex); vertices.Add(source.Vertices[(int)original]);
                coordinates.Add(chart<0?default:charts[chart].Coordinates(source,original,resolution,padding));
            }
            indices[at]=vertex;
        }
        for (var vertex=0;vertex<used.Length;vertex++) if (!used[vertex]) { vertices.Add(source.Vertices[vertex]); coordinates.Add(default); }
        var uv = coordinates.ToArray();
        var derived = MeshSource.FromDecoded(new(vertices.ToArray(),indices,source.Bounds),source.Sections.ToArray(),uv);
        cancellationToken.ThrowIfCancellationRequested();
        return new(derived,source.ContentHash,resolution,padding,charts.Select(static c=>c.Rectangle).ToArray(),ownership);
    }

    private static double Cross(double2 a,double2 b)=>a.x*b.y-a.y*b.x;

    private static float2 InputUv(MeshSource source,uint vertex)
        => source.HasBakeUvs?source.BakeUvs[(int)vertex]:source.Vertices[(int)vertex].UV;

    private readonly record struct ProjectedTriangle(double2 A,double2 B,double2 C,double2 Minimum,double2 Maximum,int Ordinal);

    private static bool Overlaps(MeshSource source,Chart chart,CancellationToken token)
    {
        if (chart.Triangles.Count < 2) return false;
        var projected = new ProjectedTriangle[chart.Triangles.Count];
        for (var at=0;at<projected.Length;at++) {
            if ((at&1023)==0) token.ThrowIfCancellationRequested();
            var triangle=chart.Triangles[at]*3;
            var a=chart.Point(source,source.Indices[triangle]);
            var b=chart.Point(source,source.Indices[triangle+1]);
            var c=chart.Point(source,source.Indices[triangle+2]);
            projected[at]=new(a,b,c,math.min(a,math.min(b,c)),math.max(a,math.max(b,c)),at);
        }
        Array.Sort(projected,static (a,b)=> {
            var order=a.Minimum.x.CompareTo(b.Minimum.x);
            return order!=0?order:a.Ordinal.CompareTo(b.Ordinal);
        });
        var comparisons=0;
        Span<double2> first=stackalloc double2[3]; Span<double2> second=stackalloc double2[3];
        for (var a=0;a<projected.Length;a++) {
            token.ThrowIfCancellationRequested();
            var p=projected[a]; first[0]=p.A;first[1]=p.B;first[2]=p.C;
            for (var b=a+1;b<projected.Length;b++) {
                var q=projected[b]; if (q.Minimum.x>=p.Maximum.x) break;
                if ((++comparisons&4095)==0) token.ThrowIfCancellationRequested();
                if (comparisons>1_000_000) return true;
                if (q.Minimum.y>=p.Maximum.y || p.Minimum.y>=q.Maximum.y) continue;
                second[0]=q.A;second[1]=q.B;second[2]=q.C;
                if (InteriorOverlap(first,second)) return true;
            }
        }
        return false;
    }

    private static bool InteriorOverlap(ReadOnlySpan<double2> a,ReadOnlySpan<double2> b)
    {
        for (var triangle=0;triangle<2;triangle++) {
            var points=triangle==0?a:b;
            for (var edge=0;edge<3;edge++) {
                var direction=points[(edge+1)%3]-points[edge]; var axis=new double2(-direction.y,direction.x);
                var lowA=double.PositiveInfinity; var highA=double.NegativeInfinity;
                var lowB=double.PositiveInfinity; var highB=double.NegativeInfinity;
                for (var corner=0;corner<3;corner++) {
                    var pa=math.dot(a[corner],axis); var pb=math.dot(b[corner],axis);
                    lowA=System.Math.Min(lowA,pa); highA=System.Math.Max(highA,pa);
                    lowB=System.Math.Min(lowB,pb); highB=System.Math.Max(highB,pb);
                }
                if (highA<=lowB || highB<=lowA) return false;
            }
        }
        return true;
    }

    private static bool Pack(List<Chart> charts,int resolution,int padding,CancellationToken token)
    {
        var order=Enumerable.Range(0,charts.Count).OrderByDescending(i=>charts[i].Size.y).ThenByDescending(i=>charts[i].Size.x).ThenBy(static i=>i).ToArray();
        bool Try(double density)
        {
            var x=0; var y=0; var shelf=0;
            foreach (var index in order) {
                token.ThrowIfCancellationRequested(); var chart=charts[index];
                var w=System.Math.Max(2,System.Math.Ceiling(chart.Size.x*density))+2*padding;
                var h=System.Math.Max(2,System.Math.Ceiling(chart.Size.y*density))+2*padding;
                if (w>resolution || h>resolution) return false;
                var width=(int)w; var height=(int)h;
                if (x+width>resolution) { x=0; y+=shelf; shelf=0; }
                if (y+height>resolution) return false;
                chart.Rectangle=new(x,y,width,height,chart.Material); x+=width; shelf=System.Math.Max(shelf,height);
            }
            return true;
        }
        if (!Try(0)) return false;
        var low=0d; var high=resolution/charts.Max(static c=>System.Math.Max(c.Size.x,c.Size.y));
        for (var iteration=0;iteration<24;iteration++) { var middle=(low+high)*.5; if (Try(middle)) low=middle; else high=middle; }
        if (!Try(low)) throw new InvalidOperationException("Bake layout packing failed.");
        return true;
    }
}
