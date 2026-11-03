using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace VisualRoomComposer.Core
{
    /// <summary>A validation failure that import and export adapters can map to structured diagnostics.</summary>
    public sealed class ModelDataException : Exception
    {
        public string Code { get; }
        public string ElementId { get; }
        public ModelDataException(string code, string message, string elementId = null) : base(message) { Code = code; ElementId = elementId; }
    }

    /// <summary>Post-parse limits. Readers must also enforce allocation limits before creating model arrays.</summary>
    public sealed class ModelValidationLimits
    {
        public int MaxNodes { get; }
        public int MaxMeshes { get; }
        public int MaxVertices { get; }
        public int MaxTriangles { get; }
        public int MaxDepth { get; }
        public long MaxEncodedTextureBytes { get; }
        public ModelValidationLimits(int maxNodes = 65536, int maxMeshes = 4096, int maxVertices = 2000000,
            int maxTriangles = 4000000, int maxDepth = 128, long maxEncodedTextureBytes = 536870912)
        {
            if (maxNodes < 1 || maxMeshes < 1 || maxVertices < 1 || maxTriangles < 1 || maxDepth < 1 || maxEncodedTextureBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maxNodes), "All model limits must be positive.");
            MaxNodes = maxNodes; MaxMeshes = maxMeshes; MaxVertices = maxVertices;
            MaxTriangles = maxTriangles; MaxDepth = maxDepth; MaxEncodedTextureBytes = maxEncodedTextureBytes;
        }
    }

    /// <summary>Rejects unsafe geometry and graph references before realization or export.</summary>
    public static class ModelSceneValidator
    {
        public static void ValidateOrThrow(ModelScene scene, ModelValidationLimits limits = null, CancellationToken cancellation = default)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            limits = limits ?? new ModelValidationLimits(); cancellation.ThrowIfCancellationRequested();
            if (scene.Nodes.Count > limits.MaxNodes || scene.Meshes.Count > limits.MaxMeshes) Fail("BudgetExceeded", "Node or mesh limit exceeded.");
            var nodes = Index(scene.Nodes, value => value.Id, "node");
            var meshes = Index(scene.Meshes, value => value.Id, "mesh");
            var materials = Index(scene.Materials, value => value.Id, "material");
            var textures = Index(scene.Textures, value => value.Id, "texture");
            long vertices = 0, triangles = 0, textureBytes = 0;
            foreach (var mesh in scene.Meshes)
            {
                cancellation.ThrowIfCancellationRequested();
                vertices += mesh.Positions.Count;
                if (vertices > limits.MaxVertices) Fail("BudgetExceeded", "Vertex limit exceeded.", mesh.Id);
                if (mesh.Positions.Count == 0 || mesh.Submeshes.Count == 0) Fail("EmptyGeometry", "A mesh requires vertices and triangles.", mesh.Id);
                AttributeCount(mesh.Normals.Count, mesh.Positions.Count, mesh.Id); AttributeCount(mesh.Tangents.Count, mesh.Positions.Count, mesh.Id);
                AttributeCount(mesh.Uv0.Count, mesh.Positions.Count, mesh.Id); AttributeCount(mesh.Uv1.Count, mesh.Positions.Count, mesh.Id); AttributeCount(mesh.Colors.Count, mesh.Positions.Count, mesh.Id);
                if (mesh.Tangents.Count > 0 && mesh.Normals.Count == 0) Fail("InvalidAttribute", "Tangents require vertex normals.", mesh.Id);
                for (int i = 0; i < mesh.Positions.Count; i++)
                {
                    if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                    if (!mesh.Positions[i].IsFinite) Fail("NonFinite", "Vertex is non-finite.", mesh.Id);
                    if (mesh.Normals.Count != 0 && (!mesh.Normals[i].IsFinite || mesh.Normals[i].Length == 0)) Fail("InvalidNormal", "Normal must be finite and nonzero.", mesh.Id);
                    if (mesh.Tangents.Count != 0 && (!mesh.Tangents[i].IsFinite || mesh.Tangents[i].Xyz.Length == 0 || Math.Abs(mesh.Tangents[i].W) != 1)) Fail("InvalidTangent", "Tangent must be finite, nonzero, and have handedness +1 or -1.", mesh.Id);
                    if (mesh.Uv0.Count != 0 && !mesh.Uv0[i].IsFinite || mesh.Uv1.Count != 0 && !mesh.Uv1[i].IsFinite || mesh.Colors.Count != 0 && !mesh.Colors[i].IsFinite)
                        Fail("NonFinite", "Vertex attribute is non-finite.", mesh.Id);
                }
                foreach (var submesh in mesh.Submeshes)
                {
                    if (submesh == null || submesh.MaterialSlot < -1 || submesh.TriangleIndices.Count == 0 || submesh.TriangleIndices.Count % 3 != 0)
                        Fail("InvalidIndices", "Each submesh needs complete triangles and a valid material slot.", mesh.Id);
                    triangles += submesh.TriangleIndices.Count / 3;
                    if (triangles > limits.MaxTriangles) Fail("BudgetExceeded", "Triangle limit exceeded.", mesh.Id);
                    for (int i = 0; i < submesh.TriangleIndices.Count; i += 3)
                    {
                        if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                        int a = submesh.TriangleIndices[i], b = submesh.TriangleIndices[i + 1], c = submesh.TriangleIndices[i + 2];
                        if (a < 0 || a >= mesh.Positions.Count || b < 0 || b >= mesh.Positions.Count || c < 0 || c >= mesh.Positions.Count) Fail("InvalidIndices", "Triangle index is out of range.", mesh.Id);
                        var cross = Double3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
                        if (!cross.IsFinite || cross.Length == 0) Fail("DegenerateGeometry", "Triangle is degenerate or numerically unsafe.", mesh.Id);
                    }
                }
            }
            foreach (var texture in scene.Textures)
            {
                textureBytes += texture.ByteLength;
                if (textureBytes > limits.MaxEncodedTextureBytes) Fail("BudgetExceeded", "Encoded texture limit exceeded.", texture.Id);
                if (texture.ByteLength == 0) Fail("MissingDependency", "Texture bytes must be resolved before a scene is accepted.", texture.Id);
            }
            foreach (var material in scene.Materials)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!material.BaseColor.IsFinite || !material.Emissive.IsFinite || !Unit(material.Metallic) || !Unit(material.Roughness) || !Unit(material.AlphaCutoff) || !Unit(material.OcclusionStrength) || !Numeric.IsFinite(material.NormalScale))
                    Fail("InvalidMaterial", "Material has an invalid scalar or color.", material.Id);
                foreach (var binding in Bindings(material))
                {
                    if (!textures.ContainsKey(binding.TextureId)) Fail("MissingDependency", "Material texture is missing.", material.Id);
                    if (binding.TexCoord < 0 || binding.TexCoord > 1 || !binding.Offset.IsFinite || !binding.Scale.IsFinite || !Numeric.IsFinite(binding.RotationRadians))
                        Fail("InvalidMaterial", "Texture sampling requires UV0/UV1 and finite transforms.", material.Id);
                    if (binding.MagFilter != TextureFilter.Nearest && binding.MagFilter != TextureFilter.Linear)
                        Fail("InvalidMaterial", "Magnification supports nearest or linear filtering.", material.Id);
                }
            }
            foreach (var node in scene.Nodes)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!node.LocalTransform.TryInverseAffine(out _)) Fail("InvalidTransform", "Node matrix is singular, non-affine, non-finite, or numerically unsafe.", node.Id);
                if (node.ParentId != null && !nodes.ContainsKey(node.ParentId)) Fail("MissingReference", "Node parent is missing.", node.Id);
                foreach (var materialId in node.MaterialIds) if (materialId == null || !materials.ContainsKey(materialId)) Fail("MissingReference", "Node material is missing.", node.Id);
                if (node.MeshId == null) continue;
                if (!meshes.TryGetValue(node.MeshId, out var mesh)) Fail("MissingReference", "Node mesh is missing.", node.Id);
                foreach (var submesh in mesh.Submeshes)
                {
                    if (submesh.MaterialSlot >= node.MaterialIds.Count) Fail("MissingReference", "A mesh material slot is not bound by its instance.", node.Id);
                    if (submesh.MaterialSlot < 0) continue;
                    foreach (var binding in Bindings(materials[node.MaterialIds[submesh.MaterialSlot]]))
                        if (binding.TexCoord == 0 && mesh.Uv0.Count == 0 || binding.TexCoord == 1 && mesh.Uv1.Count == 0)
                            Fail("MissingAttribute", "A bound texture requires the specified UV set.", node.Id);
                }
            }
            ResolveWorldTransforms(scene, limits.MaxDepth, cancellation);
        }

        public static IReadOnlyDictionary<string, Matrix4d> ResolveWorldTransforms(ModelScene scene, int maxDepth = 128, CancellationToken cancellation = default)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));
            var nodes = Index(scene.Nodes, node => node.Id, "node");
            var worlds = new Dictionary<string, Matrix4d>(StringComparer.Ordinal);
            var depths = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in scene.Nodes)
            {
                cancellation.ThrowIfCancellationRequested();
                if (worlds.ContainsKey(node.Id)) continue;
                var chain = new List<ModelNode>(); var pending = new HashSet<string>(StringComparer.Ordinal);
                var current = node;
                while (current != null && !worlds.ContainsKey(current.Id))
                {
                    if (!pending.Add(current.Id)) Fail("HierarchyCycle", "Model hierarchy contains a cycle.", current.Id);
                    chain.Add(current);
                    if (chain.Count > maxDepth) Fail("BudgetExceeded", "Model hierarchy exceeds its depth limit.", current.Id);
                    if (current.ParentId == null) current = null;
                    else if (!nodes.TryGetValue(current.ParentId, out current)) Fail("MissingReference", "Node parent is missing.", node.Id);
                }
                var parentWorld = current == null ? Matrix4d.Identity : worlds[current.Id];
                int depth = current == null ? 0 : depths[current.Id];
                for (int i = chain.Count - 1; i >= 0; i--)
                {
                    depth++;
                    if (depth > maxDepth) Fail("BudgetExceeded", "Model hierarchy exceeds its depth limit.", chain[i].Id);
                    parentWorld = parentWorld * chain[i].LocalTransform;
                    if (!parentWorld.TryInverseAffine(out _)) Fail("InvalidTransform", "Combined world transform is not safely invertible.", chain[i].Id);
                    worlds.Add(chain[i].Id, parentWorld); depths.Add(chain[i].Id, depth);
                }
            }
            return new ReadOnlyDictionary<string, Matrix4d>(worlds);
        }

        internal static IEnumerable<ModelTextureBinding> Bindings(ModelMaterial material)
        {
            if (material.BaseColorTexture != null) yield return material.BaseColorTexture;
            if (material.MetallicRoughnessTexture != null) yield return material.MetallicRoughnessTexture;
            if (material.NormalTexture != null) yield return material.NormalTexture;
            if (material.OcclusionTexture != null) yield return material.OcclusionTexture;
            if (material.EmissiveTexture != null) yield return material.EmissiveTexture;
        }
        private static Dictionary<string, T> Index<T>(IReadOnlyList<T> values, Func<T, string> id, string kind) where T : class
        {
            var result = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (value == null) Fail("MissingReference", "Null " + kind + " record.");
                string key = id(value);
                if (result.ContainsKey(key)) Fail("DuplicateId", "Duplicate " + kind + " identifier.", key);
                result.Add(key, value);
            }
            return result;
        }
        private static bool Unit(double value) => Numeric.IsFinite(value) && value >= 0 && value <= 1;
        private static void AttributeCount(int count, int vertices, string id)
        {
            if (count != 0 && count != vertices) Fail("InvalidAttribute", "Attribute count differs from vertex count.", id);
        }
        private static void Fail(string code, string message, string id = null) { throw new ModelDataException(code, message, id); }
    }
}
