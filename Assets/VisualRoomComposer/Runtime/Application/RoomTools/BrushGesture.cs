using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualRoomComposer.Core;
using VisualRoomComposer.Core.Documents;
using VisualRoomComposer.Core.RoomGeometry;

namespace VisualRoomComposer.Application.RoomTools
{
    /// <summary>A bounded preview buffer. Sampling never commits a document or publishes history.</summary>
    public sealed class BrushGesture : IDisposable
    {
        private readonly RoomToolService tools; private readonly RoomDocument source; private readonly RoomPlacement template;
        private readonly LibraryDefinition definition; private readonly Func<string> newId;
        private readonly HashSet<string> keys; private readonly HashSet<GridEdge> wallEdges;
        private readonly List<RoomPlacement> additions=new List<RoomPlacement>();
        private readonly int width,depth; private bool ended,hasOrigin; private GridCell2 origin,last;
        public IReadOnlyList<RoomPlacement> Preview => additions.AsReadOnly();
        public RoomDocument Source => source;
        internal BrushGesture(RoomToolService tools,RoomDocument source,RoomPlacement template,Func<string> newId)
        {
            this.tools=tools;this.source=source;this.template=template;this.newId=newId;definition=tools.Resolve(template.Definition);
            keys=new HashSet<string>(source.Placements.Select(RoomToolService.BrushKey),StringComparer.Ordinal);wallEdges=new HashSet<GridEdge>(tools.Index(source).WallEdges);
            var bounds=RoomGeometryFunctions.Occupancy(definition,template);
            width=Math.Max(1,(int)Math.Ceiling(bounds.Size.X*4-GeometryBox.Epsilon));depth=Math.Max(1,(int)Math.Ceiling(bounds.Size.Z*4-GeometryBox.Epsilon));
        }
        public void Sample(GridCell2 cell,CancellationToken cancellation=default)
        {
            try { SampleCore(cell,cancellation); }
            catch { Cancel(); throw; }
        }
        private void SampleCore(GridCell2 cell,CancellationToken cancellation)
        {
            if(ended)throw new InvalidOperationException("The gesture has ended.");cancellation.ThrowIfCancellationRequested();
            if(!hasOrigin){origin=last=cell;hasOrigin=true;}
            long span=Math.Max(Math.Abs((long)cell.X-last.X),Math.Abs((long)cell.Z-last.Z));
            if(span>8192)throw new InvalidOperationException("Brush interpolation span exceeds the sample budget.");
            if(definition.PlacementPolicy==PlacementPolicy.Wall)
                throw new InvalidOperationException("Wall attachments require SampleAttachment with an explicitly bound candidate.");
            int x=last.X,z=last.Z,dx=Math.Abs(cell.X-x),dz=Math.Abs(cell.Z-z),sx=x<cell.X?1:-1,sz=z<cell.Z?1:-1,error=dx-dz;
            while(true)
            {
                cancellation.ThrowIfCancellationRequested();
                int px=checked(origin.X+(int)Math.Round((x-origin.X)/(double)width,MidpointRounding.AwayFromZero)*width);
                int pz=checked(origin.Z+(int)Math.Round((z-origin.Z)/(double)depth,MidpointRounding.AwayFromZero)*depth);
                var p=RoomToolService.Clone(template,newId(),new GridPosition(px,template.Position.Y,pz));
                if(definition.Category==DefinitionCategory.Wall)
                {
                    p=RoomToolService.Clone(template,p.Id,new GridPosition(x,0,z));
                    if(tools.TrySnapWall(source,p,out var snapped))p=snapped;
                }
                Reserve(p);if(x==cell.X&&z==cell.Z)break;
                int twice=2*error;if(twice> -dz){error-=dz;x+=sx;}if(twice<dx){error+=dx;z+=sz;}
            }
            last=cell;
        }
        public void SampleAttachment(RoomPlacement candidate,CancellationToken cancellation=default)
        {
            try
            {
                if(ended)throw new InvalidOperationException("The gesture has ended.");cancellation.ThrowIfCancellationRequested();
                if(definition.PlacementPolicy!=PlacementPolicy.Wall||candidate==null||candidate.Attachment==null||!candidate.Definition.Equals(template.Definition))
                    throw new ArgumentException("A candidate bound to a wall and the selected definition is required.");
                Reserve(RoomToolService.Clone(candidate,newId(),candidate.Position));
            }
            catch { Cancel();throw; }
        }
        private void Reserve(RoomPlacement p)
        {
            var edges=definition.Category==DefinitionCategory.Wall?definition.WallBoundaries.SelectMany(b=>RoomGeometryFunctions.Edges(b,p)).ToArray():Array.Empty<GridEdge>();
            string key=RoomToolService.BrushKey(p);if(keys.Contains(key)||edges.Any(wallEdges.Contains))return;
            if(additions.Count>=RoomToolService.MaximumTransactionPlacements)throw new InvalidOperationException("Brush placement budget exceeded.");
            keys.Add(key);wallEdges.UnionWith(edges);additions.Add(p);
        }
        public RoomDocument Complete(RoomDocument current,CancellationToken cancellation=default)
        {
            if(ended)throw new InvalidOperationException("The gesture has ended.");
            if(!ReferenceEquals(current,source)){Cancel();throw new InvalidOperationException("Gesture source changed; cancel the stale preview.");}
            try { var result=tools.Place(source,additions,cancellation);ended=true;additions.Clear();return result; }
            catch { Cancel();throw; }
        }
        public void Cancel(){ended=true;additions.Clear();keys.Clear();wallEdges.Clear();}
        public void Dispose()=>Cancel();
    }
}
