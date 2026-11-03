using System;
using System.Collections.Generic;
using System.IO;
using VisualRoomComposer.Core;

namespace VisualRoomComposer.Formats.Fbx
{
    [Serializable] public sealed class FbxProbeReport
    {
        public string Status;
        public string EvidenceEnvironment;
        public FbxProbeCase[] Cases;
        public string[] CapabilityLimits;
    }
    [Serializable] public sealed class FbxProbeCase
    {
        public string Input;
        public string Output;
        public string Status;
        public string ErrorCode;
        public string Error;
        public int NodeCount;
        public int MeshCount;
        public int MaterialCount;
        public int TextureCount;
        public int TriangleCount;
        public FbxProbeMesh[] Meshes;
    }
    [Serializable] public sealed class FbxProbeMesh
    {
        public string Name;
        public string ParentId;
        public double[] WorldPositions;
        public double[] Uv0;
        public string[] MaterialIds;
    }
    /// <summary>Pure-data entry point; callers must label whether they ran Editor, standalone Player or offline .NET.</summary>
    public static class FbxProbe
    {
        public static FbxProbeReport Run(string inputDirectory, string outputDirectory, string evidenceEnvironment = "Unspecified")
        {
            var cases = new List<FbxProbeCase>(); bool passed = true;
            Directory.CreateDirectory(outputDirectory);
            foreach (string input in Directory.GetFiles(inputDirectory, "*.fbx"))
            {
                var item = new FbxProbeCase { Input = Path.GetFileName(input), Status = "Failed" }; cases.Add(item);
                try
                {
                    var scene = FbxModelReader.Read(input);
                    var worlds = ModelSceneValidator.ResolveWorldTransforms(scene); var meshes = new List<FbxProbeMesh>();
                    var lookup = new Dictionary<string, ModelMesh>(); foreach (var m in scene.Meshes) lookup.Add(m.Id, m);
                    item.NodeCount = scene.Nodes.Count; item.MeshCount = scene.Meshes.Count; item.MaterialCount = scene.Materials.Count; item.TextureCount = scene.Textures.Count;
                    foreach (var node in scene.Nodes)
                    {
                        if (node.MeshId == null) continue; var mesh = lookup[node.MeshId];
                        var positions = new double[mesh.Positions.Count * 3]; var uv = new double[mesh.Uv0.Count * 2];
                        for (int i = 0; i < mesh.Positions.Count; i++) { var p = worlds[node.Id].TransformPoint(mesh.Positions[i]); positions[i * 3] = p.X; positions[i * 3 + 1] = p.Y; positions[i * 3 + 2] = p.Z; }
                        for (int i = 0; i < mesh.Uv0.Count; i++) { uv[i * 2] = mesh.Uv0[i].X; uv[i * 2 + 1] = mesh.Uv0[i].Y; }
                        var materialIds = new string[node.MaterialIds.Count]; for (int i = 0; i < materialIds.Length; i++) materialIds[i] = node.MaterialIds[i];
                        meshes.Add(new FbxProbeMesh { Name = node.Name, ParentId = node.ParentId, WorldPositions = positions, Uv0 = uv, MaterialIds = materialIds });
                        foreach (var sub in mesh.Submeshes) item.TriangleCount += sub.TriangleIndices.Count / 3;
                    }
                    item.Meshes = meshes.ToArray();
                    string directory = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(input)); Directory.CreateDirectory(directory);
                    item.Output = FbxBinaryWriter.Write(scene, Path.Combine(directory, "probe-output.fbx")).FbxPath; item.Status = "Produced";
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    item.ErrorCode = ex is FbxFormatException fbx ? fbx.Code : ex.GetType().Name; item.Error = ex.Message; passed = false;
                }
            }
            return new FbxProbeReport { Status = cases.Count > 0 && passed ? "ProducedRequiresIndependentValidation" : "Failed", EvidenceEnvironment = evidenceEnvironment,
                Cases = cases.ToArray(), CapabilityLimits = new[] { "Static triangles only; XYZ Euler; child inheritance RSrs only.", "Diffuse PNG/JPEG mapping only; PBR and advanced FBX material semantics are lossy.", "Writer bakes world geometry and preserves logical hierarchy with identity node transforms.", "Does not establish standalone Player, native Unity FBX import, or independent consumer acceptance unless those callers actually run." } };
        }
    }
}
