using System;
using System.Collections.Generic;
using System.Linq;

namespace VisualRoomComposer.Core.RoomGeometry
{
    /// <summary>Original procedural box surfaces with explicit normals, UVs, and outward algebraic winding.</summary>
    public static class RoomMeshPrimitives
    {
        public static ModelMesh Boxes(string id, IEnumerable<GeometryBox> boxes, bool alternatingMaterials = false)
        {
            var vertices = new List<Double3>(); var normals = new List<Double3>(); var uv = new List<Double2>();
            var slots = new[] { new List<int>(), new List<int>() }; int part = 0;
            foreach (var box in boxes)
            {
                var a = box.Min; var b = box.Max;
                if (box.Size.X <= 0 || box.Size.Y <= 0 || box.Size.Z <= 0) throw new ArgumentException("Solid boxes must have positive dimensions.");
                var p = new[] { new Double3(a.X,a.Y,a.Z), new Double3(b.X,a.Y,a.Z), new Double3(b.X,b.Y,a.Z), new Double3(a.X,b.Y,a.Z),
                    new Double3(a.X,a.Y,b.Z), new Double3(b.X,a.Y,b.Z), new Double3(b.X,b.Y,b.Z), new Double3(a.X,b.Y,b.Z) };
                Face(0,3,2,1,-Double3.UnitZ); Face(4,5,6,7,Double3.UnitZ); Face(0,4,7,3,-Double3.UnitX);
                Face(1,2,6,5,Double3.UnitX); Face(0,1,5,4,-Double3.UnitY); Face(3,7,6,2,Double3.UnitY);
                part++;
                void Face(int i0, int i1, int i2, int i3, Double3 normal)
                {
                    int start = vertices.Count; vertices.AddRange(new[] { p[i0], p[i1], p[i2], p[i3] });
                    normals.AddRange(new[] { normal, normal, normal, normal }); uv.AddRange(new[] { new Double2(0,0), new Double2(1,0), new Double2(1,1), new Double2(0,1) });
                    slots[alternatingMaterials ? part % 2 : 0].AddRange(new[] { start, start+1, start+2, start, start+2, start+3 });
                }
            }
            return new ModelMesh(id, id, vertices, slots.Select((indices, i) => new ModelSubmesh(i, indices)).Where(s => s.TriangleIndices.Count > 0), normals, uv0: uv);
        }

        /// <summary>Two-sided triangle ray test used independently of logical opening metadata.</summary>
        public static bool RayHits(ModelMesh mesh, Double3 origin, Double3 direction, double maxDistance)
        {
            if (!origin.IsFinite || !direction.IsFinite || direction.LengthSquared == 0 || !Numeric.IsFinite(maxDistance) || maxDistance <= 0)
                throw new ArgumentException("Ray must be finite with a positive range.");
            direction = direction.Normalized();
            foreach (var submesh in mesh.Submeshes) for (int i = 0; i < submesh.TriangleIndices.Count; i += 3)
            {
                var a = mesh.Positions[submesh.TriangleIndices[i]]; var e1 = mesh.Positions[submesh.TriangleIndices[i+1]] - a;
                var e2 = mesh.Positions[submesh.TriangleIndices[i+2]] - a; var h = Double3.Cross(direction, e2);
                double determinant = Double3.Dot(e1, h); if (Math.Abs(determinant) < 1e-12) continue;
                var s = origin - a; double u = Double3.Dot(s, h) / determinant; if (u < 0 || u > 1) continue;
                var q = Double3.Cross(s, e1); double v = Double3.Dot(direction, q) / determinant; if (v < 0 || u + v > 1) continue;
                double distance = Double3.Dot(e2, q) / determinant; if (distance >= 0 && distance <= maxDistance) return true;
            }
            return false;
        }
    }
}
