using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using VisualRoomComposer.Core;
using VisualRoomComposer.Core.Documents;

namespace VisualRoomComposer.Infrastructure.Persistence
{
    public sealed class StoredModel
    {
        public AssetReference Asset { get; } public ModelScene Scene { get; }
        public StoredModel(AssetReference asset, ModelScene scene) { Asset = asset ?? throw new ArgumentNullException(nameof(asset)); Scene = scene ?? throw new ArgumentNullException(nameof(scene)); }
    }
    public sealed class ProjectBundle
    {
        public RoomDocument Document { get; } public IReadOnlyList<LibraryDefinition> Definitions { get; }
        public IReadOnlyList<StoredModel> Models { get; } public LibraryDraft Draft { get; }
        public ProjectBundle(RoomDocument document, IEnumerable<LibraryDefinition> definitions, IEnumerable<StoredModel> models, LibraryDraft draft = null)
        { Document = document ?? throw new ArgumentNullException(nameof(document)); Definitions = Array.AsReadOnly(definitions.ToArray()); Models = Array.AsReadOnly(models.ToArray()); Draft = draft; }
    }
    public sealed class LoadedProject
    {
        public ProjectBundle Bundle { get; } public string ContentHash { get; }
        internal LoadedProject(ProjectBundle bundle, string hash) { Bundle = bundle; ContentHash = hash; }
    }

