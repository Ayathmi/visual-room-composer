using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace VisualRoomComposer.Infrastructure.Persistence
{
    internal static class StorageJson
    {
        internal const int MaxBytes = 64 * 1024 * 1024;
        internal static readonly JsonSerializerOptions Options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 128 };
        internal static byte[] Encode(object value, CancellationToken cancellation = default)
        {
            using (var stream = new BoundedStream(MaxBytes, cancellation))
            { using (var writer = new Utf8JsonWriter(stream)) JsonSerializer.Serialize(writer, value, Options); return stream.ToArray(); }
        }
        internal static byte[] Encode(Action<Utf8JsonWriter> write, CancellationToken cancellation = default)
        {
            using (var stream = new BoundedStream(MaxBytes, cancellation))
            { using (var writer = new Utf8JsonWriter(stream)) write(writer); return stream.ToArray(); }
        }
        internal static JsonDocument Parse(byte[] bytes, CancellationToken cancellation = default)
        {
            if (bytes == null || bytes.Length > MaxBytes) throw new StorageException("LimitExceeded", "JSON exceeds the 64 MiB persistence budget.");
            try
            {
                // Reject ambiguous keys before DTO defaults or duplicate-property last-wins rules can run.
                var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 128 });
                var objects = new Stack<HashSet<string>>(); long tokens = 0;
                while (reader.Read())
                {
                    if ((tokens & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                    if (++tokens > 8000000) throw new StorageException("LimitExceeded", "JSON token budget exceeded.");
                    if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new HashSet<string>(StringComparer.Ordinal));
                    else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                    else if (reader.TokenType == JsonTokenType.PropertyName)
                    { string key = reader.GetString(); if (key.StartsWith("$", StringComparison.Ordinal) || !objects.Peek().Add(key)) throw new StorageException("InvalidSchema", "Duplicate keys and runtime type metadata are forbidden."); }
                    else if (reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length > 32 * 1024 * 1024) throw new StorageException("LimitExceeded", "JSON string budget exceeded.");
                }
                return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
            }
            catch (JsonException error) { throw new StorageException("InvalidSchema", "Malformed strict JSON.", error); }
        }
        internal static void Shape(JsonElement value, params string[] fields)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new StorageException("InvalidSchema", "Expected an object.");
            var required = new HashSet<string>(fields, StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) if (!required.Remove(property.Name)) throw new StorageException("InvalidSchema", "Unknown field: " + property.Name);
            if (required.Count != 0) throw new StorageException("InvalidSchema", "Missing field: " + required.First());
        }
        internal static JsonElement Get(JsonElement value, string field) => value.GetProperty(field);
        internal static string S(JsonElement value, string field) { var p = Get(value, field); return p.ValueKind == JsonValueKind.Null ? null : p.GetString(); }
        internal static int I(JsonElement value, string field) => Get(value, field).GetInt32();
        internal static long L(JsonElement value, string field) => Get(value, field).GetInt64();
        internal static double D(JsonElement value, string field) { double d = Get(value, field).GetDouble(); if (double.IsInfinity(d) || double.IsNaN(d)) throw new StorageException("InvalidSchema", "Nonfinite number."); return d; }
        internal static bool B(JsonElement value, string field) => Get(value, field).GetBoolean();
        internal static T EnumValue<T>(JsonElement value, string field) where T : struct
        { int n = I(value, field); if (!Enum.IsDefined(typeof(T), n)) throw new StorageException("InvalidSchema", "Unknown enum value."); return (T)Enum.ToObject(typeof(T), n); }
        internal static JsonElement.ArrayEnumerator A(JsonElement value, string field, int maximum = 2000000)
        { var a = Get(value, field); if (a.GetArrayLength() > maximum) throw new StorageException("LimitExceeded", "JSON array budget exceeded."); return a.EnumerateArray(); }
        internal static byte[] Raw(JsonElement value) => System.Text.Encoding.UTF8.GetBytes(value.GetRawText());
        internal static void Embed(Utf8JsonWriter writer, byte[] json)
        { using (var document = Parse(json)) document.RootElement.WriteTo(writer); }
        private sealed class BoundedStream : MemoryStream
        {
            private readonly int maximum; private readonly CancellationToken cancellation;
            internal BoundedStream(int maximum, CancellationToken cancellation) { this.maximum = maximum; this.cancellation = cancellation; }
            public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
            public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
            public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
            private void Check(int count) { cancellation.ThrowIfCancellationRequested(); if (Position + count > maximum) throw new StorageException("LimitExceeded", "Encoded JSON exceeds the persistence budget."); }
        }
    }
}
