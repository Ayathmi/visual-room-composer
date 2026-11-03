using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using VisualRoomComposer.Core.Documents;

namespace VisualRoomComposer.Bootstrap
{
    public sealed partial class RoomWorkspaceController
    {
        /// <summary>Injects real UITK events through registered handlers. This is not hardware, manual-input or IME validation.</summary>
        private async Task ValidateInteractionAsync()
        {
            var required = new[] {
                "UI-EVENT-SETUP", "UI-EVENT-BRUSH-PREVIEW-NO-COMMIT", "UI-EVENT-ESC-CANCEL",
                "UI-EVENT-ESC-LATE-UP-NO-COMMIT", "UI-EVENT-BRUSH-ONE-COMMAND",
                "UI-EVENT-UNDO-RESTORE", "UI-EVENT-VIEWPORT-RESTORE", "UI-EVENT-NUMERIC-FIELD-GUARD"
            };
            var recorded = new HashSet<string>(StringComparer.Ordinal);
            string previousTool = tool, committedState = null;
            RoomDocument before = null;
            byte[] beforeBytes = null;
            long beforePosition = 0, beforeRevision = 0, beforeSteps = 0, beforeLatest = 0;
            bool beforeDirty = false;
            int notifications = 0;
            Action changed = () => notifications++;
            bool subscribed = false;
            const string evidence = "UITK event injection; not physical mouse/keyboard or IME. ";
            try
            {
                if (root == null || image == null || session == null || viewport == null || cameraView == null)
                    throw new InvalidOperationException("Workspace dependencies are not ready.");
                Refresh();
                await AwaitUiProbeVisualAsync();
                SetTool("Brush");
                cameraView.Zoom(5);
                for (int i = 0; i < 3; i++) await Task.Yield();
                await AwaitUiProbeVisualAsync();

                var palette = root.Q<DropdownField>("definition");
                bool floorPalette = palette != null && palette.index >= 0 && palette.index < bundle.Definitions.Count &&
                    bundle.Definitions[palette.index].Category == DefinitionCategory.Floor && bundle.Definitions[palette.index].FloorMask == 15;
                before = session.Document;
                beforeBytes = DocumentCodec.EncodeRoom(before);
                beforePosition = session.ProjectHistory.CurrentPosition;
                beforeRevision = session.ProjectHistory.Revision;
                beforeSteps = session.ProjectHistory.RetainedSteps;
                beforeLatest = session.ProjectHistory.LatestPosition;
                beforeDirty = session.ProjectHistory.IsDirty;

                var start = UiProbePanelPoint(new Vector3(1, 0, 4));
                var end = UiProbePanelPoint(new Vector3(3, 0, 4));
                bool expectedCells = Cell(start, out var firstCell) && firstCell.X == 4 && firstCell.Z == 16 &&
                    Cell(end, out var last) && last.X == 12 && last.Z == 16;
                Record("UI-EVENT-SETUP", floorPalette && before.Placements.Count == 28 && expectedCells &&
                    viewport.PlacementCount == 28 && image.panel != null && image.contentRect.width > 100 && image.contentRect.height > 100,
                    "Expected 28 instances, floor15, projected grid (4,16) to (12,16); panel=" + start + " -> " + end +
                    "; palette=" + (palette == null ? "absent" : palette.index.ToString()) + "; placementCount=" + before.Placements.Count);
                session.DocumentChanged += changed;
                subscribed = true;

                SendUiProbePointer(EventType.MouseDown, start, Vector2.zero);
                SendUiProbePointer(EventType.MouseDrag, end, end - start);
                await AwaitUiProbeVisualAsync();
                bool previewIsolated = ReferenceEquals(session.Document, before) && session.ProjectHistory.Revision == beforeRevision &&
                    session.ProjectHistory.CurrentPosition == beforePosition && session.ProjectHistory.RetainedSteps == beforeSteps &&
                    session.ProjectHistory.LatestPosition == beforeLatest && session.ProjectHistory.IsDirty == beforeDirty && notifications == 0;
                Record("UI-EVENT-BRUSH-PREVIEW-NO-COMMIT", previewIsolated && dragging && brush != null && brush.Preview.Count == 3 &&
                    viewport.PlacementCount == 31 && image.HasPointerCapture(PointerId.mousePointerId),
                    "Expected three visible pending floor stamps, captured mouse, unchanged document/history; preview=" +
                    (brush == null ? "null" : brush.Preview.Count.ToString()) + "; visual=" + viewport.PlacementCount + "; notifications=" + notifications);

                SendUiProbeKey(KeyCode.Escape, EventModifiers.None);
                await AwaitUiProbeVisualAsync();
                Record("UI-EVENT-ESC-CANCEL", !dragging && !panning && !orbiting && brush == null &&
                    !image.HasPointerCapture(PointerId.mousePointerId) && viewport.PlacementCount == 28 &&
                    ReferenceEquals(session.Document, before) && session.ProjectHistory.Revision == beforeRevision &&
                    session.ProjectHistory.CurrentPosition == beforePosition && session.ProjectHistory.RetainedSteps == beforeSteps &&
                    session.ProjectHistory.LatestPosition == beforeLatest && session.ProjectHistory.IsDirty == beforeDirty && notifications == 0,
                    "Escape must discard the preview and release pointer capture without a history fact; captured=" +
                    image.HasPointerCapture(PointerId.mousePointerId) + "; visual=" + viewport.PlacementCount + "; notifications=" + notifications);

                SendUiProbePointer(EventType.MouseUp, end, Vector2.zero);
                await AwaitUiProbeVisualAsync();
                Record("UI-EVENT-ESC-LATE-UP-NO-COMMIT", ReferenceEquals(session.Document, before) &&
                    session.ProjectHistory.Revision == beforeRevision && session.ProjectHistory.CurrentPosition == beforePosition &&
                    session.ProjectHistory.RetainedSteps == beforeSteps && notifications == 0 && viewport.PlacementCount == 28,
                    "A late PointerUp after Escape must not resurrect the cancelled brush.");

                SendUiProbePointer(EventType.MouseDown, start, Vector2.zero);
                SendUiProbePointer(EventType.MouseDrag, end, end - start);
                SendUiProbePointer(EventType.MouseUp, end, Vector2.zero);
                await AwaitUiProbeVisualAsync();
                var oldIds = new HashSet<string>(before.Placements.Select(p => p.Id), StringComparer.Ordinal);
                var additions = session.Document.Placements.Where(p => !oldIds.Contains(p.Id)).ToArray();
                bool exactStamps = additions.Length == 3 && additions.All(p => p.Position.Y == 0 && p.Position.Z == 16 && p.QuarterTurns == 0 &&
                    Resolve(p.Definition).Category == DefinitionCategory.Floor && Resolve(p.Definition).FloorMask == 15) &&
                    additions.Select(p => p.Position.X).OrderBy(x => x).SequenceEqual(new[] { 4, 8, 12 });
                if (session.Document.StateId != before.StateId) committedState = session.Document.StateId;
                Record("UI-EVENT-BRUSH-ONE-COMMAND", exactStamps && session.Document.Placements.Count == 31 && viewport.PlacementCount == 31 &&
                    session.ProjectHistory.CurrentPosition == beforePosition + 1 && session.ProjectHistory.LatestPosition == beforePosition + 1 &&
                    session.ProjectHistory.Revision == beforeRevision + 1 && session.ProjectHistory.IsDirty && notifications == 1 &&
                    !dragging && brush == null && !image.HasPointerCapture(PointerId.mousePointerId),
                    "PointerDown/Move/Up must commit exactly one history fact containing grid X=4,8,12 at Z=16; additions=" + additions.Length +
                    "; cursor=" + session.ProjectHistory.CurrentPosition + "; revision=" + session.ProjectHistory.Revision + "; notifications=" + notifications);

                selected.Add("seat");
                var numeric = root.Q<FloatField>("scale-x");
                var fieldState = session.Document.StateId;
                using (var input = KeyDownEvent.GetPooled('\0', KeyCode.Delete, EventModifiers.None)) { input.target = numeric; numeric.SendEvent(input); }
                using (var input = KeyDownEvent.GetPooled('\0', KeyCode.Z, EventModifiers.Control)) { input.target = numeric; numeric.SendEvent(input); }
                Record("UI-EVENT-NUMERIC-FIELD-GUARD", session.Document.StateId == fieldState && session.ProjectHistory.CurrentPosition == beforePosition + 1 && notifications == 1,
                    "Delete and Ctrl+Z targeted at a FloatField must not delete placements or undo the project.");
                selected.Clear();
                SendUiProbeKey(KeyCode.Z, EventModifiers.Control);
                await AwaitUiProbeVisualAsync();
                bool sameContent = DocumentCodec.EncodeRoom(session.Document.WithState(before.Revision, before.StateId)).SequenceEqual(beforeBytes);
                Record("UI-EVENT-UNDO-RESTORE", sameContent && session.Document.StateId == before.StateId &&
                    session.ProjectHistory.CurrentPosition == beforePosition && session.ProjectHistory.Revision == beforeRevision + 2 &&
                    session.ProjectHistory.IsDirty == beforeDirty && notifications == 2,
                    "Ctrl+Z KeyDown must restore all authored content and savepoint while revision remains monotonic; notifications=" + notifications);
                committedState = null;
                Record("UI-EVENT-VIEWPORT-RESTORE", viewport.PlacementCount == 28 &&
                    before.Placements.All(p => viewport.FindPlacement(p.Id) != null) && additions.All(p => viewport.FindPlacement(p.Id) == null),
                    "Undo must remove all three committed native instances and retain every baseline instance.");
            }
            catch (Exception error)
            {
                string status = root?.Q<Label>("status")?.text;
                foreach (string id in required.Where(id => !recorded.Contains(id)))
                    Add(id, false, evidence + "Not completed after an interaction failure: " + error + "; UI status=" + status);
            }
            finally
            {
                if (subscribed) session.DocumentChanged -= changed;
                try
                {
                    if (root != null) SendUiProbeKey(KeyCode.Escape, EventModifiers.None);
                    if (committedState != null && session != null && session.Document.StateId == committedState)
                        SendUiProbeKey(KeyCode.Z, EventModifiers.Control);
                    if (image != null && image.HasPointerCapture(PointerId.mousePointerId)) image.ReleasePointer(PointerId.mousePointerId);
                    if (session != null && viewport != null)
                    {
                        SetTool(previousTool);
                        Refresh();
                        await AwaitUiProbeVisualAsync();
                        Frame();
                    }
                }
                catch (Exception cleanupError)
                {
                    Add("UI-EVENT-CLEANUP", false, evidence + "Failed to restore the probe workspace: " + cleanupError);
                }
            }
            void Record(string id, bool passed, string detail)
            {
                recorded.Add(id); Add(id, passed, evidence + detail);
                if (!passed) throw new InvalidOperationException(id + ": " + detail);
            }
        }

