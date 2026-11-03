using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using VisualRoomComposer.Infrastructure.Persistence;

namespace VisualRoomComposer.Infrastructure.Libraries
{
    public sealed class RepositoryRegistration
    {
        public string Id { get; } public string Root { get; }
        internal RepositoryRegistration(string id, string root) { Id = id; Root = root; }
    }
    /// <summary>Registration changes locations only. Removing a registration never removes a repository or its immutable contents.</summary>
    public sealed class RepositoryCatalog
    {
        private readonly string path, lockPath, metadataDirectory; private readonly AtomicFileStore files; private readonly object gate = new object();
        private List<RepositoryRegistration> registrations; private string publishing, expectedHash;
        public IReadOnlyList<RepositoryRegistration> Repositories { get { lock (gate) return registrations.ToArray(); } }
        public string PublishingId { get { lock (gate) return publishing; } }
        public RepositoryCatalog(string metadataRoot, string localRoot, AtomicFileStore files = null)
        {
            this.files = files ?? new AtomicFileStore(StorageJson.MaxBytes); metadataRoot = StoragePaths.Absolute(metadataRoot); localRoot = StoragePaths.Absolute(localRoot);
            metadataDirectory = metadataRoot;
            Directory.CreateDirectory(metadataRoot); Directory.CreateDirectory(localRoot);
            path = StoragePaths.Under(metadataRoot, "repositories.json"); lockPath = StoragePaths.Under(metadataRoot, "library-mutation.lock");
            if (File.Exists(path))
            {
                var content = this.files.Read(path); Decode(content.Bytes, out registrations, out publishing); expectedHash = content.ContentHash;
                if (registrations.Single(r => r.Id == "local").Root != localRoot) throw new StorageException("RepositoryConflict", "The default repository root changed without explicit migration.");
            }
            else { registrations = new List<RepositoryRegistration> { new RepositoryRegistration("local", localRoot) }; publishing = "local"; }
            Validate(registrations, publishing);
        }
        internal IDisposable AcquireMutationLock() => new FileStream(StoragePaths.Absolute(lockPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        public string Register(string root, CancellationToken cancellation = default)
        {
            root = StoragePaths.Absolute(root); if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            string id = Guid.NewGuid().ToString("N");
            Change(rows => rows.Add(new RepositoryRegistration(id, root)), null, cancellation); return id;
        }
        public void Remove(string id, CancellationToken cancellation = default)
        {
            if (id == "local") throw new StorageException("RepositoryConflict", "The default repository cannot be unregistered.");
            Change(rows => { if (rows.RemoveAll(r => r.Id == id) != 1) throw new StorageException("MissingRepository", "Repository is not registered."); }, PublishingId == id ? "local" : null, cancellation, false);
        }
        public void SetPublishing(string id, CancellationToken cancellation = default) => Change(rows => { }, id, cancellation);
        private void Change(Action<List<RepositoryRegistration>> mutation, string nextPublishing, CancellationToken cancellation, bool scan = true)
        {
            lock (gate) using (AcquireMutationLock())
            {
                var candidate = registrations.ToList(); mutation(candidate); string chosen = nextPublishing ?? publishing; Validate(candidate, chosen);
                if (scan) ImmutableLibrary.Scan(candidate, files, new LinkRelocations(metadataDirectory, files), cancellation);
                var receipt = files.Save(path, Encode(candidate, chosen), expectedHash, b => Decode(b, out _, out _), cancellation);
                registrations = candidate; publishing = chosen; expectedHash = receipt.ContentHash;
            }
        }
        private static byte[] Encode(List<RepositoryRegistration> rows, string publishing) => StorageJson.Encode(new
        { format = "visual-room-composer.repositories", schemaVersion = 1, publishingId = publishing, repositories = rows.Select(r => new { r.Id, r.Root }) });
        private static void Decode(byte[] bytes, out List<RepositoryRegistration> rows, out string publishing)
        {
            using (var json = StorageJson.Parse(bytes))
            {
                var e = json.RootElement; StorageJson.Shape(e, "format", "schemaVersion", "publishingId", "repositories");
                if (StorageJson.S(e, "format") != "visual-room-composer.repositories" || StorageJson.I(e, "schemaVersion") != 1) throw new StorageException("InvalidSchema", "Unsupported repository catalog.");
                publishing = StorageJson.S(e, "publishingId"); rows = new List<RepositoryRegistration>();
                foreach (var r in StorageJson.A(e, "repositories", 32)) { StorageJson.Shape(r, "id", "root"); rows.Add(new RepositoryRegistration(StorageJson.S(r, "id"), StoragePaths.Absolute(StorageJson.S(r, "root")))); }
                Validate(rows, publishing);
            }
        }
        private static void Validate(List<RepositoryRegistration> rows, string publishing)
        {
            if (rows.Count == 0 || rows.Count > 32 || rows.Count(r => r.Id == "local") != 1 || rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count || !rows.Any(r => r.Id == publishing)) throw new StorageException("InvalidSchema", "Repository identities or publishing selection are invalid.");
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Id != "local" && !Guid.TryParseExact(rows[i].Id, "N", out _)) throw new StorageException("InvalidSchema", "Repository ID is invalid.");
                if (!Path.IsPathFullyQualified(rows[i].Root)) throw new StorageException("InvalidPath", "Repository path must be absolute.");
                for (int j = 0; j < i; j++)
                {
                    string a = rows[i].Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, b = rows[j].Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase)) throw new StorageException("RepositoryConflict", "Repository roots cannot overlap.");
                }
            }
        }
    }

    /// <summary>Explicit hash-checked local path overrides do not mutate immutable manifests or their fixed identities.</summary>
    public sealed class LinkRelocations
    {
        private sealed class Entry { internal string Original, Hash, Destination; }
        private readonly string path; private readonly AtomicFileStore files; private readonly object gate = new object();
        private List<Entry> entries = new List<Entry>(); private string expectedHash;
        public LinkRelocations(string metadataRoot, AtomicFileStore files = null)
        {
            this.files = files ?? new AtomicFileStore(StorageJson.MaxBytes); Directory.CreateDirectory(StoragePaths.Absolute(metadataRoot)); path = StoragePaths.Under(metadataRoot, "relocations.json");
            if (File.Exists(path)) { var content = this.files.Read(path); entries = Decode(content.Bytes); expectedHash = content.ContentHash; }
        }
        public string Resolve(string original, string hash)
        { lock (gate) return entries.FirstOrDefault(e => e.Original == original && e.Hash == hash)?.Destination ?? original; }
        public void Relocate(string original, string expectedContentHash, string destination, CancellationToken cancellation = default)
        {
            original = StoragePaths.Absolute(original); destination = StoragePaths.Absolute(destination);
            if (!AtomicFileStore.IsHash(expectedContentHash) || files.Read(destination, cancellation).ContentHash != expectedContentHash) throw new StorageException("HashMismatch", "Relocation target does not match the pinned content.");
            lock (gate)
            {
                var candidate = entries.Where(e => e.Original != original || e.Hash != expectedContentHash).ToList(); candidate.Add(new Entry { Original = original, Hash = expectedContentHash, Destination = destination });
                byte[] bytes = StorageJson.Encode(new { format = "visual-room-composer.relocations", schemaVersion = 1, entries = candidate.Select(e => new { original = e.Original, hash = e.Hash, destination = e.Destination }) });
                var receipt = files.Save(path, bytes, expectedHash, b => Decode(b), cancellation); entries = candidate; expectedHash = receipt.ContentHash;
            }
        }
        private static List<Entry> Decode(byte[] bytes)
        {
            using (var document = StorageJson.Parse(bytes))
            {
                var e = document.RootElement; StorageJson.Shape(e, "format", "schemaVersion", "entries");
                if (StorageJson.S(e, "format") != "visual-room-composer.relocations" || StorageJson.I(e, "schemaVersion") != 1) throw new StorageException("InvalidSchema", "Unsupported relocation catalog.");
                var rows = new List<Entry>(); var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in StorageJson.A(e, "entries", 4096))
                {
                    StorageJson.Shape(r, "original", "hash", "destination"); var row = new Entry { Original = StoragePaths.Absolute(StorageJson.S(r, "original")), Hash = StorageJson.S(r, "hash"), Destination = StoragePaths.Absolute(StorageJson.S(r, "destination")) };
                    if (!AtomicFileStore.IsHash(row.Hash) || !keys.Add(row.Original + "\n" + row.Hash)) throw new StorageException("InvalidSchema", "Invalid or duplicate relocation."); rows.Add(row);
                }
                return rows;
            }
        }
    }
}
