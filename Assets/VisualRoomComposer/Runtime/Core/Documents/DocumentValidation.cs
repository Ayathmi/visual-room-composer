using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace VisualRoomComposer.Core.Documents
{
    /// <summary>A document-integrity error, distinct from nonblocking layout diagnostics.</summary>
    public sealed class DocumentValidationException : Exception
    {
        public string Code { get; } public string ElementId { get; }
        public DocumentValidationException(string code, string message, string elementId = null) : base(message) { Code = code; ElementId = elementId; }
    }

    /// <summary>Integrity validation permits open or intersecting drafts while rejecting invalid identities and references.</summary>
    public static class DocumentValidation
    {
        public const int MaximumEntities = 100000;
        public static void Validate(RoomDocument document, Func<PinnedReference, LibraryDefinition> resolve = null, CancellationToken cancellation = default)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            Text(document.ProjectId, 256, true); Text(document.StateId, 256, true); Text(document.Title, 4096); Text(document.Description, 1048576);
            if (document.Revision < 0) Fail("InvalidRevision", "Document revision cannot be negative.");
            var rooms = Index(document.Rooms, value => value.Id); var groups = Index(document.Groups, value => value.Id);
            var placements = Index(document.Placements, value => value.Id);
            if (document.PinnedDependencies.Count > MaximumEntities) Fail("BudgetExceeded", "Fixed dependency count exceeds its budget.");
            var pins = new HashSet<string>(StringComparer.Ordinal); var versions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pin in document.PinnedDependencies)
            {
                Pin(pin); if (!pins.Add(pin.Key)) Fail("DuplicateId", "Duplicate fixed dependency.", pin.Id);
                string version = pin.Id + "/" + pin.Revision;
                if (versions.TryGetValue(version, out var hash) && hash != pin.ContentHash) Fail("HashConflict", "One fixed version has conflicting hashes.", pin.Id);
                versions[version] = pin.ContentHash;
            }
            foreach (var group in document.Groups)
            {
                cancellation.ThrowIfCancellationRequested(); Text(group.Id, 256, true); Text(group.Name, 4096);
                if (group.RoomId != null && !rooms.ContainsKey(group.RoomId)) Fail("MissingReference", "Group room is missing.", group.Id);
                if (group.ParentGroupId != null && !groups.ContainsKey(group.ParentGroupId)) Fail("MissingReference", "Parent group is missing.", group.Id);
            }
            ValidateChains(groups, value => value.ParentGroupId, "GroupCycle", cancellation);
            foreach (var group in document.Groups)
            {
                string parent = group.ParentGroupId;
                while (parent != null)
                {
                    var ancestor = groups[parent];
                    if (group.RoomId != null && ancestor.RoomId != null && group.RoomId != ancestor.RoomId) Fail("RoomMembershipConflict", "Group differs from its ancestor room.", group.Id);
                    parent = ancestor.ParentGroupId;
                }
            }
            foreach (var placement in document.Placements)
            {
                cancellation.ThrowIfCancellationRequested(); Text(placement.Id, 256, true); Pin(placement.Definition);
                if (!pins.Contains(placement.Definition.Key)) Fail("MissingReference", "Placement definition is not fixed by the document.", placement.Id);
                if (placement.RoomId != null && !rooms.ContainsKey(placement.RoomId)) Fail("MissingReference", "Placement room is missing.", placement.Id);
                if (placement.GroupId != null)
                {
                    if (!groups.TryGetValue(placement.GroupId, out var group)) Fail("MissingReference", "Placement group is missing.", placement.Id);
                    while (group != null)
                    {
                        if (group.RoomId != null && group.RoomId != placement.RoomId) Fail("RoomMembershipConflict", "Placement differs from its group room.", placement.Id);
                        group = group.ParentGroupId == null ? null : groups[group.ParentGroupId];
                    }
                }
                if (placement.QuarterTurns < 0 || placement.QuarterTurns > 3 || !Positive(placement.Scale) || !placement.PivotOffset.IsFinite || placement.PivotOffset.Y != 0)
                    Fail("InvalidTransform", "Placement transform is invalid.", placement.Id);
                if (Math.Abs((long)placement.Position.X) > 65536 || Math.Abs((long)placement.Position.Y) > 65536 || Math.Abs((long)placement.Position.Z) > 65536)
                    Fail("CoordinateBudget", "Placement is outside the supported grid.", placement.Id);
                var definition = resolve?.Invoke(placement.Definition);
                if (resolve != null && (definition == null || !definition.Reference.Equals(placement.Definition))) Fail("MissingReference", "Pinned definition is not available.", placement.Id);
                if (definition != null && definition.Category != DefinitionCategory.Wall && !placement.Scale.Equals(Double3.One)) Fail("InvalidScale", "Only wall instances have author-controlled scaling.", placement.Id);
                if (placement.Attachment != null)
                {
                    var attachment = placement.Attachment;
                    if (!placements.TryGetValue(attachment.HostPlacementId, out var host)) Fail("MissingReference", "Attachment host is missing.", placement.Id);
                    if (!attachment.LocalOffset.IsFinite || attachment.LocalQuarterTurns < 0 || attachment.LocalQuarterTurns > 3) Fail("InvalidTransform", "Attachment parameters are invalid.", placement.Id);
                    var local = attachment.LocalOffset;
                    var expectedOrigin = Origin(host) + Matrix4d.RotationYQuarterTurns(host.QuarterTurns).TransformVector(new Double3(local.X * host.Scale.X, local.Y * host.Scale.Y, local.Z * host.Scale.Z));
                    if ((expectedOrigin - Origin(placement)).Length > 1e-5 || placement.QuarterTurns != (host.QuarterTurns + attachment.LocalQuarterTurns) % 4 || !placement.Scale.Equals(Double3.One))
                        Fail("InvalidAttachment", "Attachment absolute transform differs from its host-local parameters.", placement.Id);
                    if (resolve != null)
                    {
                        var hostDefinition = resolve(host.Definition);
                        if (hostDefinition == null || hostDefinition.Category != DefinitionCategory.Wall) Fail("InvalidAttachment", "Attachment host must be a wall.", placement.Id);
                        if (attachment.SlotId != null && !hostDefinition.OpeningSlots.Any(slot => slot.Id == attachment.SlotId)) Fail("MissingReference", "Attachment opening slot is missing.", placement.Id);
                    }
                }
                Unique(placement.MaterialOverrides, value => value.MaterialId);
                foreach (var material in placement.MaterialOverrides) Pin(material.MaterialReference);
                Lights(placement.LightOverrides);
                if (definition != null && placement.LightOverrides.Any(value => !definition.PreviewLights.Any(light => light.Id == value.Id))) Fail("MissingReference", "Light override does not belong to the fixed definition.", placement.Id);
            }
            ValidateChains(placements, value => value.Attachment?.HostPlacementId, "AttachmentCycle", cancellation);
            foreach (var room in document.Rooms)
            {
                Text(room.Id, 256, true); Text(room.Name, 4096); var shared = new HashSet<string>(StringComparer.Ordinal);
                if (room.SharedStructurePlacementIds.Count > MaximumEntities) Fail("BudgetExceeded", "Shared structure count exceeds its budget.", room.Id);
                foreach (var id in room.SharedStructurePlacementIds)
                {
                    if (id == null || !shared.Add(id)) Fail("DuplicateId", "Duplicate shared structure.", room.Id);
                    if (!placements.TryGetValue(id, out var placement)) Fail("MissingReference", "Shared structure is missing.", id);
                    if (resolve != null && resolve(placement.Definition)?.Category != DefinitionCategory.Wall) Fail("InvalidSharedStructure", "Shared structure must be one wall placement.", id);
                }
            }
        }
        public static void Validate(LibraryDraft draft, CancellationToken cancellation = default)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            Text(draft.DraftId, 256, true); Text(draft.RepositoryId, 256, true); Text(draft.StateId, 256, true); Text(draft.Name, 4096);
            if (draft.Revision < 0) Fail("InvalidRevision", "Library draft revision cannot be negative.");
            if (draft.BaseDefinitionRef != null) Pin(draft.BaseDefinitionRef);
            Unique(draft.Definitions, value => value.Id); Unique(draft.Assets, value => value.Reference?.Key);
            foreach (var asset in draft.Assets)
            {
                cancellation.ThrowIfCancellationRequested(); ValidateAsset(asset);
            }
            foreach (var definition in draft.Definitions)
            {
                cancellation.ThrowIfCancellationRequested(); ValidateDefinition(definition);
                if (definition.ModelReference != null && !draft.Assets.Any(asset => asset.Reference.Equals(definition.ModelReference)))
                    Fail("MissingReference", "Definition model is not in the library asset manifest.", definition.Id);
            }
        }
        public static void ValidateAsset(AssetReference asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            Pin(asset.Reference); Text(asset.Uri, 32768, true);
            EnumValue(asset.Mode); Text(asset.SourceProvenance, 32768); Text(asset.ImportOptionsJson, 1048576); Text(asset.ImporterVersion, 256, true);
            if (asset.CacheSchemaVersion < 1) Fail("InvalidVersion", "Cache schema version must be positive.");
            if (asset.Mode == AssetStorageMode.Copy && (System.IO.Path.IsPathRooted(asset.Uri) || asset.Uri.Replace('\\', '/').Split('/').Any(part => part == ".."))) Fail("InvalidPath", "Copied asset paths must be relative and contained.");
            Unique(asset.Dependencies, value => value.Uri);
            foreach (var dependency in asset.Dependencies)
            { Text(dependency.Uri, 32768, true); Hash(dependency.ContentHash); if (dependency.SourceReference != null) Text(dependency.SourceReference, 32768); }
        }
        public static void ValidateDefinition(LibraryDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            Pin(definition.Reference); Text(definition.Name, 4096); EnumValue(definition.Category); EnumValue(definition.PlacementPolicy); EnumValue(definition.OccupancyPolicy);
            Bounds(definition.LocalBounds); if (definition.Hitbox != null) Bounds(definition.Hitbox);
            if (!definition.MeshTransform.TryInverseAffine(out _)) Fail("InvalidTransform", "Definition mesh transform is invalid.", definition.Id);
            if (definition.ModelReference != null) Pin(definition.ModelReference);
            if (definition.Category == DefinitionCategory.Floor ? !new[] { 15, 7, 9, 3, 1 }.Contains(definition.FloorMask) : definition.FloorMask != 0)
                Fail("InvalidFloorMask", "Floor masks belong only to floor definitions.", definition.Id);
            Unique(definition.OpeningSlots, value => value.Id);
            foreach (var slot in definition.OpeningSlots) { EnumValue(slot.Kind); Bounds(slot.Bounds); }
            if (definition.WallBoundaries.Count > MaximumEntities) Fail("BudgetExceeded", "Wall boundary count exceeds its budget.", definition.Id);
            foreach (var boundary in definition.WallBoundaries)
            {
                if (boundary == null) Fail("MissingReference", "Null wall boundary.", definition.Id);
                EnumValue(boundary.Kind);
                long dx = Math.Abs((long)boundary.To.X - boundary.From.X), dz = Math.Abs((long)boundary.To.Z - boundary.From.Z);
                if (dx + dz == 0 || dx + dz > 65536 || dx != 0 && dz != 0) Fail("InvalidTopology", "Wall segment must be finite, orthogonal and nonempty.", definition.Id);
                if (boundary.Kind == WallBoundaryKind.Opening && !definition.OpeningSlots.Any(slot => slot.Id == boundary.SlotId)) Fail("MissingReference", "Wall opening slot is missing.", definition.Id);
            }
            if (definition.Category != DefinitionCategory.Wall && (definition.WallBoundaries.Count != 0 || definition.OpeningSlots.Count != 0)) Fail("InvalidTopology", "Only walls carry opening topology.", definition.Id);
            Lights(definition.PreviewLights);
        }
        public static void Pin(PinnedReference reference)
        {
            if (reference == null) Fail("MissingReference", "A fixed reference is required.");
            Text(reference.Id, 256, true); Hash(reference.ContentHash);
            if (reference.Revision < 1) Fail("InvalidRevision", "Fixed reference revision must be positive.", reference.Id);
        }
        public static void Hash(string value)
        {
            if (value == null || value.Length != 64 || value.Any(character => character < '0' || character > '9' && character < 'a' || character > 'f')) Fail("InvalidHash", "A lowercase SHA256 digest is required.");
        }
        private static void Bounds(Bounds3d value)
        { if (value == null || !value.Center.IsFinite || !value.Size.IsFinite || value.Size.X < 0 || value.Size.Y < 0 || value.Size.Z < 0) Fail("InvalidBounds", "Bounds must be finite with nonnegative size."); }
        private static bool Positive(Double3 value) => value.IsFinite && value.X > 0 && value.Y > 0 && value.Z > 0;
        private static Double3 Origin(RoomPlacement value) => new Double3(value.Position.X * .25 + value.PivotOffset.X, value.Position.Y * .25, value.Position.Z * .25 + value.PivotOffset.Z);
        private static void Lights(IReadOnlyList<PreviewLight> lights)
        {
            Unique(lights, value => value.Id);
            foreach (var light in lights) if (!light.Position.IsFinite || !light.Direction.IsFinite || !light.Color.IsFinite ||
                light.Color.X < 0 || light.Color.Y < 0 || light.Color.Z < 0 || light.IsSpot && light.Direction.LengthSquared == 0 ||
                !Numeric.IsFinite(light.Intensity) || light.Intensity < 0 || !Numeric.IsFinite(light.Range) || light.Range <= 0 ||
                !Numeric.IsFinite(light.SpotAngle) || light.SpotAngle <= 0 || light.SpotAngle >= 180) Fail("InvalidLight", "Light parameters are invalid.", light.Id);
        }
        private static void EnumValue<T>(T value) where T : struct { if (!Enum.IsDefined(typeof(T), value)) Fail("InvalidEnum", "Unknown " + typeof(T).Name + " value."); }
        private static void Text(string value, int maximum, bool required = false)
        { if (value == null || value.Length > maximum || required && string.IsNullOrWhiteSpace(value)) Fail("InvalidText", "Text is missing or exceeds its budget."); }
        private static Dictionary<string, T> Index<T>(IReadOnlyList<T> values, Func<T, string> id) where T : class
        {
            if (values.Count > MaximumEntities) Fail("BudgetExceeded", "Document entity limit exceeded.");
            var result = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (value == null) Fail("MissingReference", "Null document entity.");
                var key = id(value); Text(key, 32768, true);
                if (result.ContainsKey(key)) Fail("DuplicateId", "Duplicate document entity.", key);
                result.Add(key, value);
            }
            return result;
        }
        private static void Unique<T>(IReadOnlyList<T> values, Func<T, string> id) where T : class { Index(values, id); }
        private static void ValidateChains<T>(Dictionary<string, T> values, Func<T, string> parent, string code, CancellationToken cancellation)
        {
            var depths = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in values.Keys)
            {
                cancellation.ThrowIfCancellationRequested(); var path = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal); string current = id;
                while (current != null && !depths.ContainsKey(current))
                {
                    if (!seen.Add(current)) Fail(code, "Document references contain a cycle.", current);
                    path.Add(current);
                    if (path.Count > 128) Fail("BudgetExceeded", "Document hierarchy exceeds 128 levels.", current);
                    current = parent(values[current]);
                }
                int depth = current == null ? 0 : depths[current];
                for (int i = path.Count - 1; i >= 0; i--)
                {
                    if (++depth > 128) Fail("BudgetExceeded", "Document hierarchy exceeds 128 levels.", path[i]);
                    depths[path[i]] = depth;
                }
            }
        }
        private static void Fail(string code, string message, string id = null) { throw new DocumentValidationException(code, message, id); }
    }
}
