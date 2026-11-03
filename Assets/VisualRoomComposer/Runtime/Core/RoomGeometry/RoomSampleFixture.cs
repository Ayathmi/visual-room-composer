using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VisualRoomComposer.Core.Documents;

namespace VisualRoomComposer.Core.RoomGeometry
{
    /// <summary>Original meter-scale procedural sample. Recipe pins identify this in-memory fixture, not published library envelopes.</summary>
    public sealed class RoomSampleFixture
    {
        public const string RecipeVersion="vrc-original-two-rooms-1";
        public RoomDocument Document { get; }
        public IReadOnlyList<LibraryDefinition> Definitions { get; }
        public IReadOnlyDictionary<string,ModelScene> Models { get; }
        public LibraryDefinition Resolve(PinnedReference reference)=>Definitions.SingleOrDefault(d=>d.Reference.Equals(reference));
        private RoomSampleFixture(RoomDocument document,IReadOnlyList<LibraryDefinition> definitions,Dictionary<string,ModelScene> models)
        {Document=document;Definitions=Array.AsReadOnly(definitions.ToArray());Models=new ReadOnlyDictionary<string,ModelScene>(models);}
        public static RoomSampleFixture Create()
        {
            var definitions=new List<LibraryDefinition>();var models=new Dictionary<string,ModelScene>(StringComparer.Ordinal);
            foreach(int mask in new[]{15,7,9,3,1})
            {
                var b=new Bounds3d(new Double3(.5,-.05,.5),new Double3(1,.1,1));
                Add(new LibraryDefinition(Pin("floor-"+mask),"地板 / Floor "+mask,DefinitionCategory.Floor,PlacementPolicy.Structure,b,b,Matrix4d.Identity,floorMask:mask));
            }
            var wallBounds=new Bounds3d(new Double3(1.5,1.5,0),new Double3(3,3,.25));
            Add(new LibraryDefinition(Pin("wall-solid"),"三米墙 / Wall",DefinitionCategory.Wall,PlacementPolicy.Structure,wallBounds,wallBounds,Matrix4d.Identity,
                wallBoundaries:new[]{new WallBoundary(new GridCell2(0,0),new GridCell2(12,0))}));
            var slot=new OpeningSlot("door",OpeningKind.Door,new Bounds3d(new Double3(1.5,1.125,0),new Double3(1,2.25,.25)));
            Add(new LibraryDefinition(Pin("wall-door"),"门洞隔墙 / Doorway",DefinitionCategory.Wall,PlacementPolicy.Structure,wallBounds,wallBounds,Matrix4d.Identity,
                wallBoundaries:new[]{new WallBoundary(new GridCell2(0,0),new GridCell2(12,0),WallBoundaryKind.Opening,"door")},openingSlots:new[]{slot}));
            var prop=new Bounds3d(new Double3(.25,.123,.375),new Double3(.5,.987,.75));
            Add(new LibraryDefinition(Pin("prop-ground"),"原创方凳 / Block seat",DefinitionCategory.Prop,PlacementPolicy.Ground,prop,prop,Matrix4d.Identity));
            var lamp=new Bounds3d(Double3.Zero,new Double3(.5,.25,.5));
            Add(new LibraryDefinition(Pin("prop-suspended"),"悬挂方块 / Hanging block",DefinitionCategory.Prop,PlacementPolicy.Suspended,lamp,lamp,Matrix4d.Identity));
            var art=new Bounds3d(Double3.Zero,new Double3(.75,.5,.125));
            Add(new LibraryDefinition(Pin("wall-art"),"墙面色块 / Wall color",DefinitionCategory.WallAttachment,PlacementPolicy.Wall,art,art,Matrix4d.Identity));
            var placements=new List<RoomPlacement>();
            for(int z=0;z<3;z++)for(int x=0;x<6;x++)placements.Add(new RoomPlacement("floor-"+x+"-"+z,Pin("floor-15"),new GridPosition(x*4,0,z*4),roomId:x<3?"room-a":"room-b"));
            Wall("south-a",0,0,0,"room-a");Wall("south-b",12,0,0,"room-b");Wall("north-a",0,12,0,"room-a");Wall("north-b",12,12,0,"room-b");
            Wall("west",0,12,1,"room-a");Wall("east",24,12,1,"room-b");
            placements.Add(new RoomPlacement("shared-door-wall",Pin("wall-door"),new GridPosition(12,0,12),1,roomId:"room-a"));
            placements.Add(new RoomPlacement("seat",Pin("prop-ground"),new GridPosition(17,0,4),roomId:"room-b",groupId:"furniture-b"));
            placements.Add(new RoomPlacement("hanging",Pin("prop-suspended"),new GridPosition(6,10,6),roomId:"room-a"));
            placements.Add(new RoomPlacement("wall-art",Pin("wall-art"),new GridPosition(6,6,0),pivotOffset:new Double3(0,0,.1875),roomId:"room-a",
                attachment:new PlacementAttachment("south-a",localOffset:new Double3(1.5,1.5,.1875))));
            var doc=new RoomDocument("original-two-rooms","双房间 / Two rooms",
                rooms:new[]{new Room("room-a","房间 A"),new Room("room-b","Room B",new[]{"shared-door-wall"})},
                groups:new[]{new RoomGroup("furniture-b","Furniture",roomId:"room-b")},placements:placements,
                pinnedDependencies:placements.Select(p=>p.Definition).Distinct().ToArray(),stateId:"original-two-rooms-state-1",
                description:"Original procedural geometry; 6 x 3 m footprint, two 3 x 3 m rooms, one shared doorway wall.");
            DocumentValidation.Validate(doc,p=>definitions.SingleOrDefault(d=>d.Reference.Equals(p)));
            return new RoomSampleFixture(doc,definitions,models);
            void Wall(string id,int x,int z,int yaw,string room)=>placements.Add(new RoomPlacement(id,Pin("wall-solid"),new GridPosition(x,0,z),yaw,roomId:room));
            void Add(LibraryDefinition d)
            {
                DocumentValidation.ValidateDefinition(d);definitions.Add(d);
                var mesh=RoomMeshPrimitives.Boxes(d.Id+"-mesh",RoomGeometryFunctions.LocalSolidParts(d),true);
                var materials=new[]{new ModelMaterial("mat-warm","Warm neutral",new Double4(.72,.62,.47,1)),new ModelMaterial("mat-blue","Original blue",new Double4(.2,.45,.62,1))};
                var scene=new ModelScene(d.Id+"-model",new[]{new ModelNode(d.Id+"-node",d.Name,null,Matrix4d.Identity,mesh.Id,materials.Select(m=>m.Id))},new[]{mesh},materials);
                ModelSceneValidator.ValidateOrThrow(scene);models.Add(d.Reference.Key,scene);
            }
        }
        public static PinnedReference Pin(string name)
        {
            using(var sha=SHA256.Create())return new PinnedReference("original-"+name,1,BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(RecipeVersion+"/"+name))).Replace("-","").ToLowerInvariant());
        }
    }
}
