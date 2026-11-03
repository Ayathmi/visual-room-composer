using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisualRoomComposer.Core.Documents;

namespace VisualRoomComposer.Core.History
{
    /// <summary>The shared retained-history limit for independently undoable authoring contexts.</summary>
    public sealed class HistoryBudget
    {
        internal readonly object Gate = new object();
        private readonly List<IHistoryOwner> owners = new List<IHistoryOwner>(); private long sequence;
        public int MaximumSteps { get; } public long MaximumBytes { get; }
        public HistoryBudget(int maximumSteps = 500, long maximumBytes = 256L * 1024 * 1024)
        { if (maximumSteps < 1 || maximumBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maximumSteps)); MaximumSteps = maximumSteps; MaximumBytes = maximumBytes; }
        public int RetainedSteps { get { lock (Gate) return owners.Sum(owner => owner.Plan.Steps); } }
        public long RetainedBytes { get { lock (Gate) return owners.Sum(owner => owner.Plan.Bytes); } }
        internal long NextSequence() => checked(++sequence);
        internal IHistoryOwner[] Accept(IHistoryOwner author, HistoryPlan candidate, bool adding = false, bool preserveNewest = false, CancellationToken cancellation = default)
        {
            var all = owners.Contains(author) ? owners.ToArray() : owners.Concat(new[] { author }).ToArray();
            var plans = all.ToDictionary(owner => owner, owner => owner == author ? candidate : owner.Plan.Clone());
            bool Over() => plans.Values.Sum(plan => plan.Steps) > MaximumSteps || plans.Values.Sum(plan => plan.Bytes) > MaximumBytes;
            if (Over()) foreach (var plan in plans.Values) plan.DropCheckpoints();
            while (Over())
            {
                cancellation.ThrowIfCancellationRequested();
                var eligible = all.Where(owner => !owner.Busy && plans[owner].CanTrim && (owner != author || !preserveNewest || plans[owner].Steps > 1))
                    .OrderBy(owner => plans[owner].FirstSequence).FirstOrDefault();
                if (eligible == null) throw new InvalidOperationException("The command and retained history exceed the shared history budget.");
                plans[eligible].TrimFirst();
            }
            cancellation.ThrowIfCancellationRequested();
            var changed = all.Where(owner => owner == author || !plans[owner].EquivalentTo(owner.Plan)).ToArray();
            if (adding) owners.Add(author);
            foreach (var owner in changed) owner.Accept(plans[owner]);
            return changed;
        }
        internal void Remove(IHistoryOwner owner) => owners.Remove(owner);
        internal static void Notify(IEnumerable<IHistoryOwner> values) { foreach (var value in values) value.Notify(); }
    }
    internal interface IHistoryOwner
    {
        HistoryPlan Plan { get; } bool Busy { get; }
        void Accept(HistoryPlan plan); void Notify();
    }
    internal abstract class HistoryPlan
    {
        internal abstract int Steps { get; } internal abstract long Bytes { get; } internal abstract bool CanTrim { get; }
        internal abstract long FirstSequence { get; }
        internal abstract HistoryPlan Clone(); internal abstract void TrimFirst(); internal abstract void DropCheckpoints();
        internal abstract bool EquivalentTo(HistoryPlan other);
    }
    internal sealed class LeaseSet : IDisposable
    {
        internal readonly IReadOnlyList<ImportedSceneLease> Values; internal long Bytes => 64L + Values.Count * 32L;
        internal LeaseSet(IReadOnlyList<ImportedSceneLease> values) { Values = values == null ? Array.Empty<ImportedSceneLease>() : values.ToArray(); }
        internal LeaseSet Retain()
        {
            var retained = new List<ImportedSceneLease>();
            try { foreach (var value in Values) retained.Add(value.Retain()); return new LeaseSet(retained); }
            catch { foreach (var value in retained) value.Dispose(); throw; }
        }
        public void Dispose() { foreach (var value in Values) { try { value.Dispose(); } catch { } } }
    }
    /// <summary>A model-owning immutable export or background-work snapshot.</summary>
    public sealed class HistorySnapshot<T> : IDisposable where T : class
    {
        private LeaseSet leases;
        public T Document { get; } public long Revision { get; } public long Generation { get; } public string StateId { get; } public string HistoryId { get; }
        internal HistorySnapshot(T document, long revision, long generation, string stateId, string historyId, LeaseSet leases)
        { Document = document; Revision = revision; Generation = generation; StateId = stateId; HistoryId = historyId; this.leases = leases; }
        public void Dispose() { Interlocked.Exchange(ref leases, null)?.Dispose(); }
    }
    public sealed class HistoryRow
    {
        public long Position { get; } public string Name { get; } public string StateId { get; } public bool IsCurrent { get; }
        internal HistoryRow(long position, string name, string stateId, bool current) { Position = position; Name = name; StateId = stateId; IsCurrent = current; }
    }

    /// <summary>Atomic immutable-record history with compressed baselines, sparse checkpoints and generation-guarded jumps.</summary>
    public sealed class DocumentHistory<T> : IDisposable, IHistoryOwner where T : class
    {
        private readonly DocumentAdapter<T> adapter = DocumentAdapter<T>.Create();
        private readonly Action<T> validate; private readonly Func<T, IReadOnlyList<ImportedSceneLease>> acquire;
        private PlanState state; private T current; private long revision, generation = 1;
        private string savepoint; private bool disposed; private CancellationTokenSource operation;
        public HistoryBudget Budget { get; }
        public string HistoryId { get; } = Guid.NewGuid().ToString("N");
        public event Action Changed;
        public Exception LastNotificationError { get; private set; }
        public T Current { get { lock (Budget.Gate) return current; } }
        public long Revision { get { lock (Budget.Gate) return revision; } }
        public long Generation { get { lock (Budget.Gate) return generation; } }
        public string StateId { get { lock (Budget.Gate) return adapter.StateId(current); } }
        public string SavepointStateId { get { lock (Budget.Gate) return savepoint; } }
        public bool IsDirty { get { lock (Budget.Gate) return adapter.StateId(current) != savepoint; } }
        public bool IsBusy { get { lock (Budget.Gate) return operation != null; } }
        public bool CanUndo { get { lock (Budget.Gate) return !disposed && operation == null && state.Cursor > 0; } }
        public bool CanRedo { get { lock (Budget.Gate) return !disposed && operation == null && state.Cursor < state.Entries.Count; } }
        public long EarliestPosition { get { lock (Budget.Gate) return state.BasePosition; } }
        public long CurrentPosition { get { lock (Budget.Gate) return state.BasePosition + state.Cursor; } }
        public long LatestPosition { get { lock (Budget.Gate) return state.BasePosition + state.Entries.Count; } }
        public int RetainedSteps { get { lock (Budget.Gate) return state.Steps; } }
        public long RetainedBytes { get { lock (Budget.Gate) return state.Bytes; } }
        public int CheckpointCount { get { lock (Budget.Gate) return state.Checkpoints.Count; } }
        public IReadOnlyList<HistoryRow> Rows
        {
            get { lock (Budget.Gate) return Array.AsReadOnly(new[] { new HistoryRow(state.BasePosition, "Baseline", state.BaseStateId, state.Cursor == 0) }
                .Concat(state.Entries.Select((entry, i) => new HistoryRow(state.BasePosition + i + 1, entry.Delta.Name, entry.StateId, state.Cursor == i + 1))).ToArray()); }
        }
        public DocumentHistory(T document, HistoryBudget budget = null, Action<T> validate = null,
            Func<T, IReadOnlyList<ImportedSceneLease>> acquireLeases = null, bool saved = true)
        {
            Budget = budget ?? new HistoryBudget(); this.validate = validate ?? DefaultValidate; acquire = acquireLeases;
            this.validate(document); current = document; revision = adapter.Revision(document); savepoint = saved ? adapter.StateId(document) : null;
            LeaseSet leases = Acquire(document);
            try
            {
                state = new PlanState(adapter, HistoryPacking.Pack(adapter.Encode(document, default)), adapter.StateId(document), leases);
                IHistoryOwner[] notify; lock (Budget.Gate) notify = Budget.Accept(this, state, adding: true);
                HistoryBudget.Notify(notify);
            }
            catch { leases.Dispose(); throw; }
        }
        private static void DefaultValidate(T value)
        {
            if (value is RoomDocument room) DocumentValidation.Validate(room);
            else if (value is LibraryDraft library) DocumentValidation.Validate(library);
            else throw new NotSupportedException();
        }
        public DocumentDelta<T> Prepare(string name, Func<T, T> edit, CancellationToken cancellation = default)
        {
            if (edit == null) throw new ArgumentNullException(nameof(edit));
            lock (Budget.Gate)
            {
                RequireEditable(); cancellation.ThrowIfCancellationRequested(); var candidate = edit(current) ?? throw new InvalidOperationException("An editing command returned null.");
                validate(candidate); cancellation.ThrowIfCancellationRequested(); return new DocumentDelta<T>(name, current, candidate, adapter, cancellation);
            }
        }
        public bool Apply(string name, Func<T, T> edit, CancellationToken cancellation = default) => Execute(Prepare(name, edit, cancellation), cancellation);
        public bool Execute(DocumentDelta<T> delta, CancellationToken cancellation = default)
        {
            if (delta == null) throw new ArgumentNullException(nameof(delta)); IHistoryOwner[] notify;
            lock (Budget.Gate)
            {
                RequireEditable(); cancellation.ThrowIfCancellationRequested();
                if (delta.ExpectedStateId != adapter.StateId(current) || delta.ExpectedRevision != revision || delta.DocumentIdentity != adapter.Identity(current)) throw new InvalidOperationException("Prepared command is stale.");
                if (delta.ChangeCount == 0) return false;
                var candidate = delta.Apply(current, true, cancellation); validate(candidate);
                long nextRevision = checked(revision + 1); string nextState = Guid.NewGuid().ToString("N"); candidate = adapter.Stamp(candidate, nextRevision, nextState);
                var leases = Acquire(candidate); bool accepted = false;
                try
                {
                    var plan = (PlanState)state.Clone(); plan.Entries.RemoveRange(plan.Cursor, plan.Entries.Count - plan.Cursor);
                    foreach (long key in plan.Checkpoints.Keys.Where(key => key > plan.BasePosition + plan.Cursor).ToArray()) plan.Checkpoints.Remove(key);
                    plan.Entries.Add(new Entry(delta, nextState, Budget.NextSequence(), leases)); plan.Cursor++;
                    long position = plan.BasePosition + plan.Cursor;
                    if (position % 50 == 0) plan.Checkpoints[position] = HistoryPacking.Pack(adapter.Encode(candidate, cancellation), cancellation);
                    cancellation.ThrowIfCancellationRequested(); notify = Budget.Accept(this, plan, preserveNewest: true, cancellation: cancellation); accepted = true;
                    current = candidate; revision = nextRevision;
                }
                finally { if (!accepted) leases.Dispose(); }
            }
            HistoryBudget.Notify(notify); return true;
        }
        public bool Undo() => MoveOne(false);
        public bool Redo() => MoveOne(true);
        private bool MoveOne(bool forward)
        {
            lock (Budget.Gate)
            {
                RequireEditable(); if (forward ? state.Cursor == state.Entries.Count : state.Cursor == 0) return false;
                var entry = state.Entries[forward ? state.Cursor : state.Cursor - 1]; var candidate = entry.Delta.Apply(current, forward); validate(candidate);
                int cursor = state.Cursor + (forward ? 1 : -1); long next = checked(revision + 1);
                current = adapter.Stamp(candidate, next, state.StateAt(cursor)); revision = next; state = (PlanState)state.Clone(); state.Cursor = cursor;
            }
            Notify(); return true;
        }
        public async Task<bool> JumpAsync(long position, CancellationToken cancellation = default)
        {
            PlanState captured; long expectedGeneration, expectedRevision; CancellationTokenSource source;
            lock (Budget.Gate)
            {
                RequireEditable(); cancellation.ThrowIfCancellationRequested();
                if (position < state.BasePosition || position > state.BasePosition + state.Entries.Count) throw new ArgumentOutOfRangeException(nameof(position));
                if (position == state.BasePosition + state.Cursor) return false;
                captured = (PlanState)state.Clone(); expectedGeneration = generation; expectedRevision = revision;
                operation = source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            }
            Notify();
            try
            {
                T candidate = await Task.Run(() => captured.Rebuild(position, source.Token), source.Token);
                lock (Budget.Gate)
                {
                    source.Token.ThrowIfCancellationRequested();
                    if (disposed || expectedGeneration != generation || expectedRevision != revision || operation != source) throw new OperationCanceledException("History context was replaced.", source.Token);
                    validate(candidate); source.Token.ThrowIfCancellationRequested(); long next = checked(revision + 1); int cursor = checked((int)(position - state.BasePosition));
                    current = adapter.Stamp(candidate, next, state.StateAt(cursor)); revision = next; state = (PlanState)state.Clone(); state.Cursor = cursor;
                }
                return true;
            }
            finally
            {
                lock (Budget.Gate) { if (operation == source) operation = null; }
                source.Dispose(); Notify();
            }
        }
        public void CancelJump() { lock (Budget.Gate) operation?.Cancel(); }
        public bool MarkSaved(string expectedStateId, long? expectedGeneration = null)
        {
            if (string.IsNullOrWhiteSpace(expectedStateId)) throw new ArgumentException("A saved snapshot identity is required.", nameof(expectedStateId));
            bool clean; lock (Budget.Gate) { RequireAlive(); if (expectedGeneration.HasValue && expectedGeneration.Value != generation) return false; savepoint = expectedStateId; clean = adapter.StateId(current) == expectedStateId; }
            Notify(); return clean;
        }
        public bool MarkSaved(HistorySnapshot<T> snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.HistoryId != HistoryId) throw new InvalidOperationException("Save receipt belongs to another history.");
            return MarkSaved(snapshot.StateId, snapshot.Generation);
        }
        public void Replace(T document, bool saved = true)
        {
            validate(document); LeaseSet leases = Acquire(document); bool accepted = false; IHistoryOwner[] notify;
            try
            {
                byte[] packed = HistoryPacking.Pack(adapter.Encode(document, default));
                lock (Budget.Gate)
                {
                    RequireAlive(); long next = checked(Math.Max(revision, adapter.Revision(document)) + 1); long nextGeneration = checked(generation + 1);
                    var replacement = adapter.Stamp(document, next, adapter.StateId(document));
                    var plan = new PlanState(adapter, packed, adapter.StateId(document), leases);
                    notify = Budget.Accept(this, plan); accepted = true;
                    operation?.Cancel(); operation = null; generation = nextGeneration; revision = next; current = replacement; savepoint = saved ? adapter.StateId(document) : null;
                }
            }
            finally { if (!accepted) leases.Dispose(); }
            HistoryBudget.Notify(notify);
        }
        public HistorySnapshot<T> CaptureSnapshot()
        { lock (Budget.Gate) { RequireAlive(); return new HistorySnapshot<T>(current, revision, generation, adapter.StateId(current), HistoryId, state.LeasesAt(state.Cursor).Retain()); } }
        /// <summary>Reacquires an immutable resource still owned by this timeline after a cache owner has released it.</summary>
        public ImportedSceneLease TryRetainResource(ModelScene scene)
        {
            if (scene == null) return null;
            lock (Budget.Gate)
            {
                if (disposed) return null;
                foreach (var group in state.LeaseSets()) foreach (var lease in group.Values) if (ReferenceEquals(lease.Scene, scene)) return lease.Retain();
                return null;
            }
        }
        private LeaseSet Acquire(T value)
        {
            var values = acquire?.Invoke(value); if (values != null && values.Any(item => item == null)) { foreach (var item in values) item?.Dispose(); throw new InvalidOperationException("A model lease cannot be null."); }
            return new LeaseSet(values);
        }
        private void RequireAlive() { if (disposed) throw new ObjectDisposedException(nameof(DocumentHistory<T>)); }
        private void RequireEditable() { RequireAlive(); if (operation != null) throw new InvalidOperationException("History reconstruction is in progress."); }
        HistoryPlan IHistoryOwner.Plan => state;
        bool IHistoryOwner.Busy => operation != null;
        void IHistoryOwner.Accept(HistoryPlan plan)
        {
            var next = (PlanState)plan; var oldLeases = state.LeaseSets().ToArray(); var retained = new HashSet<LeaseSet>(next.LeaseSets()); state = next;
            foreach (var leases in oldLeases) if (!retained.Contains(leases)) leases.Dispose();
        }
        void IHistoryOwner.Notify() => Notify();
        private void Notify()
        {
            var callbacks = Changed; if (callbacks == null) return;
            foreach (Action callback in callbacks.GetInvocationList()) try { callback(); } catch (Exception error) { LastNotificationError = error; }
        }
        public void Dispose()
        {
            lock (Budget.Gate)
            {
                if (disposed) return; disposed = true; operation?.Cancel(); operation = null; generation++;
                Budget.Remove(this); foreach (var leases in state.LeaseSets()) leases.Dispose();
                Changed = null;
            }
        }
        private sealed class Entry
        {
            internal readonly DocumentDelta<T> Delta; internal readonly string StateId; internal readonly long Sequence; internal readonly LeaseSet Leases;
            internal Entry(DocumentDelta<T> delta, string stateId, long sequence, LeaseSet leases) { Delta = delta; StateId = stateId; Sequence = sequence; Leases = leases; }
        }
        private sealed class PlanState : HistoryPlan
        {
            private readonly DocumentAdapter<T> adapter;
            internal byte[] Baseline; internal string BaseStateId; internal LeaseSet BaseLeases; internal long BasePosition; internal int Cursor;
            internal List<Entry> Entries = new List<Entry>(); internal Dictionary<long, byte[]> Checkpoints = new Dictionary<long, byte[]>();
            internal PlanState(DocumentAdapter<T> adapter, byte[] baseline, string stateId, LeaseSet leases)
            { this.adapter = adapter; Baseline = baseline; BaseStateId = stateId; BaseLeases = leases; }
            internal override int Steps => Entries.Count;
            internal override long Bytes => Baseline.LongLength + BaseLeases.Bytes + 256 + Entries.Sum(entry => entry.Delta.RetainedBytes + entry.Leases.Bytes + 128) + Checkpoints.Values.Sum(bytes => bytes.LongLength + 64);
            internal override bool CanTrim => Cursor > 0;
            internal override long FirstSequence => Entries.Count == 0 ? long.MaxValue : Entries[0].Sequence;
            internal override HistoryPlan Clone() => new PlanState(adapter, Baseline, BaseStateId, BaseLeases)
            { BasePosition = BasePosition, Cursor = Cursor, Entries = new List<Entry>(Entries), Checkpoints = new Dictionary<long, byte[]>(Checkpoints) };
            internal override bool EquivalentTo(HistoryPlan other)
            {
                var value = (PlanState)other;
                return Baseline == value.Baseline && BasePosition == value.BasePosition && Cursor == value.Cursor && Entries.SequenceEqual(value.Entries) &&
                    Checkpoints.Count == value.Checkpoints.Count && Checkpoints.All(pair => value.Checkpoints.TryGetValue(pair.Key, out var bytes) && bytes == pair.Value);
            }
            internal IEnumerable<LeaseSet> LeaseSets() => new[] { BaseLeases }.Concat(Entries.Select(entry => entry.Leases)).Distinct();
            internal string StateAt(int cursor) => cursor == 0 ? BaseStateId : Entries[cursor - 1].StateId;
            internal LeaseSet LeasesAt(int cursor) => cursor == 0 ? BaseLeases : Entries[cursor - 1].Leases;
            internal override void DropCheckpoints() => Checkpoints.Clear();
            internal override void TrimFirst()
            {
                if (!CanTrim) throw new InvalidOperationException("Cannot trim beyond the current history state.");
                T baseline = adapter.Decode(HistoryPacking.Unpack(Baseline), default); var entry = Entries[0];
                T next = entry.Delta.Apply(baseline, true); next = adapter.Stamp(next, adapter.Revision(next), entry.StateId);
                Baseline = HistoryPacking.Pack(adapter.Encode(next, default)); BaseStateId = entry.StateId; BaseLeases = entry.Leases;
                BasePosition++; Cursor--; Entries.RemoveAt(0);
                foreach (long key in Checkpoints.Keys.Where(key => key <= BasePosition).ToArray()) Checkpoints.Remove(key);
            }
            internal T Rebuild(long position, CancellationToken cancellation)
            {
                long start = Checkpoints.Keys.Where(key => key <= position).DefaultIfEmpty(BasePosition).Max();
                byte[] packed = start == BasePosition ? Baseline : Checkpoints[start];
                T result = adapter.Decode(HistoryPacking.Unpack(packed, cancellation), cancellation);
                for (long cursor = start; cursor < position; cursor++) { cancellation.ThrowIfCancellationRequested(); result = Entries[checked((int)(cursor - BasePosition))].Delta.Apply(result, true, cancellation); }
                return result;
            }
        }
    }
}
