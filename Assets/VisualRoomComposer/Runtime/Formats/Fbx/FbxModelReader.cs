using System;
using System.Collections.Generic;
using System.IO;
using VisualRoomComposer.Core;

namespace VisualRoomComposer.Formats.Fbx
{
    /// <summary>Independent static-triangle FBX feasibility reader. Unsupported semantics fail explicitly.</summary>
    public static class FbxModelReader
    {
        private sealed class Link
        { internal string Kind, Property; internal long Child, Parent; }
        private sealed class LayerReader
        {
            private readonly string mapping, reference;
            private readonly int width;
            private readonly int[] indices;
            private readonly double[] values;
            internal LayerReader(FbxElement layer, string dataName, string indicesName, int width, FbxBudget budget)
            {
                mapping = layer.Child("MappingInformationType")?.Text(); reference = layer.Child("ReferenceInformationType")?.Text(); this.width = width;
                indices = layer.Child(indicesName)?.Integers() ?? Array.Empty<int>(); values = layer.Child(dataName)?.Doubles() ?? Array.Empty<double>();
                budget.Allocate(values.Length * 8L + indices.Length * 4L);
                if (values.Length % width != 0) throw Fail("fbx.layer.width", "Layer component count is invalid.");
            }
            internal double At(int component, int control, int corner, int polygon)
            {
                int index;
                switch (mapping)
                {
                    case "ByVertice": case "ByVertex": case "ByControlPoint": index = control; break;
                    case "ByPolygonVertex": index = corner; break;
                    case "ByPolygon": index = polygon; break;
                    case "AllSame": index = 0; break;
                    default: throw Unsupported("Layer mapping is not implemented: " + mapping);
                }
                if (reference == "IndexToDirect" || reference == "Index")
                {
                    if (index < 0 || index >= indices.Length) throw Fail("fbx.layer.index", "Layer index is out of range."); index = indices[index];
                }
                else if (reference != "Direct") throw Unsupported("Layer reference is not implemented: " + reference);
                if (index < 0 || index >= values.Length / width) throw Fail("fbx.layer.range", "Layer value is out of range.");
                return values[index * width + component];
            }
        }

        public static ModelScene Read(string path, FbxReadOptions options = null)
        {
            var budget = new FbxBudget(options);
            try { return ConvertDocument(FbxSyntax.Read(path, budget), Path.GetFullPath(path), budget); }
            catch (FbxFormatException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is OverflowException || ex is FormatException || ex is IndexOutOfRangeException || ex is ArgumentException)
            { throw new FbxFormatException("fbx.invalid.data", ex.Message); }
        }

