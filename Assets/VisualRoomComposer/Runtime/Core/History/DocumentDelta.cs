using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using VisualRoomComposer.Core.Documents;

namespace VisualRoomComposer.Core.History
{
    internal sealed class HistoryScalar
    {
        public string Id { get; } public string Value { get; }
        [JsonConstructor] public HistoryScalar(string id, string value) { Id = id; Value = value; }
    }

    /// <summary>A bounded immutable change of stable records and scalar fields, with explicit preconditions.</summary>
    public sealed class DocumentDelta<T> where T : class
    {
        private readonly byte[] packed;
        internal readonly DocumentAdapter<T> Adapter;
        public string Name { get; } public string ExpectedStateId { get; } public long ExpectedRevision { get; }
        public string DocumentIdentity { get; } public int ChangeCount { get; }
        public long RetainedBytes => packed.LongLength + 256 + Name.Length * 2L;
        internal DocumentDelta(string name, T before, T after, DocumentAdapter<T> adapter, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 4096) throw new ArgumentException("A bounded command name is required.", nameof(name));
            Name = name; Adapter = adapter; ExpectedStateId = adapter.StateId(before); ExpectedRevision = adapter.Revision(before);
            DocumentIdentity = adapter.Identity(before);
            if (DocumentIdentity != adapter.Identity(after)) throw new InvalidOperationException("An editing command cannot replace document identity.");
            var changes = new List<Change>(); var first = adapter.Sections(before); var second = adapter.Sections(after);
            foreach (var section in first.Keys)
            {
                cancellation.ThrowIfCancellationRequested();
                var left = first[section].Select((value, index) => new Indexed(value, index)).ToDictionary(value => adapter.Id(section, value.Value), StringComparer.Ordinal);
                var right = second[section].Select((value, index) => new Indexed(value, index)).ToDictionary(value => adapter.Id(section, value.Value), StringComparer.Ordinal);
                foreach (string id in left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal))
                {
                    cancellation.ThrowIfCancellationRequested(); left.TryGetValue(id, out var old); right.TryGetValue(id, out var next);
                    if (old != null && next != null && old.Index == next.Index && ReferenceEquals(old.Value, next.Value)) continue;
                    var oldBytes = old == null ? null : DocumentCodec.EncodeValue(old.Value, cancellation: cancellation);
                    var nextBytes = next == null ? null : DocumentCodec.EncodeValue(next.Value, cancellation: cancellation);
                    if (old != null && next != null && old.Index == next.Index && Equal(oldBytes, nextBytes)) continue;
                    changes.Add(new Change(section, id, old?.Index ?? -1, next?.Index ?? -1, oldBytes, nextBytes));
                }
            }
            ChangeCount = changes.Count;
            using (var memory = new MemoryStream())
            {
                using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
                {
                    writer.Write(changes.Count);
                    foreach (var change in changes)
                    {
                        cancellation.ThrowIfCancellationRequested(); writer.Write(change.Section); writer.Write(change.Id); writer.Write(change.BeforeIndex); writer.Write(change.AfterIndex);
                        WriteBytes(writer, change.Before); WriteBytes(writer, change.After);
                        if (memory.Length > DocumentCodec.DefaultMaximumBytes) throw new InvalidDataException("Command payload exceeds its budget.");
                    }
                }
                packed = HistoryPacking.Pack(memory.ToArray(), cancellation);
            }
        }
        internal T Apply(T source, bool forward, CancellationToken cancellation = default)
        {
            if (Adapter.Identity(source) != DocumentIdentity) throw new InvalidOperationException("History document identity changed.");
            var sections = Adapter.Sections(source); var changes = Read(cancellation);
            foreach (var change in changes)
            {
                cancellation.ThrowIfCancellationRequested(); int expected = forward ? change.BeforeIndex : change.AfterIndex;
                byte[] expectedBytes = forward ? change.Before : change.After;
                var records = sections[change.Section]; int actual = Array.FindIndex(records, value => Adapter.Id(change.Section, value) == change.Id);
                if (expected != actual || actual >= 0 && !Equal(expectedBytes, DocumentCodec.EncodeValue(records[actual], cancellation: cancellation)))
                    throw new InvalidOperationException("Atomic command precondition failed: " + change.Section + "/" + change.Id);
            }
            foreach (var group in changes.GroupBy(change => change.Section))
            {
                var ids = new HashSet<string>(group.Select(change => change.Id), StringComparer.Ordinal);
                var records = sections[group.Key].Where(value => !ids.Contains(Adapter.Id(group.Key, value))).ToList();
                foreach (var change in group.Where(change => (forward ? change.AfterIndex : change.BeforeIndex) >= 0)
                    .OrderBy(change => forward ? change.AfterIndex : change.BeforeIndex))
                {
                    cancellation.ThrowIfCancellationRequested(); int index = forward ? change.AfterIndex : change.BeforeIndex;
                    if (index > records.Count) throw new InvalidDataException("Invalid history record order.");
                    records.Insert(index, DocumentCodec.DecodeValue(forward ? change.After : change.Before, Adapter.RecordType(group.Key), cancellation: cancellation));
                }
                sections[group.Key] = records.ToArray();
            }
            return Adapter.Build(source, sections);
        }
        private Change[] Read(CancellationToken cancellation)
        {
            byte[] bytes = HistoryPacking.Unpack(packed, cancellation);
            using (var memory = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(memory, Encoding.UTF8))
            {
                int count = reader.ReadInt32(); if (count != ChangeCount || count < 0 || count > DocumentValidation.MaximumEntities * 8) throw new InvalidDataException("Invalid command change count.");
                var result = new Change[count];
                for (int i = 0; i < count; i++) { cancellation.ThrowIfCancellationRequested(); result[i] = new Change(reader.ReadString(), reader.ReadString(), reader.ReadInt32(), reader.ReadInt32(), ReadBytes(reader), ReadBytes(reader)); }
                if (memory.Position != memory.Length) throw new InvalidDataException("Trailing command bytes."); return result;
            }
        }
        private static byte[] ReadBytes(BinaryReader reader)
        {
            int count = reader.ReadInt32(); if (count == -1) return null;
            if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid command record size.");
            var bytes = reader.ReadBytes(count); if (bytes.Length != count) throw new EndOfStreamException(); return bytes;
        }
        private static void WriteBytes(BinaryWriter writer, byte[] bytes) { writer.Write(bytes?.Length ?? -1); if (bytes != null) writer.Write(bytes); }
        private static bool Equal(byte[] first, byte[] second) => first == second || first != null && second != null && first.AsSpan().SequenceEqual(second);
        private sealed class Indexed { internal readonly object Value; internal readonly int Index; internal Indexed(object value, int index) { Value = value; Index = index; } }
        private sealed class Change
        {
            internal readonly string Section, Id; internal readonly int BeforeIndex, AfterIndex; internal readonly byte[] Before, After;
            internal Change(string section, string id, int beforeIndex, int afterIndex, byte[] before, byte[] after)
            { Section = section; Id = id; BeforeIndex = beforeIndex; AfterIndex = afterIndex; Before = before; After = after; }
        }
    }

    internal static class HistoryPacking
    {
        internal static byte[] Pack(byte[] bytes, CancellationToken cancellation = default)
        {
            if (bytes.Length > DocumentCodec.DefaultMaximumBytes) throw new InvalidDataException("History snapshot exceeds its budget.");
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
                    for (int offset = 0; offset < bytes.Length; offset += 65536) { cancellation.ThrowIfCancellationRequested(); gzip.Write(bytes, offset, Math.Min(65536, bytes.Length - offset)); }
                return output.ToArray();
            }
        }
        internal static byte[] Unpack(byte[] packed, CancellationToken cancellation = default)
        {
            using (var input = new MemoryStream(packed, false)) using (var gzip = new GZipStream(input, CompressionMode.Decompress)) using (var output = new MemoryStream())
            {
                var buffer = new byte[65536]; int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                { cancellation.ThrowIfCancellationRequested(); if (output.Length + read > DocumentCodec.DefaultMaximumBytes) throw new InvalidDataException("Expanded history exceeds its budget."); output.Write(buffer, 0, read); }
                cancellation.ThrowIfCancellationRequested(); return output.ToArray();
            }
        }
    }

    internal abstract class DocumentAdapter<T> where T : class
    {
        internal abstract string Identity(T document); internal abstract string StateId(T document); internal abstract long Revision(T document);
        internal abstract T Stamp(T document, long revision, string stateId);
        internal abstract Dictionary<string, object[]> Sections(T document); internal abstract Type RecordType(string section);
        internal abstract string Id(string section, object value); internal abstract T Build(T source, Dictionary<string, object[]> sections);
        internal abstract byte[] Encode(T document, CancellationToken cancellation); internal abstract T Decode(byte[] bytes, CancellationToken cancellation);
        internal static DocumentAdapter<T> Create()
        {
            if (typeof(T) == typeof(RoomDocument)) return (DocumentAdapter<T>)(object)new RoomAdapter();
            if (typeof(T) == typeof(LibraryDraft)) return (DocumentAdapter<T>)(object)new LibraryAdapter();
            throw new NotSupportedException("History supports room documents and library drafts.");
        }
        protected static object[] Objects<U>(IReadOnlyList<U> values) => values.Cast<object>().ToArray();
        protected static string Scalar(Dictionary<string, object[]> sections, string id) => sections["fields"].Cast<HistoryScalar>().Single(value => value.Id == id).Value;
    }
    internal sealed class RoomAdapter : DocumentAdapter<RoomDocument>
    {
        internal override string Identity(RoomDocument value) => value.ProjectId;
        internal override string StateId(RoomDocument value) => value.StateId;
        internal override long Revision(RoomDocument value) => value.Revision;
        internal override RoomDocument Stamp(RoomDocument value, long revision, string stateId) => value.WithState(revision, stateId);
        internal override Dictionary<string, object[]> Sections(RoomDocument value) => new Dictionary<string, object[]>(StringComparer.Ordinal)
        { ["rooms"] = Objects(value.Rooms), ["groups"] = Objects(value.Groups), ["placements"] = Objects(value.Placements), ["pins"] = Objects(value.PinnedDependencies),
            ["fields"] = new object[] { new HistoryScalar("title", value.Title), new HistoryScalar("description", value.Description), new HistoryScalar("grid", value.ShowGrid ? "1" : "0") } };
        internal override Type RecordType(string section) => section == "rooms" ? typeof(Room) : section == "groups" ? typeof(RoomGroup) : section == "placements" ? typeof(RoomPlacement) : section == "pins" ? typeof(PinnedReference) : section == "fields" ? typeof(HistoryScalar) : throw new InvalidDataException("Unknown history section.");
        internal override string Id(string section, object value) => section == "rooms" ? ((Room)value).Id : section == "groups" ? ((RoomGroup)value).Id : section == "placements" ? ((RoomPlacement)value).Id : section == "pins" ? ((PinnedReference)value).Key : ((HistoryScalar)value).Id;
        internal override RoomDocument Build(RoomDocument source, Dictionary<string, object[]> sections) => source.With(sections["rooms"].Cast<Room>().ToArray(), sections["groups"].Cast<RoomGroup>().ToArray(), sections["placements"].Cast<RoomPlacement>().ToArray(), sections["pins"].Cast<PinnedReference>().ToArray(), Scalar(sections, "title"), Scalar(sections, "description"), Scalar(sections, "grid") == "1");
        internal override byte[] Encode(RoomDocument value, CancellationToken cancellation) => DocumentCodec.EncodeRoom(value, cancellation: cancellation);
        internal override RoomDocument Decode(byte[] bytes, CancellationToken cancellation) => DocumentCodec.DecodeRoom(bytes, cancellation: cancellation);
    }
    internal sealed class LibraryAdapter : DocumentAdapter<LibraryDraft>
    {
        internal override string Identity(LibraryDraft value) => value.DraftId + "/" + value.RepositoryId + "/" + value.BaseDefinitionRef?.Key;
        internal override string StateId(LibraryDraft value) => value.StateId;
        internal override long Revision(LibraryDraft value) => value.Revision;
        internal override LibraryDraft Stamp(LibraryDraft value, long revision, string stateId) => value.WithState(revision, stateId);
        internal override Dictionary<string, object[]> Sections(LibraryDraft value) => new Dictionary<string, object[]>(StringComparer.Ordinal)
        { ["definitions"] = Objects(value.Definitions), ["assets"] = Objects(value.Assets), ["fields"] = new object[] { new HistoryScalar("name", value.Name) } };
        internal override Type RecordType(string section) => section == "definitions" ? typeof(LibraryDefinition) : section == "assets" ? typeof(AssetReference) : section == "fields" ? typeof(HistoryScalar) : throw new InvalidDataException("Unknown history section.");
        internal override string Id(string section, object value) => section == "definitions" ? ((LibraryDefinition)value).Id : section == "assets" ? ((AssetReference)value).Reference.Key : ((HistoryScalar)value).Id;
        internal override LibraryDraft Build(LibraryDraft source, Dictionary<string, object[]> sections) => source.With(sections["definitions"].Cast<LibraryDefinition>().ToArray(), sections["assets"].Cast<AssetReference>().ToArray(), Scalar(sections, "name"));
        internal override byte[] Encode(LibraryDraft value, CancellationToken cancellation) => DocumentCodec.EncodeLibrary(value, cancellation: cancellation);
        internal override LibraryDraft Decode(byte[] bytes, CancellationToken cancellation) => DocumentCodec.DecodeLibrary(bytes, cancellation: cancellation);
    }
}
