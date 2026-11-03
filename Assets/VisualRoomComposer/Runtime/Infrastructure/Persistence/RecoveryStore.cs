using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using VisualRoomComposer.Core.Documents;

namespace VisualRoomComposer.Infrastructure.Persistence
{
    public sealed class RecoveryCandidate
    {
        public string SessionId { get; } public bool Available { get; } public string Error { get; }
        public string CurrentVersion { get; } public string PreviousVersion { get; }
        internal RecoveryCandidate(string id, bool available, string error, string current = null, string previous = null)
        { SessionId = id; Available = available; Error = error; CurrentVersion = current; PreviousVersion = previous; }
    }
    public sealed class RecoveredCopy
    {
        public ProjectBundle Bundle { get; } public string OriginalPath { get; }
        public bool RequiresSave => true;
        internal RecoveredCopy(ProjectBundle bundle, string path) { Bundle = bundle; OriginalPath = path; }
    }

    /// <summary>Current and previous complete snapshots. This store never changes a document state ID, history, or save point during capture.</summary>
    public sealed class RecoveryStore
    {
        private sealed class Version { internal string Id, Hash, SourcePath, CapturedUtc; }
        private sealed class Manifest { internal Version Current, Previous; }
        private readonly string root; private readonly AtomicFileStore files; private readonly ProjectStore projects;
        public RecoveryStore(string recoveryRoot, AtomicFileStore files = null)
        { root = StoragePaths.Absolute(recoveryRoot); Directory.CreateDirectory(root); this.files = files ?? new AtomicFileStore(StorageJson.MaxBytes); projects = new ProjectStore(this.files); }
        public StorageReceipt Capture(string sessionId, ProjectBundle bundle, string originalPath = null, CancellationToken cancellation = default)
        {
            string owner = Owner(sessionId); Directory.CreateDirectory(owner);
            using (var guard = new FileStream(StoragePaths.Absolute(Path.Combine(owner, "capture.lock")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                string manifestPath = Path.Combine(owner, "manifest.json"); Manifest prior = null; string expectedHash = null;
                if (File.Exists(manifestPath)) { var old = files.Read(manifestPath, cancellation); prior = Decode(old.Bytes); expectedHash = old.ContentHash; }
                string id = Guid.NewGuid().ToString("N"), directory = StoragePaths.Under(owner, id); Directory.CreateDirectory(directory); bool committed = false;
                try
                {
                    var snapshot = projects.Save(Path.Combine(directory, "project.json"), bundle, cancellation: cancellation);
                    var next = new Manifest { Current = new Version { Id = id, Hash = snapshot.ContentHash, SourcePath = originalPath, CapturedUtc = DateTime.UtcNow.ToString("O") }, Previous = prior?.Current };
                    var receipt = files.Save(manifestPath, Encode(next), expectedHash, b => Decode(b), cancellation); committed = true;
                    // Cleanup is best effort after commit. Unknown files in a version directory are preserved.
                    if (prior?.Previous != null) RemoveKnown(owner, prior.Previous.Id);
                    return receipt;
                }
                finally { if (!committed) RemoveKnown(owner, id); }
            }
        }
        public IReadOnlyList<RecoveryCandidate> List(CancellationToken cancellation = default)
        {
            var rows = new List<RecoveryCandidate>();
            foreach (string directory in Directory.EnumerateDirectories(StoragePaths.Absolute(root)))
            {
                cancellation.ThrowIfCancellationRequested(); string id = Path.GetFileName(directory);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                try
                {
                    string path = StoragePaths.Under(directory, "manifest.json"); if (!File.Exists(path)) continue;
                    var manifest = Decode(files.Read(path, cancellation).Bytes);
                    ReadSnapshot(directory, manifest.Current, cancellation);
                    if (manifest.Previous != null) ReadSnapshot(directory, manifest.Previous, cancellation);
                    rows.Add(new RecoveryCandidate(id, true, null, manifest.Current.Id, manifest.Previous?.Id));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { rows.Add(new RecoveryCandidate(id, false, e.Message)); }
                if (rows.Count > 1024) throw new StorageException("LimitExceeded", "Recovery session count exceeded.");
            }
            return rows;
        }
        public RecoveredCopy LoadCopy(string sessionId, bool previous = false, CancellationToken cancellation = default)
        {
            string owner = Owner(sessionId); var manifest = Decode(files.Read(Path.Combine(owner, "manifest.json"), cancellation).Bytes);
            var version = previous ? manifest.Previous : manifest.Current;
            if (version == null) throw new StorageException("MissingRecovery", "No previous recovery snapshot exists.");
            var bundle = ReadSnapshot(owner, version, cancellation); var d = bundle.Document;
            var copy = new RoomDocument(Guid.NewGuid().ToString("N"), d.Title, d.Rooms, d.Groups, d.Placements, d.PinnedDependencies, 0, Guid.NewGuid().ToString("N"), d.Description, d.ShowGrid);
            LibraryDraft draft = bundle.Draft == null ? null : new LibraryDraft(Guid.NewGuid().ToString("N"), bundle.Draft.RepositoryId, bundle.Draft.Definitions, bundle.Draft.Assets, bundle.Draft.Name, bundle.Draft.BaseDefinitionRef, 0, Guid.NewGuid().ToString("N"));
            return new RecoveredCopy(new ProjectBundle(copy, bundle.Definitions, bundle.Models, draft), version.SourcePath);
        }
        private ProjectBundle ReadSnapshot(string owner, Version version, CancellationToken cancellation)
        {
            var content = files.Read(StoragePaths.Under(owner, version.Id + "/project.json"), cancellation);
            if (content.ContentHash != version.Hash) throw new StorageException("HashMismatch", "Recovery snapshot differs from its committed manifest.");
            return ProjectBundleCodec.Decode(content.Bytes, cancellation);
        }
        private string Owner(string id)
        { if (!Guid.TryParseExact(id, "N", out _)) throw new StorageException("InvalidPath", "Recovery session requires a UUID."); return StoragePaths.Under(root, id); }
        private static void RemoveKnown(string owner, string version)
        {
            try
            {
                if (!Guid.TryParseExact(version, "N", out _)) return;
                string directory = StoragePaths.Under(owner, version);
                foreach (string name in new[] { "project.json", "project.json.lock" }) { string file = StoragePaths.Under(directory, name); if (File.Exists(file)) File.Delete(file); }
                if (Directory.Exists(directory)) Directory.Delete(directory, false);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        private static byte[] Encode(Manifest m) => StorageJson.Encode(w =>
        {
            w.WriteStartObject(); w.WriteString("format", "visual-room-composer.recovery"); w.WriteNumber("schemaVersion", 1);
            w.WritePropertyName("current"); WriteVersion(w, m.Current); w.WritePropertyName("previous"); if (m.Previous == null) w.WriteNullValue(); else WriteVersion(w, m.Previous); w.WriteEndObject();
        });
        private static void WriteVersion(Utf8JsonWriter w, Version v)
        { w.WriteStartObject(); w.WriteString("id", v.Id); w.WriteString("hash", v.Hash); w.WriteString("sourcePath", v.SourcePath); w.WriteString("capturedUtc", v.CapturedUtc); w.WriteEndObject(); }
        private static Manifest Decode(byte[] bytes)
        {
            using (var json = StorageJson.Parse(bytes))
            {
                var e = json.RootElement; StorageJson.Shape(e, "format", "schemaVersion", "current", "previous");
                if (StorageJson.S(e, "format") != "visual-room-composer.recovery" || StorageJson.I(e, "schemaVersion") != 1) throw new StorageException("InvalidSchema", "Unsupported recovery manifest.");
                var result = new Manifest { Current = ReadVersion(e.GetProperty("current")), Previous = e.GetProperty("previous").ValueKind == JsonValueKind.Null ? null : ReadVersion(e.GetProperty("previous")) };
                if (result.Previous != null && result.Current.Id == result.Previous.Id) throw new StorageException("InvalidSchema", "Recovery generations must be distinct."); return result;
            }
        }
        private static Version ReadVersion(JsonElement e)
        {
            StorageJson.Shape(e, "id", "hash", "sourcePath", "capturedUtc"); var v = new Version { Id = StorageJson.S(e, "id"), Hash = StorageJson.S(e, "hash"), SourcePath = StorageJson.S(e, "sourcePath"), CapturedUtc = StorageJson.S(e, "capturedUtc") };
            if (!Guid.TryParseExact(v.Id, "N", out _) || !AtomicFileStore.IsHash(v.Hash) || !DateTime.TryParse(v.CapturedUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _)) throw new StorageException("InvalidSchema", "Malformed recovery identity/hash/time."); return v;
        }
    }
}