        private static ModelScene ConvertDocument(FbxDocument document, string path, FbxBudget budget)
        {
            var objects = document.Root("Objects") ?? throw Fail("fbx.objects.missing", "Objects are required.");
            var byId = new Dictionary<long, FbxElement>();
            foreach (var obj in objects.Children)
            {
                budget.Check();
                if (obj.Properties.Count < 3) throw Fail("fbx.object.header", "Object ID, name and type are required.");
                if (!byId.TryAdd(obj.Integer(), obj)) throw Fail("fbx.object.duplicate", "Duplicate object ID.");
                if (obj.Name == "Deformer" || obj.Name.StartsWith("Animation", StringComparison.Ordinal) || obj.Name == "Constraint")
                    throw Unsupported("Dynamic deformers, animation and constraints are outside this static probe.");
                if (obj.Name == "Video" && obj.Child("Content") != null) throw Unsupported("Embedded texture blobs are outside this probe.");
            }
            var links = new List<Link>();
            foreach (var c in document.Root("Connections")?.Children ?? new List<FbxElement>())
            {
                if (c.Name != "C" || c.Properties.Count < 3) throw Fail("fbx.connection.invalid", "Malformed connection.");
                var link = new Link { Kind = c.Text(), Child = c.Integer(1), Parent = c.Integer(2), Property = c.Text(3) };
                if (!byId.ContainsKey(link.Child) || (link.Parent != 0 && !byId.ContainsKey(link.Parent)))
                    throw Fail("fbx.connection.dangling", "Connection refers to a missing object.");
                links.Add(link);
            }
            var diagnostics = new List<ModelDiagnostic> {
                new ModelDiagnostic("fbx.probe.material.loss", DiagnosticSeverity.Warning, "Decode", "Probe maps diffuse color and diffuse PNG/JPEG texture only; FBX Phong/specular and PBR parameters are not preserved.") };
            var textures = new List<ModelTexture>(); var bindings = new Dictionary<long, string>();
            string directory = Path.GetDirectoryName(path);
            foreach (var obj in objects.All("Texture"))
            {
                budget.Check();
                string relative = obj.Child("RelativeFilename")?.Text();
                if (string.IsNullOrWhiteSpace(relative)) throw Unsupported("Textures require an explicit relative local path in this probe.");
                string texturePath = ResolveContainedFile(directory, relative);
                string extension = Path.GetExtension(texturePath).ToLowerInvariant();
                if (extension != ".png" && extension != ".jpg" && extension != ".jpeg") throw Unsupported("Only PNG/JPEG encoded textures are supported.");
                var info = new FileInfo(texturePath);
                if (info.Length > budget.Options.MaximumSourceBytes || info.Length > int.MaxValue) throw Fail("fbx.budget.texture", "Texture exceeds source budget.");
                budget.Allocate(info.Length * 3);
                byte[] bytes = File.ReadAllBytes(texturePath); budget.Check();
                string id = Id(obj.Integer()); bindings.Add(obj.Integer(), id);
                textures.Add(new ModelTexture(id, ObjectName(obj), extension == ".png" ? "image/png" : "image/jpeg", bytes, relative));
                var p = obj.Child("Properties70");
                if (Vector(p, "Translation", Double3.Zero).LengthSquared != 0 || Vector(p, "Rotation", Double3.Zero).LengthSquared != 0 ||
                    (Vector(p, "Scaling", Double3.One) - Double3.One).LengthSquared != 0)
                    throw Unsupported("Texture transforms are outside this probe.");
            }
            var materials = new List<ModelMaterial>();
            foreach (var obj in objects.All("Material"))
            {
                budget.Check(); var p = obj.Child("Properties70");
                var color = Vector(p, "DiffuseColor", Double3.One) * Scalar(p, "DiffuseFactor", 1);
                double opacity = Scalar(p, "Opacity", 1 - Scalar(p, "TransparencyFactor", 0));
                ModelTextureBinding texture = null;
                foreach (var link in links)
                {
                    budget.Check();
                    if (link.Parent == obj.Integer() && link.Kind == "OP" && byId[link.Child].Name == "Texture")
                    {
                        if (link.Property != "DiffuseColor") throw Unsupported("Only the DiffuseColor texture connection is implemented.");
                        if (texture != null) throw Unsupported("Layered diffuse textures are not implemented.");
                        texture = new ModelTextureBinding(bindings[link.Child]);
                    }
                }
                materials.Add(new ModelMaterial(Id(obj.Integer()), ObjectName(obj), new Double4(color.X, color.Y, color.Z, opacity),
                    alphaMode: opacity < 1 ? MaterialAlphaMode.Blend : MaterialAlphaMode.Opaque, baseColorTexture: texture));
            }
            var nodes = new List<ModelNode>(); var meshes = new List<ModelMesh>();
            foreach (var obj in objects.All("Model"))
            {
                budget.Check(); long key = obj.Integer();
                if (obj.Text(2) != "Mesh" && obj.Text(2) != "Null") throw Unsupported("Only Mesh and Null model nodes are implemented.");
                long parent = 0; int parents = 0; FbxElement geometry = null; var materialIds = new List<string>();
                foreach (var link in links)
                {
                    budget.Check();
                    if (link.Kind != "OO") continue;
                    if (link.Child == key && (link.Parent == 0 || byId[link.Parent].Name == "Model")) { parent = link.Parent; parents++; }
                    if (link.Parent != key) continue;
                    var child = byId[link.Child];
                    if (child.Name == "Geometry") { if (geometry != null) throw Unsupported("Multiple geometry attributes per model are not implemented."); geometry = child; }
                    if (child.Name == "Material") materialIds.Add(Id(link.Child));
                }
                if (parents > 1) throw Fail("fbx.hierarchy.parents", "Model has multiple parents.");
                var props = obj.Child("Properties70");
                int inheritance = (int)Scalar(props, "InheritType", 0);
                if (parent != 0 && inheritance != 1) throw Unsupported("Only RSrs (InheritType=1) is implemented for child nodes.");
                if ((int)Scalar(props, "RotationOrder", 0) != 0) throw Unsupported("Only XYZ Euler rotation is implemented.");
                Matrix4d local = Transform(props);
                string meshId = null;
                if (geometry != null)
                {
                    if (geometry.Text(2) != "Mesh") throw Unsupported("Only static mesh geometry is implemented.");
                    meshId = Id(key) + "/geometry";
                    var mesh = Mesh(geometry, meshId, materialIds.Count, budget);
                    var geometric = Matrix4d.Translation(Vector(props, "GeometricTranslation", Double3.Zero)) *
                        Rotation(Vector(props, "GeometricRotation", Double3.Zero)) * Matrix4d.Scale(Vector(props, "GeometricScaling", Double3.One));
                    mesh = ModelGeometry.TransformMesh(mesh, geometric, cancellation: budget.Options.Cancellation);
                    meshes.Add(mesh);
                }
                nodes.Add(new ModelNode(Id(key), ObjectName(obj), parent == 0 ? null : Id(parent), local, meshId, materialIds, geometry != null));
            }
            if (meshes.Count == 0) throw Fail("fbx.mesh.missing", "No static mesh was found.");
            var source = new ModelScene(Path.GetFileNameWithoutExtension(path), nodes, meshes, materials, textures, Coordinates(document), ModelCoordinateSpace.Source, diagnostics);
            budget.Check(); return ModelSceneNormalizer.ToCanonical(source, budget.Options.Cancellation);
        }

