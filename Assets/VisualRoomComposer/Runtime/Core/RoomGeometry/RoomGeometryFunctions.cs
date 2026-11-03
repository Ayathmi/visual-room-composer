using System;
using System.Collections.Generic;
using System.Linq;
using VisualRoomComposer.Core.Documents;

namespace VisualRoomComposer.Core.RoomGeometry
{
    /// <summary>Engine-independent quarter-grid geometry shared by tools, diagnostics and procedural preview.</summary>
    public static class RoomGeometryFunctions
    {
        public const int MaximumExpandedEdges = 65536;
        public static Double3 Rotate(Double3 value, int turns) => Matrix4d.RotationYQuarterTurns(NormalizeTurns(turns)).TransformVector(value);
        public static int NormalizeTurns(int turns) => (turns % 4 + 4) % 4;
        public static Double3 Scale(Double3 a, Double3 b) => new Double3(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
        public static Double3 Origin(RoomPlacement p) => new Double3(p.Position.X * .25 + p.PivotOffset.X, p.Position.Y * .25, p.Position.Z * .25 + p.PivotOffset.Z);
        public static Double3 Transform(Double3 value, RoomPlacement p) => Origin(p) + Rotate(Scale(value, p.Scale), p.QuarterTurns);
        public static double GroundOffset(LibraryDefinition d) => d.HasHitbox && d.PlacementPolicy == PlacementPolicy.Ground
            ? PlacementTransformResolver.GroundOffsetFromHitbox(d.Hitbox.Center, d.Hitbox.Size, true) : 0;
        public static PlacementTransform PlacementTransform(LibraryDefinition d, RoomPlacement p) => new PlacementTransform(
            p.Position, p.QuarterTurns, p.Scale, p.PivotOffset, GroundOffset(d), d.MeshTransform);
        public static GeometryBox TransformBox(Bounds3d box, RoomPlacement p)
        {
            var center = Transform(box.Center, p); var size = Scale(box.Size, p.Scale);
            if ((p.QuarterTurns & 1) != 0) size = new Double3(size.Z, size.Y, size.X);
            return new GeometryBox(center - size * .5, center + size * .5);
        }
        public static GeometryBox Occupancy(LibraryDefinition d, RoomPlacement p)
        {
            var bounds = d.Category == DefinitionCategory.Floor || d.Category == DefinitionCategory.Wall ? d.LocalBounds : d.Hitbox ?? d.LocalBounds;
            var box = TransformBox(bounds, p); var offset = new Double3(0, GroundOffset(d), 0);
            return new GeometryBox(box.Min + offset, box.Max + offset);
        }
        public static Double3 FootprintPivot(LibraryDefinition d, RoomPlacement p)
        {
            var center = Scale(d.LocalBounds.Center, p.Scale); var half = Scale(d.LocalBounds.Size, p.Scale) * .5;
            return new Double3((Math.Floor((center.X-half.X+GeometryBox.Epsilon)*4)+Math.Ceiling((center.X+half.X-GeometryBox.Epsilon)*4))*.125,
                center.Y, (Math.Floor((center.Z-half.Z+GeometryBox.Epsilon)*4)+Math.Ceiling((center.Z+half.Z-GeometryBox.Epsilon)*4))*.125);
        }
        public static Double3 WorldFootprintPivot(LibraryDefinition d, RoomPlacement p) => Origin(p) + Rotate(FootprintPivot(d,p), p.QuarterTurns);
        public static RoomPlacement RotateInPlace(LibraryDefinition d, RoomPlacement p, int turns)
        {
            int next = NormalizeTurns(p.QuarterTurns + NormalizeTurns(turns));
            var before = Rotate(FootprintPivot(d,p),p.QuarterTurns); var after = Rotate(FootprintPivot(d,p),next);
            var offset = p.PivotOffset + before - after;
            return p.WithTransform(p.Position,next,p.Scale,new Double3(offset.X,0,offset.Z));
        }
        public static bool GridAligned(double value) => Numeric.IsFinite(value) && Math.Abs(value) <= 65536 && Math.Abs(value-Math.Round(value)) < GeometryBox.Epsilon;
        public static bool ValidScale(LibraryDefinition d, RoomPlacement p)
        {
            var s=p.Scale; if (!s.IsFinite || s.X<=0 || s.Y<=0 || s.Z<=0) return false;
            if (d.Category!=DefinitionCategory.Wall) return s.Equals(Double3.One);
            var size=Scale(d.LocalBounds.Size,s); var center=Scale(d.LocalBounds.Center,s);
            if (!size.IsFinite || !center.IsFinite || Math.Max(size.X,Math.Max(size.Y,size.Z))>16384 ||
                Math.Max(Math.Abs(center.X),Math.Max(Math.Abs(center.Y),Math.Abs(center.Z)))>16384) return false;
            foreach (var b in d.WallBoundaries)
            {
                double length=Math.Abs((long)b.To.X-b.From.X)*s.X+Math.Abs((long)b.To.Z-b.From.Z)*s.Z;
                if (!GridAligned(b.From.X*s.X) || !GridAligned(b.To.X*s.X) || !GridAligned(b.From.Z*s.Z) || !GridAligned(b.To.Z*s.Z) ||
                    length<1-GeometryBox.Epsilon || length>MaximumExpandedEdges) return false;
            }
            return true;
        }
        public static IReadOnlyList<GridCell2> FloorCells(LibraryDefinition d, RoomPlacement p)
        {
            if (d.Category!=DefinitionCategory.Floor) return Array.Empty<GridCell2>();
            if (!new[]{15,7,9,3,1}.Contains(d.FloorMask)) throw new ArgumentException("Unsupported floor mask.");
            if (!GridAligned(p.PivotOffset.X*4) || !GridAligned(p.PivotOffset.Z*4)) throw new ArgumentException("Structural pivot is off-grid.");
            var result=new List<GridCell2>();
            for(int bit=0;bit<4;bit++) if((d.FloorMask&(1<<bit))!=0) for(int x=0;x<2;x++) for(int z=0;z<2;z++)
            {
                var c=Rotate(new Double3(bit%2*2+x+.5,0,bit/2*2+z+.5),p.QuarterTurns);
                result.Add(new GridCell2(checked(p.Position.X+(int)Math.Floor(c.X+p.PivotOffset.X*4)),checked(p.Position.Z+(int)Math.Floor(c.Z+p.PivotOffset.Z*4))));
            }
            return result.AsReadOnly();
        }
        public static IReadOnlyList<GridEdge> Edges(WallBoundary b, RoomPlacement p)
        {
            var a=Rotate(Scale(new Double3(b.From.X,0,b.From.Z),p.Scale),p.QuarterTurns)+p.PivotOffset*4;
            var v=Rotate(Scale(new Double3(b.To.X,0,b.To.Z),p.Scale),p.QuarterTurns)+p.PivotOffset*4;
            if(!GridAligned(a.X)||!GridAligned(a.Z)||!GridAligned(v.X)||!GridAligned(v.Z)) throw new ArgumentException("Wall endpoints must align to the quarter-meter grid.");
            int x1=checked((int)Math.Round(a.X)+p.Position.X),z1=checked((int)Math.Round(a.Z)+p.Position.Z),
                x2=checked((int)Math.Round(v.X)+p.Position.X),z2=checked((int)Math.Round(v.Z)+p.Position.Z);
            long count=Math.Abs((long)x2-x1)+Math.Abs((long)z2-z1);
            if(count==0||count>MaximumExpandedEdges||(x1!=x2&&z1!=z2))throw new ArgumentException("Invalid or excessive wall topology.");
            var edges=new List<GridEdge>((int)count);
            if(z1==z2)for(int x=Math.Min(x1,x2);x<Math.Max(x1,x2);x++)edges.Add(new GridEdge(x,z1,true));
            else for(int z=Math.Min(z1,z2);z<Math.Max(z1,z2);z++)edges.Add(new GridEdge(x1,z,false));
            return edges.AsReadOnly();
        }
        public static IReadOnlyList<GridEdge> BoundaryOfFloor(ISet<GridCell2> cells)
        {
            var edges=new List<GridEdge>();
            foreach(var c in cells)
            {
                if(!cells.Contains(new GridCell2(c.X,c.Z-1)))edges.Add(new GridEdge(c.X,c.Z,true));
                if(!cells.Contains(new GridCell2(c.X,c.Z+1)))edges.Add(new GridEdge(c.X,c.Z+1,true));
                if(!cells.Contains(new GridCell2(c.X-1,c.Z)))edges.Add(new GridEdge(c.X,c.Z,false));
                if(!cells.Contains(new GridCell2(c.X+1,c.Z)))edges.Add(new GridEdge(c.X+1,c.Z,false));
            }
            return Array.AsReadOnly(edges.OrderBy(e=>e.X).ThenBy(e=>e.Z).ThenBy(e=>e.AlongX).ToArray());
        }
        public static IReadOnlyList<GeometryBox> LocalSolidParts(LibraryDefinition d)
        {
            if(d.Category==DefinitionCategory.Floor)
            {
                var boxes=new List<GeometryBox>();
                for(int bit=0;bit<4;bit++)if((d.FloorMask&(1<<bit))!=0)
                    boxes.Add(new GeometryBox(new Double3(bit%2*.5,d.LocalBounds.Min.Y,bit/2*.5),new Double3(bit%2*.5+.5,0,bit/2*.5+.5)));
                return boxes.AsReadOnly();
            }
            if(d.Category!=DefinitionCategory.Wall) return !d.HasHitbox||d.OccupancyPolicy==OccupancyPolicy.None?Array.Empty<GeometryBox>():new[]{new GeometryBox(d.Hitbox.Min,d.Hitbox.Max)};
            var result=new List<GeometryBox>();
            foreach(var boundary in d.WallBoundaries)
            {
                bool alongX=boundary.From.Z==boundary.To.Z;
                if(!alongX&&boundary.From.X!=boundary.To.X)throw new ArgumentException("Wall boundary is not orthogonal.");
                double x1=boundary.From.X*.25,z1=boundary.From.Z*.25,x2=boundary.To.X*.25,z2=boundary.To.Z*.25;
                var strip=new GeometryBox(new Double3(Math.Min(x1,x2)-(alongX?0:.125),d.LocalBounds.Min.Y,Math.Min(z1,z2)-(alongX?.125:0)),
                    new Double3(Math.Max(x1,x2)+(alongX?0:.125),d.LocalBounds.Max.Y,Math.Max(z1,z2)+(alongX?.125:0)));
                if(boundary.Kind==WallBoundaryKind.Opening)
                {
                    var slot=d.OpeningSlots.SingleOrDefault(s=>s.Id==boundary.SlotId)??throw new ArgumentException("Opening slot is missing.");
                    result.AddRange(Subtract(strip,new GeometryBox(slot.Bounds.Min,slot.Bounds.Max)));
                }
                else result.Add(strip);
            }
            return result.AsReadOnly();
        }
        public static IReadOnlyList<GeometryBox> SolidParts(LibraryDefinition d, RoomPlacement p)
        {
            if(d.Category!=DefinitionCategory.Wall && d.Category!=DefinitionCategory.Floor)
                return !d.HasHitbox||d.OccupancyPolicy==OccupancyPolicy.None?Array.Empty<GeometryBox>():new[]{Occupancy(d,p)};
            return Array.AsReadOnly(LocalSolidParts(d).Select(b=>TransformBox(new Bounds3d(b.Center,b.Size),p)).ToArray());
        }
        public static IReadOnlyList<GeometryBox> Subtract(GeometryBox box, GeometryBox cut)
        {
            if(!box.Intersects(cut))return new[]{box};
            var a=new Double3(Math.Max(box.Min.X,cut.Min.X),Math.Max(box.Min.Y,cut.Min.Y),Math.Max(box.Min.Z,cut.Min.Z));
            var b=new Double3(Math.Min(box.Max.X,cut.Max.X),Math.Min(box.Max.Y,cut.Max.Y),Math.Min(box.Max.Z,cut.Max.Z));
            var parts=new List<GeometryBox>();
            Add(box.Min,new Double3(a.X,box.Max.Y,box.Max.Z));Add(new Double3(b.X,box.Min.Y,box.Min.Z),box.Max);
            Add(new Double3(a.X,box.Min.Y,box.Min.Z),new Double3(b.X,a.Y,box.Max.Z));Add(new Double3(a.X,b.Y,box.Min.Z),new Double3(b.X,box.Max.Y,box.Max.Z));
            Add(new Double3(a.X,a.Y,box.Min.Z),new Double3(b.X,b.Y,a.Z));Add(new Double3(a.X,a.Y,b.Z),new Double3(b.X,b.Y,box.Max.Z));
            return parts.AsReadOnly();
            void Add(Double3 min,Double3 max){var s=max-min;if(s.X>GeometryBox.Epsilon&&s.Y>GeometryBox.Epsilon&&s.Z>GeometryBox.Epsilon)parts.Add(new GeometryBox(min,max));}
        }
    }
}