    public static class FixedContent
    {
        public static LibraryDefinition SealDefinition(LibraryDefinition definition, AssetReference asset = null)
        { return definition.WithReference(new PinnedReference(definition.Reference.Id, definition.Reference.Revision, DefinitionHash(definition, asset))); }
        public static string DefinitionHash(LibraryDefinition definition, AssetReference asset = null)
        {
            var zero = definition.WithReference(new PinnedReference(definition.Reference.Id, definition.Reference.Revision, new string('0', 64)));
            return AtomicFileStore.Hash(StorageJson.Encode(writer =>
            {
                writer.WriteStartObject(); writer.WritePropertyName("definition"); StorageJson.Embed(writer, DocumentCodec.EncodeDefinition(zero));
                writer.WritePropertyName("asset"); if (asset == null) writer.WriteNullValue(); else WriteAsset(writer, asset); writer.WriteEndObject();
            }));
        }
        internal static void WritePin(Utf8JsonWriter w, PinnedReference pin)
        { w.WriteStartObject(); w.WriteString("id", pin.Id); w.WriteNumber("revision", pin.Revision); w.WriteString("contentHash", pin.ContentHash); w.WriteEndObject(); }
        internal static PinnedReference ReadPin(JsonElement e)
        { StorageJson.Shape(e, "id", "revision", "contentHash"); var p = new PinnedReference(StorageJson.S(e, "id"), StorageJson.L(e, "revision"), StorageJson.S(e, "contentHash")); DocumentValidation.Pin(p); return p; }
        internal static void WriteAsset(Utf8JsonWriter w, AssetReference a)
        {
            w.WriteStartObject(); w.WritePropertyName("reference"); WritePin(w, a.Reference); w.WriteNumber("mode", (int)a.Mode); w.WriteString("uri", a.Uri);
            w.WriteString("sourceProvenance", a.SourceProvenance); w.WriteString("importOptionsJson", a.ImportOptionsJson); w.WriteString("importerVersion", a.ImporterVersion); w.WriteNumber("cacheSchemaVersion", a.CacheSchemaVersion);
            w.WriteStartArray("dependencies"); foreach (var d in a.Dependencies) { w.WriteStartObject(); w.WriteString("uri", d.Uri); w.WriteString("contentHash", d.ContentHash); w.WriteString("sourceReference", d.SourceReference); w.WriteEndObject(); } w.WriteEndArray(); w.WriteEndObject();
        }
        internal static AssetReference ReadAsset(JsonElement e)
        {
            StorageJson.Shape(e, "reference", "mode", "uri", "sourceProvenance", "importOptionsJson", "importerVersion", "cacheSchemaVersion", "dependencies");
            var dependencies = new List<AssetDependency>();
            foreach (var d in StorageJson.A(e, "dependencies", 4096)) { StorageJson.Shape(d, "uri", "contentHash", "sourceReference"); dependencies.Add(new AssetDependency(StorageJson.S(d, "uri"), StorageJson.S(d, "contentHash"), StorageJson.S(d, "sourceReference"))); }
            return new AssetReference(ReadPin(e.GetProperty("reference")), StorageJson.EnumValue<AssetStorageMode>(e, "mode"), StorageJson.S(e, "uri"), StorageJson.S(e, "sourceProvenance"), StorageJson.S(e, "importOptionsJson"), StorageJson.S(e, "importerVersion"), StorageJson.I(e, "cacheSchemaVersion"), dependencies);
        }
        public static void Validate(ProjectBundle bundle, CancellationToken cancellation = default)
        {
            var models = new Dictionary<string, StoredModel>(StringComparer.Ordinal); var definitions = new Dictionary<string, LibraryDefinition>(StringComparer.Ordinal);
            var modelVersions = new Dictionary<string, string>(StringComparer.Ordinal); var definitionVersions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var model in bundle.Models)
            {
                cancellation.ThrowIfCancellationRequested(); DocumentValidation.Pin(model.Asset.Reference);
                CheckVersion(modelVersions, model.Asset.Reference);
                if (!models.TryAdd(model.Asset.Reference.Key, model)) throw new StorageException("DuplicateId", "Duplicate model identity.");
                if (AtomicFileStore.Hash(ModelSceneCodec.Encode(model.Scene, cancellation)) != model.Asset.Reference.ContentHash) throw new StorageException("HashMismatch", "CPU model differs from its fixed identity.");
                DocumentValidation.Validate(new LibraryDraft("asset-validation", "local", assets: new[] { model.Asset }));
                var asset = model.Asset;
                if (asset.Mode == AssetStorageMode.Link && (!System.IO.Path.IsPathFullyQualified(asset.Uri) || !asset.Dependencies.Any(d => d.Uri == asset.Uri)))
                    throw new StorageException("InvalidSchema", "Linked assets require a fixed hash for the original source URI.");
                if (asset.Mode == AssetStorageMode.Copy && asset.Dependencies.Count != 0 && !asset.Dependencies.Any(d => d.Uri == asset.Uri))
                    throw new StorageException("InvalidSchema", "Copied source URI is missing from its complete dependency manifest.");
                if (asset.Mode == AssetStorageMode.Copy && asset.Dependencies.Count == 0 && asset.Uri != "model.json")
                    throw new StorageException("InvalidSchema", "Procedural CPU-only assets use the explicit model.json URI.");
                using (StorageJson.Parse(System.Text.Encoding.UTF8.GetBytes(asset.ImportOptionsJson))) { }
            }
            foreach (var definition in bundle.Definitions)
            {
                cancellation.ThrowIfCancellationRequested(); DocumentValidation.ValidateDefinition(definition);
                CheckVersion(definitionVersions, definition.Reference);
                if (!definitions.TryAdd(definition.Reference.Key, definition)) throw new StorageException("DuplicateId", "Duplicate definition identity.");
                AssetReference asset = null;
                if (definition.ModelReference != null)
                { if (!models.TryGetValue(definition.ModelReference.Key, out var model)) throw new StorageException("MissingDependency", "Pinned CPU model is missing."); asset = model.Asset; }
                if (definition.Reference.ContentHash != DefinitionHash(definition, asset)) throw new StorageException("HashMismatch", "Definition metadata differs from its fixed identity.");
            }
            foreach (var pin in bundle.Document.PinnedDependencies) if (!definitions.ContainsKey(pin.Key)) throw new StorageException("MissingDependency", "Project fixed definition is missing.");
            DocumentValidation.Validate(bundle.Document, pin => definitions.TryGetValue(pin.Key, out var d) ? d : null, cancellation);
            if (bundle.Draft != null)
            {
                DocumentValidation.Validate(bundle.Draft, cancellation);
                foreach (var asset in bundle.Draft.Assets) if (!models.ContainsKey(asset.Reference.Key)) throw new StorageException("MissingDependency", "Library draft CPU model is missing.");
            }
        }
        private static void CheckVersion(Dictionary<string, string> versions, PinnedReference pin)
        { string key = pin.Id + "/" + pin.Revision; if (versions.TryGetValue(key, out string hash) && hash != pin.ContentHash) throw new StorageException("VersionConflict", "A bundle contains different hashes for one immutable identity/revision."); versions[key] = pin.ContentHash; }
    }

    public static class ProjectBundleCodec
    {
        public const string Format = "visual-room-composer.project-bundle";
        public static byte[] Encode(ProjectBundle bundle, CancellationToken cancellation = default)
        {
            FixedContent.Validate(bundle, cancellation);
            return StorageJson.Encode(w =>
            {
                w.WriteStartObject(); w.WriteString("format", Format); w.WriteNumber("schemaVersion", 1);
                w.WritePropertyName("document"); StorageJson.Embed(w, DocumentCodec.EncodeRoom(bundle.Document, cancellation: cancellation));
                w.WriteStartArray("definitions"); foreach (var d in bundle.Definitions) { cancellation.ThrowIfCancellationRequested(); StorageJson.Embed(w, DocumentCodec.EncodeDefinition(d)); } w.WriteEndArray();
                w.WriteStartArray("models"); foreach (var m in bundle.Models)
                { cancellation.ThrowIfCancellationRequested(); w.WriteStartObject(); w.WritePropertyName("asset"); FixedContent.WriteAsset(w, m.Asset); w.WritePropertyName("scene"); StorageJson.Embed(w, ModelSceneCodec.Encode(m.Scene, cancellation)); w.WriteEndObject(); } w.WriteEndArray();
                w.WritePropertyName("draft"); if (bundle.Draft == null) w.WriteNullValue(); else StorageJson.Embed(w, DocumentCodec.EncodeLibrary(bundle.Draft)); w.WriteEndObject();
            }, cancellation);
        }
        public static ProjectBundle Decode(byte[] bytes, CancellationToken cancellation = default)
        {
            using (var json = StorageJson.Parse(bytes, cancellation))
            {
                var e = json.RootElement; StorageJson.Shape(e, "format", "schemaVersion", "document", "definitions", "models", "draft");
                if (StorageJson.S(e, "format") != Format || StorageJson.I(e, "schemaVersion") != 1) throw new StorageException("InvalidSchema", "Unsupported project bundle.");
                var room = DocumentCodec.DecodeRoom(StorageJson.Raw(e.GetProperty("document")), cancellation: cancellation);
                var definitions = StorageJson.A(e, "definitions", 100000).Select(d => DocumentCodec.DecodeDefinition(StorageJson.Raw(d), cancellation: cancellation)).ToArray();
                var models = new List<StoredModel>();
                foreach (var m in StorageJson.A(e, "models", 4096))
                { cancellation.ThrowIfCancellationRequested(); StorageJson.Shape(m, "asset", "scene"); models.Add(new StoredModel(FixedContent.ReadAsset(m.GetProperty("asset")), ModelSceneCodec.Decode(StorageJson.Raw(m.GetProperty("scene")), cancellation))); }
                var draftElement = e.GetProperty("draft"); var draft = draftElement.ValueKind == JsonValueKind.Null ? null : DocumentCodec.DecodeLibrary(StorageJson.Raw(draftElement), cancellation: cancellation);
                var bundle = new ProjectBundle(room, definitions, models, draft); FixedContent.Validate(bundle, cancellation); return bundle;
            }
        }
    }

    public sealed class ProjectStore
    {
        private readonly AtomicFileStore files;
        private readonly Libraries.LinkRelocations relocations;
        public ProjectStore(AtomicFileStore files = null, Libraries.LinkRelocations relocations = null) { this.files = files ?? new AtomicFileStore(StorageJson.MaxBytes); this.relocations = relocations; }
        public StorageReceipt Save(string path, ProjectBundle bundle, string expectedHash = null, CancellationToken cancellation = default)
        { VerifyLinks(bundle, cancellation); var bytes = ProjectBundleCodec.Encode(bundle, cancellation); return files.Save(path, bytes, expectedHash, b => ProjectBundleCodec.Decode(b, cancellation), cancellation); }
        public LoadedProject Load(string path, CancellationToken cancellation = default)
        { var bytes = files.Read(path, cancellation); var bundle = ProjectBundleCodec.Decode(bytes.Bytes, cancellation); VerifyLinks(bundle, cancellation); return new LoadedProject(bundle, bytes.ContentHash); }
        private void VerifyLinks(ProjectBundle bundle, CancellationToken cancellation)
        { foreach (var model in bundle.Models) if (model.Asset.Mode == AssetStorageMode.Link) foreach (var dependency in model.Asset.Dependencies) Libraries.ImmutableLibrary.VerifyLink(dependency, files, relocations, cancellation); }
    }

    public static class ProjectSealer
    {
        /// <summary>Explicitly adopts an authored candidate. Existing fixed projects never upgrade through this method implicitly.</summary>
        public static ProjectBundle Seal(RoomDocument document, IEnumerable<LibraryDefinition> definitions,
            IReadOnlyDictionary<string, ModelScene> scenesByDefinitionKey, CancellationToken cancellation = default)
        {
            var sealedDefinitions = new List<LibraryDefinition>(); var models = new List<StoredModel>();
            var replacements = new Dictionary<string, PinnedReference>(StringComparer.Ordinal);
            foreach (var d in definitions)
            {
                cancellation.ThrowIfCancellationRequested(); LibraryDefinition candidate = d; AssetReference asset = null;
                if (scenesByDefinitionKey.TryGetValue(d.Reference.Key, out var scene))
                {
                    var pin = new PinnedReference(d.Id + ".model", d.Reference.Revision, AtomicFileStore.Hash(ModelSceneCodec.Encode(scene, cancellation)));
                    asset = new AssetReference(pin, AssetStorageMode.Copy, "model.json", "Original procedural CPU geometry", "{}", "vrc-procedural-1");
                    candidate = WithModel(d, pin); models.Add(new StoredModel(asset, scene));
                }
                else if (d.ModelReference != null) throw new StorageException("MissingDependency", "Candidate model is missing.");
                candidate = FixedContent.SealDefinition(candidate, asset); sealedDefinitions.Add(candidate); replacements.Add(d.Reference.Key, candidate.Reference);
            }
            var room = document.With(placements: document.Placements.Select(p => p.WithDefinition(replacements[p.Definition.Key])).ToArray(),
                pinnedDependencies: document.PinnedDependencies.Select(p => replacements[p.Key]).ToArray());
            var bundle = new ProjectBundle(room, sealedDefinitions, models); FixedContent.Validate(bundle, cancellation); return bundle;
        }
        public static LibraryDefinition WithModel(LibraryDefinition d, PinnedReference model) => new LibraryDefinition(d.Reference, d.Name, d.Category, d.PlacementPolicy,
            d.LocalBounds, d.Hitbox, d.MeshTransform, model, d.FloorMask, d.OccupancyPolicy, d.WallBoundaries, d.OpeningSlots, d.PreviewLights);
    }
}
