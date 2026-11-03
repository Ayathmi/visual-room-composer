using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;
using VisualRoomComposer.Infrastructure;
using VisualRoomComposer.Core;
using VisualRoomComposer.Presentation;
using VisualRoomComposer.Formats.Fbx;

namespace VisualRoomComposer.Bootstrap
{
    /// <summary>Development-only environment probe hosted by the actual standalone UI.</summary>
    public sealed class EnvironmentProbe : MonoBehaviour
    {
        [SerializeField] private Camera previewCamera;
        [SerializeField] private StyleSheet probeStyle;
        private PanelRenderer panel;
        private VisualElement root;
        private RenderTexture target;
        private FontAsset font;
        private UnityModelRealizer affinePreview;
        private int uiVersion = -1;
        private readonly ProbeReport report = new ProbeReport();
        private readonly IFileDialogService fileDialog = new WindowsFileDialogService();

        private void OnEnable()
        {
            Application.runInBackground = true;
            Debug.Log("VRC_ENV_START");
            panel = GetComponent<PanelRenderer>();
            panel.RegisterUIReloadCallback(Bind);
        }

        private void Bind(PanelRenderer sender, VisualElement element, int version)
        {
            if (uiVersion == version) return;
            uiVersion = version;
            if (root != null)
            {
                root.Q<Button>("open").clicked -= Open;
                root.Q<Button>("export").clicked -= Save;
            }
            root = element;
            root.Q<Image>("viewport").image = target;
            if (probeStyle != null && !root.styleSheets.Contains(probeStyle)) root.styleSheets.Add(probeStyle);
            if (font != null) Destroy(font);
            font = FontAsset.CreateFontAsset("Microsoft YaHei", "Regular", 32);
            if (font != null)
            {
                font.TryAddCharacters("房间构件可行性工作区引擎文件与模型探针名称原创打开导出环境已取消", out string missing);
                Debug.Log("VRC_FONT_MISSING=" + missing);
                root.style.unityFontDefinition = new FontDefinition { fontAsset = font };
            }
            root.Q<Button>("open").clicked += Open;
            root.Q<Button>("export").clicked += Save;
        }

