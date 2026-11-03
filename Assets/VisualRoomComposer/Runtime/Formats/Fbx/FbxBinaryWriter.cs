using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using VisualRoomComposer.Core;

namespace VisualRoomComposer.Formats.Fbx
{
    public sealed class FbxWriteOptions
    {
        public CancellationToken Cancellation { get; set; }
        public long MaximumOutputBytes { get; set; } = 256L * 1024 * 1024;
        public long MaximumWorkingBytes { get; set; } = 512L * 1024 * 1024;
    }

    public sealed class FbxWriteResult
    {
        public string FbxPath { get; internal set; }
        public IReadOnlyList<string> TexturePaths { get; internal set; }
        public IReadOnlyList<string> Warnings { get; internal set; }
    }

    /// <summary>Original 7400 binary static writer. Requires a new, private output directory.</summary>
    public static class FbxBinaryWriter
    {
        public static FbxWriteResult Write(ModelScene scene, string path, FbxWriteOptions options = null)
        {
            options = options ?? new FbxWriteOptions(); options.Cancellation.ThrowIfCancellationRequested();
            if (options.MaximumOutputBytes < 1 || options.MaximumOutputBytes > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(options));
            ModelSceneValidator.ValidateOrThrow(scene, cancellation: options.Cancellation);
            if (scene.CoordinateSpace != ModelCoordinateSpace.Canonical) throw new ArgumentException("Canonical scene required.", nameof(scene));
            long estimatedWorking = checked(scene.Nodes.Count * 2048L + scene.Materials.Count * 4096L);
            var inputMeshes = new Dictionary<string, ModelMesh>(); foreach (var mesh in scene.Meshes) inputMeshes.Add(mesh.Id, mesh);
            foreach (var node in scene.Nodes)
            {
                options.Cancellation.ThrowIfCancellationRequested(); if (node.MeshId == null) continue;
                var mesh = inputMeshes[node.MeshId]; estimatedWorking = checked(estimatedWorking + mesh.Positions.Count * 512L);
                foreach (var sub in mesh.Submeshes) estimatedWorking = checked(estimatedWorking + sub.TriangleIndices.Count * 256L);
            }
            foreach (var texture in scene.Textures) estimatedWorking = checked(estimatedWorking + texture.ByteLength * 4L);
            if (options.MaximumWorkingBytes < 1 || estimatedWorking > options.MaximumWorkingBytes)
                throw new FbxFormatException("fbx.budget.working", "Conservative writer working-allocation budget exceeded before geometry baking.");
            string full = Path.GetFullPath(path), directory = Path.GetDirectoryName(full);
            if (!Directory.Exists(directory) || Directory.GetFileSystemEntries(directory).Length != 0)
                throw new IOException("The feasibility writer requires an existing empty private output directory.");
            // Probe policy: bake exact world affine geometry; keep logical names and hierarchy as identity nodes.
            // This explicitly avoids lossy shear decomposition. Full authored pivot/origin preservation is a later capability.
            var baked = ModelGeometry.BakeSceneToWorld(scene);
            var roots = new List<FbxElement>();
            var header = E("FBXHeaderExtension"); header.Children.Add(E("FBXHeaderVersion", 1003));
            header.Children.Add(E("FBXVersion", 7400)); header.Children.Add(E("EncryptionType", 0));
            var time = E("CreationTimeStamp"); time.Children.Add(E("Version", 1000));
            foreach (var pair in new[] { ("Year", 1970), ("Month", 1), ("Day", 1), ("Hour", 10), ("Minute", 0), ("Second", 0), ("Millisecond", 0) }) time.Children.Add(E(pair.Item1, pair.Item2));
            header.Children.Add(time); header.Children.Add(E("Creator", "visual-room-composer independent static probe")); roots.Add(header);
            // FBX binary consumers validate the FileId/CreationTime/footer tuple. These protocol data
            // were observed in independent reference files and isolated against the native Unity importer.
            // This fixed compatibility timestamp is not the actual export time; provenance belongs in the report.
            roots.Add(E("FileId", new byte[] { 0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1 }));
            roots.Add(E("CreationTime", "1970-01-01 10:00:00:000"));
            roots.Add(E("Creator", "visual-room-composer independent static probe"));
            var global = E("GlobalSettings"); global.Children.Add(E("Version", 1000)); var globals = E("Properties70");
            globals.Children.Add(P("UpAxis", "int", 1)); globals.Children.Add(P("UpAxisSign", "int", 1));
            globals.Children.Add(P("FrontAxis", "int", 2)); globals.Children.Add(P("FrontAxisSign", "int", 1));
            globals.Children.Add(P("CoordAxis", "int", 0)); globals.Children.Add(P("CoordAxisSign", "int", 1));
            globals.Children.Add(P("UnitScaleFactor", "double", 100.0)); globals.Children.Add(P("OriginalUnitScaleFactor", "double", 100.0));
            global.Children.Add(globals); roots.Add(global);
            var docs = E("Documents"); docs.Children.Add(E("Count", 1)); var doc = E("Document", 1L, "Scene", "Scene");
            doc.Children.Add(E("Properties70")); doc.Children.Add(E("RootNode", 0L)); docs.Children.Add(doc); roots.Add(docs); roots.Add(E("References"));
            var objects = E("Objects"); var connections = E("Connections"); var ids = new Dictionary<string, long>(); long nextId = 1000;
            foreach (var n in baked.Nodes) ids.Add(n.Id, nextId++);
            var meshIds = new Dictionary<string, long>(); foreach (var m in baked.Meshes) meshIds.Add(m.Id, nextId++);
            var matIds = new Dictionary<string, long>(); foreach (var m in baked.Materials) matIds.Add(m.Id, nextId++);
            var texIds = new Dictionary<string, long>(); foreach (var t in baked.Textures) texIds.Add(t.Id, nextId++);
            var texturePaths = new List<string>(); var warnings = new List<string> {
                "Probe bakes all world affine geometry; logical hierarchy and names remain, but node origins/pivots become identity.",
                "Only diffuse color, opacity and base-color PNG/JPEG are exported. Metallic, roughness, normal, emission, alpha mode and sampler semantics are lossy."
            };
            var files = new Dictionary<string, byte[]>(); long textureBytes = 0;
            foreach (var texture in baked.Textures)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                string extension = texture.MimeType == "image/png" ? ".png" : texture.MimeType == "image/jpeg" ? ".jpg" : null;
                if (extension == null) throw new FbxFormatException("fbx.NotImplemented", "Only PNG/JPEG output textures are implemented.");
                string name = "texture-" + texture.ContentHash + extension;
                if (!files.ContainsKey(name)) { textureBytes = checked(textureBytes + texture.ByteLength); files.Add(name, texture.CopyEncodedBytes()); }
                if (textureBytes > options.MaximumOutputBytes) throw new FbxFormatException("fbx.budget.output", "Output texture budget exceeded.");
                long textureId = texIds[texture.Id], videoId = nextId++;
                var video = E("Video", videoId, texture.Name + "\0\x01Video", "Clip"); video.Children.Add(E("Type", "Clip"));
                var videoProps = E("Properties70"); videoProps.Children.Add(P("Path", "KString", name)); video.Children.Add(videoProps);
                video.Children.Add(E("UseMipMap", 0)); video.Children.Add(E("Filename", name)); video.Children.Add(E("RelativeFilename", name)); objects.Children.Add(video);
                var tex = E("Texture", textureId, texture.Name + "\0\x01Texture", ""); tex.Children.Add(E("Type", "TextureVideoClip"));
                tex.Children.Add(E("Version", 202)); tex.Children.Add(E("TextureName", texture.Name + "\0\x01Texture")); tex.Children.Add(E("Media", texture.Name + "\0\x01Video"));
                tex.Children.Add(E("FileName", name)); tex.Children.Add(E("RelativeFilename", name)); tex.Children.Add(E("ModelUVTranslation", 0.0, 0.0));
                tex.Children.Add(E("ModelUVScaling", 1.0, 1.0)); tex.Children.Add(E("Texture_Alpha_Source", "None")); tex.Children.Add(E("Cropping", 0, 0, 0, 0));
                objects.Children.Add(tex); connections.Children.Add(E("C", "OO", videoId, textureId));
            }
            foreach (var material in baked.Materials)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                var mat = E("Material", matIds[material.Id], material.Name + "\0\x01Material", "");
                mat.Children.Add(E("Version", 102)); mat.Children.Add(E("ShadingModel", "phong")); mat.Children.Add(E("MultiLayer", 0));
                var props = E("Properties70"); var color = material.BaseColor;
                props.Children.Add(P("DiffuseColor", "Color", color.X, color.Y, color.Z)); props.Children.Add(P("DiffuseFactor", "Number", 1.0));
                props.Children.Add(P("Opacity", "Number", color.W)); props.Children.Add(P("TransparencyFactor", "Number", 1.0 - color.W));
                props.Children.Add(P("SpecularColor", "Color", 0.0, 0.0, 0.0)); mat.Children.Add(props); objects.Children.Add(mat);
                if (material.BaseColorTexture != null)
                {
                    var binding = material.BaseColorTexture;
                    if (binding.TexCoord != 0 || binding.RotationRadians != 0 || binding.Offset.X != 0 || binding.Offset.Y != 0 || binding.Scale.X != 1 || binding.Scale.Y != 1)
                        throw new FbxFormatException("fbx.NotImplemented", "Exporting UV selection or texture transforms is outside this probe.");
                    connections.Children.Add(E("C", "OP", texIds[binding.TextureId], matIds[material.Id], "DiffuseColor"));
                }
            }
            var reflect = Matrix4d.Scale(new Double3(1, 1, -1));
            foreach (var sourceMesh in baked.Meshes)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                var mesh = ModelGeometry.TransformMesh(sourceMesh, reflect, cancellation: options.Cancellation);
                var geometry = E("Geometry", meshIds[mesh.Id], mesh.Name + "\0\x01Geometry", "Mesh"); geometry.Children.Add(E("GeometryVersion", 124));
                var points = new double[mesh.Positions.Count * 3];
                for (int i = 0; i < mesh.Positions.Count; i++) { points[3 * i] = mesh.Positions[i].X; points[3 * i + 1] = mesh.Positions[i].Y; points[3 * i + 2] = mesh.Positions[i].Z; }
                var polygon = new List<int>(); var materialSlots = new List<int>(); var cornerNormals = new List<double>(); var cornerUv = new List<double>();
                foreach (var sub in mesh.Submeshes)
                    for (int i = 0; i < sub.TriangleIndices.Count; i++)
                    {
                        if ((i & 4095) == 0) options.Cancellation.ThrowIfCancellationRequested();
                        int index = sub.TriangleIndices[i]; polygon.Add(i % 3 == 2 ? ~index : index);
                        if (i % 3 == 0) materialSlots.Add(sub.MaterialSlot);
                        if (mesh.Normals.Count != 0) { var n = mesh.Normals[index]; cornerNormals.Add(n.X); cornerNormals.Add(n.Y); cornerNormals.Add(n.Z); }
                        if (mesh.Uv0.Count != 0) { var uv = mesh.Uv0[index]; cornerUv.Add(uv.X); cornerUv.Add(uv.Y); }
                    }
                geometry.Children.Add(E("Vertices", points)); geometry.Children.Add(E("PolygonVertexIndex", polygon.ToArray()));
                if (cornerNormals.Count != 0) geometry.Children.Add(Layer("LayerElementNormal", "Normals", cornerNormals.ToArray()));
                if (cornerUv.Count != 0) geometry.Children.Add(Layer("LayerElementUV", "UV", cornerUv.ToArray()));
                var materials = E("LayerElementMaterial", 0); materials.Children.Add(E("Version", 101)); materials.Children.Add(E("Name", ""));
                materials.Children.Add(E("MappingInformationType", "ByPolygon")); materials.Children.Add(E("ReferenceInformationType", "IndexToDirect"));
                materials.Children.Add(E("Materials", materialSlots.ToArray())); geometry.Children.Add(materials);
                var layer = E("Layer", 0); layer.Children.Add(E("Version", 100));
                foreach (string type in new[] { "LayerElementNormal", "LayerElementMaterial", "LayerElementUV" })
                    if (geometry.Child(type) != null) { var element = E("LayerElement"); element.Children.Add(E("Type", type)); element.Children.Add(E("TypedIndex", 0)); layer.Children.Add(element); }
                geometry.Children.Add(layer); objects.Children.Add(geometry);
            }
            foreach (var node in baked.Nodes)
            {
                var model = E("Model", ids[node.Id], node.Name + "\0\x01Model", node.MeshId == null ? "Null" : "Mesh"); model.Children.Add(E("Version", 232));
                var props = E("Properties70"); props.Children.Add(P("Lcl Translation", "Lcl Translation", 0.0, 0.0, 0.0));
                props.Children.Add(P("Lcl Rotation", "Lcl Rotation", 0.0, 0.0, 0.0)); props.Children.Add(P("Lcl Scaling", "Lcl Scaling", 1.0, 1.0, 1.0));
                props.Children.Add(P("InheritType", "enum", 1)); props.Children.Add(P("DefaultAttributeIndex", "int", 0)); model.Children.Add(props);
                model.Children.Add(E("Shading", true)); model.Children.Add(E("Culling", "CullingOff")); objects.Children.Add(model);
                connections.Children.Add(E("C", "OO", ids[node.Id], node.ParentId == null ? 0L : ids[node.ParentId]));
                if (node.MeshId != null) connections.Children.Add(E("C", "OO", meshIds[node.MeshId], ids[node.Id]));
                foreach (var material in node.MaterialIds) connections.Children.Add(E("C", "OO", matIds[material], ids[node.Id]));
            }
            var definitions = E("Definitions"); definitions.Children.Add(E("Version", 100)); definitions.Children.Add(E("Count", objects.Children.Count));
            foreach (string type in new[] { "Model", "Geometry", "Material", "Texture", "Video" })
            {
                int count = objects.Children.FindAll(item => item.Name == type).Count;
                if (count == 0) continue;
                var item = E("ObjectType", type); item.Children.Add(E("Count", count)); definitions.Children.Add(item);
            }
            roots.Add(definitions); roots.Add(objects); roots.Add(connections); var takes = E("Takes"); takes.Children.Add(E("Current", "")); roots.Add(takes);
            byte[] encoded = Encode(roots, options, textureBytes);
            // This is a probe output set, not the final user-facing atomic publish transaction.
            var created = new List<string>();
            try
            {
                foreach (var pair in files)
                {
                    options.Cancellation.ThrowIfCancellationRequested(); string target = Path.Combine(directory, pair.Key);
                    WriteNew(target, pair.Value, options.Cancellation); created.Add(target); texturePaths.Add(target);
                }
                options.Cancellation.ThrowIfCancellationRequested(); WriteNew(full, encoded, options.Cancellation); created.Add(full);
                return new FbxWriteResult { FbxPath = full, TexturePaths = texturePaths.AsReadOnly(), Warnings = warnings.AsReadOnly() };
            }
            catch
            {
                foreach (var createdPath in created) { try { File.Delete(createdPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                throw;
            }
        }

        private static void WriteNew(string path, byte[] bytes, CancellationToken cancellation)
        {
            bool created = false;
            try
            {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    for (int offset = 0; offset < bytes.Length; offset += 65536) { cancellation.ThrowIfCancellationRequested(); file.Write(bytes, offset, Math.Min(65536, bytes.Length - offset)); }
                    file.Flush();
                }
            }
            catch { if (created) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } } throw; }
        }
        private static FbxElement E(string name, params object[] properties) => new FbxElement(name, properties);
        private static FbxElement P(string name, string type, params object[] values)
        {
            string label = type == "int" ? "Integer" : type == "double" || type == "Number" ? "Number" : type == "Color" ? "Color" : "";
            string dataType = type == "Color" ? "ColorRGB" : type == "Number" ? "double" : type;
            var p = E("P", name, dataType, label, "A"); p.Properties.AddRange(values); return p;
        }
        private static FbxElement Layer(string type, string values, double[] data)
        {
            var layer = E(type, 0); layer.Children.Add(E("Version", 101)); layer.Children.Add(E("Name", type == "LayerElementUV" ? "UVMap" : ""));
            layer.Children.Add(E("MappingInformationType", "ByPolygonVertex")); layer.Children.Add(E("ReferenceInformationType", "Direct")); layer.Children.Add(E(values, data)); return layer;
        }
        private static byte[] Encode(List<FbxElement> roots, FbxWriteOptions options, long otherBytes)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(FbxSyntax.Magic); writer.Write(7400);
                foreach (var root in roots) WriteElement(writer, root, options, otherBytes);
                writer.Write(new byte[13]);
                writer.Write(new byte[] { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e });
                while (stream.Position % 16 != 0) writer.Write((byte)0);
                writer.Write(7400); writer.Write(new byte[120]);
                writer.Write(new byte[] { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b });
                if (stream.Length + otherBytes > options.MaximumOutputBytes) throw new FbxFormatException("fbx.budget.output", "Output budget exceeded.");
                return stream.ToArray();
            }
        }
        private static void WriteElement(BinaryWriter writer, FbxElement node, FbxWriteOptions options, long otherBytes)
        {
            options.Cancellation.ThrowIfCancellationRequested(); var stream = writer.BaseStream; long start = stream.Position;
            byte[] name = Encoding.UTF8.GetBytes(node.Name); if (name.Length > 255) throw new ArgumentException("FBX element name too long.");
            writer.Write(new byte[13]); writer.Write(name); long propertyStart = stream.Position;
            foreach (object value in node.Properties)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                if (value is int integer) { writer.Write((byte)'I'); writer.Write(integer); }
                else if (value is long large) { writer.Write((byte)'L'); writer.Write(large); }
                else if (value is double real) { writer.Write((byte)'D'); writer.Write(real); }
                else if (value is bool flag) { writer.Write((byte)'C'); writer.Write(flag); }
                else if (value is string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write((byte)'S'); writer.Write((uint)bytes.Length); writer.Write(bytes); }
                else if (value is byte[] bytes) { writer.Write((byte)'R'); writer.Write((uint)bytes.Length); writer.Write(bytes); }
                else if (value is double[] doubles) { writer.Write((byte)'d'); writer.Write((uint)doubles.Length); writer.Write(0); writer.Write(checked((uint)doubles.Length * 8)); for (int i = 0; i < doubles.Length; i++) { if ((i & 4095) == 0) options.Cancellation.ThrowIfCancellationRequested(); writer.Write(doubles[i]); } }
                else if (value is int[] ints) { writer.Write((byte)'i'); writer.Write((uint)ints.Length); writer.Write(0); writer.Write(checked((uint)ints.Length * 4)); for (int i = 0; i < ints.Length; i++) { if ((i & 4095) == 0) options.Cancellation.ThrowIfCancellationRequested(); writer.Write(ints[i]); } }
                else throw new InvalidOperationException("Unsupported internal FBX property type.");
                if (stream.Position + otherBytes > options.MaximumOutputBytes) throw new FbxFormatException("fbx.budget.output", "Output budget exceeded.");
            }
            long propertyLength = stream.Position - propertyStart;
            foreach (var child in node.Children) WriteElement(writer, child, options, otherBytes);
            // Empty containers (notably References and Properties70) still need a block terminator.
            // Their end offset includes that terminator; omitting it lets SDK consumers misread following sections.
            if (node.Children.Count != 0 || node.Properties.Count == 0) writer.Write(new byte[13]);
            long end = stream.Position; stream.Position = start;
            writer.Write(checked((uint)end)); writer.Write((uint)node.Properties.Count); writer.Write(checked((uint)propertyLength)); writer.Write((byte)name.Length); stream.Position = end;
        }
    }
}
