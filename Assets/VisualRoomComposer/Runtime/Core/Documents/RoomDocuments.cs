using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace VisualRoomComposer.Core.Documents
{
    public enum DefinitionCategory { Floor, Wall, WallAttachment, Prop, Light }
    public enum PlacementPolicy { Ground, Wall, Suspended, Structure }
    public enum WallBoundaryKind { Solid, Opening }
    public enum OpeningKind { Door, Window }
    public enum OccupancyPolicy { Blocking, Passage, None }
    public enum AssetStorageMode { Copy, Link }

    internal static class DocumentValues
    {
        internal static IReadOnlyList<T> Copy<T>(IEnumerable<T> values) => Array.AsReadOnly(values == null ? Array.Empty<T>() : values.ToArray());
        internal static string Required(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A stable identifier is required.", name);
            return value;
        }
        internal static string NewId() => Guid.NewGuid().ToString("N");
    }

    /// <summary>A fixed immutable identity. A location is deliberately not part of this identity.</summary>
    public sealed class PinnedReference : IEquatable<PinnedReference>
    {
        public string Id { get; }
        public long Revision { get; }
        public string ContentHash { get; }
        [JsonIgnore] public string Key => Id + "/" + Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + ContentHash;
        [JsonConstructor] public PinnedReference(string id, long revision, string contentHash)
        { Id = DocumentValues.Required(id, nameof(id)); Revision = revision; ContentHash = contentHash; }
        public bool Equals(PinnedReference other) => other != null && Id == other.Id && Revision == other.Revision && ContentHash == other.ContentHash;
        public override bool Equals(object obj) => obj is PinnedReference other && Equals(other);
        public override int GetHashCode() => unchecked((Id.GetHashCode() * 397 ^ Revision.GetHashCode()) * 397 ^ (ContentHash?.GetHashCode() ?? 0));
    }

    /// <summary>An integer position on the quarter-meter XZ lattice.</summary>
    public readonly struct GridCell2 : IEquatable<GridCell2>
    {
        public int X { get; } public int Z { get; }
        public GridCell2(int x, int z) { X = x; Z = z; }
        public bool Equals(GridCell2 other) => X == other.X && Z == other.Z;
        public override bool Equals(object obj) => obj is GridCell2 other && Equals(other);
        public override int GetHashCode() => unchecked(X * 397 ^ Z);
    }

    /// <summary>An axis-aligned definition-space box after the definition mesh transform has been applied.</summary>
    public sealed class Bounds3d
    {
        public Double3 Center { get; } public Double3 Size { get; }
        [JsonIgnore] public Double3 Min => Center - Size * 0.5;
        [JsonIgnore] public Double3 Max => Center + Size * 0.5;
        [JsonConstructor] public Bounds3d(Double3 center, Double3 size) { Center = center; Size = size; }
    }

    /// <summary>A logical wall segment in quarter-meter units, independent of its physical mesh.</summary>
    public sealed class WallBoundary
    {
        public GridCell2 From { get; } public GridCell2 To { get; }
        public WallBoundaryKind Kind { get; } public string SlotId { get; }
        [JsonConstructor] public WallBoundary(GridCell2 from, GridCell2 to, WallBoundaryKind kind = WallBoundaryKind.Solid, string slotId = null)
        { From = from; To = to; Kind = kind; SlotId = slotId; }
    }

    /// <summary>A named opening in definition space.</summary>
    public sealed class OpeningSlot
    {
        public string Id { get; } public OpeningKind Kind { get; } public Bounds3d Bounds { get; }
        [JsonConstructor] public OpeningSlot(string id, OpeningKind kind, Bounds3d bounds)
        { Id = DocumentValues.Required(id, nameof(id)); Kind = kind; Bounds = bounds; }
    }

    /// <summary>Author-controlled light data; it does not promise FBX light fidelity.</summary>
    public sealed class PreviewLight
    {
        public string Id { get; } public Double3 Position { get; } public Double3 Direction { get; } public Double3 Color { get; }
        public double Intensity { get; } public double Range { get; } public double SpotAngle { get; }
        public bool IsSpot { get; } public bool Enabled { get; }
        [JsonConstructor] public PreviewLight(string id, Double3 position, Double3 direction, Double3 color,
            double intensity = 1, double range = 10, double spotAngle = 45, bool isSpot = false, bool enabled = true)
        { Id = DocumentValues.Required(id, nameof(id)); Position = position; Direction = direction; Color = color; Intensity = intensity; Range = range; SpotAngle = spotAngle; IsSpot = isSpot; Enabled = enabled; }
    }

    /// <summary>Definition geometry, occupancy and opening metadata. Bounds already include MeshTransform.</summary>
    public sealed class LibraryDefinition
    {
        public PinnedReference Reference { get; } public string Name { get; }
        public DefinitionCategory Category { get; } public PlacementPolicy PlacementPolicy { get; }
        public Bounds3d LocalBounds { get; } public Bounds3d Hitbox { get; }
        public Matrix4d MeshTransform { get; } public PinnedReference ModelReference { get; }
        public int FloorMask { get; } public OccupancyPolicy OccupancyPolicy { get; }
        public IReadOnlyList<WallBoundary> WallBoundaries { get; } public IReadOnlyList<OpeningSlot> OpeningSlots { get; }
        public IReadOnlyList<PreviewLight> PreviewLights { get; }
        [JsonIgnore] public bool HasHitbox => Hitbox != null;
        [JsonIgnore] public string Id => Reference.Id;
        [JsonConstructor] public LibraryDefinition(PinnedReference reference, string name, DefinitionCategory category,
            PlacementPolicy placementPolicy, Bounds3d localBounds, Bounds3d hitbox, Matrix4d meshTransform,
            PinnedReference modelReference = null, int floorMask = 0, OccupancyPolicy occupancyPolicy = OccupancyPolicy.Blocking,
            IReadOnlyList<WallBoundary> wallBoundaries = null, IReadOnlyList<OpeningSlot> openingSlots = null,
            IReadOnlyList<PreviewLight> previewLights = null)
        {
            Reference = reference ?? throw new ArgumentNullException(nameof(reference)); Name = name ?? ""; Category = category;
            PlacementPolicy = placementPolicy; LocalBounds = localBounds; Hitbox = hitbox; MeshTransform = meshTransform;
            ModelReference = modelReference; FloorMask = floorMask; OccupancyPolicy = occupancyPolicy;
            WallBoundaries = DocumentValues.Copy(wallBoundaries); OpeningSlots = DocumentValues.Copy(openingSlots); PreviewLights = DocumentValues.Copy(previewLights);
        }
        public LibraryDefinition WithReference(PinnedReference reference) => new LibraryDefinition(reference, Name, Category, PlacementPolicy,
            LocalBounds, Hitbox, MeshTransform, ModelReference, FloorMask, OccupancyPolicy, WallBoundaries, OpeningSlots, PreviewLights);
    }

    /// <summary>A physical attachment retains explicit local parameters for host transformations.</summary>
    public sealed class PlacementAttachment
    {
        public string HostPlacementId { get; } public string SlotId { get; }
        public Double3 LocalOffset { get; } public int LocalQuarterTurns { get; }
        [JsonConstructor] public PlacementAttachment(string hostPlacementId, string slotId = null, Double3 localOffset = default, int localQuarterTurns = 0)
        { HostPlacementId = DocumentValues.Required(hostPlacementId, nameof(hostPlacementId)); SlotId = slotId; LocalOffset = localOffset; LocalQuarterTurns = localQuarterTurns; }
    }

    /// <summary>A material override names a fixed source material and another fixed material asset.</summary>
    public sealed class MaterialOverride
    {
        public string MaterialId { get; } public PinnedReference MaterialReference { get; }
        [JsonConstructor] public MaterialOverride(string materialId, PinnedReference materialReference)
        { MaterialId = DocumentValues.Required(materialId, nameof(materialId)); MaterialReference = materialReference; }
    }

    /// <summary>The only physical instance record; room and group membership do not add transforms.</summary>
    public sealed class RoomPlacement
    {
        public string Id { get; } public PinnedReference Definition { get; }
        public GridPosition Position { get; } public int QuarterTurns { get; } public Double3 Scale { get; } public Double3 PivotOffset { get; }
        public string RoomId { get; } public string GroupId { get; } public PlacementAttachment Attachment { get; }
        public bool HiddenInEditor { get; } public bool IncludeInExport { get; }
        public IReadOnlyList<MaterialOverride> MaterialOverrides { get; } public IReadOnlyList<PreviewLight> LightOverrides { get; }
        public RoomPlacement(string id, PinnedReference definition, GridPosition position, int quarterTurns = 0,
            Double3? scale = null, Double3? pivotOffset = null, string roomId = null, string groupId = null,
            PlacementAttachment attachment = null, bool hiddenInEditor = false, bool includeInExport = true,
            IReadOnlyList<MaterialOverride> materialOverrides = null, IReadOnlyList<PreviewLight> lightOverrides = null)
        {
            Id = DocumentValues.Required(id, nameof(id)); Definition = definition; Position = position; QuarterTurns = quarterTurns;
            Scale = scale ?? Double3.One; PivotOffset = pivotOffset ?? Double3.Zero; RoomId = roomId; GroupId = groupId;
            Attachment = attachment; HiddenInEditor = hiddenInEditor; IncludeInExport = includeInExport;
            MaterialOverrides = DocumentValues.Copy(materialOverrides); LightOverrides = DocumentValues.Copy(lightOverrides);
        }
        [JsonConstructor] public RoomPlacement(string id, PinnedReference definition, GridPosition position, int quarterTurns,
            Double3 scale, Double3 pivotOffset, string roomId, string groupId, PlacementAttachment attachment,
            bool hiddenInEditor, bool includeInExport, IReadOnlyList<MaterialOverride> materialOverrides, IReadOnlyList<PreviewLight> lightOverrides)
            : this(id, definition, position, quarterTurns, (Double3?)scale, (Double3?)pivotOffset, roomId, groupId, attachment,
                hiddenInEditor, includeInExport, materialOverrides, lightOverrides) { }
        public RoomPlacement WithTransform(GridPosition position, int quarterTurns, Double3 scale, Double3 pivotOffset,
            PlacementAttachment attachment = null) => new RoomPlacement(Id, Definition, position, quarterTurns, scale, pivotOffset,
                RoomId, GroupId, attachment ?? Attachment, HiddenInEditor, IncludeInExport, MaterialOverrides, LightOverrides);
        public RoomPlacement WithMembership(string roomId, string groupId) => new RoomPlacement(Id, Definition, Position, QuarterTurns,
            Scale, PivotOffset, roomId, groupId, Attachment, HiddenInEditor, IncludeInExport, MaterialOverrides, LightOverrides);
        public RoomPlacement WithDefinition(PinnedReference definition) => new RoomPlacement(Id, definition, Position, QuarterTurns,
            Scale, PivotOffset, RoomId, GroupId, Attachment, HiddenInEditor, IncludeInExport, MaterialOverrides, LightOverrides);
    }

    /// <summary>A room owns no duplicate placement table; shared structures point to global placement IDs.</summary>
    public sealed class Room
    {
        public string Id { get; } public string Name { get; } public IReadOnlyList<string> SharedStructurePlacementIds { get; }
        [JsonConstructor] public Room(string id, string name, IReadOnlyList<string> sharedStructurePlacementIds = null)
        { Id = DocumentValues.Required(id, nameof(id)); Name = name ?? ""; SharedStructurePlacementIds = DocumentValues.Copy(sharedStructurePlacementIds); }
    }

    /// <summary>A logical group with no position, rotation or scale.</summary>
    public sealed class RoomGroup
    {
        public string Id { get; } public string Name { get; } public string ParentGroupId { get; } public string RoomId { get; }
        [JsonConstructor] public RoomGroup(string id, string name, string parentGroupId = null, string roomId = null)
        { Id = DocumentValues.Required(id, nameof(id)); Name = name ?? ""; ParentGroupId = parentGroupId; RoomId = roomId; }
    }

    /// <summary>One immutable continuous room-composition space, with independent logical state and revision.</summary>
    public sealed class RoomDocument
    {
        public const string FormatName = "visual-room-composer.project";
        public string Format => FormatName; public int SchemaVersion => 1; public string Unit => "meter";
        public string ProjectId { get; } public long Revision { get; } public string StateId { get; }
        public string Title { get; } public string Description { get; } public bool ShowGrid { get; }
        public IReadOnlyList<Room> Rooms { get; } public IReadOnlyList<RoomGroup> Groups { get; }
        public IReadOnlyList<RoomPlacement> Placements { get; } public IReadOnlyList<PinnedReference> PinnedDependencies { get; }
        [JsonConstructor] public RoomDocument(string projectId, string title = "", IReadOnlyList<Room> rooms = null,
            IReadOnlyList<RoomGroup> groups = null, IReadOnlyList<RoomPlacement> placements = null,
            IReadOnlyList<PinnedReference> pinnedDependencies = null, long revision = 0, string stateId = null,
            string description = "", bool showGrid = true)
        {
            ProjectId = DocumentValues.Required(projectId, nameof(projectId)); Title = title ?? ""; Description = description ?? "";
            Rooms = DocumentValues.Copy(rooms); Groups = DocumentValues.Copy(groups); Placements = DocumentValues.Copy(placements);
            PinnedDependencies = DocumentValues.Copy(pinnedDependencies); Revision = revision; StateId = stateId ?? DocumentValues.NewId(); ShowGrid = showGrid;
        }
        public RoomDocument With(IReadOnlyList<Room> rooms = null, IReadOnlyList<RoomGroup> groups = null,
            IReadOnlyList<RoomPlacement> placements = null, IReadOnlyList<PinnedReference> pinnedDependencies = null,
            string title = null, string description = null, bool? showGrid = null) => new RoomDocument(ProjectId, title ?? Title,
                rooms ?? Rooms, groups ?? Groups, placements ?? Placements, pinnedDependencies ?? PinnedDependencies, Revision, StateId, description ?? Description, showGrid ?? ShowGrid);
        public RoomDocument WithState(long revision, string stateId) => new RoomDocument(ProjectId, Title, Rooms, Groups, Placements, PinnedDependencies, revision, stateId, Description, ShowGrid);
    }

    /// <summary>A fixed file dependency and its original alias.</summary>
    public sealed class AssetDependency
    {
        public string Uri { get; } public string ContentHash { get; } public string SourceReference { get; }
        [JsonConstructor] public AssetDependency(string uri, string contentHash, string sourceReference = null)
        { Uri = uri; ContentHash = contentHash; SourceReference = sourceReference; }
    }

    /// <summary>Persistent model provenance, distinct from a disposable parsed-model cache.</summary>
    public sealed class AssetReference
    {
        public PinnedReference Reference { get; } public AssetStorageMode Mode { get; } public string Uri { get; }
        public string SourceProvenance { get; } public string ImportOptionsJson { get; } public string ImporterVersion { get; }
        public int CacheSchemaVersion { get; } public IReadOnlyList<AssetDependency> Dependencies { get; }
        [JsonConstructor] public AssetReference(PinnedReference reference, AssetStorageMode mode, string uri,
            string sourceProvenance = "", string importOptionsJson = "{}", string importerVersion = "vrc-1", int cacheSchemaVersion = 1,
            IReadOnlyList<AssetDependency> dependencies = null)
        { Reference = reference; Mode = mode; Uri = uri; SourceProvenance = sourceProvenance; ImportOptionsJson = importOptionsJson;
            ImporterVersion = importerVersion; CacheSchemaVersion = cacheSchemaVersion; Dependencies = DocumentValues.Copy(dependencies); }
    }

    /// <summary>The current editable library workspace has a history independent of the room project.</summary>
    public sealed class LibraryDraft
    {
        public const string FormatName = "visual-room-composer.library-draft";
        public string Format => FormatName; public int SchemaVersion => 1;
        public string DraftId { get; } public string RepositoryId { get; } public string StateId { get; } public long Revision { get; }
        public PinnedReference BaseDefinitionRef { get; } public IReadOnlyList<LibraryDefinition> Definitions { get; }
        public IReadOnlyList<AssetReference> Assets { get; } public string Name { get; }
        [JsonConstructor] public LibraryDraft(string draftId, string repositoryId, IReadOnlyList<LibraryDefinition> definitions = null,
            IReadOnlyList<AssetReference> assets = null, string name = "", PinnedReference baseDefinitionRef = null, long revision = 0, string stateId = null)
        { DraftId = DocumentValues.Required(draftId, nameof(draftId)); RepositoryId = DocumentValues.Required(repositoryId, nameof(repositoryId));
            Definitions = DocumentValues.Copy(definitions); Assets = DocumentValues.Copy(assets); Name = name ?? "";
            BaseDefinitionRef = baseDefinitionRef; Revision = revision; StateId = stateId ?? DocumentValues.NewId(); }
        public LibraryDraft With(IReadOnlyList<LibraryDefinition> definitions = null, IReadOnlyList<AssetReference> assets = null,
            string name = null) => new LibraryDraft(DraftId, RepositoryId, definitions ?? Definitions, assets ?? Assets, name ?? Name, BaseDefinitionRef, Revision, StateId);
        public LibraryDraft WithState(long revision, string stateId) => new LibraryDraft(DraftId, RepositoryId, Definitions, Assets, Name, BaseDefinitionRef, revision, stateId);
    }
}
