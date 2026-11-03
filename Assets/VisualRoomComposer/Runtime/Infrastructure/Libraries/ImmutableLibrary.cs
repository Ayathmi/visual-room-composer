using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using VisualRoomComposer.Core.Documents;
using VisualRoomComposer.Infrastructure.Persistence;

namespace VisualRoomComposer.Infrastructure.Libraries
{
    public sealed class LibraryCandidate
    {
        public LibraryDefinition Definition { get; } public StoredModel Model { get; }
        public IReadOnlyDictionary<string, string> CopySources { get; }
        public LibraryCandidate(LibraryDefinition definition, StoredModel model = null, IReadOnlyDictionary<string, string> copySources = null)
        { Definition = definition ?? throw new ArgumentNullException(nameof(definition)); Model = model; CopySources = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(copySources ?? new Dictionary<string, string>(), StringComparer.Ordinal)); }
    }
    public sealed class LibraryVersion
    {
        public string RepositoryId { get; } public string Directory { get; } public LibraryDefinition Definition { get; }
        public StoredModel Model { get; } public PinnedReference Reference => Definition.Reference;
        internal LibraryVersion(string repositoryId, string directory, LibraryDefinition definition, StoredModel model)
        { RepositoryId = repositoryId; Directory = directory; Definition = definition; Model = model; }
    }

    /// <summary>Complete candidates become discoverable only after one same-volume directory rename.</summary>
    public sealed class ImmutableLibrary
    {
        private readonly RepositoryCatalog catalog; private readonly AtomicFileStore files; private readonly LinkRelocations relocations;
        public ImmutableLibrary(RepositoryCatalog catalog, LinkRelocations relocations = null, AtomicFileStore files = null)
        { this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); this.relocations = relocations; this.files = files ?? new AtomicFileStore(StorageJson.MaxBytes); }
        public LibraryVersion Publish(LibraryCandidate candidate, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            var definition = FixedContent.SealDefinition(candidate.Definition, candidate.Model?.Asset);
            if (candidate.Model == null ? definition.ModelReference != null : !candidate.Model.Asset.Reference.Equals(definition.ModelReference)) throw new StorageException("MissingDependency", "Candidate model does not match the definition.");
            var bundle = new ProjectBundle(new RoomDocument("library-version", stateId: "library-version"), new[] { definition }, candidate.Model == null ? Array.Empty<StoredModel>() : new[] { candidate.Model });
            byte[] bytes = ProjectBundleCodec.Encode(bundle, cancellation);
            using (catalog.AcquireMutationLock())
            {
                foreach (var version in ListVersions(cancellation)) if (version.Reference.Id == definition.Id && version.Reference.Revision == definition.Reference.Revision) throw new StorageException("VersionConflict", "This immutable identity/revision is already registered.");
                var repository = catalog.Repositories.Single(r => r.Id == catalog.PublishingId);
                string parent = StoragePaths.Under(repository.Root, StoragePaths.Segment(definition.Id)); Directory.CreateDirectory(parent);
                string destination = StoragePaths.Under(parent, definition.Reference.Revision.ToString(CultureInfo.InvariantCulture));
                string staging = StoragePaths.Under(parent, ".staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
                bool committed = false;
                try
                {
                    if (candidate.Model != null)
                    {
                        var asset = candidate.Model.Asset; var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var dependency in asset.Dependencies)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (asset.Mode == AssetStorageMode.Link) { VerifyLink(dependency, files, relocations, cancellation); continue; }
                            string target = StoragePaths.Under(staging, dependency.Uri);
                            if (!expectedPaths.Add(target) || string.Equals(dependency.Uri, "version.json", StringComparison.OrdinalIgnoreCase) || dependency.Uri.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) throw new StorageException("InvalidPath", "Copied dependencies collide with a reserved path.");
                            if (!candidate.CopySources.TryGetValue(dependency.Uri, out var source)) throw new StorageException("MissingDependency", "Copy source is missing: " + dependency.Uri);
                            var content = files.Read(source, cancellation);
                            if (content.ContentHash != dependency.ContentHash) throw new StorageException("HashMismatch", "Source changed before publication: " + source);
                            files.Save(target, content.Bytes, cancellation: cancellation);
                        }
                        if (asset.Mode == AssetStorageMode.Copy && candidate.CopySources.Count != asset.Dependencies.Count) throw new StorageException("InvalidSchema", "Candidate contains unregistered source files.");
                    }
                    files.Save(Path.Combine(staging, "version.json"), bytes, validate: b => ProjectBundleCodec.Decode(b, cancellation), cancellation: cancellation);
                    ReadVersion(repository.Id, staging, files, relocations, cancellation);
                    StoragePaths.PublishDirectory(repository.Root, staging, destination, cancellation); committed = true;
                    return new LibraryVersion(repository.Id, destination, definition, candidate.Model);
                }
                finally { if (!committed && Directory.Exists(staging)) StoragePaths.DeleteOwnedTree(parent, staging); }
            }
        }
        public IReadOnlyList<LibraryVersion> ListVersions(CancellationToken cancellation = default)
        { return Scan(catalog.Repositories, files, relocations, cancellation); }
        public LibraryVersion ReadPinned(PinnedReference reference, CancellationToken cancellation = default)
        {
            DocumentValidation.Pin(reference);
            LibraryVersion found = null;
            foreach (var repository in catalog.Repositories)
            {
                string directory = StoragePaths.Under(repository.Root, StoragePaths.Segment(reference.Id) + "/" + reference.Revision.ToString(CultureInfo.InvariantCulture));
                if (!Directory.Exists(directory)) continue;
                var version = ReadVersion(repository.Id, directory, files, relocations, cancellation);
                if (!version.Reference.Equals(reference)) throw new StorageException("VersionConflict", "A registered repository contains a conflicting pinned version.");
                found = found ?? version;
            }
            if (found == null) throw new StorageException("MissingDependency", "The exact library version is unavailable; no latest-version substitution was made."); return found;
        }
        internal static IReadOnlyList<LibraryVersion> Scan(IEnumerable<RepositoryRegistration> repositories, AtomicFileStore files, LinkRelocations relocations, CancellationToken cancellation)
        {
            var versions = new Dictionary<string, LibraryVersion>(StringComparer.Ordinal); int count = 0;
            foreach (var repository in repositories)
            {
                string root = StoragePaths.Absolute(repository.Root); if (!Directory.Exists(root)) throw new StorageException("MissingRepository", "Registered repository is unavailable: " + root);
                foreach (string identity in Directory.EnumerateDirectories(root))
                {
                    cancellation.ThrowIfCancellationRequested(); if (Path.GetFileName(identity).StartsWith(".", StringComparison.Ordinal)) continue; StoragePaths.Absolute(identity);
                    foreach (string versionPath in Directory.EnumerateDirectories(identity))
                    {
                        cancellation.ThrowIfCancellationRequested(); if (Path.GetFileName(versionPath).StartsWith(".", StringComparison.Ordinal)) continue;
                        if (!long.TryParse(Path.GetFileName(versionPath), NumberStyles.None, CultureInfo.InvariantCulture, out long revision) || revision < 1) continue;
                        if (++count > 4096) throw new StorageException("LimitExceeded", "Repository version count exceeded.");
                        var version = ReadVersion(repository.Id, versionPath, files, relocations, cancellation);
                        if (version.Reference.Id != Path.GetFileName(identity) || version.Reference.Revision != revision) throw new StorageException("InvalidSchema", "Repository path and immutable identity differ.");
                        string key = version.Reference.Id + "/" + version.Reference.Revision;
                        if (versions.TryGetValue(key, out var previous))
                        { if (!previous.Reference.Equals(version.Reference)) throw new StorageException("VersionConflict", "Repositories contain different hashes for one identity/revision."); }
                        else versions.Add(key, version);
                    }
                }
            }
            return versions.Values.OrderBy(v => v.Reference.Id, StringComparer.Ordinal).ThenBy(v => v.Reference.Revision).ToArray();
        }
        internal static LibraryVersion ReadVersion(string repository, string directory, AtomicFileStore files, LinkRelocations relocations, CancellationToken cancellation)
        {
            var bytes = files.Read(StoragePaths.Under(directory, "version.json"), cancellation); var bundle = ProjectBundleCodec.Decode(bytes.Bytes, cancellation);
            if (bundle.Definitions.Count != 1 || bundle.Models.Count > 1 || bundle.Document.Placements.Count != 0 || bundle.Draft != null) throw new StorageException("InvalidSchema", "Invalid library version envelope.");
            var model = bundle.Models.SingleOrDefault();
            if (bundle.Definitions[0].ModelReference == null ? model != null : model == null || !bundle.Definitions[0].ModelReference.Equals(model.Asset.Reference)) throw new StorageException("InvalidSchema", "Library version contains an unrelated CPU model.");
            if (model != null) foreach (var dependency in model.Asset.Dependencies)
            {
                if (model.Asset.Mode == AssetStorageMode.Link) VerifyLink(dependency, files, relocations, cancellation);
                else if (files.Read(StoragePaths.Under(directory, dependency.Uri), cancellation).ContentHash != dependency.ContentHash) throw new StorageException("HashMismatch", "Copied dependency changed.");
            }
            return new LibraryVersion(repository, directory, bundle.Definitions[0], model);
        }
        internal static void VerifyLink(AssetDependency dependency, AtomicFileStore files, LinkRelocations relocations, CancellationToken cancellation)
        {
            if (!Path.IsPathFullyQualified(dependency.Uri)) throw new StorageException("InvalidPath", "A linked dependency must use an explicit absolute local path.");
            string path = relocations?.Resolve(dependency.Uri, dependency.ContentHash) ?? dependency.Uri;
            if (files.Read(path, cancellation).ContentHash != dependency.ContentHash) throw new StorageException("HashMismatch", "Linked dependency changed; explicit reimport is required.");
        }
    }
}
