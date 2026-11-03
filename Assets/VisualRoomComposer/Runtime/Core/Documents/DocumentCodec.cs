using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace VisualRoomComposer.Core.Documents
{
    /// <summary>Bounded explicit authoring JSON. Math values use arrays and never depend on readonly-struct reflection.</summary>
    public static class DocumentCodec
    {
        public const int DefaultMaximumBytes = 128 * 1024 * 1024;
        private static readonly JsonSerializerOptions Options = CreateOptions();
        public static byte[] EncodeRoom(RoomDocument document, int maximumBytes = DefaultMaximumBytes, CancellationToken cancellation = default)
        { DocumentValidation.Validate(document, cancellation: cancellation); return EncodeValue(document, maximumBytes, cancellation); }
        public static RoomDocument DecodeRoom(byte[] bytes, int maximumBytes = DefaultMaximumBytes, int maximumDepth = 128, CancellationToken cancellation = default)
        {
            var value = DecodeRoot<RoomDocument>(bytes, RoomDocument.FormatName, maximumBytes, maximumDepth, cancellation);
            DocumentValidation.Validate(value, cancellation: cancellation); return value;
        }
        public static byte[] EncodeLibrary(LibraryDraft document, int maximumBytes = DefaultMaximumBytes, CancellationToken cancellation = default)
        { DocumentValidation.Validate(document, cancellation); return EncodeValue(document, maximumBytes, cancellation); }
        public static LibraryDraft DecodeLibrary(byte[] bytes, int maximumBytes = DefaultMaximumBytes, int maximumDepth = 128, CancellationToken cancellation = default)
        {
            var value = DecodeRoot<LibraryDraft>(bytes, LibraryDraft.FormatName, maximumBytes, maximumDepth, cancellation);
            DocumentValidation.Validate(value, cancellation); return value;
        }
        public static byte[] EncodeDefinition(LibraryDefinition definition, int maximumBytes = DefaultMaximumBytes, CancellationToken cancellation = default)
        { DocumentValidation.ValidateDefinition(definition); return EncodeValue(definition, maximumBytes, cancellation); }
        public static LibraryDefinition DecodeDefinition(byte[] bytes, int maximumBytes = DefaultMaximumBytes, int maximumDepth = 128, CancellationToken cancellation = default)
        {
            var value = (LibraryDefinition)DecodeValue(bytes, typeof(LibraryDefinition), maximumBytes, maximumDepth, cancellation);
            DocumentValidation.ValidateDefinition(value); return value;
        }
        public static byte[] EncodeAsset(AssetReference asset, int maximumBytes = DefaultMaximumBytes, CancellationToken cancellation = default)
        { DocumentValidation.ValidateAsset(asset); return EncodeValue(asset, maximumBytes, cancellation); }
        public static AssetReference DecodeAsset(byte[] bytes, int maximumBytes = DefaultMaximumBytes, int maximumDepth = 128, CancellationToken cancellation = default)
        {
            var value = (AssetReference)DecodeValue(bytes, typeof(AssetReference), maximumBytes, maximumDepth, cancellation);
            DocumentValidation.ValidateAsset(value); return value;
        }

        internal static byte[] EncodeValue(object value, int maximumBytes = DefaultMaximumBytes, CancellationToken cancellation = default)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            cancellation.ThrowIfCancellationRequested();
            using (var stream = new LimitedOutput(maximumBytes, cancellation))
            {
                using (var writer = new Utf8JsonWriter(stream)) JsonSerializer.Serialize(writer, value, value.GetType(), Options);
                cancellation.ThrowIfCancellationRequested(); return stream.ToArray();
            }
        }
        internal static object DecodeValue(byte[] bytes, Type type, int maximumBytes = DefaultMaximumBytes, int maximumDepth = 128, CancellationToken cancellation = default)
        {
            using (var document = Parse(bytes, maximumBytes, maximumDepth, cancellation))
            {
                ValidateShape(document.RootElement, type, cancellation);
                cancellation.ThrowIfCancellationRequested();
                return JsonSerializer.Deserialize(bytes, type, OptionsForDepth(maximumDepth)) ?? throw new InvalidDataException("Authoring value cannot be null.");
            }
        }
        private static T DecodeRoot<T>(byte[] bytes, string format, int maximumBytes, int maximumDepth, CancellationToken cancellation)
        {
            using (var document = Parse(bytes, maximumBytes, maximumDepth, cancellation))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var tag) || tag.GetString() != format ||
                    !root.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out int number) || number != 1)
                    throw new InvalidDataException("Unsupported authoring format or schema version.");
                if (!root.TryGetProperty("stateId", out var state) || state.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(state.GetString())) throw new InvalidDataException("A state identity is required.");
                if (typeof(T) == typeof(RoomDocument) && (!root.TryGetProperty("unit", out var unit) || unit.GetString() != "meter")) throw new InvalidDataException("Room documents use meters.");
                ValidateShape(root, typeof(T), cancellation); cancellation.ThrowIfCancellationRequested();
                return JsonSerializer.Deserialize<T>(bytes, OptionsForDepth(maximumDepth));
            }
        }
        private static JsonDocument Parse(byte[] bytes, int maximumBytes, int maximumDepth, CancellationToken cancellation)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (maximumBytes < 1 || maximumDepth < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (bytes.Length > maximumBytes) throw new InvalidDataException("Authoring JSON byte budget exceeded.");
            cancellation.ThrowIfCancellationRequested();
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = maximumDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        private static void ValidateShape(JsonElement value, Type type, CancellationToken cancellation, bool allowNull = false)
        {
            cancellation.ThrowIfCancellationRequested();
            if (value.ValueKind == JsonValueKind.Null) { if (!allowNull) throw new InvalidDataException("A required authoring value cannot be null."); return; }
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(Double2) || type == typeof(Double3) || type == typeof(Double4) || type == typeof(Matrix4d) || type == typeof(GridPosition) || type == typeof(GridCell2)) return;
            if (type.IsEnum || type == typeof(string) || type.IsPrimitive || type == typeof(decimal)) return;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            {
                if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Expected an authoring array.");
                if (value.GetArrayLength() > DocumentValidation.MaximumEntities) throw new InvalidDataException("Authoring collection exceeds its entity budget.");
                foreach (var item in value.EnumerateArray()) ValidateShape(item, type.GetGenericArguments()[0], cancellation);
                return;
            }
            if (type.Namespace != typeof(RoomDocument).Namespace && type != typeof(History.HistoryScalar)) throw new InvalidDataException("Unknown authoring record type.");
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected an authoring object.");
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                .ToDictionary(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate authoring property: " + property.Name);
                if (!properties.TryGetValue(property.Name, out var expected)) throw new InvalidDataException("Unknown authoring property: " + property.Name);
                ValidateShape(property.Value, expected.PropertyType, cancellation, NullableProperty(type, expected.Name));
            }
            if (seen.Count != properties.Count) throw new InvalidDataException("Authoring record is missing required fields.");
        }
        private static bool NullableProperty(Type type, string name) =>
            type == typeof(LibraryDefinition) && (name == nameof(LibraryDefinition.Hitbox) || name == nameof(LibraryDefinition.ModelReference)) ||
            type == typeof(WallBoundary) && name == nameof(WallBoundary.SlotId) ||
            type == typeof(PlacementAttachment) && name == nameof(PlacementAttachment.SlotId) ||
            type == typeof(RoomPlacement) && (name == nameof(RoomPlacement.RoomId) || name == nameof(RoomPlacement.GroupId) || name == nameof(RoomPlacement.Attachment)) ||
            type == typeof(RoomGroup) && (name == nameof(RoomGroup.ParentGroupId) || name == nameof(RoomGroup.RoomId)) ||
            type == typeof(LibraryDraft) && name == nameof(LibraryDraft.BaseDefinitionRef) ||
            type == typeof(AssetDependency) && name == nameof(AssetDependency.SourceReference);
        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, MaxDepth = 128 };
            options.Converters.Add(new JsonStringEnumConverter()); options.Converters.Add(new Vector2Converter()); options.Converters.Add(new Vector3Converter());
            options.Converters.Add(new Vector4Converter()); options.Converters.Add(new MatrixConverter()); options.Converters.Add(new GridConverter()); options.Converters.Add(new CellConverter());
            return options;
        }
        private static JsonSerializerOptions OptionsForDepth(int maximumDepth) => maximumDepth == 128 ? Options : new JsonSerializerOptions(Options) { MaxDepth = maximumDepth };
        private static double[] Numbers(ref Utf8JsonReader reader, int count)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Math values must be arrays.");
            var values = new double[count];
            for (int i = 0; i < count; i++) if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out values[i]) || !Numeric.IsFinite(values[i])) throw new JsonException("A finite math component is required.");
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Unexpected math array length.");
            return values;
        }
        private static int Integer(double value)
        { if (value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue) throw new JsonException("Grid coordinates must be integers."); return (int)value; }
        private static void Numbers(Utf8JsonWriter writer, params double[] values)
        { writer.WriteStartArray(); foreach (double value in values) { Numeric.RequireFinite(value, nameof(values)); writer.WriteNumberValue(value); } writer.WriteEndArray(); }
        private sealed class Vector2Converter : JsonConverter<Double2>
        { public override Double2 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) { var v = Numbers(ref reader, 2); return new Double2(v[0], v[1]); } public override void Write(Utf8JsonWriter writer, Double2 value, JsonSerializerOptions options) => Numbers(writer, value.X, value.Y); }
        private sealed class Vector3Converter : JsonConverter<Double3>
        { public override Double3 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) { var v = Numbers(ref reader, 3); return new Double3(v[0], v[1], v[2]); } public override void Write(Utf8JsonWriter writer, Double3 value, JsonSerializerOptions options) => Numbers(writer, value.X, value.Y, value.Z); }
        private sealed class Vector4Converter : JsonConverter<Double4>
        { public override Double4 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) { var v = Numbers(ref reader, 4); return new Double4(v[0], v[1], v[2], v[3]); } public override void Write(Utf8JsonWriter writer, Double4 value, JsonSerializerOptions options) => Numbers(writer, value.X, value.Y, value.Z, value.W); }
        private sealed class MatrixConverter : JsonConverter<Matrix4d>
        { public override Matrix4d Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Matrix4d.FromRowMajor(Numbers(ref reader, 16)); public override void Write(Utf8JsonWriter writer, Matrix4d value, JsonSerializerOptions options) => Numbers(writer, value.ToRowMajorArray()); }
        private sealed class GridConverter : JsonConverter<GridPosition>
        { public override GridPosition Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) { var v = Numbers(ref reader, 3); return new GridPosition(Integer(v[0]), Integer(v[1]), Integer(v[2])); } public override void Write(Utf8JsonWriter writer, GridPosition value, JsonSerializerOptions options) => Numbers(writer, value.X, value.Y, value.Z); }
        private sealed class CellConverter : JsonConverter<GridCell2>
        { public override GridCell2 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) { var v = Numbers(ref reader, 2); return new GridCell2(Integer(v[0]), Integer(v[1])); } public override void Write(Utf8JsonWriter writer, GridCell2 value, JsonSerializerOptions options) => Numbers(writer, value.X, value.Z); }
        private sealed class LimitedOutput : MemoryStream
        {
            private readonly int maximum; private readonly CancellationToken cancellation;
            internal LimitedOutput(int maximum, CancellationToken cancellation) { if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum)); this.maximum = maximum; this.cancellation = cancellation; }
            public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
            public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
            private void Check(int count) { cancellation.ThrowIfCancellationRequested(); if (Length + count > maximum) throw new InvalidDataException("Authoring JSON byte budget exceeded."); }
        }
    }
}
