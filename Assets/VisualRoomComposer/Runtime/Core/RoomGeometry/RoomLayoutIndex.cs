using System;
using System.Collections.Generic;
using System.Linq;
using VisualRoomComposer.Core.Documents;

namespace VisualRoomComposer.Core.RoomGeometry
{
    /// <summary>A state-bound immutable projection; callers replace it only after a committed document change.</summary>
    public sealed class RoomLayoutIndex
    {
        private sealed class Entry
        {
            internal RoomPlacement Placement; internal LibraryDefinition Definition; internal GeometryBox Bounds;
            internal IReadOnlyList<GeometryBox> Parts;
        }
        private readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>(StringComparer.Ordinal);
        private readonly HashSet<GridCell2> floors=new HashSet<GridCell2>();
        private readonly Dictionary<GridCell2,string> floorOwners=new Dictionary<GridCell2,string>();
        private readonly Dictionary<GridEdge,string> walls=new Dictionary<GridEdge,string>();
        private readonly Dictionary<GridCell2,List<GridEdge>> floorBoundaryBuckets=new Dictionary<GridCell2,List<GridEdge>>();
        private readonly Dictionary<(int,int),HashSet<string>> selectionBuckets=new Dictionary<(int,int),HashSet<string>>();
        private readonly HashSet<string> broadSelection=new HashSet<string>(StringComparer.Ordinal);
        private readonly List<RoomGeometryIssue> buildIssues=new List<RoomGeometryIssue>();
        private readonly RoomSpatialIndex spatial;
        public IReadOnlyList<GridCell2> FloorCells { get; }
        public IReadOnlyList<GridEdge> WallEdges { get; }
        public IReadOnlyList<GridEdge> FloorBoundaryEdges { get; }
        public RoomDocument Document { get; }
        public RoomLayoutIndex(RoomDocument document, Func<PinnedReference,LibraryDefinition> resolve)
        {
            Document=document??throw new ArgumentNullException(nameof(document));
            if(resolve==null)throw new ArgumentNullException(nameof(resolve));
            DocumentValidation.Validate(document);
            if(document.Placements.Count>100000)throw new InvalidOperationException("Layout placement budget exceeded.");
            long edgeCount=0;
            foreach(var p in document.Placements)
            {
                var d=resolve(p.Definition);
                if(d==null || !d.Reference.Equals(p.Definition)){buildIssues.Add(new RoomGeometryIssue("geometry.missing-definition",p.Id,kind:RoomIssueKind.BlockingDataError));continue;}
                if(!RoomGeometryFunctions.ValidScale(d,p) || ((d.Category==DefinitionCategory.Wall||d.Category==DefinitionCategory.Floor)&&
                    (!RoomGeometryFunctions.GridAligned(p.PivotOffset.X*4)||!RoomGeometryFunctions.GridAligned(p.PivotOffset.Z*4))))
                {buildIssues.Add(new RoomGeometryIssue("geometry.invalid-scale-grid",p.Id,kind:RoomIssueKind.BlockingDataError));continue;}
                var e=new Entry{Placement=p,Definition=d,Bounds=RoomGeometryFunctions.Occupancy(d,p),Parts=RoomGeometryFunctions.SolidParts(d,p)};
                RequireSpatialBounds(e.Bounds);
                foreach(var part in e.Parts)RequireSpatialBounds(part);
                entries.Add(p.Id,e);
                int bx1=checked((int)Math.Floor(e.Bounds.Min.X/4)),bz1=checked((int)Math.Floor(e.Bounds.Min.Z/4)),
                    bx2=checked((int)Math.Floor(e.Bounds.Max.X/4)),bz2=checked((int)Math.Floor(e.Bounds.Max.Z/4));
                if(((long)bx2-bx1+1)*((long)bz2-bz1+1)>4096)broadSelection.Add(p.Id);
                else for(int x=bx1;x<=bx2;x++)for(int z=bz1;z<=bz2;z++)
                {if(!selectionBuckets.TryGetValue((x,z),out var bucket))selectionBuckets.Add((x,z),bucket=new HashSet<string>(StringComparer.Ordinal));bucket.Add(p.Id);}
                if(d.Category==DefinitionCategory.Floor)
                {
                    foreach(var cell in RoomGeometryFunctions.FloorCells(d,p))
                        if(!floors.Add(cell))buildIssues.Add(new RoomGeometryIssue("geometry.floor-overlap",p.Id,floorOwners[cell]));else floorOwners.Add(cell,p.Id);
                }
                if(d.Category==DefinitionCategory.Wall)foreach(var b in d.WallBoundaries)
                {
                    var edges=RoomGeometryFunctions.Edges(b,p);edgeCount+=edges.Count;
                    if(edgeCount>2000000)throw new InvalidOperationException("Layout edge budget exceeded.");
                    foreach(var edge in edges)if(walls.TryGetValue(edge,out var previous))buildIssues.Add(new RoomGeometryIssue("geometry.wall-overlap",p.Id,previous));else walls.Add(edge,p.Id);
                }
            }
            FloorCells=Array.AsReadOnly(floors.OrderBy(c=>c.X).ThenBy(c=>c.Z).ToArray());
            FloorBoundaryEdges=RoomGeometryFunctions.BoundaryOfFloor(floors);
            foreach(var edge in FloorBoundaryEdges)
            {if(!floorBoundaryBuckets.TryGetValue(edge.Start,out var bucket))floorBoundaryBuckets.Add(edge.Start,bucket=new List<GridEdge>());bucket.Add(edge);}
            WallEdges=Array.AsReadOnly(walls.Keys.OrderBy(e=>e.X).ThenBy(e=>e.Z).ThenBy(e=>e.AlongX).ToArray());
            spatial=new RoomSpatialIndex(entries.Values.Where(e=>e.Definition.Category!=DefinitionCategory.Floor)
                .Select(e=>new KeyValuePair<string,IReadOnlyList<GeometryBox>>(e.Placement.Id,e.Parts)));
        }
        public IReadOnlyList<RoomGeometryIssue> Diagnose(bool includeClosure=true)
        {
            var issues=new List<RoomGeometryIssue>(buildIssues);var pairs=new HashSet<string>(StringComparer.Ordinal);
            foreach(var e in entries.Values)
            {
                var p=e.Placement;var d=e.Definition;
                bool floor=d.Category==DefinitionCategory.Floor,wall=d.Category==DefinitionCategory.Wall;
                if((floor||wall)&&p.Position.Y!=0)issues.Add(new RoomGeometryIssue("geometry.structure-height",p.Id));
                if(floor)continue;
                bool attachment=d.PlacementPolicy==PlacementPolicy.Wall && ValidAttachment(p.Id);
                if(wall)
                {
                    if(d.WallBoundaries.SelectMany(b=>RoomGeometryFunctions.Edges(b,p)).Any(edge=>!floors.Contains(edge.SideA)&&!floors.Contains(edge.SideB)))
                        issues.Add(new RoomGeometryIssue("geometry.wall-support",p.Id));
                }
                else if(d.PlacementPolicy==PlacementPolicy.Wall)
                {if(!attachment)issues.Add(new RoomGeometryIssue("geometry.wall-attachment",p.Id,p.Attachment?.HostPlacementId));}
                else
                {
                    if(d.HasHitbox && d.PlacementPolicy==PlacementPolicy.Ground && Math.Abs(e.Bounds.Min.Y)>GeometryBox.Epsilon)
                        issues.Add(new RoomGeometryIssue("geometry.ground-height",p.Id));
                    if(e.Bounds.Min.Y< -GeometryBox.Epsilon)issues.Add(new RoomGeometryIssue("geometry.below-floor",p.Id));
                    if(d.HasHitbox?!FootprintSupported(e.Bounds):!floors.Contains(new GridCell2((int)Math.Floor(RoomGeometryFunctions.Origin(p).X*4),(int)Math.Floor(RoomGeometryFunctions.Origin(p).Z*4))))
                        issues.Add(new RoomGeometryIssue("geometry.floor-support",p.Id));
                }
                foreach(var part in e.Parts)foreach(var id in spatial.Query(part))
                {
                    if(id==p.Id)continue;var other=entries[id];
                    if(wall&&other.Definition.Category==DefinitionCategory.Wall)continue;
                    if(attachment&&id==p.Attachment.HostPlacementId)continue;
                    if(other.Placement.Attachment?.HostPlacementId==p.Id&&ValidAttachment(id))continue;
                    string key=string.CompareOrdinal(p.Id,id)<0?p.Id+"\0"+id:id+"\0"+p.Id;
                    if(pairs.Add(key))issues.Add(new RoomGeometryIssue("geometry.intersection",p.Id,id));
                }
            }
            if(includeClosure)
            {
                if(floors.Count==0)issues.Add(new RoomGeometryIssue("geometry.no-floor"));
                foreach(var edge in FloorBoundaryEdges)if(!walls.ContainsKey(edge))
                {issues.Add(new RoomGeometryIssue("geometry.open-boundary",floorOwners[floors.Contains(edge.SideA)?edge.SideA:edge.SideB]));break;}
                var degrees=VertexDegrees();if(degrees.Any(v=>v.Value==1))issues.Add(new RoomGeometryIssue("geometry.dangling-wall"));
            }
            return issues.AsReadOnly();
        }
        public IReadOnlyDictionary<GridCell2,int> VertexDegrees()
        {
            var counts=new Dictionary<GridCell2,int>();foreach(var edge in walls.Keys){Add(edge.Start);Add(edge.End);}
            return new System.Collections.ObjectModel.ReadOnlyDictionary<GridCell2,int>(counts);
            void Add(GridCell2 p){counts.TryGetValue(p,out int n);counts[p]=n+1;}
        }
        public bool FootprintSupported(GeometryBox box)
        {
            int x1=checked((int)Math.Floor((box.Min.X+GeometryBox.Epsilon)*4)),x2=checked((int)Math.Ceiling((box.Max.X-GeometryBox.Epsilon)*4)),
                z1=checked((int)Math.Floor((box.Min.Z+GeometryBox.Epsilon)*4)),z2=checked((int)Math.Ceiling((box.Max.Z-GeometryBox.Epsilon)*4));
            if(((long)x2-x1)*((long)z2-z1)>2000000)return false;
            for(int x=x1;x<x2;x++)for(int z=z1;z<z2;z++)if(!floors.Contains(new GridCell2(x,z)))return false;
            return x2>x1&&z2>z1;
        }
        public bool Supports(GridEdge edge)=>floors.Contains(edge.SideA)||floors.Contains(edge.SideB);
        public IReadOnlyList<GridEdge> NearbyFloorBoundary(int x,int z,int radius=4)
        {
            if(radius<0||radius>16)throw new ArgumentOutOfRangeException(nameof(radius));var result=new List<GridEdge>();
            for(int dx=-radius;dx<=radius;dx++)for(int dz=-radius;dz<=radius;dz++)
                if(floorBoundaryBuckets.TryGetValue(new GridCell2(checked(x+dx),checked(z+dz)),out var bucket))result.AddRange(bucket);
            return result.AsReadOnly();
        }
        public bool ValidAttachment(string id)
        {
            if(!entries.TryGetValue(id,out var e)||e.Placement.Attachment==null)return false;
            var a=e.Placement.Attachment;
            if(!entries.TryGetValue(a.HostPlacementId,out var host)||host.Definition.Category!=DefinitionCategory.Wall)return false;
            if(!string.IsNullOrEmpty(a.SlotId))
            {
                var slot=host.Definition.OpeningSlots.SingleOrDefault(s=>s.Id==a.SlotId);
                return slot!=null && (e.Definition.OccupancyPolicy!=OccupancyPolicy.Passage||slot.Kind==OpeningKind.Door) && RoomGeometryFunctions.TransformBox(slot.Bounds,host.Placement).Contains(e.Bounds);
            }
            if(e.Definition.OccupancyPolicy==OccupancyPolicy.Passage)return false;
            foreach(var b in host.Definition.WallBoundaries.Where(b=>b.Kind==WallBoundaryKind.Solid))
            {
                var p=RoomGeometryFunctions.Transform(new Double3(b.From.X*.25,0,b.From.Z*.25),host.Placement);
                var q=RoomGeometryFunctions.Transform(new Double3(b.To.X*.25,0,b.To.Z*.25),host.Placement);
                double half=.125*(b.From.Z==b.To.Z?host.Placement.Scale.Z:host.Placement.Scale.X);
                var box=e.Bounds;if(box.Min.Y<host.Bounds.Min.Y-GeometryBox.Epsilon||box.Max.Y>host.Bounds.Max.Y+GeometryBox.Epsilon)continue;
                if(Math.Abs(p.Z-q.Z)<GeometryBox.Epsilon)
                {
                    if(box.Min.X>=Math.Min(p.X,q.X)-GeometryBox.Epsilon&&box.Max.X<=Math.Max(p.X,q.X)+GeometryBox.Epsilon&&
                        (Math.Abs(box.Min.Z-p.Z-half)<GeometryBox.Epsilon||Math.Abs(box.Max.Z-p.Z+half)<GeometryBox.Epsilon))return true;
                }
                else if(box.Min.Z>=Math.Min(p.Z,q.Z)-GeometryBox.Epsilon&&box.Max.Z<=Math.Max(p.Z,q.Z)+GeometryBox.Epsilon&&
                    (Math.Abs(box.Min.X-p.X-half)<GeometryBox.Epsilon||Math.Abs(box.Max.X-p.X+half)<GeometryBox.Epsilon))return true;
            }
            return false;
        }
        public bool TryBounds(string id,out GeometryBox box)
        {if(entries.TryGetValue(id,out var e)){box=e.Bounds;return true;}box=default;return false;}
        private static void RequireSpatialBounds(GeometryBox bounds)
        {
            const double maximum=1000000;
            if(!bounds.Min.IsFinite||!bounds.Max.IsFinite||
                Math.Abs(bounds.Min.X)>maximum||Math.Abs(bounds.Min.Y)>maximum||Math.Abs(bounds.Min.Z)>maximum||
                Math.Abs(bounds.Max.X)>maximum||Math.Abs(bounds.Max.Y)>maximum||Math.Abs(bounds.Max.Z)>maximum)
                throw new ArgumentOutOfRangeException(nameof(bounds),"Layout bounds exceed the finite world-coordinate budget.");
        }
        public IReadOnlyList<string> Select(GridRect region,string roomId=null)
        {
            var shared=roomId==null?null:new HashSet<string>(Document.Rooms.Single(r=>r.Id==roomId).SharedStructurePlacementIds,StringComparer.Ordinal);
            int x1=(int)Math.Floor(region.MinX/16d),z1=(int)Math.Floor(region.MinZ/16d),x2=(int)Math.Floor((region.MaxX-1)/16d),z2=(int)Math.Floor((region.MaxZ-1)/16d);
            var candidates=new HashSet<string>(broadSelection,StringComparer.Ordinal);
            if(((long)x2-x1+1)*((long)z2-z1+1)>4096)candidates.UnionWith(entries.Keys);
            else for(int x=x1;x<=x2;x++)for(int z=z1;z<=z2;z++)if(selectionBuckets.TryGetValue((x,z),out var bucket))candidates.UnionWith(bucket);
            var selected=new HashSet<string>(candidates.Select(id=>entries[id]).Where(e=>roomId==null||e.Placement.RoomId==roomId||shared.Contains(e.Placement.Id)).Where(e=>
                e.Definition.Category==DefinitionCategory.Light&&!e.Definition.HasHitbox?
                region.Contains((int)Math.Floor(RoomGeometryFunctions.Origin(e.Placement).X*4),(int)Math.Floor(RoomGeometryFunctions.Origin(e.Placement).Z*4)):e.Bounds.IntersectsProjection(region))
                .Select(e=>e.Placement.Id),StringComparer.Ordinal);
            bool changed;do{changed=false;foreach(var e in entries.Values)if(e.Placement.Attachment!=null&&selected.Contains(e.Placement.Attachment.HostPlacementId))changed|=selected.Add(e.Placement.Id);}while(changed);
            return Array.AsReadOnly(selected.OrderBy(id=>id,StringComparer.Ordinal).ToArray());
        }
    }
}
