using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualRoomComposer.Core.Documents;

namespace VisualRoomComposer.Core.RoomGeometry
{
    /// <summary>Copied definition-space triangles for structural validation.</summary>
    public sealed class StructureTriangleData
    {
        public IReadOnlyList<Double3> Vertices {get;}public IReadOnlyList<int> Triangles {get;}
        public StructureTriangleData(Double3[] vertices,int[] triangles){Vertices=Array.AsReadOnly((Double3[])vertices.Clone());Triangles=Array.AsReadOnly((int[])triangles.Clone());}
    }
    /// <summary>Checks triangle support against structural metadata; sampling is not a manifold proof.</summary>
    public static class StructureGeometryValidator
    {
        private const double Tolerance=.002;
        public static IReadOnlyList<RoomGeometryIssue> Validate(LibraryDefinition tile,IReadOnlyList<StructureTriangleData> meshes,CancellationToken cancellation=default)
        {
            if(tile.Category!=DefinitionCategory.Floor && tile.Category!=DefinitionCategory.Wall)return Array.Empty<RoomGeometryIssue>();
            if(meshes.Count==0)return Failure("structure.mesh-empty");
            long triangles=0;double minY=double.PositiveInfinity,maxY=double.NegativeInfinity;
            foreach(var mesh in meshes)
            {
                if(mesh.Triangles.Count%3!=0)return Failure("structure.mesh-indices");triangles+=mesh.Triangles.Count/3;
                foreach(var point in mesh.Vertices)
                {
                    if(!point.IsFinite)return Failure("structure.mesh-finite");minY=Math.Min(minY,point.Y);maxY=Math.Max(maxY,point.Y);
                }
                if(mesh.Triangles.Any(i=>i<0 || i>=mesh.Vertices.Count))return Failure("structure.mesh-indices");
            }
            if(triangles==0)return Failure("structure.mesh-empty");
            if(triangles>100000)return Failure("structure.validation-budget");
            if(tile.Category==DefinitionCategory.Floor)
            {
                if(Math.Abs(maxY)>Tolerance || minY>Tolerance)return Failure("structure.floor-height");
                return Floor(tile,meshes,cancellation);
            }
            if(Math.Abs(minY-tile.LocalBounds.Min.Y)>Tolerance || Math.Abs(maxY-tile.LocalBounds.Max.Y)>Tolerance)return Failure("structure.wall-height");
            return Wall(tile,meshes,cancellation);
        }
        private static IReadOnlyList<RoomGeometryIssue> Floor(LibraryDefinition tile,IReadOnlyList<StructureTriangleData> meshes,CancellationToken cancellation)
        {
            var scratchA=new Double3[12];var scratchB=new Double3[12];ulong required=0,covered=0;var area=new double[4];
            for(int z=0;z<8;z++)for(int x=0;x<8;x++)if((tile.FloorMask&(1<<(x/4+z/4*2)))!=0)required|=1UL<<(z*8+x);
            foreach(var triangle in Enumerate(meshes,cancellation))
            {
                double total=Math.Abs(Cross2(triangle.A,triangle.B,triangle.C))*.5;
                if(total<1e-12)continue;double accepted=0;
                bool top=Math.Abs(triangle.A.Y)<Tolerance && Math.Abs(triangle.B.Y)<Tolerance && Math.Abs(triangle.C.Y)<Tolerance;
                for(int bit=0;bit<4;bit++)if((tile.FloorMask&(1<<bit))!=0)
                {
                    double part=ClipArea(triangle,bit%2*.5,bit/2*.5,scratchA,scratchB);accepted+=part;if(top)area[bit]+=part;
                }
                if(total-accepted>Math.Max(1e-8,total*1e-5))return Failure("structure.floor-mask");
                if(top)
                {
                    int minX=Math.Max(0,(int)Math.Floor(Math.Min(triangle.A.X,Math.Min(triangle.B.X,triangle.C.X))*8));
                    int maxX=Math.Min(7,(int)Math.Floor(Math.Max(triangle.A.X,Math.Max(triangle.B.X,triangle.C.X))*8));
                    int minZ=Math.Max(0,(int)Math.Floor(Math.Min(triangle.A.Z,Math.Min(triangle.B.Z,triangle.C.Z))*8));
                    int maxZ=Math.Min(7,(int)Math.Floor(Math.Max(triangle.A.Z,Math.Max(triangle.B.Z,triangle.C.Z))*8));
                    for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)
                        if(Contains2(triangle,new Double3((x+.5)/8,0,(z+.5)/8)))covered|=1UL<<(z*8+x);
                }
            }
            if((covered&required)!=required || Enumerable.Range(0,4).Any(i=>(tile.FloorMask&(1<<i))!=0 && area[i]<.25-1e-5))return Failure("structure.floor-support");
            return Array.Empty<RoomGeometryIssue>();
        }
        private static IReadOnlyList<RoomGeometryIssue> Wall(LibraryDefinition tile,IReadOnlyList<StructureTriangleData> meshes,CancellationToken cancellation)
        {
            if(tile.WallBoundaries.Count>4096 || tile.OpeningSlots.Count>4096)return Failure("structure.validation-budget");
            var parts=RoomGeometryFunctions.LocalSolidParts(tile);
            long rawProbes=tile.WallBoundaries.Sum(b=>(Math.Abs((long)b.To.X-b.From.X)+Math.Abs((long)b.To.Z-b.From.Z))*3);
            if(rawProbes>131072 || rawProbes*Math.Max(1,parts.Count)>4000000)return Failure("structure.validation-budget");
            var slots=new List<OpeningMeshBox>();
            foreach(var slot in tile.OpeningSlots)
            {
                var boundaries=tile.WallBoundaries.Where(b=>b.Kind==WallBoundaryKind.Opening && b.SlotId==slot.Id).ToArray();
                if(boundaries.Length==0)return Failure("structure.opening-unbound");
                foreach(var boundary in boundaries)slots.Add(new OpeningMeshBox{Box=new GeometryBox(Sub(slot.Bounds.Center,Scale(slot.Bounds.Size,.5)),Add(slot.Bounds.Center,Scale(slot.Bounds.Size,.5))),AlongX=boundary.From.Z==boundary.To.Z});
            }
            var probes=new List<WallProbe>();
            foreach(var boundary in tile.WallBoundaries)
            {
                int length=Math.Abs(boundary.To.X-boundary.From.X)+Math.Abs(boundary.To.Z-boundary.From.Z);
                if(length>65536 || probes.Count+(long)length*3>131072)return Failure("structure.validation-budget");
                bool alongX=boundary.From.Z==boundary.To.Z;
                for(int i=0;i<length;i++)foreach(double height in new[]{tile.LocalBounds.Min.Y+tile.LocalBounds.Size.Y/12,tile.LocalBounds.Center.Y,tile.LocalBounds.Max.Y-tile.LocalBounds.Size.Y/12})
                {
                    double t=(i+.5)/length;var point=new Double3((boundary.From.X+(boundary.To.X-boundary.From.X)*t)*.25,height,(boundary.From.Z+(boundary.To.Z-boundary.From.Z)*t)*.25);
                    if(parts.Any(p=>Inside(p,point,0)))probes.Add(new WallProbe{Point=point,Direction=alongX?new Double3(0,0,1):new Double3(1,0,0)});
                }
            }
            long work=meshes.Sum(m=>(long)m.Triangles.Count/3)*(slots.Count+parts.Count*4L+probes.Count);
            if(work>20000000)return Failure("structure.validation-budget");
            foreach(var triangle in Enumerate(meshes,cancellation))
            {
                foreach(var slot in slots)if(TriangleBox(triangle,slot.Box,slot.AlongX))return Failure("structure.opening-filled");
                if(!InsideAny(triangle.A) || !InsideAny(triangle.B) || !InsideAny(triangle.C) || !InsideAny(Scale(Add(Add(triangle.A,triangle.B),triangle.C),1d/3)))return Failure("structure.wall-bounds");
                for(int i=0;i<probes.Count;i++)
                {
                    var probe=probes[i];if(probe.Hit || !Nearby(triangle,probe.Point))continue;
                    if(Ray(triangle,Sub(probe.Point,Scale(probe.Direction,.3)),probe.Direction,.6)){probe.Hit=true;probes[i]=probe;}
                }
            }
            return probes.Any(p=>!p.Hit)?Failure("structure.wall-support"):Array.Empty<RoomGeometryIssue>();
            bool InsideAny(Double3 point)=>parts.Any(part=>Inside(part,point,Tolerance));
        }
        private static IEnumerable<Triangle> Enumerate(IReadOnlyList<StructureTriangleData> meshes,CancellationToken cancellation)
        {
            foreach(var mesh in meshes)for(int i=0;i<mesh.Triangles.Count;i+=3)
            {
                if((i&255)==0)cancellation.ThrowIfCancellationRequested();
                yield return new Triangle(mesh.Vertices[mesh.Triangles[i]],mesh.Vertices[mesh.Triangles[i+1]],mesh.Vertices[mesh.Triangles[i+2]]);
            }
        }
        private static double ClipArea(Triangle triangle,double x,double z,Double3[] a,Double3[] b)
        {
            a[0]=triangle.A;a[1]=triangle.B;a[2]=triangle.C;int count=3;
            for(int edge=0;edge<4;edge++)
            {
                int next=0;double limit=edge==0?x:edge==1?x+.5:edge==2?z:z+.5;
                for(int i=0;i<count;i++)
                {
                    var p=a[i];var q=a[(i+1)%count];double pv=edge<2?p.X:p.Z,qv=edge<2?q.X:q.Z;
                    bool pin=(edge%2==0?pv>=limit:pv<=limit),qin=(edge%2==0?qv>=limit:qv<=limit);
                    if(pin)b[next++]=p;
                    if(pin!=qin)b[next++]=Add(p,Scale(Sub(q,p),(limit-pv)/(qv-pv)));
                }
                count=next;var swap=a;a=b;b=swap;if(count==0)return 0;
            }
            double area=0;for(int i=0;i<count;i++){var p=a[i];var q=a[(i+1)%count];area+=p.X*q.Z-q.X*p.Z;}return Math.Abs(area)*.5;
        }
        private static bool TriangleBox(Triangle triangle,GeometryBox box,bool wallAlongX)
        {
            var center=Scale(Add(box.Min,box.Max),.5);var half=Sub(Scale(Sub(box.Max,box.Min),.5),new Double3(Tolerance,Tolerance,Tolerance));
            // 洞口宽/高排除门框接触面，但厚度轴包含墙正反面，否则封住入口的面会被误判为洞外。
            half=wallAlongX?new Double3(half.X,half.Y,half.Z+Tolerance*2):new Double3(half.X+Tolerance*2,half.Y,half.Z);
            if(half.X<=0 || half.Y<=0 || half.Z<=0)return false;
            var a=Sub(triangle.A,center);var b=Sub(triangle.B,center);var c=Sub(triangle.C,center);
            var e0=Sub(b,a);var e1=Sub(c,b);var e2=Sub(a,c);
            if(Separated(new Double3(1,0,0)) || Separated(new Double3(0,1,0)) || Separated(new Double3(0,0,1)) || Separated(Cross(e0,e1)))return false;
            foreach(var edge in new[]{e0,e1,e2})
                if(Separated(new Double3(0,edge.Z,-edge.Y)) || Separated(new Double3(-edge.Z,0,edge.X)) || Separated(new Double3(edge.Y,-edge.X,0)))return false;
            return true;
            bool Separated(Double3 axis)
            {
                if(Dot(axis,axis)<1e-20)return false;double pa=Dot(a,axis),pb=Dot(b,axis),pc=Dot(c,axis);
                double radius=half.X*Math.Abs(axis.X)+half.Y*Math.Abs(axis.Y)+half.Z*Math.Abs(axis.Z);
                return Math.Min(pa,Math.Min(pb,pc))>radius || Math.Max(pa,Math.Max(pb,pc))<-radius;
            }
        }
        private static bool Ray(Triangle triangle,Double3 origin,Double3 direction,double length)
        {
            var e1=Sub(triangle.B,triangle.A);var e2=Sub(triangle.C,triangle.A);var h=Cross(direction,e2);double det=Dot(e1,h);if(Math.Abs(det)<1e-10)return false;
            var s=Sub(origin,triangle.A);double u=Dot(s,h)/det;if(u<0 || u>1)return false;var q=Cross(s,e1);double v=Dot(direction,q)/det;if(v<0 || u+v>1)return false;
            double distance=Dot(e2,q)/det;return distance>=0 && distance<=length;
        }
        private static bool Nearby(Triangle t,Double3 p)=>p.X>=Math.Min(t.A.X,Math.Min(t.B.X,t.C.X))-.3 && p.X<=Math.Max(t.A.X,Math.Max(t.B.X,t.C.X))+.3 &&
            p.Y>=Math.Min(t.A.Y,Math.Min(t.B.Y,t.C.Y))-Tolerance && p.Y<=Math.Max(t.A.Y,Math.Max(t.B.Y,t.C.Y))+Tolerance &&
            p.Z>=Math.Min(t.A.Z,Math.Min(t.B.Z,t.C.Z))-.3 && p.Z<=Math.Max(t.A.Z,Math.Max(t.B.Z,t.C.Z))+.3;
        private static bool Inside(GeometryBox box,Double3 p,double epsilon)=>p.X>=box.Min.X-epsilon && p.X<=box.Max.X+epsilon && p.Y>=box.Min.Y-epsilon && p.Y<=box.Max.Y+epsilon && p.Z>=box.Min.Z-epsilon && p.Z<=box.Max.Z+epsilon;
        private static bool Contains2(Triangle t,Double3 p){double a=Cross2(t.A,t.B,p),b=Cross2(t.B,t.C,p),c=Cross2(t.C,t.A,p);return a>=-1e-9 && b>=-1e-9 && c>=-1e-9 || a<=1e-9 && b<=1e-9 && c<=1e-9;}
        private static double Cross2(Double3 a,Double3 b,Double3 c)=>(b.X-a.X)*(c.Z-a.Z)-(b.Z-a.Z)*(c.X-a.X);
        private static Double3 Add(Double3 a,Double3 b)=>new Double3(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        private static Double3 Sub(Double3 a,Double3 b)=>new Double3(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        private static Double3 Scale(Double3 a,double scale)=>new Double3(a.X*scale,a.Y*scale,a.Z*scale);
        private static double Dot(Double3 a,Double3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
        private static Double3 Cross(Double3 a,Double3 b)=>new Double3(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
        private static IReadOnlyList<RoomGeometryIssue> Failure(string code)=>new[]{new RoomGeometryIssue(code,kind:RoomIssueKind.BlockingDataError)};
        private readonly struct Triangle{public readonly Double3 A,B,C;public Triangle(Double3 a,Double3 b,Double3 c){A=a;B=b;C=c;}}
        private struct WallProbe{public Double3 Point,Direction;public bool Hit;}
        private struct OpeningMeshBox{public GeometryBox Box;public bool AlongX;}
    }
}
