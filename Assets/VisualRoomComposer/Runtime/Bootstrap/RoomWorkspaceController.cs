using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;
using VisualRoomComposer.Application;
using VisualRoomComposer.Application.Diagnostics;
using VisualRoomComposer.Application.RoomTools;
using VisualRoomComposer.Core;
using VisualRoomComposer.Core.Documents;
using VisualRoomComposer.Core.RoomGeometry;
using VisualRoomComposer.Infrastructure.Diagnostics;
using VisualRoomComposer.Infrastructure.Persistence;
using VisualRoomComposer.Presentation;

namespace VisualRoomComposer.Bootstrap
{
    /// <summary>A functional authoring host; commands and persistence remain in their own services.</summary>
    public sealed partial class RoomWorkspaceController : MonoBehaviour
    {
        [SerializeField] private Camera previewCamera;
        [SerializeField] private StyleSheet workspaceStyle;
        [SerializeField] private Material previewMaterial;
        private PanelRenderer panel;
        private VisualElement root;
        private Image image;
        private FontAsset font;
        private RoomSession session;
        private RoomToolService tools;
        private ProjectBundle bundle;
        private IncrementalRoomViewport viewport;
        private RoomViewportCamera cameraView;
        private readonly HashSet<string> selected = new HashSet<string>(StringComparer.Ordinal);
        private CancellationTokenSource lifetime;
        private BrushGesture brush;
        private GridCell2 startCell, lastCell;
        private bool dragging, panning, orbiting, refreshing, stopping;
        private string tool = "Select", fileHash, filePath;
        private int uiVersion = -1;
        private int capturedPointer = -1;
        private Task visualUpdate = Task.CompletedTask;
        private readonly BatchReport report = new BatchReport();
        public RoomSession Session => session;

