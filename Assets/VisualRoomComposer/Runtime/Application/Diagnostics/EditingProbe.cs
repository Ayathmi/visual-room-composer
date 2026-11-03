using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VisualRoomComposer.Core;
using VisualRoomComposer.Core.Documents;
using VisualRoomComposer.Core.History;

namespace VisualRoomComposer.Application.Diagnostics
{
    public sealed class EditingCheck
    {
        public string Id { get; } public bool Passed { get; } public string Detail { get; }
        internal EditingCheck(string id, bool passed, string detail) { Id = id; Passed = passed; Detail = detail; }
    }
    public sealed class EditingProbeReport
    {
        public int Passed => Checks.Count(value => value.Passed); public int Failed => Checks.Count - Passed;
        public IReadOnlyList<EditingCheck> Checks { get; }
        internal EditingProbeReport(IReadOnlyList<EditingCheck> checks) { Checks = checks; }
    }
    /// <summary>Player-callable production editing checks with independent expected states and resource release observations.</summary>
    public static class EditingProbe
    {
        public static async Task<EditingProbeReport> RunAsync()
        {
            var checks = new List<EditingCheck>();
            void Check(string id, Action body) { try { body(); checks.Add(new EditingCheck(id, true, "Passed")); } catch (Exception error) { checks.Add(new EditingCheck(id, false, error.ToString())); } }
            async Task CheckAsync(string id, Func<Task> body) { try { await body(); checks.Add(new EditingCheck(id, true, "Passed")); } catch (Exception error) { checks.Add(new EditingCheck(id, false, error.ToString())); } }
            var pin = Pin("prop"); var initial = new RoomDocument("original", "Initial", rooms: new[] { new Room("room-a", "A"), new Room("room-b", "B") }, stateId: "initial-state");
            Check("DOC-EXPLICIT-NUMERIC-CODEC", () =>
            {
                var matrix = new Matrix4d(1, .25, 0, 2.5, 0, 2, .5, -3, 0, 0, 1, 4.75, 0, 0, 0, 1);
                var definition = new LibraryDefinition(pin, "Asymmetric", DefinitionCategory.Prop, PlacementPolicy.Ground,
                    new Bounds3d(new Double3(.25, .375, -.5), new Double3(1, 2, 3)), null, matrix);
                var decoded = DocumentCodec.DecodeDefinition(DocumentCodec.EncodeDefinition(definition));
                Equal(decoded.MeshTransform.TransformPoint(new Double3(2, 3, 4)), new Double3(5.25, 5, 8.75));
                Equal(decoded.LocalBounds.Center, new Double3(.25, .375, -.5));
                var placement = new RoomPlacement("p", pin, new GridPosition(7, 2, -9), 3, pivotOffset: new Double3(.125, 0, -.0625), roomId: "room-a", hiddenInEditor: true, includeInExport: false);
                var doc = DocumentCodec.DecodeRoom(DocumentCodec.EncodeRoom(initial.With(placements: new[] { placement }, pinnedDependencies: new[] { pin })));
                Require(doc.Placements[0].Position.Equals(new GridPosition(7, 2, -9)) && doc.Placements[0].QuarterTurns == 3 && doc.Placements[0].HiddenInEditor && !doc.Placements[0].IncludeInExport, "Placement components or flags lost.");
                Equal(doc.Placements[0].PivotOffset, new Double3(.125, 0, -.0625));
            });
            Check("DOC-STRICT-UNTRUSTED-JSON", () =>
            {
                string json = Encoding.UTF8.GetString(DocumentCodec.EncodeRoom(initial));
                foreach (string invalid in new[] { json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"), json.Replace("\"title\":\"Initial\"", "\"title\":\"Initial\",\"title\":\"Duplicate\""),
                    json.Replace("\"title\":\"Initial\",", ""), json.Replace("\"title\":\"Initial\"", "\"$type\":\"Injected\",\"title\":\"Initial\""), json.Replace("\"unit\":\"meter\"", "\"unit\":\"inch\"") })
                    Reject(() => DocumentCodec.DecodeRoom(Encoding.UTF8.GetBytes(invalid)));
                Reject(() => DocumentCodec.DecodeRoom(Encoding.UTF8.GetBytes(json), 8));
                Reject(() => DocumentCodec.DecodeRoom(Encoding.UTF8.GetBytes(json), maximumDepth: 1));
                var d = Definition(pin); string encoded = Encoding.UTF8.GetString(DocumentCodec.EncodeDefinition(d));
                Reject(() => DocumentCodec.DecodeDefinition(Encoding.UTF8.GetBytes(encoded.Replace("\"meshTransform\":[1,0,0,0", "\"meshTransform\":[1e400,0,0,0"))));
            });
            Check("DOC-IMMUTABLE-INPUTS-AND-GLOBAL-ENTITY-TABLE", () =>
            {
                var entities = new[] { new RoomPlacement("shared", pin, default) }; var doc = initial.With(placements: entities, pinnedDependencies: new[] { pin },
                    rooms: new[] { new Room("room-a", "A", new[] { "shared" }), new Room("room-b", "B", new[] { "shared" }) });
                entities[0] = new RoomPlacement("replaced", pin, default);
                DocumentValidation.Validate(doc); Require(doc.Placements.Single().Id == "shared", "Constructor failed to isolate the global table.");
                Reject(() => DocumentValidation.Validate(doc.With(placements: new[] { doc.Placements[0], doc.Placements[0] })));
                Require(typeof(RoomGroup).GetProperties().All(value => value.Name != "Position" && value.Name != "Scale" && value.Name != "Rotation"), "Groups must have no transform.");
            });
            Check("DOC-NULL-RECORDS-AND-ASSET-CODEC-REJECT", () =>
            {
                string json = Encoding.UTF8.GetString(DocumentCodec.EncodeRoom(new RoomDocument("empty", stateId: "empty-state")));
                foreach (string property in new[] { "rooms", "groups", "placements", "pinnedDependencies" })
                    Reject(() => DocumentCodec.DecodeRoom(Encoding.UTF8.GetBytes(json.Replace("\"" + property + "\":[]", "\"" + property + "\":null"))));
                Reject(() => DocumentCodec.DecodeRoom(Encoding.UTF8.GetBytes(json.Replace("\"title\":\"\"", "\"title\":null"))));
                var asset = new AssetReference(Pin("model"), AssetStorageMode.Copy, "model.json");
                string encoded = Encoding.UTF8.GetString(DocumentCodec.EncodeAsset(asset));
                Reject(() => DocumentCodec.DecodeAsset(Encoding.UTF8.GetBytes(encoded.Replace("\"mode\":\"Copy\"", "\"mode\":99"))));
                Reject(() => DocumentCodec.DecodeAsset(Encoding.UTF8.GetBytes(encoded.Replace("\"uri\":\"model.json\"", "\"uri\":null"))));
                Reject(() => DocumentCodec.EncodeAsset(new AssetReference(Pin("model"), (AssetStorageMode)99, "model.json")));
            });
            Check("DOC-REFERENCE-CYCLES-AND-DEPTH", () =>
            {
                Reject(() => DocumentValidation.Validate(initial.With(groups: new[] { new RoomGroup("a", "A", "b"), new RoomGroup("b", "B", "a") })));
                var groups = Enumerable.Range(0, 129).Select(i => new RoomGroup("g" + i, "G", i == 0 ? null : "g" + (i - 1))).ToArray();
                DocumentValidation.Validate(initial.With(groups: groups.Take(128).ToArray()));
                Reject(() => DocumentValidation.Validate(initial.With(groups: groups)));
                Reject(() => DocumentValidation.Validate(initial.With(groups: groups.Reverse().ToArray())));
                Reject(() => DocumentValidation.Validate(initial.With(groups: new[] { new RoomGroup("a", "A", roomId: "room-a"), new RoomGroup("b", "B", "a", "room-b") })));
            });
            Check("DOC-ATTACHMENT-TWO-REPRESENTATIONS", () =>
            {
                var host = new RoomPlacement("host", pin, new GridPosition(4, 0, 8), 1, new Double3(2, 1, 1));
                var local = new Double3(.5, .25, 0); var child = new RoomPlacement("child", pin, new GridPosition(4, 1, 4), 1, attachment: new PlacementAttachment("host", localOffset: local));
                var doc = initial.With(placements: new[] { host, child }, pinnedDependencies: new[] { pin }); DocumentValidation.Validate(doc);
                Reject(() => DocumentValidation.Validate(doc.With(placements: new[] { host, child.WithTransform(new GridPosition(4, 1, 5), 1, Double3.One, Double3.Zero) })));
                Reject(() => DocumentValidation.Validate(doc.With(placements: new[] { child })));
            });
            Check("DOC-FIVE-FLOOR-MASKS", () =>
            {
                foreach (int mask in new[] { 15, 7, 9, 3, 1 }) DocumentValidation.ValidateDefinition(new LibraryDefinition(pin, "Floor", DefinitionCategory.Floor, PlacementPolicy.Structure, new Bounds3d(Double3.Zero, Double3.One), null, Matrix4d.Identity, floorMask: mask));
                foreach (int mask in new[] { 0, 2, 5, 8, 10, 16 }) Reject(() => DocumentValidation.ValidateDefinition(new LibraryDefinition(pin, "Floor", DefinitionCategory.Floor, PlacementPolicy.Structure, new Bounds3d(Double3.Zero, Double3.One), null, Matrix4d.Identity, floorMask: mask)));
            });
            Check("HISTORY-NOOP-AND-SAVEPOINT", () =>
            {
                using (var history = new DocumentHistory<RoomDocument>(initial))
                {
                    Require(!history.Apply("No change", value => value.With(title: "Initial")) && history.RetainedSteps == 0 && !history.IsDirty && history.Revision == 0, "No-op created history.");
                    history.Apply("Rename", value => value.With(title: "One")); long edited = history.Revision; string editedState = history.StateId;
                    Require(history.IsDirty && history.Undo() && !history.IsDirty && history.Revision > edited && history.Current.Title == "Initial", "Undo did not return to savepoint.");
                    Require(history.Redo() && history.StateId == editedState && history.IsDirty && history.Revision > edited + 1, "Redo state or revision differs.");
                    Require(history.MarkSaved(editedState) && !history.IsDirty, "Successful save did not update its state.");
                    history.Apply("Later", value => value.With(title: "Two")); Require(!history.MarkSaved(editedState) && history.IsDirty, "Older save cleared later edits.");
                }
            });
            Check("HISTORY-PREPARED-STALE-ATOMIC-FAILURE", () =>
            {
                using (var history = new DocumentHistory<RoomDocument>(initial))
                {
                    var command = history.Prepare("Delayed", value => value.With(title: "Delayed")); history.Apply("Immediate", value => value.With(title: "Immediate"));
                    string state = history.StateId; long revision = history.Revision; int events = 0; history.Changed += () => events++;
                    Reject(() => history.Execute(command));
                    Reject(() => history.Apply("Invalid bundle", value => value.With(title: "Partial", groups: new[] { new RoomGroup("broken", "Broken", "absent") })));
                    Require(history.Current.Title == "Immediate" && history.StateId == state && history.Revision == revision && events == 0, "Failure published partial state.");
                }
            });
            Check("HISTORY-BATCH-ORDER-UNDO-REDO", () =>
            {
                var doc = initial.With(rooms: Enumerable.Range(0, 6).Select(i => new Room("r" + i, "Room " + i)).ToArray());
                using (var history = new DocumentHistory<RoomDocument>(doc))
                {
                    history.Apply("Delete middle pair", value => value.With(rooms: value.Rooms.Where(room => room.Id != "r1" && room.Id != "r4").ToArray()));
                    Require(history.Current.Rooms.Select(room => room.Id).SequenceEqual(new[] { "r0", "r2", "r3", "r5" }), "Delete order differs.");
                    history.Undo(); Require(history.Current.Rooms.Select(room => room.Id).SequenceEqual(new[] { "r0", "r1", "r2", "r3", "r4", "r5" }), "Undo order differs.");
                    history.Redo(); Require(history.Current.Rooms.Select(room => room.Id).SequenceEqual(new[] { "r0", "r2", "r3", "r5" }), "Redo order differs.");
                }
            });
            Check("HISTORY-INDEPENDENT-BRANCHES-AND-DIRTY", () =>
            {
                using (var session = new RoomSession(initial))
                {
                    session.Apply("Room one", value => value.With(title: "Room one")); session.ApplyLibrary("Library one", value => value.With(name: "Library one"));
                    session.ProjectHistory.MarkSaved(session.Document.StateId); Require(!session.ProjectHistory.IsDirty && session.LibraryHistory.IsDirty, "Save leaked across contexts.");
                    session.ProjectHistory.Undo(); session.LibraryHistory.Undo();
                    session.Apply("New room branch", value => value.With(title: "Branch"));
                    Require(!session.ProjectHistory.CanRedo && session.LibraryHistory.CanRedo, "Room branch truncated library redo.");
                    session.LibraryHistory.Redo(); Require(session.LibraryDraft.Name == "Library one" && session.Document.Title == "Branch", "Independent redo mutated room.");
                }
            });
            Check("HISTORY-SHARED-BUDGET-OLDEST-BASELINE", () =>
            {
                var budget = new HistoryBudget(3, 1024 * 1024);
                using (var session = new RoomSession(initial, budget: budget))
                {
                    session.Apply("P1", value => value.With(title: "P1")); session.ApplyLibrary("L1", value => value.With(name: "L1"));
                    session.Apply("P2", value => value.With(title: "P2")); session.ApplyLibrary("L2", value => value.With(name: "L2"));
                    Require(budget.RetainedSteps == 3 && session.ProjectHistory.EarliestPosition == 1, "Shared oldest-entry eviction differs.");
                    Require(session.ProjectHistory.Undo() && session.Document.Title == "P1" && !session.ProjectHistory.CanUndo, "Trimmed baseline not preserved.");
                    Require(session.LibraryHistory.Undo() && session.LibraryDraft.Name == "L1" && session.LibraryHistory.Undo() && session.LibraryDraft.Name == "", "Other timeline was merged.");
                }
            });
            Check("HISTORY-BUDGET-REJECTION-ROLLBACK", () =>
            {
                var budget = new HistoryBudget(10, 4096);
                using (var history = new DocumentHistory<RoomDocument>(initial, budget))
                {
                    long bytes = budget.RetainedBytes; string state = history.StateId;
                    var random = new Random(417); string large = new string(Enumerable.Range(0, 30000).Select(_ => (char)random.Next(33, 126)).ToArray());
                    Reject(() => history.Apply("Oversized atomic edit", value => value.With(description: large)));
                    Require(history.StateId == state && history.Current.Description == "" && !history.IsDirty && budget.RetainedBytes == bytes && budget.RetainedSteps == 0, "Rejected command changed state or budget.");
                }
            });
            Check("HISTORY-SMALL-DELTA-NOT-WHOLE-DOCUMENT", () =>
            {
                var random = new Random(112); string text = new string(Enumerable.Range(0, 100000).Select(_ => (char)random.Next(33, 126)).ToArray());
                using (var history = new DocumentHistory<RoomDocument>(initial.With(description: text)))
                {
                    var delta = history.Prepare("Rename", value => value.With(title: "Tiny"));
                    Require(delta.ChangeCount == 1 && delta.RetainedBytes < 1024, "A title command retained the unchanged document.");
                    history.Execute(delta); history.Undo(); Require(history.Current.Description == text && history.Current.Title == "Initial", "Scalar undo altered unchanged content.");
                }
            });
            Check("HISTORY-DEFAULT-500-STEPS", () =>
            {
                using (var history = new DocumentHistory<RoomDocument>(initial))
                {
                    Require(history.Budget.MaximumSteps == 500 && history.Budget.MaximumBytes == 256L * 1024 * 1024, "Default session limits differ.");
                    for (int i = 1; i <= 501; i++) { int step = i; history.Apply("Step", value => value.With(title: "T" + step)); }
                    Require(history.RetainedSteps == 500 && history.EarliestPosition == 1 && history.Current.Title == "T501", "Default step cap or current state lost.");
                }
            });
            Check("HISTORY-CANCEL-AFTER-STAGING-AND-CROSS-BUDGET-ROLLBACK", () =>
            {
                using (var cancellation = new CancellationTokenSource())
                {
                    bool cancelOnAcquire = false;
                    using (var history = new DocumentHistory<RoomDocument>(initial, acquireLeases: _ => { if (cancelOnAcquire) cancellation.Cancel(); return Array.Empty<ImportedSceneLease>(); }))
                    {
                        cancelOnAcquire = true; string state = history.StateId;
                        Reject(() => history.Apply("Cancel staged resources", value => value.With(title: "Never published"), cancellation.Token));
                        Require(history.StateId == state && history.RetainedSteps == 0 && !history.IsDirty, "Cancellation after staging published data.");
                    }
                }
                using (var session = new RoomSession(initial, budget: new HistoryBudget(4, 4096)))
                {
                    session.ApplyLibrary("L1", value => value.With(name: "L1")); session.ApplyLibrary("L2", value => value.With(name: "L2"));
                    long earliest = session.LibraryHistory.EarliestPosition; long bytes = session.Budget.RetainedBytes;
                    var random = new Random(74); string large = new string(Enumerable.Range(0, 30000).Select(_ => (char)random.Next(33, 126)).ToArray());
                    Reject(() => session.Apply("Too large", value => value.With(description: large)));
                    Require(session.LibraryHistory.EarliestPosition == earliest && session.LibraryHistory.RetainedSteps == 2 && session.Budget.RetainedBytes == bytes, "Failed reservation evicted another context.");
                }
            });
            Check("HISTORY-NOTIFICATION-IS-AFTER-COMMIT", () =>
            {
                using (var history = new DocumentHistory<RoomDocument>(initial))
                {
                    history.Changed += () => { throw new InvalidOperationException("View failed"); };
                    Require(history.Apply("Accepted", value => value.With(title: "Accepted")) && history.Current.Title == "Accepted" && history.LastNotificationError != null, "View callback turned committed edit into a partial failure.");
                }
            });
            Check("HISTORY-SAVE-RECEIPT-GENERATION", () =>
            {
                using (var history = new DocumentHistory<RoomDocument>(initial)) using (var snapshot = history.CaptureSnapshot())
                {
                    history.Apply("Later", value => value.With(title: "Later"));
                    Require(!history.MarkSaved(snapshot) && history.IsDirty && history.SavepointStateId == snapshot.StateId, "Snapshot save changed a later state.");
                    history.Replace(new RoomDocument("new", "New", stateId: "new-state"));
                    Require(!history.MarkSaved(snapshot) && !history.IsDirty && history.SavepointStateId == "new-state", "Late save receipt marked a replacement document dirty.");
                }
            });
            Check("SESSION-EXPLICIT-PIN-UPGRADE-UNDO", () =>
            {
                var old = Definition(pin); var next = Definition(Pin("prop", 2, 'b'));
                using (var session = new RoomSession())
                {
                    session.RegisterDefinition(old); session.RegisterDefinition(next);
                    session.OpenRoom(initial.With(placements: new[] { new RoomPlacement("p", old.Reference, default) }, pinnedDependencies: new[] { old.Reference }));
                    Require(session.Document.Placements[0].Definition.Equals(old.Reference), "Publishing silently upgraded the project.");
                    Require(session.UpgradeDefinition(old.Reference, next) && session.Document.Placements[0].Definition.Equals(next.Reference) && session.ProjectHistory.RetainedSteps == 1, "Upgrade did not atomically replace fixed references.");
                    session.ProjectHistory.Undo(); Require(session.Document.Placements[0].Definition.Equals(old.Reference) && session.Document.PinnedDependencies[0].Equals(old.Reference), "Upgrade undo lost the old fixed version.");
                    Require(session.ResolveDefinition(next.Reference) == next, "Undo removed the published definition.");
                }
            });
            Check("SESSION-RESOURCE-LEASES-HISTORY-AND-SNAPSHOT", () =>
            {
                int releases = 0; var modelPin = Pin("model"); var definition = Definition(pin, modelPin); var source = ImportedSceneLease.Create(AffineProbeFixture.Create(), () => releases++);
                var session = new RoomSession(); HistorySnapshot<RoomDocument> snapshot = null;
                try
                {
                    session.RegisterDefinition(definition); session.RegisterModel(modelPin, source); source.Dispose();
                    session.OpenRoom(initial.With(placements: new[] { new RoomPlacement("p", pin, default) }, pinnedDependencies: new[] { pin }));
                    snapshot = session.ProjectHistory.CaptureSnapshot();
                    session.Apply("Delete and detach unused definition", value => value.With(placements: Array.Empty<RoomPlacement>(), pinnedDependencies: Array.Empty<PinnedReference>()));
                    session.UnregisterModel(modelPin); Require(releases == 0, "Deleted model was released while undo and export owned it.");
                    session.ProjectHistory.Undo(); Require(session.Document.Placements.Count == 1, "Undo failed after registry owner release.");
                    session.Apply("Rename restored model", value => value.With(title: "Restored"));
                    Require(session.Document.Title == "Restored" && releases == 0, "Restored history model could not be acquired for a later edit.");
                    session.Dispose(); Require(releases == 0 && snapshot.Document.Placements.Single().Id == "p", "Export snapshot lost its own model lifetime.");
                    snapshot.Dispose(); Require(releases == 1, "Final lease did not release exactly once.");
                }
                finally { snapshot?.Dispose(); session.Dispose(); source.Dispose(); }
            });
            Check("SESSION-FAILED-RESOURCE-ACQUISITION-ROLLBACK", () =>
            {
                using (var session = new RoomSession(initial))
                {
                    var d = Definition(pin, Pin("missing-model")); session.RegisterDefinition(d); string state = session.Document.StateId; long bytes = session.Budget.RetainedBytes;
                    Reject(() => session.Apply("Add missing model", value => value.With(placements: new[] { new RoomPlacement("p", pin, default) }, pinnedDependencies: new[] { pin })));
                    Require(session.Document.StateId == state && session.Document.Placements.Count == 0 && session.Budget.RetainedBytes == bytes, "Model failure partially committed.");
                }
            });
            Check("SESSION-EVICTION-RELEASES-ONLY-UNOWNED-MODEL", () =>
            {
                int released = 0; var model = Pin("trimmed-model"); var definition = Definition(pin, model);
                using (var source = ImportedSceneLease.Create(AffineProbeFixture.Create(), () => released++)) using (var session = new RoomSession(budget: new HistoryBudget(1)))
                {
                    session.RegisterDefinition(definition); session.RegisterModel(model, source); source.Dispose();
                    session.OpenRoom(initial.With(placements: new[] { new RoomPlacement("p", pin, default) }, pinnedDependencies: new[] { pin }));
                    session.Apply("Delete", value => value.With(placements: Array.Empty<RoomPlacement>(), pinnedDependencies: Array.Empty<PinnedReference>()));
                    session.UnregisterModel(model); Require(released == 0, "Undo model released too early.");
                    session.Apply("Advance baseline", value => value.With(title: "Next"));
                    Require(released == 1 && session.ProjectHistory.EarliestPosition == 1 && session.Document.Placements.Count == 0, "Evicted unused model retained or current document lost.");
                }
            });
            Check("DOC-LIBRARY-CODEC-AND-COPY-PATH", () =>
            {
                var model = Pin("model"); var d = Definition(pin, model); var asset = new AssetReference(model, AssetStorageMode.Copy, "model.json", "Original fixture", "{}", "vrc-probe", dependencies: new[] { new AssetDependency("source.obj", new string('c', 64), "original") });
                var draft = new LibraryDraft("draft", "repository", new[] { d }, new[] { asset }, "Draft");
                var decoded = DocumentCodec.DecodeLibrary(DocumentCodec.EncodeLibrary(draft)); Require(decoded.Definitions[0].Reference.Equals(pin) && decoded.Assets[0].Dependencies[0].SourceReference == "original", "Library manifest lost fixed provenance.");
                Reject(() => DocumentValidation.Validate(draft.With(assets: new[] { new AssetReference(model, AssetStorageMode.Copy, "../outside.json") })));
                Reject(() => DocumentValidation.Validate(draft.With(assets: Array.Empty<AssetReference>())));
            });
            await CheckAsync("HISTORY-CHECKPOINT-RECONSTRUCTION", async () =>
            {
                using (var history = new DocumentHistory<RoomDocument>(initial))
                {
                    for (int i = 1; i <= 105; i++) { int value = i; history.Apply("Step " + value, document => document.With(title: "T" + value)); }
                    Require(history.CheckpointCount == 2, "Periodic compressed checkpoints missing."); long before = history.Revision;
                    Require(await history.JumpAsync(53) && history.Current.Title == "T53" && history.CurrentPosition == 53 && history.Revision > before, "Checkpoint reconstruction differs.");
                    Require(await history.JumpAsync(0) && history.Current.Title == "Initial" && !history.IsDirty, "Baseline reconstruction differs.");
                    Require(await history.JumpAsync(105) && history.Current.Title == "T105", "Forward reconstruction differs.");
                }
            });
            await CheckAsync("HISTORY-CANCELLED-JUMP-IS-ATOMIC", async () =>
            {
                using (var history = new DocumentHistory<RoomDocument>(initial)) using (var cancellation = new CancellationTokenSource())
                {
                    history.Apply("One", value => value.With(title: "One")); string state = history.StateId; long revision = history.Revision;
                    cancellation.Cancel(); await RejectAsync(() => history.JumpAsync(0, cancellation.Token));
                    Require(history.StateId == state && history.Revision == revision && history.Current.Title == "One" && history.IsDirty && !history.IsBusy, "Cancelled jump published a state.");
                }
            });
            await CheckAsync("HISTORY-DOCUMENT-REPLACEMENT-INVALIDATES-JUMP", async () =>
            {
                using (var history = new DocumentHistory<RoomDocument>(initial))
                {
                    for (int i = 1; i <= 49; i++) { int step = i; history.Apply("Step", value => value.With(title: "T" + step)); }
                    long generation = history.Generation; var jump = history.JumpAsync(1);
                    history.Replace(new RoomDocument("replacement", "Replacement", stateId: "replacement-state"));
                    try { await jump; } catch (OperationCanceledException) { }
                    Require(history.Generation == generation + 1 && history.Current.ProjectId == "replacement" && history.Current.Title == "Replacement" && history.RetainedSteps == 0 && !history.IsDirty, "Late jump overwrote replacement.");
                }
            });
            return new EditingProbeReport(checks.AsReadOnly());
        }
        private static PinnedReference Pin(string id, long revision = 1, char digest = 'a') => new PinnedReference(id, revision, new string(digest, 64));
        private static LibraryDefinition Definition(PinnedReference reference, PinnedReference model = null) => new LibraryDefinition(reference, "Original prop", DefinitionCategory.Prop,
            PlacementPolicy.Ground, new Bounds3d(new Double3(.5, .5, .5), Double3.One), null, Matrix4d.Identity, model);
        private static void Equal(Double3 actual, Double3 expected) { Require((actual - expected).Length < 1e-10, "Expected " + expected + ", actual " + actual); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Reject(Action action) { try { action(); } catch { return; } throw new InvalidOperationException("Expected rejection."); }
        private static async Task RejectAsync(Func<Task> action) { try { await action(); } catch (OperationCanceledException) { return; } throw new InvalidOperationException("Expected cancellation."); }
    }
}
