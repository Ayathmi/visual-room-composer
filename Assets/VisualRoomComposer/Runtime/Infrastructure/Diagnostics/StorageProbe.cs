using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using VisualRoomComposer.Core;
using VisualRoomComposer.Core.Documents;
using VisualRoomComposer.Core.RoomGeometry;
using VisualRoomComposer.Infrastructure.Libraries;
using VisualRoomComposer.Infrastructure.Persistence;

namespace VisualRoomComposer.Infrastructure.Diagnostics
{
    [Serializable] public sealed class StorageProbeCheck { public string id, status, detail; }
    [Serializable] public sealed class StorageProbeReport
    {
        public string phase, root, utc; public int passed, failed, blocked; public string projectHash, projectStateId;
        public int placements, models, vertices, textures; public List<StorageProbeCheck> checks = new List<StorageProbeCheck>();
    }
    /// <summary>Invoke seed and reopen in separate Player processes with the same fresh isolated root. These functions do not invoke Unity APIs.</summary>
    public static class StorageProbe
    {
        private const string SessionId = "f81fd9f184c04bc4b027967fe7a6e871";
        public static StorageProbeReport RunSeed(string root)
        {
            var report = New("seed", root); root = report.root;
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("Seed requires a fresh empty isolated directory.");
            Directory.CreateDirectory(root); var files = new AtomicFileStore();
            var fixture = RoomSampleFixture.Create(); var bundle = ProjectSealer.Seal(fixture.Document, fixture.Definitions, fixture.Models);
            string projectPath = Path.Combine(root, "原创样间.vrc.json"); var store = new ProjectStore(); StorageReceipt projectReceipt = null;
            Check(report, "STORE-PROJECT-SAVE", () => { projectReceipt = store.Save(projectPath, bundle); Assert(store.Load(projectPath).Bundle.Document.StateId == bundle.Document.StateId, "State changed on save."); });
            if (projectReceipt == null) return report;
            report.projectHash = projectReceipt.ContentHash; report.projectStateId = bundle.Document.StateId; Counts(report, bundle);
            Check(report, "STORE-CPU-ATTRIBUTES", () =>
            {
                var source = TexturedFixture(); var decoded = ModelSceneCodec.Decode(ModelSceneCodec.Encode(source));
                Assert(ModelSceneCodec.Encode(source).SequenceEqual(ModelSceneCodec.Encode(decoded)), "CPU attributes did not round-trip exactly.");
                Assert(decoded.Meshes[0].Positions[1].X == 2 && decoded.Meshes[0].Uv1[0].X == 0.1 && decoded.Meshes[0].Tangents[5].W == -1 && decoded.Textures[0].ByteLength > 0, "Independent attribute sentinels failed.");
                var world = ModelSceneValidator.ResolveWorldTransforms(decoded); Assert((world["shear"].TransformPoint(decoded.Meshes[0].Positions[1]) - new Double3(7.5, 2.5, -1.75)).Length < 1e-12, "Persisted affine transform changed.");
                files.Save(Path.Combine(root, "textured-model.json"), ModelSceneCodec.Encode(source));
            });
            byte[] old = files.Read(projectPath).Bytes; var changed = new ProjectBundle(bundle.Document.With(title: "changed candidate"), bundle.Definitions, bundle.Models);
            Check(report, "STORE-EXTERNAL-MODIFICATION", () =>
            { ExpectFailure(() => store.Save(projectPath, changed, new string('0', 64))); Assert(files.Read(projectPath).Bytes.SequenceEqual(old), "External conflict overwrote bytes."); });
            Check(report, "STORE-WRITE-FAILURE", () => FailurePreserves(root, bundle, old, StorageFaultPoint.AfterTemporaryWrite, new IOException("Injected storage failure.")));
            Check(report, "STORE-PERMISSION-INJECTION", () => FailurePreserves(root, bundle, old, StorageFaultPoint.BeforeCommit, new UnauthorizedAccessException("Injected permission refusal; this is not a real ACL test.")));
            Check(report, "STORE-READBACK-CORRUPTION", () =>
            {
                string path = Path.Combine(root, "readback.json"); files.Save(path, old); var broken = new ProjectStore(new AtomicFileStore(fault: (p, f) => { if (p == StorageFaultPoint.BeforeReadBack) File.WriteAllText(f, "{}"); }));
                ExpectFailure(() => broken.Save(path, changed, AtomicFileStore.Hash(old))); Assert(files.Read(path).Bytes.SequenceEqual(old), "Readback failure overwrote bytes.");
            });
            Check(report, "STORE-EXTERNAL-BEFORE-COMMIT", () =>
            {
                string path = Path.Combine(root, "external.json"); files.Save(path, old); byte[] external = Encoding.UTF8.GetBytes("external owner bytes");
                var conflict = new ProjectStore(new AtomicFileStore(fault: (p, f) => { if (p == StorageFaultPoint.BeforeCommit) File.WriteAllBytes(f, external); }));
                ExpectFailure(() => conflict.Save(path, changed, AtomicFileStore.Hash(old))); Assert(files.Read(path).Bytes.SequenceEqual(external), "External edit was overwritten.");
            });
            Check(report, "STORE-CANCEL-BEFORE-COMMIT", () =>
            {
                string path = Path.Combine(root, "cancel.json"); files.Save(path, old);
                using (var stop = new CancellationTokenSource())
                { var cancelled = new ProjectStore(new AtomicFileStore(fault: (p, f) => { if (p == StorageFaultPoint.BeforeCommit) stop.Cancel(); })); ExpectCancel(() => cancelled.Save(path, changed, AtomicFileStore.Hash(old), stop.Token)); }
                Assert(files.Read(path).Bytes.SequenceEqual(old), "Cancelled save overwrote bytes.");
            });
            Check(report, "STORE-CANCEL-AFTER-COMMIT", () =>
            {
                string path = Path.Combine(root, "committed.json"); using (var stop = new CancellationTokenSource())
                { var committed = new ProjectStore(new AtomicFileStore(fault: (p, f) => { if (p == StorageFaultPoint.AfterCommit) stop.Cancel(); })); var receipt = committed.Save(path, changed, cancellation: stop.Token); Assert(receipt.Committed && store.Load(path).Bundle.Document.Title == "changed candidate", "Late cancellation lost commit receipt."); }
            });
            Check(report, "STORE-LOCK-OLD-BYTES", () =>
            {
                string path = Path.Combine(root, "locked.json"); files.Save(path, old);
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) ExpectFailure(() => store.Save(path, changed, AtomicFileStore.Hash(old)));
                Assert(files.Read(path).Bytes.SequenceEqual(old), "Locked destination changed.");
            });
            Check(report, "STORE-BRIEF-LOCK-RETRY", () =>
            {
                string path = Path.Combine(root, "brief-lock.json"); files.Save(path, old); var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var release = new Thread(() => { Thread.Sleep(80); held.Dispose(); }); release.Start();
                try { var receipt = store.Save(path, changed, AtomicFileStore.Hash(old)); Assert(receipt.Committed && store.Load(path).Bundle.Document.Title == "changed candidate", "Brief read lock did not recover."); }
                finally { release.Join(); held.Dispose(); }
            });
            Check(report, "STORE-STRICT-SCHEMA", () =>
            {
                var json = Encoding.UTF8.GetString(old); ExpectFailure(() => ProjectBundleCodec.Decode(Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":99"))));
                ExpectFailure(() => ProjectBundleCodec.Decode(Encoding.UTF8.GetBytes(json.Replace("\"format\":", "\"$type\":"))));
                ExpectFailure(() => ProjectBundleCodec.Decode(Encoding.UTF8.GetBytes("{\"format\":1,\"format\":2}")));
                ExpectFailure(() => new AtomicFileStore(8).Read(projectPath)); ExpectFailure(() => StoragePaths.Under(root, "../escape"));
            });
            Check(report, "STORE-MISSING-DEPENDENCY", () => { ExpectFailure(() => store.Save(projectPath, new ProjectBundle(bundle.Document, bundle.Definitions, Array.Empty<StoredModel>()), projectReceipt.ContentHash)); Assert(files.Read(projectPath).Bytes.SequenceEqual(old), "Missing dependency changed project."); });
            Check(report, "STORE-PIN-HASH-CONFLICT", () =>
            {
                var definition = bundle.Definitions[0]; var conflicting = definition.WithReference(new PinnedReference(definition.Id, definition.Reference.Revision, new string('0', 64)));
                ExpectFailure(() => ProjectBundleCodec.Encode(new ProjectBundle(bundle.Document, bundle.Definitions.Concat(new[] { conflicting }), bundle.Models)));
                var badModels = bundle.Models.ToArray(); badModels[0] = new StoredModel(badModels[0].Asset, TexturedFixture());
                ExpectFailure(() => store.Save(projectPath, new ProjectBundle(bundle.Document, bundle.Definitions, badModels), projectReceipt.ContentHash));
                Assert(files.Read(projectPath).Bytes.SequenceEqual(old), "Conflicting pins or altered CPU payload changed previous project.");
            });
            RunLibraryChecks(report, root, bundle);
            RunReparseChecks(report, root, bundle);
            var recovery = new RecoveryStore(Path.Combine(root, "recovery"));
            Check(report, "STORE-RECOVERY-TWO-GENERATIONS", () =>
            {
                recovery.Capture(SessionId, bundle, projectPath); recovery.Capture(SessionId, changed, projectPath);
                var third = new ProjectBundle(bundle.Document.With(title: "third recovery"), bundle.Definitions, bundle.Models); recovery.Capture(SessionId, third, projectPath);
                Assert(recovery.List().Single().Available && Directory.GetDirectories(Path.Combine(root, "recovery", SessionId)).Length == 2, "Recovery did not retain two generations.");
                var current = recovery.LoadCopy(SessionId); var previous = recovery.LoadCopy(SessionId, true);
                Assert(current.RequiresSave && current.Bundle.Document.Title == "third recovery" && previous.Bundle.Document.Title == "changed candidate" && current.Bundle.Document.ProjectId != bundle.Document.ProjectId, "Recovery copy contract failed.");
                Assert(bundle.Document.StateId == report.projectStateId && files.Read(projectPath).Bytes.SequenceEqual(old), "Capture modified live state or original project.");
                using (var stop = new CancellationTokenSource()) { stop.Cancel(); ExpectCancel(() => recovery.Capture(SessionId, bundle, cancellation: stop.Token)); }
                Assert(recovery.LoadCopy(SessionId).Bundle.Document.Title == "third recovery", "Cancellation changed recovery entry.");
            });
            Check(report, "STORE-RECOVERY-TAMPER-REJECTION", () =>
            {
                string otherId = Guid.NewGuid().ToString("N"); recovery.Capture(otherId, bundle); string version = recovery.List().Single(c => c.SessionId == otherId).CurrentVersion;
                File.AppendAllText(Path.Combine(root, "recovery", otherId, version, "project.json"), " ");
                ExpectFailure(() => recovery.LoadCopy(otherId)); Assert(!recovery.List().Single(c => c.SessionId == otherId).Available, "Corrupt recovery was hidden or accepted.");
            });
            Check(report, "STORE-TEMP-CLEANUP", () => Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "Uncommitted temporary files remain."));
            files.Save(Path.Combine(root, "restart-expectations.json"), StorageJson.Encode(new { format = "visual-room-composer.storage-probe", schemaVersion = 1, projectHash = report.projectHash, projectStateId = report.projectStateId, report.placements, report.models, report.vertices }));
            return report;
        }

        public static StorageProbeReport RunReopen(string root)
        {
            var report = New("reopen", root); root = report.root; var files = new AtomicFileStore();
            Check(report, "STORE-PROCESS-REOPEN", () =>
            {
                using (var json = StorageJson.Parse(files.Read(Path.Combine(root, "restart-expectations.json")).Bytes))
                {
                    var e = json.RootElement; var loaded = new ProjectStore().Load(Path.Combine(root, "原创样间.vrc.json")); Counts(report, loaded.Bundle);
                    report.projectHash = loaded.ContentHash; report.projectStateId = loaded.Bundle.Document.StateId;
                    Assert(report.projectHash == StorageJson.S(e, "projectHash") && report.projectStateId == StorageJson.S(e, "projectStateId") && report.placements == StorageJson.I(e, "placements") && report.models == StorageJson.I(e, "models") && report.vertices == StorageJson.I(e, "vertices"), "Project or actual CPU geometry changed across process restart.");
                    var fixture = RoomSampleFixture.Create(); var original = ProjectSealer.Seal(fixture.Document, fixture.Definitions, fixture.Models);
                    foreach (var model in loaded.Bundle.Models) Assert(ModelSceneCodec.Encode(model.Scene).SequenceEqual(ModelSceneCodec.Encode(original.Models.Single(m => m.Asset.Reference.Equals(model.Asset.Reference)).Scene)), "CPU model differs from original fixture after restart.");
                }
            });
            Check(report, "STORE-TEXTURE-PROCESS-REOPEN", () => { var scene = ModelSceneCodec.Decode(files.Read(Path.Combine(root, "textured-model.json")).Bytes); Assert(scene.Textures.Count == 1 && scene.Materials[0].NormalTexture.TexCoord == 1 && scene.Materials[0].BaseColorTexture.RotationRadians == 0.25 && scene.Meshes[0].Tangents[5].W == -1, "Texture/sampler/tangent state lost on restart."); });
            Check(report, "LIB-PROCESS-REOPEN", () =>
            { var catalog = new RepositoryCatalog(Path.Combine(root, "metadata"), Path.Combine(root, "library")); var versions = new ImmutableLibrary(catalog, new LinkRelocations(Path.Combine(root, "metadata"))).ListVersions(); Assert(versions.Count >= 3 && versions.Any(v => v.Reference.Revision == 2), "Immutable versions lost on restart."); });
            Check(report, "STORE-RECOVERY-PROCESS-REOPEN", () =>
            { var recovery = new RecoveryStore(Path.Combine(root, "recovery")); Assert(recovery.LoadCopy(SessionId).Bundle.Document.Title == "third recovery" && recovery.LoadCopy(SessionId, true).Bundle.Document.Title == "changed candidate", "Recovery generations lost across restart."); });
            return report;
        }

        private static void RunLibraryChecks(StorageProbeReport report, string root, ProjectBundle bundle)
        {
            var catalog = new RepositoryCatalog(Path.Combine(root, "metadata"), Path.Combine(root, "library")); var relocations = new LinkRelocations(Path.Combine(root, "metadata")); var library = new ImmutableLibrary(catalog, relocations);
            var definition = bundle.Definitions.First(d => d.ModelReference != null); var model = bundle.Models.Single(m => m.Asset.Reference.Equals(definition.ModelReference)); LibraryVersion first = null, next = null;
            Check(report, "LIB-IMMUTABLE-PUBLISH", () => { first = library.Publish(new LibraryCandidate(definition, model)); ExpectFailure(() => library.Publish(new LibraryCandidate(definition, model))); Assert(library.ReadPinned(first.Reference).Model.Scene.Meshes.Count > 0, "Published actual model missing."); });
            if (first == null) return;
            Check(report, "LIB-NEW-VERSION-PINNED-OLD", () =>
            {
                var revised = definition.WithReference(new PinnedReference(definition.Id, 2, new string('0', 64))); next = library.Publish(new LibraryCandidate(revised, model));
                Assert(library.ReadPinned(first.Reference).Reference.Revision == 1 && next.Reference.Revision == 2 && bundle.Document.PinnedDependencies.Any(p => p.Equals(first.Reference)), "Publication changed a project reference.");
            });
            Check(report, "LIB-REGISTER-REMOVE-NO-DELETE", () =>
            {
                string external = Path.Combine(root, "external-library"); Directory.CreateDirectory(external); string id = catalog.Register(external); catalog.SetPublishing(id);
                var d = definition.WithReference(new PinnedReference("external-original", 1, new string('0', 64))); var published = library.Publish(new LibraryCandidate(d, model)); catalog.Remove(id);
                Assert(File.Exists(Path.Combine(published.Directory, "version.json")) && catalog.PublishingId == "local", "Unregister deleted data or retained invalid publishing selection.");
                catalog.Register(external); Assert(library.ReadPinned(published.Reference).Reference.Equals(published.Reference), "Re-registration lost the same fixed identity.");
            });
            Check(report, "LIB-CROSS-REPOSITORY-CONFLICT", () =>
            {
                string conflictRoot = Path.Combine(root, "conflict-library"); var otherCatalog = new RepositoryCatalog(Path.Combine(root, "conflict-metadata"), conflictRoot); var otherLibrary = new ImmutableLibrary(otherCatalog);
                var changed = new LibraryDefinition(definition.Reference, "conflicting metadata", definition.Category, definition.PlacementPolicy, definition.LocalBounds, definition.Hitbox, definition.MeshTransform, definition.ModelReference, definition.FloorMask, definition.OccupancyPolicy, definition.WallBoundaries, definition.OpeningSlots, definition.PreviewLights);
                otherLibrary.Publish(new LibraryCandidate(changed, model)); int before = catalog.Repositories.Count; ExpectFailure(() => catalog.Register(conflictRoot)); Assert(catalog.Repositories.Count == before && library.ReadPinned(first.Reference) != null, "Conflict altered catalog or old version.");
            });
            Check(report, "LIB-COPY-COMPLETE-CANDIDATE", () =>
            {
                string source = Path.Combine(root, "original-source.bin"); File.WriteAllBytes(source, new byte[] { 4, 9, 16, 25 }); string hash = AtomicFileStore.Hash(File.ReadAllBytes(source));
                var asset = new AssetReference(model.Asset.Reference, AssetStorageMode.Copy, "sources/original.bin", dependencies: new[] { new AssetDependency("sources/original.bin", hash) });
                var candidate = new LibraryCandidate(definition.WithReference(new PinnedReference("copy-original", 1, new string('0', 64))), new StoredModel(asset, model.Scene), new Dictionary<string, string> { ["sources/original.bin"] = source });
                var published = library.Publish(candidate); File.Delete(source); Assert(library.ReadPinned(published.Reference).Model != null, "Copied version depends on its deleted original.");
            });
            Check(report, "LIB-MISSING-SOURCE-ROLLBACK", () =>
            {
                int count = library.ListVersions().Count; var asset = new AssetReference(model.Asset.Reference, AssetStorageMode.Copy, "sources/missing.bin", dependencies: new[] { new AssetDependency("sources/missing.bin", new string('0', 64)) });
                ExpectFailure(() => library.Publish(new LibraryCandidate(definition.WithReference(new PinnedReference("missing-original", 1, new string('0', 64))), new StoredModel(asset, model.Scene))));
                Assert(library.ListVersions().Count == count && !Directory.EnumerateDirectories(Path.Combine(root, "library"), ".staging-*", SearchOption.AllDirectories).Any(), "Failed candidate became visible.");
            });
            Check(report, "LIB-CANCEL-STAGED-CANDIDATE", () =>
            {
                int count = library.ListVersions().Count; using (var stop = new CancellationTokenSource())
                {
                    var stopped = new ImmutableLibrary(catalog, relocations, new AtomicFileStore(fault: (point, path) => { if (point == StorageFaultPoint.AfterCommit && Path.GetFileName(path) == "version.json") stop.Cancel(); }));
                    var candidate = new LibraryCandidate(definition.WithReference(new PinnedReference("cancelled-original", 1, new string('0', 64))), model);
                    ExpectCancel(() => stopped.Publish(candidate, stop.Token));
                }
                Assert(library.ListVersions().Count == count && !Directory.EnumerateDirectories(Path.Combine(root, "library"), ".staging-*", SearchOption.AllDirectories).Any(), "Cancelled candidate became registered.");
            });
            Check(report, "LIB-LINK-HASH-RELOCATION", () =>
            {
                string original = Path.Combine(root, "linked-source.bin"), destination = Path.Combine(root, "relocated-source.bin"); File.WriteAllBytes(original, new byte[] { 2, 3, 5, 7 }); string hash = AtomicFileStore.Hash(File.ReadAllBytes(original));
                var asset = new AssetReference(model.Asset.Reference, AssetStorageMode.Link, original, dependencies: new[] { new AssetDependency(original, hash) });
                var published = library.Publish(new LibraryCandidate(definition.WithReference(new PinnedReference("linked-original", 1, new string('0', 64))), new StoredModel(asset, model.Scene)));
                File.Move(original, destination); ExpectFailure(() => library.ReadPinned(published.Reference)); relocations.Relocate(original, hash, destination);
                Assert(library.ReadPinned(published.Reference).Reference.Equals(published.Reference), "Relocation changed fixed identity.");
                ExpectFailure(() => relocations.Relocate(original, new string('0', 64), destination));
                byte[] prior = File.ReadAllBytes(destination); File.AppendAllText(destination, "changed"); ExpectFailure(() => library.ReadPinned(published.Reference)); File.WriteAllBytes(destination, prior);
                string extra = Path.Combine(root, "post-relocation-library"); Directory.CreateDirectory(extra); catalog.Register(extra);
                Assert(library.ReadPinned(first.Reference) != null, "Unrelated old version became unreadable after relocation.");
            });
        }
        private static ModelScene TexturedFixture()
        {
            var s = AffineProbeFixture.Create(); byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
            var texture = new ModelTexture("test-texture", "原创纹理", "image/png", png, "original-procedural");
            var material = new ModelMaterial("red", "Textured original", new Double4(.2, .3, .4, .5), .35, .65, new Double3(.1, .2, .3), MaterialAlphaMode.Mask, .4, true,
                new ModelTextureBinding(texture.Id, rotationRadians: .25, offset: new Double2(.1, .2)), normalTexture: new ModelTextureBinding(texture.Id, 1, TextureColorSpace.Linear, TextureWrap.Clamp, TextureWrap.Mirror), normalScale: .7);
            return new ModelScene(s.Id, s.Nodes, s.Meshes, new[] { material, s.Materials[1] }, new[] { texture }, diagnostics: new[] { new ModelDiagnostic("OriginalProbe", DiagnosticSeverity.Information, "fixture", "Original CPU texture fixture") });
        }
        private static void RunReparseChecks(StorageProbeReport report, string root, ProjectBundle bundle)
        {
            string scope = Path.Combine(root, "reparse-checks"), outside = Path.Combine(scope, "outside"), inside = Path.Combine(scope, "inside"), alias = Path.Combine(inside, "directory-link");
            Directory.CreateDirectory(outside); Directory.CreateDirectory(inside); string source = Path.Combine(outside, "original.bin"); byte[] bytes = { 4, 9, 16, 25 }; File.WriteAllBytes(source, bytes);
            try { CreateProbeLink(alias, outside, true); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is PlatformNotSupportedException)
            { report.blocked++; report.checks.Add(new StorageProbeCheck { id = "STORE-OS-REPARSE", status = "Blocked", detail = "Actual OS directory symbolic link could not be created: " + error.Message }); return; }
            var files = new AtomicFileStore(); var catalog = new RepositoryCatalog(Path.Combine(root, "metadata"), Path.Combine(root, "library")); var library = new ImmutableLibrary(catalog, new LinkRelocations(Path.Combine(root, "metadata")));
            string linkedSource = Path.Combine(alias, "original.bin"); var definition = bundle.Definitions.First(d => d.ModelReference != null); var model = bundle.Models.Single(m => m.Asset.Reference.Equals(definition.ModelReference));
            try
            {
                Check(report, "STORE-DIRECTORY-REPARSE", () =>
                {
                    ExpectInvalidPath(() => files.Read(linkedSource)); ExpectInvalidPath(() => files.Save(Path.Combine(alias, "new.json"), bytes));
                    ExpectInvalidPath(() => StoragePaths.Under(inside, "directory-link/original.bin")); ExpectInvalidPath(() => catalog.Register(alias));
                    Assert(File.ReadAllBytes(source).SequenceEqual(bytes) && !File.Exists(Path.Combine(outside, "new.json")), "Reparse target was read or modified through the storage API.");
                });
                Check(report, "LIB-SOURCE-REPARSE", () =>
                {
                    int before = library.ListVersions().Count; string hash = AtomicFileStore.Hash(bytes);
                    var copy = new AssetReference(model.Asset.Reference, AssetStorageMode.Copy, "sources/original.bin", dependencies: new[] { new AssetDependency("sources/original.bin", hash) });
                    ExpectInvalidPath(() => library.Publish(new LibraryCandidate(definition.WithReference(new PinnedReference("symlink-copy", 1, new string('0', 64))), new StoredModel(copy, model.Scene), new Dictionary<string, string> { ["sources/original.bin"] = linkedSource })));
                    var link = new AssetReference(model.Asset.Reference, AssetStorageMode.Link, linkedSource, dependencies: new[] { new AssetDependency(linkedSource, hash) });
                    ExpectInvalidPath(() => library.Publish(new LibraryCandidate(definition.WithReference(new PinnedReference("symlink-link", 1, new string('0', 64))), new StoredModel(link, model.Scene))));
                    Assert(library.ListVersions().Count == before, "OS-linked candidate became registered.");
                });
                string lockAlias = Path.Combine(inside, "target.json.lock");
                try { CreateProbeLink(lockAlias, source, false); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is PlatformNotSupportedException)
                { report.blocked++; report.checks.Add(new StorageProbeCheck { id = "STORE-FILE-REPARSE", status = "Blocked", detail = "Actual OS file symbolic link could not be created: " + error.Message }); return; }
                try
                {
                    Check(report, "STORE-SIDECAR-REPARSE", () => { ExpectInvalidPath(() => files.Save(Path.Combine(inside, "target.json"), bytes)); Assert(File.ReadAllBytes(source).SequenceEqual(bytes), "Sidecar followed a reparse point."); });
                }
                finally { DeleteProbeLink(root, lockAlias, false); }
                Check(report, "LIB-PUBLISHED-REPARSE", () =>
                {
                    var version = library.ListVersions().Single(v => v.Reference.Id == "copy-original"); string dependency = Path.Combine(version.Directory, "sources", "original.bin"); byte[] original = files.Read(dependency).Bytes;
                    File.Delete(dependency);
                    try { CreateProbeLink(dependency, source, false); ExpectInvalidPath(() => library.ReadPinned(version.Reference)); }
                    finally { if ((File.GetAttributes(dependency) & FileAttributes.ReparsePoint) != 0) DeleteProbeLink(root, dependency, false); File.WriteAllBytes(dependency, original); }
                    Assert(library.ReadPinned(version.Reference).Reference.Equals(version.Reference), "Original immutable fixture was not restored after the negative test.");
                });
                Check(report, "STORE-DANGLING-REPARSE", () =>
                {
                    string dangling = Path.Combine(inside, "dangling"); CreateProbeLink(dangling, Path.Combine(outside, "does-not-exist"), false);
                    try { ExpectInvalidPath(() => StoragePaths.Absolute(dangling)); } finally { DeleteProbeLink(root, dangling, false); }
                });
            }
            finally { DeleteProbeLink(root, alias, true); }
        }
        // Unity's compile-time BCL profile omits the modern managed link-creation API.
        // The Windows probe still creates actual OS links; production IO never follows them.
        [DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool CreateSymbolicLink(string link, string target, uint flags);
        private static void CreateProbeLink(string link, string target, bool directory)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("Windows link probe.");
            if (!CreateSymbolicLink(link, target, directory ? 3u : 2u)) throw new IOException("CreateSymbolicLinkW failed: " + Marshal.GetLastWin32Error());
        }
        private static void DeleteProbeLink(string root, string path, bool directory)
        {
            string absolute = Path.GetFullPath(path), prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) == 0) throw new InvalidOperationException("Refusing to remove a path that is not an owned probe link.");
            if (directory) Directory.Delete(absolute, false); else File.Delete(absolute);
        }
        private static void ExpectInvalidPath(Action action)
        { bool failed = false; try { action(); } catch (StorageException e) when (e.Code == "InvalidPath") { failed = true; } Assert(failed, "An actual OS reparse point must be rejected before IO with InvalidPath."); }
        private static void FailurePreserves(string root, ProjectBundle bundle, byte[] old, StorageFaultPoint at, Exception error)
        {
            var plain = new AtomicFileStore(); string path = Path.Combine(root, "failure-" + error.GetType().Name + ".json"); plain.Save(path, old);
            var store = new ProjectStore(new AtomicFileStore(fault: (p, _) => { if (p == at) throw error; }));
            ExpectFailure(() => store.Save(path, bundle, AtomicFileStore.Hash(old))); Assert(plain.Read(path).Bytes.SequenceEqual(old), "Failed save changed previous bytes.");
        }
        private static StorageProbeReport New(string phase, string root) => new StorageProbeReport { phase = phase, root = StoragePaths.Absolute(root), utc = DateTime.UtcNow.ToString("O") };
        private static void Counts(StorageProbeReport report, ProjectBundle bundle) { report.placements = bundle.Document.Placements.Count; report.models = bundle.Models.Count; report.vertices = bundle.Models.Sum(m => m.Scene.Meshes.Sum(g => g.Positions.Count)); report.textures = bundle.Models.Sum(m => m.Scene.Textures.Count); }
        private static void Check(StorageProbeReport r, string id, Action action) { try { action(); r.passed++; r.checks.Add(new StorageProbeCheck { id = id, status = "Passed", detail = "Observed in this process." }); } catch (Exception e) { r.failed++; r.checks.Add(new StorageProbeCheck { id = id, status = "Failed", detail = e.ToString() }); } }
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void ExpectFailure(Action action) { bool failed = false; try { action(); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is DocumentValidationException || e is System.Text.Json.JsonException || e is ArgumentException) { failed = true; } Assert(failed, "Expected operation to fail."); }
        private static void ExpectCancel(Action action) { bool cancelled = false; try { action(); } catch (OperationCanceledException) { cancelled = true; } Assert(cancelled, "Expected cancellation."); }
    }
}
