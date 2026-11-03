using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using VisualRoomComposer.Core;

namespace VisualRoomComposer.Infrastructure.Persistence
{
    /// <summary>Explicit numeric wire format; immutable math and model objects are always rebuilt through constructors.</summary>
    public static class ModelSceneCodec
    {
        public const string Format = "visual-room-composer.cpu-model";
        private static double[] V(Double2 v) => new[] { v.X, v.Y };
        private static double[] V(Double3 v) => new[] { v.X, v.Y, v.Z };
        private static double[] V(Double4 v) => new[] { v.X, v.Y, v.Z, v.W };
        private static object Binding(ModelTextureBinding b) => b == null ? null : new
        { b.TextureId, b.TexCoord, colorSpace = (int)b.ColorSpace, wrapU = (int)b.WrapU, wrapV = (int)b.WrapV,
            minFilter = (int)b.MinFilter, magFilter = (int)b.MagFilter, offset = V(b.Offset), scale = V(b.Scale), b.RotationRadians, b.Channels };
        public static byte[] Encode(ModelScene scene, CancellationToken cancellation = default)
        {
            ModelSceneValidator.ValidateOrThrow(scene, cancellation: cancellation);
            if (scene.CoordinateSpace != ModelCoordinateSpace.Canonical) throw new StorageException("InvalidSchema", "Persisted models must already use canonical coordinates.");
            var c = scene.SourceCoordinates;
            var bytes = StorageJson.Encode(new
            {
                format = Format, schemaVersion = 1, scene.Id, coordinateSpace = (int)scene.CoordinateSpace,
                sourceCoordinates = new { c.Name, c.MetersPerUnit, x = V(c.SourceXInCanonical), y = V(c.SourceYInCanonical), z = V(c.SourceZInCanonical), c.SourceFrontFaceClockwise },
                nodes = scene.Nodes.Select(n => new { n.Id, n.Name, n.ParentId, localTransform = n.LocalTransform.ToRowMajorArray(), n.MeshId, n.MaterialIds, n.GeometryTransformBaked }),
                meshes = scene.Meshes.Select(m => new { m.Id, m.Name, positions = m.Positions.Select(V), normals = m.Normals.Select(V), tangents = m.Tangents.Select(V), uv0 = m.Uv0.Select(V), uv1 = m.Uv1.Select(V), colors = m.Colors.Select(V), submeshes = m.Submeshes.Select(s => new { s.MaterialSlot, s.TriangleIndices }) }),
                materials = scene.Materials.Select(m => new { m.Id, m.Name, baseColor = V(m.BaseColor), m.Metallic, m.Roughness, emissive = V(m.Emissive), alphaMode = (int)m.AlphaMode, m.AlphaCutoff, m.DoubleSided, m.NormalScale, m.OcclusionStrength,
                    baseColorTexture = Binding(m.BaseColorTexture), metallicRoughnessTexture = Binding(m.MetallicRoughnessTexture), normalTexture = Binding(m.NormalTexture), occlusionTexture = Binding(m.OcclusionTexture), emissiveTexture = Binding(m.EmissiveTexture) }),
                textures = scene.Textures.Select(t => new { t.Id, t.Name, t.MimeType, t.SourceUri, t.ContentHash, bytes = Convert.ToBase64String(t.CopyEncodedBytes()) }),
                diagnostics = scene.Diagnostics.Select(d => new { d.Code, severity = (int)d.Severity, d.Stage, d.Message, d.Source, d.ByteOffset, d.NodeId, d.SuggestedAction })
            }, cancellation);
            cancellation.ThrowIfCancellationRequested(); return bytes;
        }
        public static ModelScene Decode(byte[] bytes, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            using (var document = StorageJson.Parse(bytes, cancellation)) return DecodeElement(document.RootElement, cancellation);
        }
        private static ModelScene DecodeElement(JsonElement e, CancellationToken cancellation)
        {
            StorageJson.Shape(e, "format", "schemaVersion", "id", "coordinateSpace", "sourceCoordinates", "nodes", "meshes", "materials", "textures", "diagnostics");
            if (StorageJson.S(e, "format") != Format || StorageJson.I(e, "schemaVersion") != 1 || StorageJson.I(e, "coordinateSpace") != 1)
                throw new StorageException("InvalidSchema", "Unsupported CPU model schema or coordinate space.");
            var c = StorageJson.Get(e, "sourceCoordinates"); StorageJson.Shape(c, "name", "metersPerUnit", "x", "y", "z", "sourceFrontFaceClockwise");
            var coordinates = new SourceCoordinateSystem(StorageJson.S(c, "name"), StorageJson.D(c, "metersPerUnit"), V3(c.GetProperty("x")), V3(c.GetProperty("y")), V3(c.GetProperty("z")), StorageJson.B(c, "sourceFrontFaceClockwise"));
            var nodes = new List<ModelNode>();
            foreach (var n in StorageJson.A(e, "nodes", 65536))
            {
                cancellation.ThrowIfCancellationRequested(); StorageJson.Shape(n, "id", "name", "parentId", "localTransform", "meshId", "materialIds", "geometryTransformBaked");
                nodes.Add(new ModelNode(StorageJson.S(n, "id"), StorageJson.S(n, "name"), StorageJson.S(n, "parentId"), Matrix4d.FromRowMajor(Numbers(n.GetProperty("localTransform"), 16)), StorageJson.S(n, "meshId"), StorageJson.A(n, "materialIds", 4096).Select(v => v.GetString()), StorageJson.B(n, "geometryTransformBaked")));
            }
            var meshes = new List<ModelMesh>(); long vertices = 0, triangles = 0, payload = 0;
            foreach (var m in StorageJson.A(e, "meshes", 4096))
            {
                cancellation.ThrowIfCancellationRequested(); StorageJson.Shape(m, "id", "name", "positions", "normals", "tangents", "uv0", "uv1", "colors", "submeshes");
                vertices += m.GetProperty("positions").GetArrayLength();
                foreach (string field in new[] { "positions", "normals", "tangents", "uv0", "uv1", "colors" }) payload += (long)m.GetProperty(field).GetArrayLength() * 32;
                if (vertices > 2000000 || payload > 128L * 1024 * 1024) throw new StorageException("LimitExceeded", "Decoded model payload budget exceeded.");
                var submeshes = new List<ModelSubmesh>();
                foreach (var s in StorageJson.A(m, "submeshes", 4096))
                { StorageJson.Shape(s, "materialSlot", "triangleIndices"); int length = s.GetProperty("triangleIndices").GetArrayLength(); triangles += length / 3; if (triangles > 4000000 || length % 3 != 0) throw new StorageException("LimitExceeded", "Triangle budget or index grouping invalid."); submeshes.Add(new ModelSubmesh(StorageJson.I(s, "materialSlot"), StorageJson.A(s, "triangleIndices", 12000000).Select(v => v.GetInt32()))); }
                meshes.Add(new ModelMesh(StorageJson.S(m, "id"), StorageJson.S(m, "name"), StorageJson.A(m, "positions").Select(V3), submeshes,
                    StorageJson.A(m, "normals").Select(V3), StorageJson.A(m, "tangents").Select(V4), StorageJson.A(m, "uv0").Select(V2), StorageJson.A(m, "uv1").Select(V2), StorageJson.A(m, "colors").Select(V4)));
            }
            var materials = new List<ModelMaterial>();
            foreach (var m in StorageJson.A(e, "materials", 4096))
            {
                cancellation.ThrowIfCancellationRequested(); StorageJson.Shape(m, "id", "name", "baseColor", "metallic", "roughness", "emissive", "alphaMode", "alphaCutoff", "doubleSided", "normalScale", "occlusionStrength", "baseColorTexture", "metallicRoughnessTexture", "normalTexture", "occlusionTexture", "emissiveTexture");
                materials.Add(new ModelMaterial(StorageJson.S(m, "id"), StorageJson.S(m, "name"), V4(m.GetProperty("baseColor")), StorageJson.D(m, "metallic"), StorageJson.D(m, "roughness"), V3(m.GetProperty("emissive")), StorageJson.EnumValue<MaterialAlphaMode>(m, "alphaMode"), StorageJson.D(m, "alphaCutoff"), StorageJson.B(m, "doubleSided"), ReadBinding(m.GetProperty("baseColorTexture")), ReadBinding(m.GetProperty("metallicRoughnessTexture")), ReadBinding(m.GetProperty("normalTexture")), ReadBinding(m.GetProperty("occlusionTexture")), ReadBinding(m.GetProperty("emissiveTexture")), StorageJson.D(m, "normalScale"), StorageJson.D(m, "occlusionStrength")));
            }
            var textures = new List<ModelTexture>(); long textureBytes = 0;
            foreach (var t in StorageJson.A(e, "textures", 4096))
            {
                cancellation.ThrowIfCancellationRequested(); StorageJson.Shape(t, "id", "name", "mimeType", "sourceUri", "contentHash", "bytes");
                string encoded = StorageJson.S(t, "bytes"); textureBytes += ((long)encoded.Length + 3) / 4 * 3;
                if (textureBytes > 32L * 1024 * 1024) throw new StorageException("LimitExceeded", "Encoded texture budget exceeded.");
                textures.Add(new ModelTexture(StorageJson.S(t, "id"), StorageJson.S(t, "name"), StorageJson.S(t, "mimeType"), Convert.FromBase64String(encoded), StorageJson.S(t, "sourceUri"), StorageJson.S(t, "contentHash")));
            }
            var diagnostics = new List<ModelDiagnostic>();
            foreach (var d in StorageJson.A(e, "diagnostics", 4096))
            { StorageJson.Shape(d, "code", "severity", "stage", "message", "source", "byteOffset", "nodeId", "suggestedAction"); diagnostics.Add(new ModelDiagnostic(StorageJson.S(d, "code"), StorageJson.EnumValue<DiagnosticSeverity>(d, "severity"), StorageJson.S(d, "stage"), StorageJson.S(d, "message"), StorageJson.S(d, "source"), d.GetProperty("byteOffset").ValueKind == JsonValueKind.Null ? (long?)null : StorageJson.L(d, "byteOffset"), StorageJson.S(d, "nodeId"), StorageJson.S(d, "suggestedAction"))); }
            var scene = new ModelScene(StorageJson.S(e, "id"), nodes, meshes, materials, textures, coordinates, ModelCoordinateSpace.Canonical, diagnostics);
            ModelSceneValidator.ValidateOrThrow(scene, cancellation: cancellation); return scene;
        }
        private static ModelTextureBinding ReadBinding(JsonElement b)
        {
            if (b.ValueKind == JsonValueKind.Null) return null;
            StorageJson.Shape(b, "textureId", "texCoord", "colorSpace", "wrapU", "wrapV", "minFilter", "magFilter", "offset", "scale", "rotationRadians", "channels");
            return new ModelTextureBinding(StorageJson.S(b, "textureId"), StorageJson.I(b, "texCoord"), StorageJson.EnumValue<TextureColorSpace>(b, "colorSpace"), StorageJson.EnumValue<TextureWrap>(b, "wrapU"), StorageJson.EnumValue<TextureWrap>(b, "wrapV"), StorageJson.EnumValue<TextureFilter>(b, "minFilter"), StorageJson.EnumValue<TextureFilter>(b, "magFilter"), V2(b.GetProperty("offset")), V2(b.GetProperty("scale")), StorageJson.D(b, "rotationRadians"), StorageJson.S(b, "channels"));
        }
        private static double[] Numbers(JsonElement e, int count)
        { if (e.GetArrayLength() != count) throw new StorageException("InvalidSchema", "Wrong numeric tuple size."); var values = e.EnumerateArray().Select(v => v.GetDouble()).ToArray(); foreach (double n in values) if (double.IsNaN(n) || double.IsInfinity(n)) throw new StorageException("InvalidSchema", "Nonfinite tuple."); return values; }
        private static Double2 V2(JsonElement e) { var a = Numbers(e, 2); return new Double2(a[0], a[1]); }
        private static Double3 V3(JsonElement e) { var a = Numbers(e, 3); return new Double3(a[0], a[1], a[2]); }
        private static Double4 V4(JsonElement e) { var a = Numbers(e, 4); return new Double4(a[0], a[1], a[2], a[3]); }
    }
}