        private static ModelMesh Mesh(FbxElement geometry, string id, int materialCount, FbxBudget budget)
        {
            double[] vertices = geometry.Child("Vertices")?.Doubles() ?? throw Fail("fbx.vertices.missing", "Vertices are required.");
            int[] polygons = geometry.Child("PolygonVertexIndex")?.Integers() ?? throw Fail("fbx.indices.missing", "Polygon indices are required.");
            if (vertices.Length % 3 != 0 || polygons.Length % 3 != 0) throw Unsupported("Probe accepts triangulated geometry only.");
            if (polygons.Length > budget.Options.MaximumElements) throw Fail("fbx.budget.vertices", "Corner count exceeds limit.");
            budget.Allocate(polygons.Length * 200L + vertices.Length * 8L);
            var positions = new List<Double3>(polygons.Length); var normals = new List<Double3>(); var uv = new List<Double2>();
            var submeshes = new SortedDictionary<int, List<int>>();
            var normalLayer = geometry.Child("LayerElementNormal"); var uvLayer = geometry.Child("LayerElementUV");
            if (geometry.All("LayerElementUV") is ICollection<FbxElement> layers && layers.Count > 1) throw Unsupported("Multiple UV channels are outside this probe.");
            var materialLayer = geometry.Child("LayerElementMaterial");
            if (geometry.Child("LayerElementColor") != null || geometry.Child("LayerElementTangent") != null || geometry.Child("LayerElementBinormal") != null)
                throw Unsupported("Authored colors, tangent and binormal layers are outside this probe.");
            var normalReader = normalLayer == null ? null : new LayerReader(normalLayer, "Normals", "NormalsIndex", 3, budget);
            var uvReader = uvLayer == null ? null : new LayerReader(uvLayer, "UV", "UVIndex", 2, budget);
            int[] materialValues = materialLayer?.Child("Materials")?.Integers() ?? Array.Empty<int>();
            budget.Allocate(materialValues.Length * 4L);
            for (int i = 0; i < polygons.Length; i++)
            {
                if ((i & 1023) == 0) budget.Check();
                bool end = polygons[i] < 0;
                if (end != (i % 3 == 2)) throw Unsupported("Probe requires three corners per polygon; triangulate before import.");
                int control = end ? ~polygons[i] : polygons[i];
                if (control < 0 || control >= vertices.Length / 3) throw Fail("fbx.indices.range", "Control point index is out of range.");
                positions.Add(new Double3(vertices[control * 3], vertices[control * 3 + 1], vertices[control * 3 + 2]));
                if (normalReader != null) normals.Add(new Double3(normalReader.At(0, control, i, i / 3), normalReader.At(1, control, i, i / 3), normalReader.At(2, control, i, i / 3)));
                if (uvReader != null) uv.Add(new Double2(uvReader.At(0, control, i, i / 3), uvReader.At(1, control, i, i / 3)));
                int slot = materialCount == 0 ? -1 : 0;
                if (materialLayer != null)
                {
                    string map = materialLayer.Child("MappingInformationType")?.Text();
                    if (map != "AllSame" && map != "ByPolygon") throw Unsupported("Material mapping must be AllSame or ByPolygon.");
                    int at = map == "AllSame" ? 0 : i / 3;
                    if (at >= materialValues.Length) throw Fail("fbx.material.range", "Missing polygon material slot.");
                    slot = materialValues[at];
                }
                if (slot < -1 || slot >= materialCount) throw Fail("fbx.material.range", "Material slot is out of range.");
                if (!submeshes.TryGetValue(slot, out var indices)) { indices = new List<int>(); submeshes.Add(slot, indices); }
                indices.Add(i);
            }
            var subs = new List<ModelSubmesh>(); foreach (var pair in submeshes) subs.Add(new ModelSubmesh(pair.Key, pair.Value));
            return new ModelMesh(id, ObjectName(geometry), positions, subs, normals, uv0: uv);
        }

