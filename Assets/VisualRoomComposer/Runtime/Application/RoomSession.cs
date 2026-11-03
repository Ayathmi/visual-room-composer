using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VisualRoomComposer.Core;
using VisualRoomComposer.Core.Documents;
using VisualRoomComposer.Core.History;

namespace VisualRoomComposer.Application
{
    /// <summary>Independent room and library editing contexts with shared resource ownership and history limits.</summary>
    public sealed class RoomSession : IDisposable
    {
        private readonly object registryGate = new object();
        private readonly Dictionary<string, LibraryDefinition> definitions = new Dictionary<string, LibraryDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, ImportedSceneLease> models = new Dictionary<string, ImportedSceneLease>(StringComparer.Ordinal);
        private readonly Dictionary<string, WeakReference<ModelScene>> modelIdentities = new Dictionary<string, WeakReference<ModelScene>>(StringComparer.Ordinal);
        private readonly Func<PinnedReference, LibraryDefinition> externalDefinitions;
        private readonly Func<PinnedReference, ImportedSceneLease> externalModels;
        private bool disposed;
        public HistoryBudget Budget { get; }
        public DocumentHistory<RoomDocument> ProjectHistory { get; }
        public DocumentHistory<LibraryDraft> LibraryHistory { get; }
        public RoomDocument Document => ProjectHistory.Current;
        public LibraryDraft LibraryDraft => LibraryHistory.Current;
        public event Action DocumentChanged { add { ProjectHistory.Changed += value; } remove { ProjectHistory.Changed -= value; } }
        public event Action LibraryChanged { add { LibraryHistory.Changed += value; } remove { LibraryHistory.Changed -= value; } }
        /// <param name="modelLeaseResolver">Returns a fresh owned lease on each call. RegisterModel instead retains a caller-owned lease.</param>
        public RoomSession(RoomDocument document = null, LibraryDraft libraryDraft = null, HistoryBudget budget = null,
            Func<PinnedReference, LibraryDefinition> definitionResolver = null, Func<PinnedReference, ImportedSceneLease> modelLeaseResolver = null)
        {
            Budget = budget ?? new HistoryBudget(); externalDefinitions = definitionResolver; externalModels = modelLeaseResolver;
            ProjectHistory = new DocumentHistory<RoomDocument>(document ?? new RoomDocument(Guid.NewGuid().ToString("N")), Budget,
                value => DocumentValidation.Validate(value, ResolveDefinition), AcquireRoom);
            try { LibraryHistory = new DocumentHistory<LibraryDraft>(libraryDraft ?? new LibraryDraft(Guid.NewGuid().ToString("N"), "local"), Budget,
                value => DocumentValidation.Validate(value), AcquireLibrary); }
            catch { ProjectHistory.Dispose(); throw; }
        }
        public LibraryDefinition ResolveDefinition(PinnedReference reference)
        {
            if (reference == null) return null;
            lock (registryGate) { RequireAlive(); return definitions.TryGetValue(reference.Key, out var value) ? value : externalDefinitions?.Invoke(reference); }
        }
        public void RegisterDefinition(LibraryDefinition definition)
        {
            DocumentValidation.ValidateDefinition(definition);
            lock (registryGate)
            {
                RequireAlive();
                if (definitions.Values.Any(value => value.Reference.Id == definition.Reference.Id && value.Reference.Revision == definition.Reference.Revision && !value.Reference.Equals(definition.Reference)))
                    throw new InvalidOperationException("A published definition version already has another content hash.");
                if (definitions.TryGetValue(definition.Reference.Key, out var old) && !DocumentCodec.EncodeDefinition(old).AsSpan().SequenceEqual(DocumentCodec.EncodeDefinition(definition)))
                    throw new InvalidOperationException("Fixed definition content cannot be replaced.");
                definitions[definition.Reference.Key] = definition;
            }
        }
        /// <summary>Retains the supplied lease. The caller keeps ownership of its own handle.</summary>
        public void RegisterModel(PinnedReference reference, ImportedSceneLease lease)
        {
            DocumentValidation.Pin(reference); if (lease == null) throw new ArgumentNullException(nameof(lease));
            lock (registryGate)
            {
                RequireAlive();
                if (models.TryGetValue(reference.Key, out var old))
                { if (!ReferenceEquals(old.Scene, lease.Scene)) throw new InvalidOperationException("A fixed model is already registered."); return; }
                models.Add(reference.Key, lease.Retain());
                modelIdentities[reference.Key] = new WeakReference<ModelScene>(lease.Scene);
            }
        }
        public bool UnregisterModel(PinnedReference reference)
        {
            ImportedSceneLease lease; lock (registryGate) { RequireAlive(); if (!models.TryGetValue(reference.Key, out lease)) return false; models.Remove(reference.Key); }
            lease.Dispose(); return true;
        }
        public ImportedSceneLease AcquireModel(PinnedReference reference)
        {
            ModelScene previous = null;
            lock (registryGate)
            {
                RequireAlive(); if (models.TryGetValue(reference.Key, out var lease)) return lease.Retain();
                if (modelIdentities.TryGetValue(reference.Key, out var weak)) weak.TryGetTarget(out previous);
            }
            var retained = ProjectHistory?.TryRetainResource(previous) ?? LibraryHistory?.TryRetainResource(previous);
            return retained ?? externalModels?.Invoke(reference) ?? throw new InvalidOperationException("Pinned model is unavailable: " + reference.Key);
        }
        public bool Apply(string name, Func<RoomDocument, RoomDocument> edit, CancellationToken cancellation = default) => ProjectHistory.Apply(name, edit, cancellation);
        public bool ApplyLibrary(string name, Func<LibraryDraft, LibraryDraft> edit, CancellationToken cancellation = default) => LibraryHistory.Apply(name, edit, cancellation);
        public void OpenRoom(RoomDocument document, bool saved = true) => ProjectHistory.Replace(document, saved);
        public void OpenLibrary(LibraryDraft draft, bool saved = true) => LibraryHistory.Replace(draft, saved);
        public bool UpgradeDefinition(PinnedReference oldReference, LibraryDefinition newDefinition)
        {
            DocumentValidation.Pin(oldReference); RegisterDefinition(newDefinition);
            return Apply("Upgrade fixed definition", document =>
            {
                if (!document.PinnedDependencies.Any(reference => reference.Equals(oldReference))) return document;
                var placements = document.Placements.Select(value => value.Definition.Equals(oldReference) ? value.WithDefinition(newDefinition.Reference) : value).ToArray();
                var pins = document.PinnedDependencies.Select(value => value.Equals(oldReference) ? newDefinition.Reference : value).Distinct().ToArray();
                return document.With(placements: placements, pinnedDependencies: pins);
            });
        }
        private IReadOnlyList<ImportedSceneLease> AcquireRoom(RoomDocument document)
        {
            var references = new List<PinnedReference>();
            foreach (var pin in document.PinnedDependencies)
            {
                var definition = ResolveDefinition(pin) ?? throw new InvalidOperationException("Pinned definition is unavailable: " + pin.Key);
                if (!definition.Reference.Equals(pin)) throw new InvalidOperationException("Definition resolver returned another fixed version.");
                if (definition.ModelReference != null) references.Add(definition.ModelReference);
            }
            return AcquireMany(references);
        }
        private IReadOnlyList<ImportedSceneLease> AcquireLibrary(LibraryDraft document) => AcquireMany(document.Assets.Select(asset => asset.Reference));
        private IReadOnlyList<ImportedSceneLease> AcquireMany(IEnumerable<PinnedReference> references)
        {
            var leases = new List<ImportedSceneLease>();
            try { foreach (var reference in references.Distinct()) leases.Add(AcquireModel(reference)); return leases; }
            catch { foreach (var lease in leases) lease.Dispose(); throw; }
        }
        private void RequireAlive() { if (disposed) throw new ObjectDisposedException(nameof(RoomSession)); }
        public void Dispose()
        {
            ProjectHistory.Dispose(); LibraryHistory.Dispose();
            lock (registryGate) { if (disposed) return; disposed = true; foreach (var lease in models.Values) lease.Dispose(); models.Clear(); modelIdentities.Clear(); definitions.Clear(); }
        }
    }
}
