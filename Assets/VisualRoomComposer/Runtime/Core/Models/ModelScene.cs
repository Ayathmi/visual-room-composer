using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace VisualRoomComposer.Core
{
    /// <summary>Coordinate status prevents applying a source conversion twice.</summary>
    public enum ModelCoordinateSpace { Source, Canonical }
    public enum TextureColorSpace { Linear, Srgb }
    public enum TextureWrap { Repeat, Clamp, Mirror }
    public enum TextureFilter { Nearest, Linear, NearestMipmapNearest, LinearMipmapNearest, NearestMipmapLinear, LinearMipmapLinear }
    public enum MaterialAlphaMode { Opaque, Mask, Blend }
    public enum DiagnosticSeverity { Information, Warning, Error }

    internal static class ModelCollections
    {
        internal static IReadOnlyList<T> Copy<T>(IEnumerable<T> values) => new ReadOnlyCollection<T>(values == null ? new List<T>() : new List<T>(values));
        internal static string Id(string id, string parameter)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("An identifier is required.", parameter);
            return id;
        }
    }

    /// <summary>Immutable CPU data. Constructors copy collection inputs; no Unity object is stored here.</summary>
    public sealed class ModelScene
    {
        public const int SchemaVersion = 1;
        public string Id { get; }
        public IReadOnlyList<ModelNode> Nodes { get; }
        public IReadOnlyList<ModelMesh> Meshes { get; }
        public IReadOnlyList<ModelMaterial> Materials { get; }
        public IReadOnlyList<ModelTexture> Textures { get; }
        public SourceCoordinateSystem SourceCoordinates { get; }
        public ModelCoordinateSpace CoordinateSpace { get; }
        public IReadOnlyList<ModelDiagnostic> Diagnostics { get; }
        public ModelScene(string id, IEnumerable<ModelNode> nodes, IEnumerable<ModelMesh> meshes,
            IEnumerable<ModelMaterial> materials = null, IEnumerable<ModelTexture> textures = null,
            SourceCoordinateSystem sourceCoordinates = null, ModelCoordinateSpace coordinateSpace = ModelCoordinateSpace.Canonical,
            IEnumerable<ModelDiagnostic> diagnostics = null)
        {
            Id = ModelCollections.Id(id, nameof(id)); Nodes = ModelCollections.Copy(nodes); Meshes = ModelCollections.Copy(meshes);
            Materials = ModelCollections.Copy(materials); Textures = ModelCollections.Copy(textures);
            SourceCoordinates = sourceCoordinates ?? SourceCoordinateSystem.UnityMeters;
            CoordinateSpace = coordinateSpace; Diagnostics = ModelCollections.Copy(diagnostics);
        }
    }

    /// <summary>A logical node. Material slots belong to the instance, allowing geometry reuse.</summary>
    public sealed class ModelNode
    {
        public string Id { get; }
        public string Name { get; }
        public string ParentId { get; }
        public Matrix4d LocalTransform { get; }
        public string MeshId { get; }
        public IReadOnlyList<string> MaterialIds { get; }
        public bool GeometryTransformBaked { get; }
        public ModelNode(string id, string name, string parentId, Matrix4d localTransform,
            string meshId = null, IEnumerable<string> materialIds = null, bool geometryTransformBaked = false)
        {
            Id = ModelCollections.Id(id, nameof(id)); Name = name ?? id; ParentId = parentId; LocalTransform = localTransform;
            MeshId = meshId; MaterialIds = ModelCollections.Copy(materialIds); GeometryTransformBaked = geometryTransformBaked;
        }
    }

    /// <summary>Vertices have unified attribute indices. Optional attributes are empty or match Positions.Count.</summary>
    public sealed class ModelMesh
    {
        public string Id { get; }
        public string Name { get; }
        public IReadOnlyList<Double3> Positions { get; }
        public IReadOnlyList<Double3> Normals { get; }
        public IReadOnlyList<Double4> Tangents { get; }
        public IReadOnlyList<Double2> Uv0 { get; }
        public IReadOnlyList<Double2> Uv1 { get; }
        public IReadOnlyList<Double4> Colors { get; }
        public IReadOnlyList<ModelSubmesh> Submeshes { get; }
        public ModelMesh(string id, string name, IEnumerable<Double3> positions, IEnumerable<ModelSubmesh> submeshes,
            IEnumerable<Double3> normals = null, IEnumerable<Double4> tangents = null,
            IEnumerable<Double2> uv0 = null, IEnumerable<Double2> uv1 = null, IEnumerable<Double4> colors = null)
        {
            Id = ModelCollections.Id(id, nameof(id)); Name = name ?? id;
            Positions = ModelCollections.Copy(positions); Submeshes = ModelCollections.Copy(submeshes);
            Normals = ModelCollections.Copy(normals); Tangents = ModelCollections.Copy(tangents);
            Uv0 = ModelCollections.Copy(uv0); Uv1 = ModelCollections.Copy(uv1); Colors = ModelCollections.Copy(colors);
        }
    }

    /// <summary>Triangle indices use canonical clockwise front faces; slot -1 explicitly means no authored material.</summary>
    public sealed class ModelSubmesh
    {
        public int MaterialSlot { get; }
        public IReadOnlyList<int> TriangleIndices { get; }
        public ModelSubmesh(int materialSlot, IEnumerable<int> triangleIndices)
        {
            MaterialSlot = materialSlot; TriangleIndices = ModelCollections.Copy(triangleIndices);
        }
    }

    /// <summary>UV sampling is explicit per material binding, including color interpretation and channel selection.</summary>
    public sealed class ModelTextureBinding
    {
        public string TextureId { get; }
        public int TexCoord { get; }
        public TextureColorSpace ColorSpace { get; }
        public TextureWrap WrapU { get; }
        public TextureWrap WrapV { get; }
        public TextureFilter MinFilter { get; }
        public TextureFilter MagFilter { get; }
        public Double2 Offset { get; }
        public Double2 Scale { get; }
        public double RotationRadians { get; }
        public string Channels { get; }
        public ModelTextureBinding(string textureId, int texCoord = 0, TextureColorSpace colorSpace = TextureColorSpace.Srgb,
            TextureWrap wrapU = TextureWrap.Repeat, TextureWrap wrapV = TextureWrap.Repeat,
            TextureFilter minFilter = TextureFilter.LinearMipmapLinear, TextureFilter magFilter = TextureFilter.Linear,
            Double2? offset = null, Double2? scale = null, double rotationRadians = 0, string channels = "rgba")
        {
            TextureId = ModelCollections.Id(textureId, nameof(textureId)); TexCoord = texCoord; ColorSpace = colorSpace;
            WrapU = wrapU; WrapV = wrapV; MinFilter = minFilter; MagFilter = magFilter;
            Offset = offset ?? new Double2(0, 0); Scale = scale ?? new Double2(1, 1);
            RotationRadians = rotationRadians; Channels = channels;
        }
    }

    /// <summary>Static PBR values remain independent of the preview shader and FBX material conversion.</summary>
    public sealed class ModelMaterial
    {
        public string Id { get; }
        public string Name { get; }
        public Double4 BaseColor { get; }
        public double Metallic { get; }
        public double Roughness { get; }
        public Double3 Emissive { get; }
        public MaterialAlphaMode AlphaMode { get; }
        public double AlphaCutoff { get; }
        public bool DoubleSided { get; }
        public double NormalScale { get; }
        public double OcclusionStrength { get; }
        public ModelTextureBinding BaseColorTexture { get; }
        public ModelTextureBinding MetallicRoughnessTexture { get; }
        public ModelTextureBinding NormalTexture { get; }
        public ModelTextureBinding OcclusionTexture { get; }
        public ModelTextureBinding EmissiveTexture { get; }
        public ModelMaterial(string id, string name, Double4? baseColor = null, double metallic = 0, double roughness = 1,
            Double3? emissive = null, MaterialAlphaMode alphaMode = MaterialAlphaMode.Opaque, double alphaCutoff = 0.5,
            bool doubleSided = false, ModelTextureBinding baseColorTexture = null,
            ModelTextureBinding metallicRoughnessTexture = null, ModelTextureBinding normalTexture = null,
            ModelTextureBinding occlusionTexture = null, ModelTextureBinding emissiveTexture = null,
            double normalScale = 1, double occlusionStrength = 1)
        {
            Id = ModelCollections.Id(id, nameof(id)); Name = name ?? id; BaseColor = baseColor ?? new Double4(1, 1, 1, 1);
            Metallic = metallic; Roughness = roughness; Emissive = emissive ?? Double3.Zero; AlphaMode = alphaMode;
            AlphaCutoff = alphaCutoff; DoubleSided = doubleSided; BaseColorTexture = baseColorTexture;
            MetallicRoughnessTexture = metallicRoughnessTexture; NormalTexture = normalTexture;
            OcclusionTexture = occlusionTexture; EmissiveTexture = emissiveTexture;
            NormalScale = normalScale; OcclusionStrength = occlusionStrength;
        }
    }

    /// <summary>Encoded image bytes and their verified digest. The source URI is provenance, never an implicit file read.</summary>
    public sealed class ModelTexture
    {
        private readonly byte[] encodedBytes;
        public string Id { get; }
        public string Name { get; }
        public string MimeType { get; }
        public string SourceUri { get; }
        public string ContentHash { get; }
        public IReadOnlyList<byte> EncodedBytes { get; }
        public int ByteLength => encodedBytes.Length;
        public ModelTexture(string id, string name, string mimeType, byte[] encodedBytes, string sourceUri = null, string contentHash = null)
        {
            Id = ModelCollections.Id(id, nameof(id)); Name = name ?? id; MimeType = mimeType ?? "application/octet-stream";
            this.encodedBytes = encodedBytes == null ? Array.Empty<byte>() : (byte[])encodedBytes.Clone();
            EncodedBytes = Array.AsReadOnly(this.encodedBytes); SourceUri = sourceUri;
            using (var sha = SHA256.Create())
            {
                ContentHash = BitConverter.ToString(sha.ComputeHash(this.encodedBytes)).Replace("-", "").ToLowerInvariant();
            }
            if (contentHash != null && !string.Equals(contentHash, ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Texture bytes do not match the supplied digest.", nameof(contentHash));
        }
        public byte[] CopyEncodedBytes() => (byte[])encodedBytes.Clone();
    }

    /// <summary>A portable diagnostic with a stable code and a source location.</summary>
    public sealed class ModelDiagnostic
    {
        public string Code { get; }
        public DiagnosticSeverity Severity { get; }
        public string Stage { get; }
        public string Message { get; }
        public string Source { get; }
        public long? ByteOffset { get; }
        public string NodeId { get; }
        public string SuggestedAction { get; }
        public ModelDiagnostic(string code, DiagnosticSeverity severity, string stage, string message,
            string source = null, long? byteOffset = null, string nodeId = null, string suggestedAction = null)
        {
            Code = ModelCollections.Id(code, nameof(code)); Severity = severity; Stage = stage; Message = message;
            Source = source; ByteOffset = byteOffset; NodeId = nodeId; SuggestedAction = suggestedAction;
        }
    }
}
