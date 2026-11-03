using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualRoomComposer.Core;
using VisualRoomComposer.Core.Documents;
using VisualRoomComposer.Core.RoomGeometry;
using VisualRoomComposer.Application.RoomTools;

namespace VisualRoomComposer.Application.Diagnostics
{
    public sealed class RoomGeometryCheck
    {
        public string Id {get;} public bool Passed {get;} public string Detail {get;}
        public RoomGeometryCheck(string id,bool passed,string detail){Id=id;Passed=passed;Detail=detail;}
    }
    /// <summary>Player-callable checks of production geometry/tools with independently authored numerical expectations.</summary>
    public static class RoomGeometryProbe
    {
        public static IReadOnlyList<RoomGeometryCheck> Run()
        {
            var results=new List<RoomGeometryCheck>();var fixture=RoomSampleFixture.Create();int identity=0;
            var tools=new RoomToolService(fixture.Resolve,()=>"probe-"+(++identity));
            var empty=new RoomDocument("geometry-probe",rooms:new[]{new Room("room-a","A")},stateId:"geometry-empty");
            var floor=RoomSampleFixture.Pin("floor-15");var wall=RoomSampleFixture.Pin("wall-solid");
            Check("GEOM-FIVE-MASKS",()=>
            {
                foreach(var pair in new[]{(15,16),(7,12),(9,8),(3,8),(1,4)})for(int yaw=0;yaw<4;yaw++)
                {var d=fixture.Resolve(RoomSampleFixture.Pin("floor-"+pair.Item1));var p=new RoomPlacement("p",d.Reference,default,yaw);Require(RoomGeometryFunctions.FloorCells(d,p).Distinct().Count()==pair.Item2,"mask area");}
                var l=fixture.Resolve(RoomSampleFixture.Pin("floor-7"));var cells=RoomGeometryFunctions.FloorCells(l,new RoomPlacement("l",l.Reference,default));
                Require(!cells.Contains(new GridCell2(3,3))&&cells.Contains(new GridCell2(1,3)),"missing corner was filled");
            });
            Check("GEOM-FLOOR-ACTUAL-SURFACES",()=>
            {
                foreach(int mask in new[]{15,7,9,3,1})
                {
                    var d=fixture.Resolve(RoomSampleFixture.Pin("floor-"+mask));var mesh=fixture.Models[d.Reference.Key].Meshes.Single();
                    Require(StructureGeometryValidator.Validate(d,Triangles(mesh)).Count==0,"original floor surface mismatch");
                    if(mask!=15)
                    {var solid=fixture.Models[floor.Key].Meshes.Single();Require(StructureGeometryValidator.Validate(d,Triangles(solid)).Any(i=>i.Code=="structure.floor-mask"),"full floor falsely accepted as missing-corner mask");}
                }
            });
            Check("GEOM-DOOR-ACTUAL-HOLE",()=>
            {
                var d=fixture.Resolve(RoomSampleFixture.Pin("wall-door"));var mesh=fixture.Models[d.Reference.Key].Meshes.Single();
                Require(StructureGeometryValidator.Validate(d,Triangles(mesh)).Count==0,"doorway mesh metadata mismatch");
                Require(!RoomMeshPrimitives.RayHits(mesh,new Double3(1.5,1,-1),Double3.UnitZ,2),"door hole is filled");
                Require(RoomMeshPrimitives.RayHits(mesh,new Double3(.5,1,-1),Double3.UnitZ,2),"wall pier absent");
                Require(RoomMeshPrimitives.RayHits(mesh,new Double3(1.5,2.5,-1),Double3.UnitZ,2),"lintel absent");
                Require(StructureGeometryValidator.Validate(d,Triangles(fixture.Models[wall.Key].Meshes.Single())).Any(i=>i.Code=="structure.opening-filled"),"solid wall falsely accepted as door");
            });
            Check("GEOM-ORIGINAL-TWO-ROOMS",()=>
            {
                var index=tools.Index(fixture.Document);Require(fixture.Document.Placements.Count==28,"independent placement count");
                Require(index.FloorCells.Count==288,"18 square meters equals 288 quarter-grid cells");
                Require(index.WallEdges.Count==84,"18m perimeter plus 3m shared wall equals 84 edges");
                Require(fixture.Document.Rooms[1].SharedStructurePlacementIds.SequenceEqual(new[]{"shared-door-wall"}),"shared wall identity");
                Require(index.Diagnose().Count==0,string.Join(",",index.Diagnose().Select(i=>i.Code+":"+i.PlacementId)));
                Require(index.VertexDegrees().Values.Count(n=>n==3)==2,"partition should create two T junctions");
            });
            Check("GEOM-CROSS-JUNCTION",()=>
            {
                var doc=tools.Place(empty,new[]{new RoomPlacement("x",wall,default),new RoomPlacement("z",wall,new GridPosition(6,0,6),1)});
                Require(tools.Index(doc).VertexDegrees()[new GridCell2(6,0)]==4,"cross topology degree");
            });
            Check("GEOM-TOUCH-NOT-PENETRATION",()=>
            {
                var a=new GeometryBox(Double3.Zero,Double3.One);var b=new GeometryBox(Double3.UnitX,new Double3(2,1,1));
                Require(!a.Intersects(b),"face contact penetrates");Require(a.Intersects(new GeometryBox(new Double3(.75,0,0),new Double3(1.75,1,1))),"overlap absent");
                var index=new RoomSpatialIndex(new[]{new KeyValuePair<string,IReadOnlyList<GeometryBox>>("a",new[]{a}),new KeyValuePair<string,IReadOnlyList<GeometryBox>>("b",new[]{b})});
                Require(index.Query(a).SequenceEqual(new[]{"a"}),"broad phase claimed face contact as volume");
            });
            Check("GEOM-GROUND-OFFGRID-PIVOT",()=>
            {
                var p=fixture.Document.Placements.Single(p=>p.Id=="seat");var d=fixture.Resolve(p.Definition);var box=RoomGeometryFunctions.Occupancy(d,p);
                Near(box.Min.Y,0);Near(box.Max.Y,.987);
                var moved=tools.Move(fixture.Document,new[]{"seat"},new GridPosition(0,1,0));
                Require(tools.Index(moved).Diagnose().Any(i=>i.Code=="geometry.ground-height"&&i.PlacementId=="seat"),"raised ground prop must be draft diagnostic");
                DocumentValidation.Validate(moved,fixture.Resolve);
            });
            Check("GEOM-SUSPENDED-PROJECTION",()=>
            {
                var moved=tools.Move(fixture.Document,new[]{"hanging"},new GridPosition(40,0,0));
                Require(tools.Index(moved).Diagnose().Any(i=>i.Code=="geometry.floor-support"&&i.PlacementId=="hanging"),"suspended projection unsupported");
                Require(!tools.Index(fixture.Document).Diagnose().Any(i=>i.PlacementId=="hanging"),"supported hanging fixture rejected");
            });
            Check("EDIT-RECTANGLE-COMPLETE-FILL",()=>
            {
                var doc=tools.Fill(empty,new GridRect(0,0,9,13),floor,roomId:"room-a");Require(doc.Placements.Count==6,"rectangle must contain six complete tiles");
                Require(doc.Placements.Select(p=>p.Position.X+","+p.Position.Z).OrderBy(s=>s).SequenceEqual(new[]{"0,0","0,4","0,8","4,0","4,4","4,8"}),"unexpected fill positions");
                Require(ReferenceEquals(doc,tools.Fill(doc,new GridRect(0,0,9,13),floor,roomId:"room-a")),"duplicate fill is not a no-op");
                Require(tools.Fill(empty,new GridRect(0,0,1,1),floor).Placements.Count==0,"clipped tile committed");
                Throws(()=>tools.Fill(empty,new GridRect(0,0,404,400),floor));
            });
            Check("EDIT-RECTANGLE-CLEAR-HIGHLIGHT",()=>
            {
                var doc=tools.Fill(empty,new GridRect(0,0,8,4),floor);var selected=tools.Select(doc,new GridRect(1,1,4,3));Require(selected.Count==1,"touching edge selected");
                var cleared=tools.Clear(doc,new GridRect(1,1,4,3));Require(cleared.Placements.Count==1&&cleared.Placements[0].Position.X==4,"clear differs from selection");
                Require(tools.Select(doc,new GridRect(1,1,4,3)).SequenceEqual(selected),"index reused stale candidate data");
            });
            Check("EDIT-SERVICE-FOUR-TURNS",()=>
            {
                var doc=fixture.Document;var source=doc.Placements.Single(p=>p.Id=="seat");var center=RoomGeometryFunctions.WorldFootprintPivot(fixture.Resolve(source.Definition),source);
                for(int i=0;i<4;i++)
                {
                    doc=tools.Move(doc,new[]{"seat","hanging"},default,1);var p=doc.Placements.Single(p=>p.Id=="seat");
                    Near(RoomGeometryFunctions.WorldFootprintPivot(fixture.Resolve(p.Definition),p).X,center.X);Near(RoomGeometryFunctions.WorldFootprintPivot(fixture.Resolve(p.Definition),p).Z,center.Z);
                }
                var final=doc.Placements.Single(p=>p.Id=="seat");Require(final.QuarterTurns==source.QuarterTurns&&final.PivotOffset.Equals(source.PivotOffset),"four turns drifted");
            });
            Check("EDIT-HOST-ATTACHMENT-MOVE-ROTATE",()=>
            {
                var original=fixture.Document;var doc=tools.Move(original,new[]{"south-a"},new GridPosition(4,0,8),1);
                var art=doc.Placements.Single(p=>p.Id=="wall-art");var host=doc.Placements.Single(p=>p.Id=="south-a");
                var root=RoomGeometryFunctions.Origin(art);Near(root.X,2.6875);Near(root.Y,1.5);Near(root.Z,2);
                Require(art.QuarterTurns==1&&tools.Index(doc).ValidAttachment("wall-art"),"attachment did not follow host");
                Require(original.Placements.Single(p=>p.Id=="south-a").Position.X==0,"source mutated");
                for(int i=0;i<3;i++)doc=tools.Move(doc,new[]{"south-a"},default,1);
                Near(RoomGeometryFunctions.Origin(doc.Placements.Single(p=>p.Id=="wall-art")).X,2.5);
                Near(RoomGeometryFunctions.Origin(doc.Placements.Single(p=>p.Id=="wall-art")).Z,2.1875);
            });
            Check("EDIT-SERVICE-WALL-XYZ-SCALE",()=>
            {
                var doc=tools.SetWallScale(fixture.Document,"south-a",new Double3(2,.5,3));
                var p=doc.Placements.Single(v=>v.Id=="south-a");var b=RoomGeometryFunctions.Occupancy(fixture.Resolve(p.Definition),p);
                Near(b.Max.X,6);Near(b.Max.Y,1.5);Near(b.Max.Z,.375);
                var art=doc.Placements.Single(v=>v.Id=="wall-art");Near(RoomGeometryFunctions.Origin(art).X,3);Near(RoomGeometryFunctions.Origin(art).Y,.75);Near(RoomGeometryFunctions.Origin(art).Z,.5625);
                Require(art.Scale.Equals(Double3.One),"wall scale improperly changed independent attachment size");
                Throws(()=>tools.SetWallScale(fixture.Document,"south-a",new Double3(.3,1,1)));Throws(()=>tools.SetWallScale(fixture.Document,"south-a",new Double3(1,.1,1)));
                Throws(()=>tools.SetWallScale(fixture.Document,"seat",new Double3(2,1,1)));Require(fixture.Document.Placements.Single(v=>v.Id=="south-a").Scale.Equals(Double3.One),"failed scale mutated source");
            });
            Check("EDIT-HOST-DELETE-AND-SHARED-REFERENCE",()=>
            {
                var doc=tools.Delete(fixture.Document,new[]{"south-a","shared-door-wall"});
                Require(!doc.Placements.Any(p=>p.Id=="wall-art"||p.Id=="south-a"||p.Id=="shared-door-wall"),"host delete did not cascade atomically");
                Require(doc.Rooms.All(r=>r.SharedStructurePlacementIds.Count==0),"shared structure reference is dangling");DocumentValidation.Validate(doc,fixture.Resolve);
            });
            Check("EDIT-BRUSH-INTERPOLATION-DEDUP",()=>
            {
                using(var brush=tools.BeginBrush(empty,tools.Prepare(floor,default)))
                {brush.Sample(new GridCell2(0,0));brush.Sample(new GridCell2(16,0));brush.Sample(new GridCell2(0,0));Require(brush.Preview.Count==5,"sparse pointer samples skipped or duplicated stamps");
                    Require(empty.Placements.Count==0,"preview mutated document");var doc=brush.Complete(empty);Require(doc.Placements.Count==5,"brush complete count");}
            });
            Check("EDIT-BRUSH-CANCEL-STALE-BUDGET",()=>
            {
                using(var brush=tools.BeginBrush(empty,tools.Prepare(floor,default)))
                {brush.Sample(new GridCell2(0,0));brush.Cancel();Require(brush.Preview.Count==0,"cancel retained stamps");Throws(()=>brush.Complete(empty));}
                using(var brush=tools.BeginBrush(empty,tools.Prepare(floor,default)))
                {brush.Sample(new GridCell2(0,0));Throws(()=>brush.Sample(new GridCell2(9000,0)));Throws(()=>brush.Complete(empty.With(title:"changed")));}
                using(var c=new CancellationTokenSource()){c.Cancel();Throws<OperationCanceledException>(()=>tools.Fill(empty,new GridRect(0,0,8,8),floor,cancellation:c.Token));}
                Require(empty.Placements.Count==0,"cancel or failure changed source");
            });
            Check("EDIT-WALL-SNAP-OVERLAP-RESERVATION",()=>
            {
                var doc=tools.Fill(empty,new GridRect(0,0,12,12),floor);
                var candidate=tools.Prepare(wall,new GridPosition(12,0,6));Require(tools.TrySnapWall(doc,candidate,out var snapped),"wall failed to snap");
                var center=RoomGeometryFunctions.WorldFootprintPivot(fixture.Resolve(wall),snapped);Near(center.X,3);Near(center.Z,1.5);Require(snapped.QuarterTurns%2==1,"wall yaw did not align");
                using(var brush=tools.BeginBrush(doc,candidate))
                {brush.Sample(new GridCell2(12,6));brush.Sample(new GridCell2(12,7));Require(brush.Preview.Count==1,"partially overlapping wall stamps accepted");}
            });
            Check("GEOM-DRAFT-DIAGNOSTICS-SAVEABLE",()=>
            {
                var doc=tools.Delete(fixture.Document,fixture.Document.Placements.Where(p=>p.Definition.Equals(floor)).Select(p=>p.Id));
                var issues=tools.Index(doc).Diagnose();Require(issues.Any(i=>i.Code=="geometry.no-floor")&&issues.Any(i=>i.Code=="geometry.floor-support"),"draft issues missing");
                Require(issues.All(i=>i.Kind==RoomIssueKind.DraftDiagnostic),"draft warnings became data errors");DocumentValidation.Validate(doc,fixture.Resolve);
            });
            Check("EDIT-ROTATED-FILL-COMPLETE-FOOTPRINT",()=>
            {
                var region=new GridRect(-3,-5,3,-1);var doc=tools.Fill(empty,region,RoomSampleFixture.Pin("prop-ground"),1);
                Require(doc.Placements.Count==1,"center-preserving rotation occupies a 4 by 3 quarter-grid envelope; only one complete envelope fits");
                foreach(var p in doc.Placements)
                {var box=RoomGeometryFunctions.Occupancy(fixture.Resolve(p.Definition),p);Require(box.Min.X>=-.75-1e-9&&box.Max.X<=.75+1e-9&&box.Min.Z>=-1.25-1e-9&&box.Max.Z<=-.25+1e-9,"rotated fill clipped boundary");}
            });
            Check("GEOM-L-MASK-SUPPORT",()=>
            {
                var doc=tools.Place(empty,new[]{tools.Prepare(RoomSampleFixture.Pin("floor-7"),default)});var index=tools.Index(doc);
                Require(!index.FootprintSupported(new GeometryBox(new Double3(.625,0,.625),new Double3(.875,1,.875))),"missing floor corner accepted");
                Require(index.FootprintSupported(new GeometryBox(new Double3(.125,0,.625),new Double3(.375,1,.875))),"covered floor rejected");
            });
            Check("GEOM-STRUCTURE-Y-DRAFT",()=>
            {
                var doc=tools.Move(fixture.Document,new[]{"floor-0-0","west"},new GridPosition(0,1,0));
                Require(tools.Index(doc).Diagnose().Count(i=>i.Code=="geometry.structure-height")==2,"structural Y=0 warnings missing");DocumentValidation.Validate(doc,fixture.Resolve);
            });
            Check("GEOM-ATTACHMENT-ONLY-HOST-EXEMPT",()=>
            {
                var doc=tools.Place(fixture.Document,new[]{new RoomPlacement("intruder-wall",wall,new GridPosition(0,0,1))});
                Require(tools.Index(doc).Diagnose().Any(i=>i.Code=="geometry.intersection"&&((i.PlacementId=="wall-art"&&i.RelatedId=="intruder-wall")||(i.PlacementId=="intruder-wall"&&i.RelatedId=="wall-art"))),"attachment was exempted from an unrelated wall");
                Throws(()=>tools.Place(empty,new[]{tools.Prepare(RoomSampleFixture.Pin("wall-art"),default,attachment:new PlacementAttachment("absent"))}));
            });
            Check("GEOM-SHARED-ROOM-SELECTION",()=>
            {
                var selected=tools.Select(fixture.Document,new GridRect(11,0,13,12),"room-b");
                Require(selected.Count(id=>id=="shared-door-wall")==1,"shared wall must be selectable once in secondary room");
                Require(!selected.Contains("floor-2-0"),"primary room floor leaked into secondary room selection");
            });
            Check("GEOM-DATA-ERRORS-AND-BOUNDS",()=>
            {
                var unknown=new RoomLayoutIndex(fixture.Document,_=>null).Diagnose();Require(unknown.Count(i=>i.Kind==RoomIssueKind.BlockingDataError)==28,"missing definition was not a data error");
                var d=fixture.Resolve(wall);var p=new RoomPlacement("scale",wall,default,scale:new Double3(1,1,65536));
                Require(RoomGeometryFunctions.ValidScale(d,p),"bounded 16384 meter thickness rejected");
                Require(!RoomGeometryFunctions.ValidScale(d,p.WithTransform(default,0,new Double3(1,1,65536.01),Double3.Zero)),"oversized thickness accepted");
                Throws(()=>tools.Place(empty,new[]{new RoomPlacement("bad",floor,default,pivotOffset:new Double3(double.NaN,0,0))}));
                Throws(()=>tools.Place(empty,new[]{new RoomPlacement("bad",floor,default,quarterTurns:4)}));
            });
            Check("GEOM-STRUCTURE-INVALID-TRIANGLES",()=>
            {
                var d=fixture.Resolve(floor);
                Require(StructureGeometryValidator.Validate(d,new[]{new StructureTriangleData(new[]{Double3.Zero},new[]{0,1,2})}).Single().Code=="structure.mesh-indices","invalid mesh indices accepted");
                Require(StructureGeometryValidator.Validate(d,new[]{new StructureTriangleData(new[]{new Double3(double.NaN,0,0)},new[]{0,0,0})}).Single().Code=="structure.mesh-finite","nonfinite mesh accepted");
                var mesh=RoomMeshPrimitives.Boxes("partial",new[]{new GeometryBox(new Double3(0,-.1,0),new Double3(.5,0,.5))});
                Require(StructureGeometryValidator.Validate(d,Triangles(mesh)).Single().Code=="structure.floor-support","missing floor surfaces accepted");
                using(var cancel=new CancellationTokenSource()){cancel.Cancel();Throws<OperationCanceledException>(()=>StructureGeometryValidator.Validate(d,Triangles(fixture.Models[floor.Key].Meshes.Single()),cancel.Token));}
            });
            Check("EDIT-EXPLICIT-ATTACHMENT-PARAMETERS",()=>
            {
                var b=new Bounds3d(Double3.Zero,new Double3(.75,2,.2));
                var definition=new LibraryDefinition(RoomSampleFixture.Pin("door-panel"),"Original door panel",DefinitionCategory.WallAttachment,PlacementPolicy.Wall,b,b,Matrix4d.Identity,occupancyPolicy:OccupancyPolicy.Passage);
                LibraryDefinition Resolve(PinnedReference pin)=>definition.Reference.Equals(pin)?definition:fixture.Resolve(pin);
                var service=new RoomToolService(Resolve);var art=service.PrepareAttachment(fixture.Document,definition.Reference,"shared-door-wall","door",new Double3(1.5,1,0));
                var doc=service.Place(fixture.Document,new[]{art});Require(service.Index(doc).ValidAttachment(art.Id),"explicit door slot did not contain attachment");
                Near(RoomGeometryFunctions.Origin(art).X,3);Near(RoomGeometryFunctions.Origin(art).Y,1);Near(RoomGeometryFunctions.Origin(art).Z,1.5);
                Throws(()=>service.PrepareAttachment(fixture.Document,definition.Reference,"south-a","door",Double3.Zero));
                var inconsistent=art.WithTransform(new GridPosition(0,4,0),art.QuarterTurns,art.Scale,Double3.Zero);
                Throws(()=>service.Place(fixture.Document,new[]{inconsistent}));
                using(var brush=service.BeginBrush(fixture.Document,art))
                {brush.SampleAttachment(art);brush.SampleAttachment(art);Require(brush.Preview.Count==1,"bound attachment stamps did not deduplicate");Require(brush.Complete(fixture.Document).Placements.Count==29,"bound attachment brush did not commit");}
            });
            Check("EDIT-SESSION-BRUSH-ONE-HISTORY-ENTRY",()=>
            {
                using(var session=new RoomSession(empty,definitionResolver:fixture.Resolve))
                using(var brush=tools.BeginBrush(session.Document,tools.Prepare(floor,default)))
                {
                    int notifications=0;session.DocumentChanged+=()=>notifications++;
                    brush.Sample(new GridCell2(0,0));brush.Sample(new GridCell2(16,0));
                    Require(session.ProjectHistory.RetainedSteps==0&&!session.ProjectHistory.IsDirty,"preview entered history");
                    Require(session.Apply("Brush",current=>brush.Complete(current)),"brush command was not committed");
                    Require(session.ProjectHistory.RetainedSteps==1&&session.Document.Placements.Count==5&&notifications==1,"one gesture produced multiple history facts");
                    Require(session.ProjectHistory.Undo()&&session.Document.Placements.Count==0&&!session.ProjectHistory.IsDirty,"undo brush did not restore baseline/savepoint");
                    Require(session.ProjectHistory.Redo()&&session.Document.Placements.Count==5,"redo brush lost placements");
                }
            });
            Check("EDIT-SESSION-HOST-ATOMIC-UNDO",()=>
            {
                using(var session=new RoomSession(fixture.Document,definitionResolver:fixture.Resolve))
                {
                    string initial=session.Document.StateId;int notifications=0;session.DocumentChanged+=()=>notifications++;
                    session.Apply("Scale wall",doc=>tools.SetWallScale(doc,"south-a",new Double3(2,.5,3)));
                    Require(session.ProjectHistory.RetainedSteps==1&&notifications==1,"host and attachment were split into commands");
                    Near(RoomGeometryFunctions.Origin(session.Document.Placements.Single(p=>p.Id=="wall-art")).Y,.75);
                    session.ProjectHistory.Undo();Require(session.Document.StateId==initial,"host undo state mismatch");
                    Near(RoomGeometryFunctions.Origin(session.Document.Placements.Single(p=>p.Id=="wall-art")).Y,1.5);
                    session.ProjectHistory.Redo();Near(RoomGeometryFunctions.Origin(session.Document.Placements.Single(p=>p.Id=="wall-art")).Y,.75);
                    session.Apply("Delete host",doc=>tools.Delete(doc,new[]{"south-a"}));Require(!session.Document.Placements.Any(p=>p.Id=="wall-art"),"delete left attachment");
                    session.ProjectHistory.Undo();Require(session.Document.Placements.Any(p=>p.Id=="wall-art"),"undo did not restore attachment");
                }
            });
            Check("EDIT-SESSION-REJECT-CANCEL-NO-SIDE-EFFECT",()=>
            {
                using(var session=new RoomSession(fixture.Document,definitionResolver:fixture.Resolve))
                {
                    var before=session.Document;int notifications=0;session.DocumentChanged+=()=>notifications++;
                    Throws(()=>session.Apply("Invalid scale",doc=>tools.SetWallScale(doc,"south-a",new Double3(1,.1,1))));
                    using(var cancel=new CancellationTokenSource()){cancel.Cancel();Throws<OperationCanceledException>(()=>session.Apply("Cancelled fill",doc=>tools.Fill(doc,new GridRect(0,0,8,8),floor,cancellation:cancel.Token),cancel.Token));}
                    Require(ReferenceEquals(session.Document,before)&&session.ProjectHistory.RetainedSteps==0&&notifications==0,"failed transaction published state");
                    using(var brush=tools.BeginBrush(session.Document,tools.Prepare(floor,new GridPosition(40,0,0))))
                    {brush.Sample(new GridCell2(40,0));brush.Cancel();Require(session.ProjectHistory.RetainedSteps==0,"cancel created history");}
                }
            });
            Check("EDIT-SESSION-FOUR-TURNS-AND-NOOP",()=>
            {
                using(var session=new RoomSession(fixture.Document,definitionResolver:fixture.Resolve))
                {
                    var original=session.Document.Placements.Single(p=>p.Id=="seat");string start=session.Document.StateId;
                    for(int i=0;i<4;i++)session.Apply("Quarter turn",doc=>tools.Move(doc,new[]{"seat","hanging"},default,1));
                    var final=session.Document.Placements.Single(p=>p.Id=="seat");Require(final.PivotOffset.Equals(original.PivotOffset)&&final.QuarterTurns==0,"service/history rotations drifted");
                    long steps=session.ProjectHistory.RetainedSteps;Require(!session.Apply("No move",doc=>tools.Move(doc,new[]{"seat"},default)),"no-op entered history");
                    Require(session.ProjectHistory.RetainedSteps==steps,"no-op consumed budget");
                    for(int i=0;i<4;i++)session.ProjectHistory.Undo();Require(session.Document.StateId==start&&!session.ProjectHistory.IsDirty,"four undo operations missed savepoint");
                }
            });
            Check("EDIT-SESSION-DRAFT-SAVEPOINT",()=>
            {
                using(var session=new RoomSession(fixture.Document,definitionResolver:fixture.Resolve))
                {
                    session.Apply("Raised draft",doc=>tools.Move(doc,new[]{"seat"},new GridPosition(0,1,0)));
                    Require(session.ProjectHistory.IsDirty&&tools.Index(session.Document).Diagnose().Any(i=>i.Code=="geometry.ground-height"),"invalid draft lost warning or dirty state");
                    string saved=session.Document.StateId;Require(session.ProjectHistory.MarkSaved(saved)&&!session.ProjectHistory.IsDirty,"draft savepoint refused");
                    session.ProjectHistory.Undo();Require(session.ProjectHistory.IsDirty,"undo away from saved draft should be dirty");
                    session.ProjectHistory.Redo();Require(!session.ProjectHistory.IsDirty,"redo to saved draft should be clean");
                }
            });
            Check("EDIT-SESSION-INDEX-REFRESH",()=>
            {
                using(var session=new RoomSession(fixture.Document,definitionResolver:fixture.Resolve))
                {
                    var area=new GridRect(17,4,19,7);Require(tools.Select(session.Document,area).Contains("seat"),"initial selection missing");
                    session.Apply("Move seat",doc=>tools.Move(doc,new[]{"seat"},new GridPosition(40,0,0)));Require(!tools.Select(session.Document,area).Contains("seat"),"index stale after move");
                    session.ProjectHistory.Undo();Require(tools.Select(session.Document,area).Contains("seat"),"index stale after undo");
                    session.Apply("Delete seat",doc=>tools.Delete(doc,new[]{"seat"}));Require(!tools.Select(session.Document,area).Contains("seat"),"index stale after delete");
                }
            });
            Check("GEOM-SPATIAL-BOUNDS-BEFORE-INTEGER-BUCKETS",()=>
            {
                double extreme=(double)int.MaxValue*4;
                foreach(var center in new[]{new Double3(extreme,0,0),new Double3(0,extreme,0),new Double3(0,0,-extreme)})
                {
                    var bounds=new Bounds3d(center,Double3.Zero);var pin=RoomSampleFixture.Pin("huge-zero-prop");
                    var definition=new LibraryDefinition(pin,"Bounded index rejection",DefinitionCategory.Prop,PlacementPolicy.Suspended,bounds,bounds,Matrix4d.Identity);
                    var placement=new RoomPlacement("huge",pin,default);Require(RoomGeometryFunctions.ValidScale(definition,placement),"case must reach the layout bounds guard");
                    var doc=new RoomDocument("huge-probe",placements:new[]{placement},pinnedDependencies:new[]{pin});
                    Throws<ArgumentOutOfRangeException>(()=>new RoomLayoutIndex(doc,p=>p.Equals(pin)?definition:null));
                }
                var ground=fixture.Resolve(RoomSampleFixture.Pin("prop-ground"));
                var pivot=new RoomPlacement("huge-pivot",ground.Reference,default,pivotOffset:new Double3(double.MaxValue,0,0));
                var pivotDoc=new RoomDocument("huge-pivot-probe",placements:new[]{pivot},pinnedDependencies:new[]{ground.Reference});
                Throws<ArgumentOutOfRangeException>(()=>new RoomLayoutIndex(pivotDoc,fixture.Resolve));
            });
            return results.AsReadOnly();
            void Check(string id,Action action){try{action();results.Add(new RoomGeometryCheck(id,true,"Expected independent invariants matched."));}catch(Exception ex){results.Add(new RoomGeometryCheck(id,false,ex.GetType().Name+": "+ex.Message));}}
        }
        private static IReadOnlyList<StructureTriangleData> Triangles(ModelMesh mesh)=>new[]{new StructureTriangleData(mesh.Positions.ToArray(),mesh.Submeshes.SelectMany(s=>s.TriangleIndices).ToArray())};
        private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        private static void Near(double actual,double expected){if(Math.Abs(actual-expected)>1e-9)throw new InvalidOperationException("Expected "+expected+", got "+actual);}
        private static void Throws(Action action)=>Throws<Exception>(action);
        private static void Throws<T>(Action action) where T:Exception {try{action();}catch(T){return;}throw new InvalidOperationException("Expected "+typeof(T).Name);}
    }
}
