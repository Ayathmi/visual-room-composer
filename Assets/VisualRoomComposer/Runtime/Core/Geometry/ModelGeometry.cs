using System;
using System.Collections.Generic;
using System.Threading;

namespace VisualRoomComposer.Core
{
    /// <summary>One implementation serves both preview realization and export geometry baking.</summary>
    public static class ModelGeometry
    {
        public const string BakeRuleVersion = "vrc-affine-bake-1";

        public static ModelMesh TransformMesh(ModelMesh mesh, Matrix4d transform, string newId = null, CancellationToken cancellation = default)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            cancellation.ThrowIfCancellationRequested();
            var inverse = transform.InverseAffine(); int orientation = transform.OrientationSign;
            var positions = new Double3[mesh.Positions.Count]; var normals = new Double3[mesh.Normals.Count];
            var tangents = new Double4[mesh.Tangents.Count];
            if (normals.Length != 0 && normals.Length != positions.Length || tangents.Length != 0 && tangents.Length != positions.Length || tangents.Length != 0 && normals.Length == 0)
                throw new ModelDataException("InvalidAttribute", "Normals and tangents must match the vertices before baking.", mesh.Id);
            for (int i = 0; i < positions.Length; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                positions[i] = transform.TransformPoint(mesh.Positions[i]);
                if (!positions[i].IsFinite) throw new ModelDataException("NonFinite", "Baked position overflowed.", mesh.Id);
                if (normals.Length != 0)
                {
                    var normal = mesh.Normals[i];
                    normals[i] = new Double3(inverse.M11 * normal.X + inverse.M21 * normal.Y + inverse.M31 * normal.Z,
                        inverse.M12 * normal.X + inverse.M22 * normal.Y + inverse.M32 * normal.Z,
                        inverse.M13 * normal.X + inverse.M23 * normal.Y + inverse.M33 * normal.Z).Normalized();
                }
                if (tangents.Length != 0)
                {
                    var tangent = transform.TransformVector(mesh.Tangents[i].Xyz);
                    tangent = (tangent - normals[i] * Double3.Dot(normals[i], tangent)).Normalized();
                    tangents[i] = new Double4(tangent.X, tangent.Y, tangent.Z, mesh.Tangents[i].W * orientation);
                }
            }
            var submeshes = new List<ModelSubmesh>(mesh.Submeshes.Count);
            foreach (var submesh in mesh.Submeshes)
            {
                if (submesh.TriangleIndices.Count % 3 != 0) throw new ModelDataException("InvalidIndices", "Baking requires complete triangles.", mesh.Id);
                var indices = new int[submesh.TriangleIndices.Count];
                for (int i = 0; i < indices.Length; i += 3)
                {
                    if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                    indices[i] = submesh.TriangleIndices[i];
                    indices[i + 1] = submesh.TriangleIndices[i + (orientation < 0 ? 2 : 1)];
                    indices[i + 2] = submesh.TriangleIndices[i + (orientation < 0 ? 1 : 2)];
                }
                submeshes.Add(new ModelSubmesh(submesh.MaterialSlot, indices));
            }
            return new ModelMesh(newId ?? mesh.Id, mesh.Name, positions, submeshes, normals, tangents, mesh.Uv0, mesh.Uv1, mesh.Colors);
        }

        /// <summary>Bakes every node's full world matrix into distinct geometry and retains an identity-transform name hierarchy.</summary>
        public static ModelScene BakeSceneToWorld(ModelScene scene, Matrix4d? prefixTransform = null, CancellationToken cancellation = default,
            ModelValidationLimits limits = null)
        {
            limits = limits ?? new ModelValidationLimits();
            ModelSceneValidator.ValidateOrThrow(scene, limits, cancellation);
            if (scene.CoordinateSpace != ModelCoordinateSpace.Canonical) throw new InvalidOperationException("Normalize source coordinates before world baking.");
            var prefix = prefixTransform ?? Matrix4d.Identity; prefix.InverseAffine();
            var worlds = ModelSceneValidator.ResolveWorldTransforms(scene, limits.MaxDepth, cancellation);
            var sourceMeshes = new Dictionary<string, ModelMesh>(StringComparer.Ordinal);
            foreach (var mesh in scene.Meshes) sourceMeshes.Add(mesh.Id, mesh);
            long expandedVertices = 0, expandedTriangles = 0, expandedMeshes = 0;
            foreach (var node in scene.Nodes)
            {
                cancellation.ThrowIfCancellationRequested();
                if (node.MeshId == null) continue;
                var mesh = sourceMeshes[node.MeshId];
                expandedMeshes++; expandedVertices += mesh.Positions.Count;
                foreach (var submesh in mesh.Submeshes) expandedTriangles += submesh.TriangleIndices.Count / 3;
                if (expandedMeshes > limits.MaxMeshes || expandedVertices > limits.MaxVertices || expandedTriangles > limits.MaxTriangles)
                    throw new ModelDataException("BudgetExceeded", "World baking would exceed its expanded geometry budget.", node.Id);
            }
            var nodes = new List<ModelNode>(scene.Nodes.Count); var meshes = new List<ModelMesh>();
            foreach (var node in scene.Nodes)
            {
                cancellation.ThrowIfCancellationRequested();
                string meshId = null;
                if (node.MeshId != null)
                {
                    meshId = "world:" + node.Id;
                    meshes.Add(TransformMesh(sourceMeshes[node.MeshId], prefix * worlds[node.Id], meshId, cancellation));
                }
                nodes.Add(new ModelNode(node.Id, node.Name, node.ParentId, Matrix4d.Identity, meshId, node.MaterialIds, true));
            }
            var result = new ModelScene(scene.Id, nodes, meshes, scene.Materials, scene.Textures, scene.SourceCoordinates,
                ModelCoordinateSpace.Canonical, scene.Diagnostics);
            ModelSceneValidator.ValidateOrThrow(result, limits, cancellation);
            return result;
        }
    }

    /// <summary>Converts positions and nodes together so source axes and units are applied exactly once.</summary>
    public static class ModelSceneNormalizer
    {
        public static ModelScene ToCanonical(ModelScene source, CancellationToken cancellation = default, ModelValidationLimits limits = null)
        {
            limits = limits ?? new ModelValidationLimits();
            ModelSceneValidator.ValidateOrThrow(source, limits, cancellation);
            if (source.CoordinateSpace != ModelCoordinateSpace.Source) throw new InvalidOperationException("The scene is already canonical; a second conversion is not allowed.");
            var conversion = source.SourceCoordinates.ToCanonicalMatrix; var inverse = conversion.InverseAffine();
            var meshes = new List<ModelMesh>(source.Meshes.Count); var nodes = new List<ModelNode>(source.Nodes.Count);
            foreach (var mesh in source.Meshes) meshes.Add(ModelGeometry.TransformMesh(mesh, conversion, cancellation: cancellation));
            foreach (var node in source.Nodes)
            {
                cancellation.ThrowIfCancellationRequested();
                nodes.Add(new ModelNode(node.Id, node.Name, node.ParentId, conversion * node.LocalTransform * inverse,
                    node.MeshId, node.MaterialIds, node.GeometryTransformBaked));
            }
            var result = new ModelScene(source.Id, nodes, meshes, source.Materials, source.Textures, source.SourceCoordinates,
                ModelCoordinateSpace.Canonical, source.Diagnostics);
            ModelSceneValidator.ValidateOrThrow(result, limits, cancellation);
            return result;
        }
    }
}