        private void OnEnable()
        {
            stopping = false;
            lifetime = new CancellationTokenSource();
            UnityEngine.Application.runInBackground = true;
            panel = GetComponent<PanelRenderer>();
            panel.RegisterUIReloadCallback(Bind);
            if (session != null) { CreatePresentation(); Refresh(); }
        }
        private void Bind(PanelRenderer sender, VisualElement element, int version)
        {
            if (uiVersion == version && ReferenceEquals(root, element)) { EnsureFont(); return; }
            CancelGesture();
            ReleaseFont();
            uiVersion = version; root = element;
            root.styleSheets.Add(workspaceStyle);
            EnsureFont();
            image = root.Q<Image>("viewport");
            image.scaleMode = ScaleMode.StretchToFill;
            if (cameraView != null) image.image = cameraView.Texture;
            image.RegisterCallback<GeometryChangedEvent>(e => {
                if (cameraView != null && cameraView.Resize((int)e.newRect.width, (int)e.newRect.height)) image.image = cameraView.Texture;
            });
            image.RegisterCallback<PointerDownEvent>(OnDown);
            image.RegisterCallback<PointerMoveEvent>(OnMove);
            image.RegisterCallback<PointerUpEvent>(OnUp);
            image.RegisterCallback<PointerCaptureOutEvent>(e => CancelGesture());
            image.RegisterCallback<WheelEvent>(e => { cameraView?.Zoom(e.delta.y); e.StopPropagation(); });
            root.RegisterCallback<KeyDownEvent>(OnKey);
            root.Q<Button>("export").SetEnabled(false);
            Click("select", () => SetTool("Select")); Click("brush", () => SetTool("Brush"));
            Click("rectangle", () => SetTool("Fill")); Click("clear", () => SetTool("Clear"));
            Click("rotate", () => MoveSelection(default, 1)); Click("delete", DeleteSelection);
            Click("cancel", CancelGesture); Click("frame", Frame);
            Click("move-left", () => MoveSelection(new GridPosition(-1, 0, 0)));
            Click("move-right", () => MoveSelection(new GridPosition(1, 0, 0)));
            Click("move-forward", () => MoveSelection(new GridPosition(0, 0, 1)));
            Click("move-back", () => MoveSelection(new GridPosition(0, 0, -1)));
            Click("undo", () => { CancelGesture(); session.ProjectHistory.Undo(); Refresh(); });
            Click("redo", () => { CancelGesture(); session.ProjectHistory.Redo(); Refresh(); });
            Click("save", SaveProject); Click("reopen", ReopenProject);
            Click("apply-scale", () => {
                if (selected.Count != 1) throw new InvalidOperationException("请选择一面墙。");
                var scale = new Double3(root.Q<FloatField>("scale-x").value, root.Q<FloatField>("scale-y").value, root.Q<FloatField>("scale-z").value);
                Apply("Scale wall", d => tools.SetWallScale(d, selected.Single(), scale));
            });
            root.Q<DropdownField>("room").RegisterValueChangedCallback(e => CancelGesture());
            root.Q<DropdownField>("definition").RegisterValueChangedCallback(e => CancelGesture());
            root.Q<TextField>("project-name").isDelayed = true;
            root.Q<TextField>("project-name").RegisterValueChangedCallback(e => { if (!refreshing && session != null) Safe(() => Apply("Rename project", d => d.With(title: e.newValue))); });
            if (session != null) Refresh();
        }
        private IEnumerator Start()
        {
            for (var i = 0; i < 30 && root == null; i++) yield return null;
            if (root == null) throw new InvalidOperationException("Workspace UI did not initialize.");
            CreatePresentation();
            var sample = RoomSampleFixture.Create();
            Adopt(ProjectSealer.Seal(sample.Document, sample.Definitions, sample.Models));
            filePath = Path.Combine(UnityEngine.Application.persistentDataPath, "RoomComposer", "workspace.vrc.json");
            image.image = cameraView.Texture;
            Refresh();
            while (!visualUpdate.IsCompleted) yield return null;
            Frame();
            if (Argument("--vrc-room-output") == null) yield break;
            var task = ValidateAsync();
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) Add("ROOM-FATAL", false, task.Exception.ToString());
            var output = Argument("--vrc-room-output");
            Directory.CreateDirectory(output);
            ScreenCapture.CaptureScreenshot(Path.Combine(output, "room-workspace.png"));
            for (var i = 0; i < 3; i++) yield return null;
            report.Passed = report.Checks.Count > 0 && report.Checks.All(c => c.Passed);
            File.WriteAllText(Path.Combine(output, "room-result.json"), JsonSerializer.Serialize(report, JsonOptions));
            UnityEngine.Application.Quit(report.Passed ? 0 : 1);
        }
        private void Adopt(ProjectBundle next)
        {
            CancelGesture(); session?.Dispose(); bundle = next;
            session = new RoomSession();
            foreach (var definition in bundle.Definitions) session.RegisterDefinition(definition);
            foreach (var model in bundle.Models)
            {
                using var lease = ImportedSceneLease.Create(model.Scene);
                session.RegisterModel(model.Asset.Reference, lease);
            }
            session.OpenRoom(bundle.Document);
            if (bundle.Draft != null) session.OpenLibrary(bundle.Draft);
            tools = new RoomToolService(Resolve);
            selected.Clear();
            refreshing = true;
            root.Q<DropdownField>("definition").choices = bundle.Definitions.Select(d => d.Name).ToList();
            root.Q<DropdownField>("definition").index = 0;
            root.Q<DropdownField>("room").choices = session.Document.Rooms.Select(r => r.Name).ToList();
            root.Q<DropdownField>("room").index = 0;
            refreshing = false;
        }
        private LibraryDefinition Resolve(PinnedReference pin) => bundle.Definitions.Single(d => d.Reference.Equals(pin));
        private ModelScene Model(LibraryDefinition definition) => definition.ModelReference == null ? null : bundle.Models.Single(m => m.Asset.Reference.Equals(definition.ModelReference)).Scene;
        private void CreatePresentation()
        {
            cameraView = new RoomViewportCamera(previewCamera);
            viewport = new IncrementalRoomViewport();
            if (image != null)
            {
                cameraView.Resize((int)image.contentRect.width, (int)image.contentRect.height);
                image.image = cameraView.Texture;
            }
        }
        private void EnsureFont()
        {
            if (root == null) return;
            if (font == null) font = FontAsset.CreateFontAsset("Microsoft YaHei", "Regular", 32);
            if (font != null) root.style.unityFontDefinition = new FontDefinition { fontAsset = font };
        }
        private void ReleaseFont()
        {
            if (font == null) return;
            var owned = font; font = null;
            if (owned.material != null) Destroy(owned.material);
            foreach (var atlas in owned.atlasTextures) if (atlas != null) Destroy(atlas);
            Destroy(owned);
        }
        private void Click(string name, Action action) => root.Q<Button>(name).clicked += () => Safe(action);
        private void Safe(Action action) { try { action(); } catch (Exception error) { CancelGesture(); Status(error.Message); } }
        private void SetTool(string value) { CancelGesture(); tool = value; Status("工具：" + value + "。Esc取消；中键平移，右键环视，滚轮缩放。"); }
        private bool Cell(Vector3 pointer, out GridCell2 cell)
        {
            var p = image.WorldToLocal(pointer);
            if (cameraView.Floor(new Vector2(p.x / image.contentRect.width, p.y / image.contentRect.height), out var position)) { cell = new GridCell2(position.X, position.Z); return true; }
            cell = default; return false;
        }
        private void OnDown(PointerDownEvent e)
        {
            if (session == null) return;
            if (e.button == 2 || e.button == 1) { panning = e.button == 2; orbiting = e.button == 1; capturedPointer = e.pointerId; image.CapturePointer(e.pointerId); return; }
            if (e.button != 0 || !Cell(e.position, out var cell)) return;
            Safe(() => {
                if (tool == "Select")
                {
                    var p = image.WorldToLocal(e.position);
                    var id = viewport.Pick(previewCamera, new Vector2(p.x / image.contentRect.width, p.y / image.contentRect.height));
                    if (!e.ctrlKey) selected.Clear();
                    if (id != null && !selected.Add(id)) selected.Remove(id);
                    Refresh(); return;
                }
                dragging = true; startCell = lastCell = cell;
                if (tool == "Brush")
                {
                    var definition = bundle.Definitions[root.Q<DropdownField>("definition").index];
                    var roomIndex = root.Q<DropdownField>("room").index;
                    var room = roomIndex >= 0 ? session.Document.Rooms[roomIndex].Id : null;
                    var template = tools.Prepare(definition.Reference, new GridPosition(cell.X, 0, cell.Z), roomId: room);
                    if (definition.PlacementPolicy == PlacementPolicy.Wall)
                    {
                        if (selected.Count != 1) throw new InvalidOperationException("添加墙饰前，请先选择一面宿主墙。");
                        var host = session.Document.Placements.Single(p => p.Id == selected.Single());
                        var hostDefinition = Resolve(host.Definition);
                        if (hostDefinition.Category != DefinitionCategory.Wall) throw new InvalidOperationException("请选择一面宿主墙。");
                        var local = hostDefinition.LocalBounds.Center + new Double3(0, 0, hostDefinition.LocalBounds.Size.Z * .5 + definition.LocalBounds.Size.Z * .5);
                        template = tools.PrepareAttachment(session.Document, definition.Reference, host.Id, null, local);
                    }
                    brush = tools.BeginBrush(session.Document, template);
                    if (template.Attachment != null) brush.SampleAttachment(template); else brush.Sample(cell);
                    PreviewBrush();
                }
                capturedPointer = e.pointerId; image.CapturePointer(e.pointerId); e.StopPropagation();
            });
        }
        private void OnMove(PointerMoveEvent e)
        {
            if (panning) { cameraView.Pan(e.deltaPosition); return; }
            if (orbiting) { cameraView.Orbit(e.deltaPosition.x); return; }
            if (!dragging || !Cell(e.position, out var cell)) return;
            lastCell = cell;
            Safe(() => { if (brush != null && brush.Preview.All(p => p.Attachment == null)) { brush.Sample(cell); PreviewBrush(); } });
        }
        private void OnUp(PointerUpEvent e)
        {
            if (panning || orbiting) { panning = orbiting = false; ReleaseCapture(); return; }
            if (!dragging || e.button != 0) return;
            Safe(() => {
                if (brush != null) { var candidate = brush.Complete(session.Document); brush = null; Apply("Brush", _ => candidate); }
                else if (tool == "Fill") { var definition = bundle.Definitions[root.Q<DropdownField>("definition").index]; Apply("Rectangle fill", d => tools.Fill(d, GridRect.Between(startCell, lastCell), definition.Reference, roomId: d.Rooms[root.Q<DropdownField>("room").index].Id)); }
                else if (tool == "Clear") Apply("Rectangle clear", d => tools.Clear(d, GridRect.Between(startCell, lastCell)));
            });
            dragging = false; ReleaseCapture(); Refresh();
        }
        private void PreviewBrush()
        {
            var preview = tools.Place(brush.Source, brush.Preview);
            QueueVisual(preview);
        }
        private void CancelGesture()
        {
            brush?.Cancel(); brush = null; dragging = panning = orbiting = false;
            ReleaseCapture();
            if (!stopping && session != null && viewport != null) QueueVisual(session.Document);
        }
        private void ReleaseCapture()
        {
            var pointer = capturedPointer; capturedPointer = -1;
            if (pointer >= 0 && image != null && image.HasPointerCapture(pointer)) image.ReleasePointer(pointer);
        }
        private void Apply(string name, Func<RoomDocument, RoomDocument> edit)
        {
            session.Apply(name, edit); Refresh();
        }
        private void MoveSelection(GridPosition delta, int turns = 0)
        { CancelGesture(); if (selected.Count > 0) Apply("Transform selection", d => tools.Move(d, selected.ToArray(), delta, turns)); }
        private void DeleteSelection()
        { CancelGesture(); if (selected.Count > 0) Apply("Delete selection", d => tools.Delete(d, selected.ToArray())); selected.Clear(); }
        private void OnKey(KeyDownEvent e)
        {
            if (e.target is VisualElement target)
                for (var current = target; current != null; current = current.parent)
                    if (current is TextField || current is FloatField || current.ClassListContains("unity-base-field")) return;
            if (e.keyCode == KeyCode.Escape) { CancelGesture(); e.StopPropagation(); return; }
            if (e.ctrlKey && e.keyCode == KeyCode.Z) { Safe(() => { CancelGesture(); session.ProjectHistory.Undo(); Refresh(); }); e.StopPropagation(); }
            if (e.ctrlKey && e.keyCode == KeyCode.Y) { Safe(() => { CancelGesture(); session.ProjectHistory.Redo(); Refresh(); }); e.StopPropagation(); }
            if (e.ctrlKey && e.keyCode == KeyCode.S) { Safe(SaveProject); e.StopPropagation(); }
            if (e.keyCode == KeyCode.Delete) { Safe(DeleteSelection); e.StopPropagation(); }
        }
        private void QueueVisual(RoomDocument document)
        {
            visualUpdate = viewport.ReconcileAsync(RoomViewportProbe.Plans(document, Resolve, Model), lifetime.Token);
            Observe(visualUpdate);
        }
        private async void Observe(Task task)
        {
            try { await task; } catch (OperationCanceledException) { } catch (Exception error) { Status(error.Message); }
        }
        private void Refresh()
        {
            if (session == null || root == null) return;
            refreshing = true;
            root.Q<TextField>("project-name").SetValueWithoutNotify(session.Document.Title);
            root.Q<Label>("document-state").text = session.Document.Title + (session.ProjectHistory.IsDirty ? " · 未保存" : " · 已保存");
            selected.RemoveWhere(id => !session.Document.Placements.Any(p => p.Id == id));
            root.Q<Label>("selection-state").text = selected.Count + " 个选中 · " + session.Document.Placements.Count + " 个构件";
            var list = root.Q("placements"); list.Clear();
            foreach (var p in session.Document.Placements)
            {
                var id = p.Id; var button = new Button(() => { CancelGesture(); selected.Clear(); selected.Add(id); Refresh(); }) { text = Resolve(p.Definition).Name + " · " + id };
                if (selected.Contains(id)) button.AddToClassList("selected"); list.Add(button);
            }
            var issues = tools.Index(session.Document).Diagnose();
            root.Q<Label>("diagnostics").text = string.Join("\n", issues.Take(12).Select(i => i.Code + " · " + i.PlacementId));
            refreshing = false; QueueVisual(session.Document);
        }
        private void Frame()
        {
            var renderers = viewport.ContentRoot.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            var bounds = renderers[0].bounds; foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds); cameraView.Frame(bounds);
        }
        private void SaveProject()
        {
            CancelGesture(); var state = session.Document.StateId;
            var receipt = new ProjectStore().Save(filePath, new ProjectBundle(session.Document, bundle.Definitions, bundle.Models, session.LibraryDraft), fileHash);
            fileHash = receipt.ContentHash; session.ProjectHistory.MarkSaved(state); Refresh(); Status("已保存：" + filePath);
        }
        private void ReopenProject()
        {
            if (session.ProjectHistory.IsDirty) throw new InvalidOperationException("请先保存当前修改，再重新打开。");
            var loaded = new ProjectStore().Load(filePath); fileHash = loaded.ContentHash; Adopt(loaded.Bundle); Refresh(); Status("已重新打开工程。");
        }
        private void Status(string message) { if (root != null) root.Q<Label>("status").text = message; }
        private static string Argument(string name)
        { var args = Environment.GetCommandLineArgs(); var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        private async Task ValidateAsync()
        {
            var output = Argument("--vrc-room-output"); Directory.CreateDirectory(output);
            var mode = Argument("--vrc-room-mode") ?? "seed";
            var persistent = Argument("--vrc-room-data");
            if (mode == "seed")
            {
                Merge("editing", await EditingProbe.RunAsync(), output);
                Merge("geometry", RoomGeometryProbe.Run(), output);
                Merge("viewport", await RoomViewportProbe.RunAsync(viewport, cameraView), output);
                Merge("storage-seed", await Task.Run(() => StorageProbe.RunSeed(persistent)), output);
                Refresh(); await visualUpdate;
                await ValidateInteractionAsync();
                await ValidateWorkspaceLifecycleAsync();
                var state = session.Document.StateId;
                var added = tools.Prepare(bundle.Definitions.First(d => d.Id.EndsWith("prop-ground", StringComparison.Ordinal)).Reference, new GridPosition(40, 0, 0), roomId: "room-a");
                session.Apply("UI service add", d => tools.Place(d, new[] { added }));
                Add("UI-HISTORY-DIRTY", session.ProjectHistory.IsDirty);
                session.ProjectHistory.Undo(); Add("UI-UNDO-SAVEPOINT", !session.ProjectHistory.IsDirty && session.Document.StateId == state);
                session.ProjectHistory.Redo(); Add("UI-REDO", session.Document.Placements.Any(p => p.Id == added.Id));
                session.ProjectHistory.Undo();
                filePath = Path.Combine(persistent, "ui-workspace.vrc.json");
                session.Apply("Persist edited workspace", d => tools.Place(d.With(title: "Saved UI " + Guid.NewGuid().ToString("N")), new[] { added }));
                session.ApplyLibrary("Edit independent draft", d => d.With(name: "Saved draft " + Guid.NewGuid().ToString("N")));
                SaveProject(); await visualUpdate;
                File.WriteAllText(Path.Combine(persistent, "ui-saved-state.json"), JsonSerializer.Serialize(new SavedUiState { StateId = session.Document.StateId, Title = session.Document.Title, Placements = session.Document.Placements.Count, Hash = fileHash, DraftState = session.LibraryDraft.StateId, DraftName = session.LibraryDraft.Name }, JsonOptions));
                Add("UI-SAVE-CLEARS-ONLY-OWN-SAVEPOINT", !session.ProjectHistory.IsDirty && session.LibraryHistory.IsDirty && File.Exists(filePath));
                var storedDraft = new ProjectStore().Load(filePath).Bundle.Draft;
                Add("UI-SAVE-PRESERVES-LIBRARY-DRAFT", storedDraft != null && DocumentCodec.EncodeLibrary(storedDraft).SequenceEqual(DocumentCodec.EncodeLibrary(session.LibraryDraft)));
            }
            else
            {
                Merge("storage-reopen", await Task.Run(() => StorageProbe.RunReopen(persistent)), output);
                filePath = Path.Combine(persistent, "ui-workspace.vrc.json");
                var expected = JsonSerializer.Deserialize<SavedUiState>(File.ReadAllText(Path.Combine(persistent, "ui-saved-state.json")), JsonOptions);
                await viewport.ReconcileAsync(Array.Empty<RoomVisualPlan>());
                for (var i = 0; i < 3; i++) await Task.Yield();
                Add("UI-REOPEN-EMPTY-NATIVE-CACHE", viewport.PlacementCount == 0 && viewport.DefinitionCount == 0);
                ReopenProject();
                Add("UI-INDEPENDENT-PROCESS-REOPEN", session.Document.StateId == expected.StateId && session.Document.Title == expected.Title && session.Document.Placements.Count == expected.Placements && fileHash == expected.Hash && !session.ProjectHistory.IsDirty);
                Add("UI-REOPEN-LIBRARY-DRAFT", session.LibraryDraft.StateId == expected.DraftState && session.LibraryDraft.Name == expected.DraftName);
                await visualUpdate;
                Add("UI-REOPEN-DISK-GEOMETRY", MatchesNativeGeometry(), "Compares every realized world vertex to the persisted CPU scene after clearing all definition templates.");
            }
            Refresh(); await visualUpdate;
            Add("UI-CURRENT-DOCUMENT-NATIVE-COUNT", viewport.PlacementCount == session.Document.Placements.Count);
            Add("UI-VIEWPORT-LAYOUT", image.worldBound.width > 100 && image.worldBound.height > 100);
        }
        private void Merge(string name, object child, string output)
        {
            var json = JsonSerializer.Serialize(child, child.GetType(), JsonOptions);
            File.WriteAllText(Path.Combine(output, name + ".json"), json);
            using var data = JsonDocument.Parse(json);
            var entries = data.RootElement;
            if (entries.ValueKind != JsonValueKind.Array)
            {
                foreach (var key in new[] { "Checks", "checks", "Cases", "cases", "Results", "results" })
                    if (entries.TryGetProperty(key, out var found)) { entries = found; break; }
            }
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0) throw new InvalidDataException("The " + name + " probe returned no checks.");
            foreach (var item in entries.EnumerateArray())
            {
                string id = item.TryGetProperty("Id", out var idValue) ? idValue.GetString() : item.GetProperty("id").GetString();
                bool passed = item.TryGetProperty("Passed", out var passedValue) ? passedValue.GetBoolean() :
                    (item.TryGetProperty("Status", out var status) || item.TryGetProperty("status", out status)) && status.GetString() == "Passed";
                Add(name + "/" + id, passed, passed ? null : item.ToString());
            }
        }
        private void Add(string id, bool passed, string detail = null) => report.Checks.Add(new BatchCheck { Id = id, Passed = passed, Detail = detail });
        private void OnApplicationFocus(bool focused) { if (!focused) CancelGesture(); }
        private void OnDisable()
        {
            stopping = true; CancelGesture();
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            panel?.UnregisterUIReloadCallback(Bind);
            viewport?.Dispose(); viewport = null; cameraView?.Dispose(); cameraView = null;
            visualUpdate = Task.CompletedTask;
            if (image != null) image.image = null;
            ReleaseFont();
        }
        private void OnDestroy() { session?.Dispose(); session = null; }
        [Serializable] private sealed class BatchReport { public bool Passed; public List<BatchCheck> Checks = new List<BatchCheck>(); }
        [Serializable] private sealed class BatchCheck { public string Id; public bool Passed; public string Detail; }
        [Serializable] private sealed class SavedUiState { public string StateId, Title, Hash, DraftState, DraftName; public int Placements; }
    }
}