        private static SourceCoordinateSystem Coordinates(FbxDocument document)
        {
            var props = document.Root("GlobalSettings")?.Child("Properties70") ?? throw Fail("fbx.axis.missing", "Explicit global axes and units are required.");
            int up = (int)Scalar(props, "UpAxis", -1), front = (int)Scalar(props, "FrontAxis", -1), right = (int)Scalar(props, "CoordAxis", -1);
            int us = (int)Scalar(props, "UpAxisSign", 0), fs = (int)Scalar(props, "FrontAxisSign", 0), rs = (int)Scalar(props, "CoordAxisSign", 0);
            if (up < 0 || up > 2 || front < 0 || front > 2 || right < 0 || right > 2 || up == front || up == right || front == right ||
                Math.Abs(us) != 1 || Math.Abs(fs) != 1 || Math.Abs(rs) != 1) throw Fail("fbx.axis.invalid", "Invalid or missing axis declarations.");
            var axes = new Double3[3]; axes[up] = Double3.UnitY * us; axes[front] = -Double3.UnitZ * fs; axes[right] = Double3.UnitX * rs;
            return new SourceCoordinateSystem("FBX declared axes", Scalar(props, "UnitScaleFactor", -1) / 100, axes[0], axes[1], axes[2], false);
        }
        private static Matrix4d Transform(FbxElement p)
        {
            var rp = Vector(p, "RotationPivot", Double3.Zero); var sp = Vector(p, "ScalingPivot", Double3.Zero);
            return Matrix4d.Translation(Vector(p, "Lcl Translation", Double3.Zero)) * Matrix4d.Translation(Vector(p, "RotationOffset", Double3.Zero)) *
                Matrix4d.Translation(rp) * Rotation(Vector(p, "PreRotation", Double3.Zero)) * Rotation(Vector(p, "Lcl Rotation", Double3.Zero)) *
                Rotation(Vector(p, "PostRotation", Double3.Zero)).InverseAffine() * Matrix4d.Translation(-rp) *
                Matrix4d.Translation(Vector(p, "ScalingOffset", Double3.Zero)) * Matrix4d.Translation(sp) *
                Matrix4d.Scale(Vector(p, "Lcl Scaling", Double3.One)) * Matrix4d.Translation(-sp);
        }
        private static Matrix4d Rotation(Double3 degrees)
        { const double r = Math.PI / 180; return Matrix4d.RotationZ(degrees.Z * r) * Matrix4d.RotationY(degrees.Y * r) * Matrix4d.RotationX(degrees.X * r); }
        private static FbxElement Property(FbxElement props, string name) => props?.Children.Find(p => p.Name == "P" && p.Text() == name);
        private static double Scalar(FbxElement props, string name, double fallback) => Property(props, name)?.Number(4) ?? fallback;
        private static Double3 Vector(FbxElement props, string name, Double3 fallback)
        { var p = Property(props, name); return p == null ? fallback : new Double3(p.Number(4), p.Number(5), p.Number(6)); }
        private static string Id(long id) => "fbx:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static string ObjectName(FbxElement obj)
        { string name = obj.Text(1); int i = name.IndexOf('\0'); if (i >= 0) name = name.Substring(0, i); i = name.IndexOf("::", StringComparison.Ordinal); return i < 0 ? name : name.Substring(i + 2); }
        private static string ResolveContainedFile(string root, string relative)
        {
            if (Path.IsPathRooted(relative) || relative.Contains(":") || relative.StartsWith("\\", StringComparison.Ordinal)) throw Fail("fbx.path.denied", "Texture path must be relative.");
            string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(basePath, relative.Replace('\\', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw Fail("fbx.path.denied", "Texture path escapes source directory.");
            string current = full;
            while (current.Length >= basePath.Length)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Fail("fbx.path.denied", "Reparse points are rejected by the probe.");
                current = Path.GetDirectoryName(current);
            }
            return full;
        }
        private static FbxFormatException Unsupported(string message) => Fail("fbx.NotImplemented", message);
        private static FbxFormatException Fail(string code, string message) => new FbxFormatException(code, message);
    }
}