        private IEnumerator Start()
        {
            target = new RenderTexture(960, 640, 24);
            target.name = "VRC preview";
            target.Create();
            previewCamera.targetTexture = target;
            affinePreview = UnityModelRealizer.Create(AffineProbeFixture.Create());
            foreach (var item in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                if (item.name.StartsWith("Original probe block", StringComparison.Ordinal)) item.gameObject.SetActive(false);
            previewCamera.transform.position = new Vector3(11, 8, -14);
            previewCamera.transform.LookAt(new Vector3(1, 3, 1));
            for (var i = 0; i < 10 && root == null; i++) yield return null;
            if (root != null) root.Q<Image>("viewport").image = target;
            for (var i = 0; i < 5; i++) yield return null;
            if (!Environment.GetCommandLineArgs().Contains("--vrc-probe")) yield break;
            report.unity = Application.unityVersion;
            report.platform = Application.platform.ToString();
            report.machine = SystemInfo.operatingSystem + "; " + SystemInfo.processorType + "; " + SystemInfo.graphicsDeviceName;
            var output = Argument("--vrc-output") ?? Path.Combine(Application.persistentDataPath, "probe");
            Directory.CreateDirectory(output);
            Check("ENV-UI-ROOT", () => root != null && root.Q("probe-root").worldBound.width > 100);
            Check("ENV-UI-FONT", () => font != null && font.HasCharacter('房'));
            Check("ENV-INPUT-SYSTEM", () => Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem") != null);
            Check("ENV-UI-PICK", () => {
                var image = root.Q<Image>("viewport");
                var center = image.worldBound.center;
                return image.ContainsPoint(image.WorldToLocal(center)) && root.panel.Pick(center) == image;
            });
            Check("ENV-JSON", () => JsonUtility.FromJson<ProbeCase>(JsonUtility.ToJson(new ProbeCase { id = "中文 path", passed = true })).id == "中文 path");
            Check("XFORM-PLAYER-SHEAR", () => VerifyVertices("world:shear", AffineProbeFixture.ExpectedShearedWorldVertices()));
            Check("XFORM-PLAYER-MIRROR", () => VerifyVertices("world:mirror", AffineProbeFixture.ExpectedMirroredWorldVertices()));
            Check("XFORM-PLAYER-ATTRIBUTES", () => affinePreview.Root.GetComponentsInChildren<MeshFilter>().All(f =>
                f.sharedMesh.subMeshCount == 2 && f.sharedMesh.uv2.Length == 6 && f.sharedMesh.tangents.Length == 6 && f.sharedMesh.colors.Length == 6));
            var task = Task.Run(() => {
                var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("房间 ABC 123", 100)));
                using var packed = new MemoryStream();
                using (var zip = new DeflateStream(packed, System.IO.Compression.CompressionLevel.Optimal, true)) zip.Write(bytes, 0, bytes.Length);
                packed.Position = 0;
                using var unzip = new DeflateStream(packed, CompressionMode.Decompress);
                using var restored = new MemoryStream();
                unzip.CopyTo(restored);
                return bytes.SequenceEqual(restored.ToArray());
            });
            while (!task.IsCompleted) yield return null;
            Check("ENV-TASK-DEFLATE", () => task.GetAwaiter().GetResult());
            if (Environment.GetCommandLineArgs().Contains("--vrc-dialog-probe"))
            {
                Check("ENV-NATIVE-OPEN-CANCEL-OWNER", () => NativeCheck(() => NativeDialogProbe.Cancel(false)));
                Check("ENV-NATIVE-SAVE-CANCEL-OWNER", () => NativeCheck(() => NativeDialogProbe.Cancel(true)));
                var unicodeDirectory = Path.Combine(output, "native-paths", "中文 空格");
                Directory.CreateDirectory(unicodeDirectory);
                var unicodePath = Path.Combine(unicodeDirectory, "原创模型.obj");
                File.WriteAllText(unicodePath, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
                Check("ENV-NATIVE-OPEN-UNICODE", () => NativeCheck(() => NativeDialogProbe.SelectOpen(unicodePath)));
                var longDirectory = unicodeDirectory;
                for (var i = 0; i < 18; i++) longDirectory = Path.Combine(longDirectory, "原创长路径目录-" + i.ToString("00"));
                Directory.CreateDirectory(longDirectory);
                var longPath = Path.Combine(longDirectory, "模型文件.obj");
                File.WriteAllText(longPath, "v 0 0 0\n");
                Check("ENV-NATIVE-OPEN-LONG-PATH", () => NativeCheck(() => NativeDialogProbe.SelectOpen(longPath)));
            }
            yield return null;
            Check("ENV-RENDER-TEXTURE", () => {
                var previous = RenderTexture.active;
                var pixels = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                try {
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                    pixels.Apply();
                    var distinct = pixels.GetPixels32().Select(c => c.r + c.g * 256 + c.b * 65536).Distinct().Take(8).Count();
                    return distinct >= 3;
                } finally { RenderTexture.active = previous; Destroy(pixels); }
            });
            var fixtures = Argument("--vrc-fixtures");
            if (fixtures != null)
            {
                var evidenceEnvironment = "WindowsPlayer " + Application.unityVersion;
                var fbxTask = Task.Run(() => FbxProbe.Run(fixtures, Path.Combine(output, "fbx-outputs"), evidenceEnvironment));
                while (!fbxTask.IsCompleted) yield return null;
                Check("FBX-PLAYER-READ-WRITE", () => fbxTask.GetAwaiter().GetResult().Cases.Length == Directory.GetFiles(fixtures, "*.fbx").Length && fbxTask.Result.Cases.Length >= 4 && fbxTask.Result.Cases.All(c => c.Status == "Produced"));
                if (fbxTask.Status == TaskStatus.RanToCompletion)
                    File.WriteAllText(Path.Combine(output, "fbx-probe.json"), JsonUtility.ToJson(fbxTask.Result, true));
                var guards = FbxGuardProbe.Run(Path.Combine(fixtures, "blender-original-7400.fbx"), Path.Combine(Path.GetDirectoryName(fixtures), "Negative"), Path.Combine(output, "guard-scratch"));
                Check("FBX-PLAYER-GUARDS", () => guards.Length > 0 && guards.All(g => g.Status == "Passed"));
                File.WriteAllText(Path.Combine(output, "fbx-guards.json"), JsonUtility.ToJson(new GuardReport { cases = guards }, true));
                affinePreview.Root.SetActive(false);
                foreach (var input in Directory.GetFiles(fixtures, "*.fbx"))
                {
                    var importTask = Task.Run(() => FbxModelReader.Read(input));
                    while (!importTask.IsCompleted) yield return null;
                    UnityModelRealizer imported = null;
                    Check("FBX-PLAYER-REALIZE-" + Path.GetFileName(input), () => {
                        imported = UnityModelRealizer.Create(importTask.GetAwaiter().GetResult());
                        return imported.Root.GetComponentsInChildren<MeshFilter>().Length == 2 && imported.OwnedResourceCount > 0;
                    });
                    if (imported != null)
                    {
                        var renderers = imported.Root.GetComponentsInChildren<Renderer>();
                        var bounds = renderers[0].bounds;
                        foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                        previewCamera.transform.position = bounds.center + new Vector3(1.3f, 0.8f, -1.5f).normalized * bounds.extents.magnitude * 2.8f;
                        previewCamera.transform.LookAt(bounds.center);
                    }
                    for (var i = 0; i < 3; i++) yield return null;
                    ScreenCapture.CaptureScreenshot(Path.Combine(output, Path.GetFileNameWithoutExtension(input) + ".png"));
                    for (var i = 0; i < 3; i++) yield return null;
                    var refs = imported?.Root.GetComponentsInChildren<MeshFilter>().Select(m => m.sharedMesh).ToArray();
                    var nativeMaterials = imported?.Root.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().ToArray();
                    var nativeTextures = nativeMaterials?.Select(m => m.mainTexture).Where(t => t != null).Distinct().ToArray();
                    Check("FBX-PLAYER-TEXTURE-" + Path.GetFileName(input), () => nativeTextures != null && nativeTextures.Length == 1 && nativeTextures[0].width == 4 && nativeTextures[0].height == 4);
                    imported?.Dispose();
                    yield return null;
                    Check("FBX-PLAYER-RELEASE-" + Path.GetFileName(input), () => refs != null && refs.All(m => m == null));
                    Check("FBX-PLAYER-MATERIAL-TEXTURE-RELEASE-" + Path.GetFileName(input), () => nativeMaterials != null && nativeMaterials.All(m => m == null) && nativeTextures.All(t => t == null));
                }
                affinePreview.Root.SetActive(true);
                previewCamera.transform.position = new Vector3(11, 8, -14);
                previewCamera.transform.LookAt(new Vector3(1, 3, 1));
            }
            ScreenCapture.CaptureScreenshot(Path.Combine(output, "workspace.png"));
            for (var i = 0; i < 3; i++) yield return null;
            var ownedMeshes = affinePreview.Root.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh).ToArray();
            var ownedMaterials = affinePreview.Root.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().ToArray();
            var ownedTarget = target;
            ReleasePreview();
            yield return null;
            Check("ENV-RESOURCE-RELEASE", () => ownedTarget == null && previewCamera.targetTexture == null);
            Check("XFORM-PLAYER-NATIVE-RELEASE", () => affinePreview == null && ownedMeshes.All(m => m == null));
            Check("XFORM-PLAYER-MATERIAL-RELEASE", () => ownedMaterials.All(m => m == null));
            report.passed = report.cases.All(c => c.passed);
            File.WriteAllText(Path.Combine(output, "environment-result.json"), JsonUtility.ToJson(report, true));
            Debug.Log("VRC_ENV_RESULT " + report.passed);
            Application.Quit(report.passed ? 0 : 1);
        }