        private Vector2 UiProbePanelPoint(Vector3 world)
        {
            var projected = cameraView.Camera.WorldToViewportPoint(world);
            if (projected.z <= 0 || projected.x <= 0 || projected.x >= 1 || projected.y <= 0 || projected.y >= 1)
                throw new InvalidOperationException("Interaction world point lies outside the camera viewport: " + world + " -> " + projected);
            var local = new Vector2(projected.x * image.contentRect.width, (1 - projected.y) * image.contentRect.height);
            var point = image.LocalToWorld(local);
            if (!image.worldBound.Contains(point)) throw new InvalidOperationException("Projected pointer is outside the Image bounds: " + point);
            return point;
        }

        private void SendUiProbePointer(EventType kind, Vector2 position, Vector2 delta)
        {
            var system = new Event { type = kind, mousePosition = position, delta = delta, button = 0, clickCount = 1 };
            if (kind == EventType.MouseDown)
            {
                using (var input = PointerDownEvent.GetPooled(system)) { input.target = image; image.SendEvent(input); }
            }
            else if (kind == EventType.MouseDrag)
            {
                using (var input = PointerMoveEvent.GetPooled(system)) { input.target = image; image.SendEvent(input); }
            }
            else if (kind == EventType.MouseUp)
            {
                using (var input = PointerUpEvent.GetPooled(system)) { input.target = image; image.SendEvent(input); }
            }
            else throw new ArgumentOutOfRangeException(nameof(kind));
        }

        private void SendUiProbeKey(KeyCode key, EventModifiers modifiers)
        {
            using (var input = KeyDownEvent.GetPooled('\0', key, modifiers)) { input.target = root; root.SendEvent(input); }
        }

        private async Task AwaitUiProbeVisualAsync()
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (clock.Elapsed.TotalSeconds > 10) throw new TimeoutException("UI probe viewport update did not settle within 10 seconds.");
                var pending = visualUpdate;
                while (!pending.IsCompleted)
                {
                    if (clock.Elapsed.TotalSeconds > 10) throw new TimeoutException("UI probe viewport update did not finish within 10 seconds.");
                    await Task.Yield();
                }
                if (!ReferenceEquals(pending, visualUpdate)) continue;
                await pending;
                await Task.Yield();
                if (ReferenceEquals(pending, visualUpdate)) return;
            }
        }
    }
}
