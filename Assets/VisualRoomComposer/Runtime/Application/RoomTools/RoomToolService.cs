using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using VisualRoomComposer.Core;
using VisualRoomComposer.Core.Documents;
using VisualRoomComposer.Core.RoomGeometry;

namespace VisualRoomComposer.Application.RoomTools
{
    /// <summary>Produces complete immutable candidates. A caller commits one candidate through RoomSession.Apply per gesture.</summary>
    public sealed class RoomToolService
    {
        public const int MaximumTransactionPlacements=10000;
        private readonly Func<PinnedReference,LibraryDefinition> resolve;
        private readonly Func<string> newId;
        private RoomLayoutIndex cached;
        public RoomToolService(Func<PinnedReference,LibraryDefinition> resolve,Func<string> newId=null)
        {this.resolve=resolve??throw new ArgumentNullException(nameof(resolve));this.newId=newId??(()=>Guid.NewGuid().ToString("N"));}
        public LibraryDefinition Resolve(PinnedReference reference)
        {var d=resolve(reference);return d!=null&&d.Reference.Equals(reference)?d:throw new InvalidOperationException("Missing fixed definition: "+reference?.Key);}
        public RoomLayoutIndex Index(RoomDocument document)
        {if(cached==null||!ReferenceEquals(cached.Document,document))cached=new RoomLayoutIndex(document,Resolve);return cached;}
        public RoomPlacement Prepare(PinnedReference definition,GridPosition position,int quarterTurns=0,string roomId=null,string groupId=null,PlacementAttachment attachment=null)
        {
            var d=Resolve(definition);var p=new RoomPlacement(newId(),definition,position,roomId:roomId,groupId:groupId,attachment:attachment);
            return RoomGeometryFunctions.RotateInPlace(d,p,quarterTurns);
        }
        public RoomPlacement PrepareAttachment(RoomDocument document,PinnedReference definition,string hostId,string slotId,Double3 localOffset,int localQuarterTurns=0)
        {
            var d=Resolve(definition);if(d.PlacementPolicy!=PlacementPolicy.Wall)throw new ArgumentException("The selected definition does not use wall attachment placement.");
            var host=document.Placements.Single(p=>p.Id==hostId);var hostDefinition=Resolve(host.Definition);
            if(hostDefinition.Category!=DefinitionCategory.Wall||!localOffset.IsFinite)throw new ArgumentException("Attachment host or local offset is invalid.");
            if(slotId!=null&&!hostDefinition.OpeningSlots.Any(s=>s.Id==slotId))throw new ArgumentException("The fixed host does not contain this slot.");
            var world=RoomGeometryFunctions.Transform(localOffset,host);double gy=world.Y*4;
            if(Math.Abs(gy-Math.Round(gy))>GeometryBox.Epsilon)throw new ArgumentException("Attachment height cannot be represented on the quarter-meter grid.");
            int x=checked((int)Math.Floor(world.X*4)),y=checked((int)Math.Round(gy)),z=checked((int)Math.Floor(world.Z*4));
            int yaw=RoomGeometryFunctions.NormalizeTurns(localQuarterTurns);
            return new RoomPlacement(newId(),definition,new GridPosition(x,y,z),RoomGeometryFunctions.NormalizeTurns(host.QuarterTurns+yaw),pivotOffset:new Double3(world.X-x*.25,0,world.Z-z*.25),
                roomId:host.RoomId,attachment:new PlacementAttachment(hostId,slotId,localOffset,yaw));
        }
        public IReadOnlyList<string> Select(RoomDocument document,GridRect region,string roomId=null)=>Index(document).Select(region,roomId);
        public RoomDocument Place(RoomDocument document,IReadOnlyList<RoomPlacement> candidates,CancellationToken cancellation=default)
        {
            cancellation.ThrowIfCancellationRequested();if(candidates.Count>MaximumTransactionPlacements)throw new InvalidOperationException("Placement transaction budget exceeded.");
            var keys=new HashSet<string>(document.Placements.Select(BrushKey),StringComparer.Ordinal);
            var ids=new HashSet<string>(document.Placements.Select(p=>p.Id),StringComparer.Ordinal);var add=new List<RoomPlacement>();
            foreach(var p in candidates)
            {
                cancellation.ThrowIfCancellationRequested();RequirePlacement(p);
                if(!keys.Add(BrushKey(p)))continue;
                if(!ids.Add(p.Id))throw new ArgumentException("Duplicate placement identity.");add.Add(p);
            }
            if(add.Count==0)return document;
            var pins=document.PinnedDependencies.Concat(add.Select(p=>p.Definition)).GroupBy(p=>p.Key,StringComparer.Ordinal).Select(g=>g.First()).ToArray();
            var result=document.With(placements:document.Placements.Concat(add).ToArray(),pinnedDependencies:pins);
            return RequireData(result,cancellation);
        }
        public RoomDocument Move(RoomDocument document,IEnumerable<string> selected,GridPosition delta,int quarterTurns=0,CancellationToken cancellation=default)
        {
            var ids=AttachmentClosure(document,selected);if(ids.Count==0)return document;
            var source=document.Placements.ToDictionary(p=>p.Id,StringComparer.Ordinal);var next=new Dictionary<string,RoomPlacement>(source,StringComparer.Ordinal);
            int turns=RoomGeometryFunctions.NormalizeTurns(quarterTurns);
            foreach(string id in ids)
            {
                cancellation.ThrowIfCancellationRequested();var p=source[id];if(p.Attachment!=null&&ids.Contains(p.Attachment.HostPlacementId))continue;
                var transformed=RoomGeometryFunctions.RotateInPlace(Resolve(p.Definition),p,turns);
                transformed=transformed.WithTransform(new GridPosition(checked(p.Position.X+delta.X),checked(p.Position.Y+delta.Y),checked(p.Position.Z+delta.Z)),transformed.QuarterTurns,p.Scale,transformed.PivotOffset);
                if(p.Attachment!=null)transformed=WithAttachmentFromWorld(transformed,source[p.Attachment.HostPlacementId]);
                next[id]=transformed;
            }
            FollowHosts(source,next,ids,cancellation);
            return RequireData(document.With(placements:document.Placements.Select(p=>next[p.Id]).ToArray()),cancellation);
        }
        public RoomDocument SetWallScale(RoomDocument document,string id,Double3 scale,CancellationToken cancellation=default)
        {
            var source=document.Placements.ToDictionary(p=>p.Id,StringComparer.Ordinal);var p=source[id];var d=Resolve(p.Definition);
            if(d.Category!=DefinitionCategory.Wall)throw new ArgumentException("Only wall instances expose scale handles.");
            var scaled=p.WithTransform(p.Position,p.QuarterTurns,scale,p.PivotOffset);
            if(!RoomGeometryFunctions.ValidScale(d,scaled))throw new ArgumentException("Wall scale must be positive, bounded and grid-aligned.");
            var next=new Dictionary<string,RoomPlacement>(source,StringComparer.Ordinal);
            next[id]=scaled;var ids=AttachmentClosure(document,new[]{id});FollowHosts(source,next,ids,cancellation);
            return RequireData(document.With(placements:document.Placements.Select(v=>next[v.Id]).ToArray()),cancellation);
        }
        public RoomDocument Delete(RoomDocument document,IEnumerable<string> selected,CancellationToken cancellation=default)
        {
            cancellation.ThrowIfCancellationRequested();var ids=AttachmentClosure(document,selected);if(ids.Count==0)return document;
            return RequireData(document.With(placements:document.Placements.Where(p=>!ids.Contains(p.Id)).ToArray(),
                rooms:document.Rooms.Select(r=>new Room(r.Id,r.Name,r.SharedStructurePlacementIds.Where(id=>!ids.Contains(id)).ToArray())).ToArray()),cancellation);
        }
        public RoomDocument Clear(RoomDocument document,GridRect region,string roomId=null,CancellationToken cancellation=default)=>Delete(document,Select(document,region,roomId),cancellation);
        public RoomDocument Fill(RoomDocument document,GridRect region,PinnedReference definition,int quarterTurns=0,string roomId=null,CancellationToken cancellation=default)
        {
            var template=Prepare(definition,default,quarterTurns,roomId);var d=Resolve(definition);
            if(d.PlacementPolicy==PlacementPolicy.Wall)throw new InvalidOperationException("A wall attachment requires an explicit host.");
            var b=RoomGeometryFunctions.Occupancy(d,template);
            int left=(int)Math.Floor(b.Min.X*4+GeometryBox.Epsilon),bottom=(int)Math.Floor(b.Min.Z*4+GeometryBox.Epsilon);
            int width=Math.Max(1,(int)Math.Ceiling(b.Max.X*4-GeometryBox.Epsilon)-left),depth=Math.Max(1,(int)Math.Ceiling(b.Max.Z*4-GeometryBox.Epsilon)-bottom);
            long count=((long)region.MaxX-region.MinX)/width*(((long)region.MaxZ-region.MinZ)/depth);
            if(count>MaximumTransactionPlacements)throw new InvalidOperationException("Rectangle fill exceeds the transaction budget.");
            var additions=new List<RoomPlacement>();
            for(int z=region.MinZ;z+depth<=region.MaxZ;z+=depth)for(int x=region.MinX;x+width<=region.MaxX;x+=width)
            {cancellation.ThrowIfCancellationRequested();additions.Add(Clone(template,newId(),new GridPosition(checked(x-left),0,checked(z-bottom))));}
            return Place(document,additions,cancellation);
        }
        public bool TrySnapWall(RoomDocument document,RoomPlacement candidate,out RoomPlacement snapped)
        {
            var d=Resolve(candidate.Definition);snapped=candidate;if(d.Category!=DefinitionCategory.Wall)return false;
            var index=Index(document);
            int px=candidate.Position.X,pz=candidate.Position.Z;
            var nearby=index.NearbyFloorBoundary(px,pz);
            double best=2.25;RoomPlacement winner=null;
            for(int yaw=0;yaw<4;yaw++)
            {
                var localPlacement=RoomGeometryFunctions.RotateInPlace(d,candidate.WithTransform(default,0,candidate.Scale,Double3.Zero),yaw);
                var local=d.WallBoundaries.SelectMany(b=>RoomGeometryFunctions.Edges(b,localPlacement)).ToArray();
                if(local.Length>4096||(long)nearby.Count*local.Length*local.Length>2000000)throw new InvalidOperationException("Wall snap search exceeds the operation budget.");
                foreach(var target in nearby)foreach(var edge in local)
                {
                    if(edge.AlongX!=target.AlongX)continue;int dx=target.X-edge.X,dz=target.Z-edge.Z;
                    if(local.Any(e=>!index.Supports(new GridEdge(e.X+dx,e.Z+dz,e.AlongX))))continue;
                    var p=localPlacement.WithTransform(new GridPosition(dx,0,dz),yaw,candidate.Scale,localPlacement.PivotOffset);
                    var box=RoomGeometryFunctions.Occupancy(d,p);double x=box.Center.X-px*.25,z=box.Center.Z-pz*.25;
                    double score=x*x+z*z+(yaw==candidate.QuarterTurns?0:.0001);if(score>=best)continue;best=score;winner=p;
                }
            }
            if(winner==null)return false;snapped=winner;return true;
        }
        public BrushGesture BeginBrush(RoomDocument document,RoomPlacement template)=>new BrushGesture(this,document,template,newId);
        public static string BrushKey(RoomPlacement p)
        {
            string F(double value)=>value.ToString("R",CultureInfo.InvariantCulture);
            return string.Join("|",p.Definition.Key,p.Position.X,p.Position.Y,p.Position.Z,p.QuarterTurns,F(p.Scale.X),F(p.Scale.Y),F(p.Scale.Z),
                F(p.PivotOffset.X),F(p.PivotOffset.Z),p.Attachment?.HostPlacementId,p.Attachment?.SlotId,p.RoomId,p.GroupId);
        }
        public static HashSet<string> AttachmentClosure(RoomDocument document,IEnumerable<string> selected)
        {
            var ids=new HashSet<string>(selected??throw new ArgumentNullException(nameof(selected)),StringComparer.Ordinal);
            var all=new HashSet<string>(document.Placements.Select(p=>p.Id),StringComparer.Ordinal);
            if(ids.Any(id=>!all.Contains(id)))throw new ArgumentException("A selected placement no longer exists.");
            bool changed;do{changed=false;foreach(var p in document.Placements)if(p.Attachment!=null&&ids.Contains(p.Attachment.HostPlacementId))changed|=ids.Add(p.Id);}while(changed);
            return ids;
        }
        private void FollowHosts(Dictionary<string,RoomPlacement> before,Dictionary<string,RoomPlacement> next,HashSet<string> ids,CancellationToken cancellation)
        {
            var pending=new HashSet<string>(ids.Where(id=>before[id].Attachment!=null&&ids.Contains(before[id].Attachment.HostPlacementId)),StringComparer.Ordinal);
            while(pending.Count>0)
            {
                bool progress=false;
                foreach(string id in pending.ToArray())
                {
                    cancellation.ThrowIfCancellationRequested();var p=before[id];var a=p.Attachment;
                    if(pending.Contains(a.HostPlacementId))continue;var host=next[a.HostPlacementId];
                    var world=RoomGeometryFunctions.Transform(a.LocalOffset,host);
                    double gy=world.Y*4;if(!Numeric.IsFinite(gy)||Math.Abs(gy-Math.Round(gy))>GeometryBox.Epsilon)
                        throw new ArgumentException("Attachment height cannot be represented on the quarter-meter grid after host scaling.");
                    var position=new GridPosition(p.Position.X,checked((int)Math.Round(gy)),p.Position.Z);
                    next[id]=p.WithTransform(position,RoomGeometryFunctions.NormalizeTurns(host.QuarterTurns+a.LocalQuarterTurns),p.Scale,
                        new Double3(world.X-position.X*.25,0,world.Z-position.Z*.25));pending.Remove(id);progress=true;
                }
                if(!progress)throw new ArgumentException("Attachment hierarchy contains a cycle.");
            }
        }
        private static RoomPlacement WithAttachmentFromWorld(RoomPlacement p,RoomPlacement host)
        {
            var local=RoomGeometryFunctions.Rotate(RoomGeometryFunctions.Origin(p)-RoomGeometryFunctions.Origin(host),-host.QuarterTurns);
            local=new Double3(local.X/host.Scale.X,local.Y/host.Scale.Y,local.Z/host.Scale.Z);
            return p.WithTransform(p.Position,p.QuarterTurns,p.Scale,p.PivotOffset,new PlacementAttachment(host.Id,p.Attachment.SlotId,local,RoomGeometryFunctions.NormalizeTurns(p.QuarterTurns-host.QuarterTurns)));
        }
        internal static RoomPlacement Clone(RoomPlacement p,string id,GridPosition position)=>new RoomPlacement(id,p.Definition,position,p.QuarterTurns,p.Scale,p.PivotOffset,p.RoomId,p.GroupId,p.Attachment,p.HiddenInEditor,p.IncludeInExport,p.MaterialOverrides,p.LightOverrides);
        private void RequirePlacement(RoomPlacement p)
        {
            var d=Resolve(p.Definition);
            if(!RoomGeometryFunctions.ValidScale(d,p)||p.QuarterTurns<0||p.QuarterTurns>3||!p.PivotOffset.IsFinite||p.PivotOffset.Y!=0)
                throw new ArgumentException("Placement has invalid scale or transform.");
            if((d.Category==DefinitionCategory.Floor||d.Category==DefinitionCategory.Wall)&&(!RoomGeometryFunctions.GridAligned(p.PivotOffset.X*4)||!RoomGeometryFunctions.GridAligned(p.PivotOffset.Z*4)))
                throw new ArgumentException("Structural placement pivot must remain on-grid.");
        }
        private RoomDocument RequireData(RoomDocument document,CancellationToken cancellation)
        {
            DocumentValidation.Validate(document,Resolve,cancellation);
            cancellation.ThrowIfCancellationRequested();foreach(var p in document.Placements){RequirePlacement(p);cancellation.ThrowIfCancellationRequested();}
            var byId=document.Placements.ToDictionary(p=>p.Id,StringComparer.Ordinal);
            foreach(var p in document.Placements)if(p.Attachment!=null)
            {
                var visited=new HashSet<string>(StringComparer.Ordinal){p.Id};var a=p.Attachment;
                while(a!=null)
                {
                    if(!visited.Add(a.HostPlacementId)||!byId.TryGetValue(a.HostPlacementId,out var host))throw new ArgumentException("Missing or cyclic attachment host.");
                    var d=Resolve(host.Definition);if(d.Category!=DefinitionCategory.Wall)throw new ArgumentException("Attachment host must be a wall.");
                    if(!string.IsNullOrEmpty(a.SlotId)&&!d.OpeningSlots.Any(s=>s.Id==a.SlotId))throw new ArgumentException("Attachment slot is absent from the pinned host definition.");
                    if(ReferenceEquals(a,p.Attachment))
                    {
                        var expected=RoomGeometryFunctions.Transform(a.LocalOffset,host);var actual=RoomGeometryFunctions.Origin(p);
                        if((expected-actual).Length>GeometryBox.Epsilon||p.QuarterTurns!=RoomGeometryFunctions.NormalizeTurns(host.QuarterTurns+a.LocalQuarterTurns))
                            throw new ArgumentException("Attachment absolute transform differs from its explicit host-local parameters.");
                    }
                    a=host.Attachment;
                }
            }
            return document;
        }
    }
}