        private void Check(string id, Func<bool> action)
        {
            var item = new ProbeCase { id = id };
            try { item.passed = action(); } catch (Exception ex) { item.error = ex.ToString(); }
            report.cases.Add(item);
        }

        private static bool NativeCheck(Func<bool> action)
        {
            if (!action()) throw new InvalidOperationException(NativeDialogProbe.LastDiagnostic);
            return true;
        }

        private void Open()
        {
            try { root.Q<Label>("status").text = fileDialog.OpenModel() ?? "已取消 / Cancelled"; }
            catch (Exception ex) { root.Q<Label>("status").text = ex.Message; }
        }

        private void Save()
        {
            try { root.Q<Label>("status").text = fileDialog.SaveFbx() ?? "已取消 / Cancelled"; }
            catch (Exception ex) { root.Q<Label>("status").text = ex.Message; }
        }

        private static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private void ReleasePreview()
        {
            if (affinePreview != null) { affinePreview.Dispose(); affinePreview = null; }
            if (previewCamera != null) previewCamera.targetTexture = null;
            if (root != null) root.Q<Image>("viewport").image = null;
            if (target != null) { target.Release(); Destroy(target); target = null; }
        }

        private bool VerifyVertices(string meshId, Double3[] expected)
        {
            var node = affinePreview.BakedScene.Nodes.Single(n => n.MeshId == meshId);
            var mesh = affinePreview.Root.GetComponentsInChildren<MeshFilter>().Single(f => f.gameObject.name == node.Name).sharedMesh;
            var vertices = mesh.vertices;
            if (vertices.Length != expected.Length) return false;
            for (var i = 0; i < expected.Length; i++)
                if ((vertices[i] - new Vector3((float)expected[i].X, (float)expected[i].Y, (float)expected[i].Z)).magnitude > 0.0001f) return false;
            return true;
        }

        private void OnDisable()
        {
            if (panel != null) panel.UnregisterUIReloadCallback(Bind);
            ReleasePreview();
            if (font != null) Destroy(font);
            uiVersion = -1;
        }

        [Serializable] private sealed class ProbeReport
        {
            public string unity, platform, machine;
            public bool passed;
            public List<ProbeCase> cases = new List<ProbeCase>();
        }
        [Serializable] private sealed class ProbeCase
        {
            public string id, error;
            public bool passed;
        }
        [Serializable] private sealed class GuardReport { public FbxGuardResult[] cases; }
    }
}
