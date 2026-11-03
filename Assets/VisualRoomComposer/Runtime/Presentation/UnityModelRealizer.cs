using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using VisualRoomComposer.Core;

namespace VisualRoomComposer.Presentation
{
    /// <summary>Owns a preview and every native resource it allocates; release on Unity's main thread.</summary>
    public sealed class UnityModelRealizer : IDisposable
    {
        private readonly List<UnityEngine.Object> resources = new List<UnityEngine.Object>();
        private readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
        private readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
        private readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        public GameObject Root { get; private set; }
        public int OwnedResourceCount => resources.Count;
        public ModelScene BakedScene { get; private set; }

        /// <summary>Uses the same full-affine bake as export, without decomposing sheared matrices to TRS.</summary>
        public static UnityModelRealizer Create(ModelScene canonical)
        {
            var result = new UnityModelRealizer();
            try { result.Build(canonical); return result; }
            catch { result.Dispose(); throw; }
        }

        private void Build(ModelScene scene)
        {
            ModelSceneValidator.ValidateOrThrow(scene);
            BakedScene = ModelGeometry.BakeSceneToWorld(scene);
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new InvalidOperationException("The preview's URP shader is unavailable.");
            Root = new GameObject("Imported model preview");
            foreach (var mesh in BakedScene.Meshes)
                foreach (var vertex in mesh.Positions)
                    if (Math.Abs(vertex.X) > float.MaxValue || Math.Abs(vertex.Y) > float.MaxValue || Math.Abs(vertex.Z) > float.MaxValue)
                        throw new InvalidOperationException("Geometry exceeds the Unity preview coordinate range.");
            var objects = new Dictionary<string, GameObject>();
            var fallback = new Material(shader) { name = "Unassigned material" };
            resources.Add(fallback);
            long decodedBytes = 0;
            var encoded = new Dictionary<string, byte[]>();
            foreach (var texture in BakedScene.Textures)
            {
                var bytes = texture.EncodedBytes.ToArray();
                decodedBytes = checked(decodedBytes + ImageHeaderGuard.DecodedBytes(bytes));
                if (decodedBytes > 512L * 1024 * 1024) throw new InvalidOperationException("Texture decode budget exceeded.");
                encoded.Add(texture.Id, bytes);
            }
            foreach (var texture in BakedScene.Textures)
            {
                var image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                resources.Add(image);
                image.name = texture.Name;
                if (!image.LoadImage(encoded[texture.Id], false)) throw new InvalidOperationException("Unsupported image: " + texture.Name);
                if (image.width > 4096 || image.height > 4096) throw new InvalidOperationException("Preview texture exceeds the probe limit.");
                textures.Add(texture.Id, image);
            }
            foreach (var data in BakedScene.Materials)
            {
                var material = new Material(shader) { name = data.Name };
                resources.Add(material);
                material.SetColor("_BaseColor", ColorOf(data.BaseColor));
                if (data.BaseColorTexture != null)
                {
                    var binding = data.BaseColorTexture;
                    material.SetTexture("_BaseMap", textures[binding.TextureId]);
                    material.SetTextureScale("_BaseMap", new Vector2((float)binding.Scale.X, (float)binding.Scale.Y));
                    material.SetTextureOffset("_BaseMap", new Vector2((float)binding.Offset.X, (float)binding.Offset.Y));
                }
                material.SetFloat("_Cull", data.DoubleSided ? 0 : 2);
                materials.Add(data.Id, material);
            }
            foreach (var data in BakedScene.Meshes)
            {
                var mesh = new Mesh { name = data.Name, indexFormat = IndexFormat.UInt32 };
                resources.Add(mesh);
                mesh.vertices = data.Positions.Select(Vector).ToArray();
                if (data.Normals.Count > 0) mesh.normals = data.Normals.Select(Vector).ToArray();
                if (data.Uv0.Count > 0) mesh.uv = data.Uv0.Select(v => new Vector2((float)v.X, (float)v.Y)).ToArray();
                if (data.Uv1.Count > 0) mesh.uv2 = data.Uv1.Select(v => new Vector2((float)v.X, (float)v.Y)).ToArray();
                if (data.Tangents.Count > 0) mesh.tangents = data.Tangents.Select(v => new Vector4((float)v.X, (float)v.Y, (float)v.Z, (float)v.W)).ToArray();
                if (data.Colors.Count > 0) mesh.colors = data.Colors.Select(ColorOf).ToArray();
                mesh.subMeshCount = data.Submeshes.Count;
                for (var i = 0; i < data.Submeshes.Count; i++) mesh.SetTriangles(data.Submeshes[i].TriangleIndices.ToArray(), i);
                if (data.Normals.Count == 0) mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                meshes.Add(data.Id, mesh);
            }
            foreach (var node in BakedScene.Nodes)
            {
                var obj = new GameObject(node.Name);
                obj.transform.SetParent(Root.transform, false);
                objects.Add(node.Id, obj);
                if (node.MeshId == null) continue;
                obj.AddComponent<MeshFilter>().sharedMesh = meshes[node.MeshId];
                var mesh = BakedScene.Meshes.First(m => m.Id == node.MeshId);
                obj.AddComponent<MeshRenderer>().sharedMaterials = mesh.Submeshes.Select(s =>
                    s.MaterialSlot >= 0 && s.MaterialSlot < node.MaterialIds.Count ? materials[node.MaterialIds[s.MaterialSlot]] : fallback).ToArray();
            }
            foreach (var node in BakedScene.Nodes)
                if (node.ParentId != null) objects[node.Id].transform.SetParent(objects[node.ParentId].transform, false);
        }

        /// <summary>Releases only this preview; published CPU data and other previews remain valid.</summary>
        public void Dispose()
        {
            if (Root != null) UnityEngine.Object.Destroy(Root);
            Root = null;
            foreach (var resource in resources) if (resource != null) UnityEngine.Object.Destroy(resource);
            resources.Clear(); meshes.Clear(); materials.Clear(); textures.Clear(); BakedScene = null;
        }

        private static Vector3 Vector(Double3 value) => new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        private static Color ColorOf(Double4 value) => new Color((float)value.X, (float)value.Y, (float)value.Z, (float)value.W);
    }
}
