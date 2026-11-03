using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace VisualRoomComposer.Formats.Fbx
{
    /// <summary>Explicit limits for the first independent FBX feasibility implementation.</summary>
    public sealed class FbxReadOptions
    {
        public long MaximumSourceBytes { get; set; } = 256L * 1024 * 1024;
        public long MaximumDecodedBytes { get; set; } = 512L * 1024 * 1024;
        public int MaximumElements { get; set; } = 2000000;
        public int MaximumDepth { get; set; } = 128;
        public int MaximumNodes { get; set; } = 100000;
        public int MaximumStringBytes { get; set; } = 1024 * 1024;
        public double TimeoutSeconds { get; set; } = 30;
        public CancellationToken Cancellation { get; set; }
    }

    /// <summary>A controlled format rejection, with a stable code and source position.</summary>
    public sealed class FbxFormatException : IOException
    {
        public string Code { get; }
        public long Offset { get; }
        public FbxFormatException(string code, string message, long offset = -1) : base(message)
        { Code = code; Offset = offset; }
    }

    internal sealed class FbxBudget
    {
        internal readonly FbxReadOptions Options;
        private readonly Stopwatch timer = Stopwatch.StartNew();
        private long allocated;
        private int nodes;
        internal FbxBudget(FbxReadOptions options)
        {
            Options = options ?? new FbxReadOptions();
            if (Options.MaximumSourceBytes < 1 || Options.MaximumDecodedBytes < 1 || Options.MaximumElements < 1 ||
                Options.MaximumDepth < 1 || Options.MaximumDepth > 256 || Options.MaximumNodes < 1 || Options.MaximumStringBytes < 1 ||
                Options.TimeoutSeconds <= 0 || double.IsNaN(Options.TimeoutSeconds) || double.IsInfinity(Options.TimeoutSeconds))
                throw new ArgumentOutOfRangeException(nameof(options));
        }
        internal void Check()
        {
            Options.Cancellation.ThrowIfCancellationRequested();
            if (timer.Elapsed.TotalSeconds > Options.TimeoutSeconds)
                throw new FbxFormatException("fbx.timeout", "FBX processing exceeded its time budget.");
        }
        internal void Allocate(long bytes)
        {
            Check();
            if (bytes < 0 || bytes > Options.MaximumDecodedBytes - allocated)
                throw new FbxFormatException("fbx.budget.bytes", "FBX decoded allocation budget exceeded.");
            allocated += bytes;
        }
        internal void Node(int depth)
        {
            Check();
            if (depth > Options.MaximumDepth || ++nodes > Options.MaximumNodes)
                throw new FbxFormatException("fbx.budget.nodes", "FBX nesting or node budget exceeded.");
            Allocate(256);
        }
    }

    internal sealed class FbxElement
    {
        internal readonly string Name;
        internal readonly List<object> Properties = new List<object>();
        internal readonly List<FbxElement> Children = new List<FbxElement>();
        internal FbxElement(string name, params object[] properties)
        { Name = name; Properties.AddRange(properties); }
        internal FbxElement Child(string name) => Children.Find(n => n.Name == name);
        internal IEnumerable<FbxElement> All(string name) => Children.FindAll(n => n.Name == name);
        internal string Text(int index = 0) => index < Properties.Count ? Convert.ToString(Properties[index], CultureInfo.InvariantCulture) : "";
        internal long Integer(int index = 0) => index < Properties.Count ? Convert.ToInt64(Properties[index], CultureInfo.InvariantCulture) : 0;
        internal double Number(int index = 0) => index < Properties.Count ? Convert.ToDouble(Properties[index], CultureInfo.InvariantCulture) : 0;
        internal double[] Doubles()
        {
            if (Properties.Count == 1 && Properties[0] is double[] doubles) return doubles;
            if (Properties.Count == 1 && Properties[0] is float[] singles) return Array.ConvertAll(singles, x => (double)x);
            var values = Child("a")?.Properties ?? Properties;
            var result = new double[values.Count];
            for (int i = 0; i < result.Length; i++) result[i] = Convert.ToDouble(values[i], CultureInfo.InvariantCulture);
            return result;
        }
        internal int[] Integers()
        {
            if (Properties.Count == 1 && Properties[0] is int[] integers) return integers;
            var values = Child("a")?.Properties ?? Properties;
            var result = new int[values.Count];
            for (int i = 0; i < result.Length; i++) result[i] = checked(Convert.ToInt32(values[i], CultureInfo.InvariantCulture));
            return result;
        }
    }

    internal sealed class FbxDocument
    {
        internal int Version;
        internal readonly List<FbxElement> Roots = new List<FbxElement>();
        internal FbxElement Root(string name) => Roots.Find(n => n.Name == name);
    }

    internal static class FbxSyntax
    {
        internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0");

        internal static FbxDocument Read(string path, FbxBudget budget)
        {
            budget.Check();
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > budget.Options.MaximumSourceBytes || input.Length > int.MaxValue)
                    throw new FbxFormatException("fbx.budget.source", "FBX source exceeds the configured byte limit.");
                budget.Allocate(input.Length);
                var data = new byte[(int)input.Length];
                int offset = 0;
                while (offset < data.Length)
                {
                    budget.Check();
                    int read = input.Read(data, offset, Math.Min(65536, data.Length - offset));
                    if (read == 0) throw new FbxFormatException("fbx.truncated", "Source ended during reading.", offset);
                    offset += read;
                }
                bool binary = data.Length >= Magic.Length;
                for (int i = 0; binary && i < Magic.Length; i++) binary &= data[i] == Magic[i];
                return binary ? new BinaryParser(data, budget).Read() : new AsciiParser(data, budget).Read();
            }
        }

        private sealed class BinaryParser
        {
            private readonly BinaryReader reader;
            private readonly FbxBudget budget;
            private bool wide;
            private long Position => reader.BaseStream.Position;
            private long Length => reader.BaseStream.Length;
            internal BinaryParser(byte[] bytes, FbxBudget budget)
            { reader = new BinaryReader(new MemoryStream(bytes, false), Encoding.UTF8); this.budget = budget; }
            internal FbxDocument Read()
            {
                using (reader)
                {
                    Need(27, Length);
                    reader.BaseStream.Position = 23;
                    int version = reader.ReadInt32();
                    if (version != 7400 && version != 7500) throw Error("fbx.version.not-implemented", "Only FBX 7400 and 7500 are implemented in the feasibility reader.");
                    wide = version >= 7500;
                    var document = new FbxDocument { Version = version };
                    while (Position < Length)
                    {
                        var node = Element(0, Length);
                        if (node == null) break;
                        document.Roots.Add(node);
                    }
                    return document;
                }
            }
            private FbxElement Element(int depth, long parentEnd)
            {
                budget.Node(depth);
                Need(wide ? 25 : 13, parentEnd);
                ulong endValue = wide ? reader.ReadUInt64() : reader.ReadUInt32();
                ulong count = wide ? reader.ReadUInt64() : reader.ReadUInt32();
                ulong propertiesLength = wide ? reader.ReadUInt64() : reader.ReadUInt32();
                byte nameLength = reader.ReadByte();
                if (endValue == 0)
                {
                    if (count != 0 || propertiesLength != 0 || nameLength != 0) throw Error("fbx.null-record", "Invalid FBX end record.");
                    return null;
                }
                if (endValue > (ulong)parentEnd || endValue <= (ulong)Position || count > (ulong)budget.Options.MaximumElements)
                    throw Error("fbx.record.bounds", "FBX record points outside its containing record.");
                long end = checked((long)endValue);
                Need(nameLength, end);
                var result = new FbxElement(Encoding.UTF8.GetString(reader.ReadBytes(nameLength)));
                if (propertiesLength > (ulong)(end - Position)) throw Error("fbx.properties.bounds", "Property bytes exceed record.");
                long propertyEnd = Position + (long)propertiesLength;
                budget.Allocate(checked((long)count * 16));
                for (ulong i = 0; i < count; i++) result.Properties.Add(Property(propertyEnd));
                if (Position != propertyEnd) throw Error("fbx.properties.length", "Property count and byte length disagree.");
                while (Position < end)
                {
                    var child = Element(depth + 1, end);
                    if (child == null)
                    {
                        if (Position != end) throw Error("fbx.record.end", "End record did not finish the parent record.");
                        break;
                    }
                    result.Children.Add(child);
                }
                if (Position != end) throw Error("fbx.record.end", "Record length mismatch.");
                return result;
            }
            private object Property(long end)
            {
                budget.Check(); Need(1, end);
                char type = (char)reader.ReadByte();
                switch (type)
                {
                    case 'Y': Need(2, end); return reader.ReadInt16();
                    case 'C': case 'B': Need(1, end); return reader.ReadByte() != 0;
                    case 'I': Need(4, end); return reader.ReadInt32();
                    case 'L': Need(8, end); return reader.ReadInt64();
                    case 'F': Need(4, end); return reader.ReadSingle();
                    case 'D': Need(8, end); return reader.ReadDouble();
                    case 'S': case 'R':
                        Need(4, end); uint size = reader.ReadUInt32();
                        if (size > budget.Options.MaximumStringBytes) throw Error("fbx.budget.string", "Property string or raw data exceeds the probe limit.");
                        Need(size, end); budget.Allocate(type == 'S' ? size * 3L : size);
                        byte[] value = reader.ReadBytes((int)size);
                        return type == 'S' ? (object)Encoding.UTF8.GetString(value) : value;
                    case 'f': case 'd': case 'i': case 'l': case 'b': case 'c': return ArrayProperty(type, end);
                    default: throw Error("fbx.property.not-implemented", "Unsupported FBX property code " + type + ".");
                }
            }
            private object ArrayProperty(char type, long end)
            {
                Need(12, end);
                uint count = reader.ReadUInt32(), encoding = reader.ReadUInt32(), bytes = reader.ReadUInt32();
                int width = type == 'd' || type == 'l' ? 8 : type == 'i' || type == 'f' ? 4 : 1;
                long expected = checked((long)count * width);
                if (count > budget.Options.MaximumElements || expected > int.MaxValue || encoding > 1)
                    throw Error("fbx.array.limit", "Unsupported array encoding or excessive element count.");
                Need(bytes, end); budget.Allocate(checked(bytes + expected * 2));
                byte[] source = reader.ReadBytes(checked((int)bytes));
                byte[] decoded;
                if (encoding == 0)
                {
                    if (bytes != expected) throw Error("fbx.array.length", "Uncompressed array length mismatch.");
                    decoded = source;
                }
                else decoded = InflateZlib(source, (int)expected, budget);
                int length = (int)count;
                using (var values = new BinaryReader(new MemoryStream(decoded, false)))
                {
                    if (type == 'd') { var a = new double[length]; for (int i = 0; i < length; i++) { if ((i & 4095) == 0) budget.Check(); a[i] = values.ReadDouble(); } return a; }
                    if (type == 'f') { var a = new float[length]; for (int i = 0; i < length; i++) { if ((i & 4095) == 0) budget.Check(); a[i] = values.ReadSingle(); } return a; }
                    if (type == 'i') { var a = new int[length]; for (int i = 0; i < length; i++) { if ((i & 4095) == 0) budget.Check(); a[i] = values.ReadInt32(); } return a; }
                    if (type == 'l') { var a = new long[length]; for (int i = 0; i < length; i++) { if ((i & 4095) == 0) budget.Check(); a[i] = values.ReadInt64(); } return a; }
                    return decoded;
                }
            }
            private void Need(long count, long end)
            {
                if (count < 0 || count > end - Position || count > Length - Position)
                    throw Error("fbx.truncated", "Read would cross the containing record or source boundary.");
            }
            private FbxFormatException Error(string code, string message) => new FbxFormatException(code, message, Position);
        }

        internal static byte[] InflateZlib(byte[] source, int expected, FbxBudget budget)
        {
            if (source.Length < 6 || (source[0] & 15) != 8 || (source[0] >> 4) > 7 ||
                ((source[0] << 8) + source[1]) % 31 != 0 || (source[1] & 32) != 0)
                throw new FbxFormatException("fbx.zlib.header", "Invalid or unsupported zlib header.");
            byte[] result = new byte[expected];
            using (var compressed = new MemoryStream(source, 2, source.Length - 6, false))
            using (var deflate = new DeflateStream(compressed, CompressionMode.Decompress))
            {
                int offset = 0;
                while (offset < expected)
                {
                    budget.Check();
                    int read = deflate.Read(result, offset, Math.Min(65536, expected - offset));
                    if (read == 0) throw new FbxFormatException("fbx.zlib.length", "Compressed array ended before its declared length.");
                    offset += read;
                }
                if (deflate.ReadByte() != -1) throw new FbxFormatException("fbx.zlib.length", "Compressed array exceeds its declared length.");
            }
            uint a = 1, b = 0;
            for (int i = 0; i < result.Length; i++) { if ((i & 65535) == 0) budget.Check(); a = (a + result[i]) % 65521; b = (b + a) % 65521; }
            int n = source.Length;
            uint checksum = (uint)source[n - 4] << 24 | (uint)source[n - 3] << 16 | (uint)source[n - 2] << 8 | source[n - 1];
            if ((b << 16 | a) != checksum) throw new FbxFormatException("fbx.zlib.checksum", "Compressed array checksum mismatch.");
            return result;
        }

        private sealed class AsciiParser
        {
            private readonly string text;
            private readonly FbxBudget budget;
            private int cursor;
            private string token;
            private bool quoted;
            internal AsciiParser(byte[] data, FbxBudget budget)
            {
                this.budget = budget; budget.Allocate(checked((long)data.Length * 2));
                text = new UTF8Encoding(false, true).GetString(data); Next();
            }
            internal FbxDocument Read()
            {
                var result = new FbxDocument();
                while (token != null)
                {
                    if (token == "\n") { Next(); continue; }
                    result.Roots.Add(Element(0));
                }
                var version = result.Root("FBXHeaderExtension")?.Child("FBXVersion");
                result.Version = version == null ? 0 : checked((int)version.Integer());
                if (result.Version != 7400 && result.Version != 7500)
                    throw Error("fbx.version.not-implemented", "ASCII feasibility reader requires FBXVersion 7400 or 7500.");
                return result;
            }
            private FbxElement Element(int depth)
            {
                budget.Node(depth);
                if (token == null || token == "}" || token == "{") throw Error("fbx.ascii.node", "Expected node identifier.");
                var node = new FbxElement(token); Next(); Require(":");
                int declared = -1;
                while (token != null && token != "\n" && token != "{" && token != "}")
                {
                    budget.Check();
                    if (token == ",") { Next(); if (token == "\n") Next(); continue; }
                    if (token == "*")
                    {
                        Next();
                        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out declared) || declared < 0 || declared > budget.Options.MaximumElements)
                            throw Error("fbx.ascii.array", "Invalid array element count.");
                        Next(); continue;
                    }
                    if (node.Properties.Count >= budget.Options.MaximumElements) throw Error("fbx.budget.elements", "Too many properties.");
                    budget.Allocate(24);
                    if (quoted) node.Properties.Add(token);
                    else if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)) node.Properties.Add(integer);
                    else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double real)) node.Properties.Add(real);
                    else node.Properties.Add(token);
                    Next();
                }
                if (token == "\n") { Next(); }
                if (token == "{")
                {
                    Next();
                    while (token != "}")
                    {
                        if (token == null) throw Error("fbx.ascii.truncated", "Unclosed FBX node.");
                        if (token == "\n") { Next(); continue; }
                        node.Children.Add(Element(depth + 1));
                    }
                    Next();
                }
                if (declared >= 0 && (node.Child("a")?.Properties.Count ?? 0) != declared)
                    throw Error("fbx.ascii.array", "Declared array count does not match its data.");
                return node;
            }
            private void Require(string expected)
            { if (token != expected) throw Error("fbx.ascii.syntax", "Expected '" + expected + "'."); Next(); }
            private void Next()
            {
                budget.Check(); quoted = false;
                while (cursor < text.Length)
                {
                    char c = text[cursor];
                    if (c == ';') { while (cursor < text.Length && text[cursor] != '\n') cursor++; continue; }
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\ufeff') { cursor++; continue; }
                    break;
                }
                if (cursor == text.Length) { token = null; return; }
                char first = text[cursor++];
                if (first == '\n' || first == ':' || first == ',' || first == '{' || first == '}' || first == '*')
                { token = first.ToString(); return; }
                if (first == '"')
                {
                    quoted = true; var builder = new StringBuilder(); bool closed = false;
                    while (cursor < text.Length)
                    {
                        char c = text[cursor++];
                        if (c == '"') { closed = true; break; }
                        if (c == '\\' && cursor < text.Length)
                        {
                            char escaped = text[cursor++];
                            if (escaped == '"' || escaped == '\\') builder.Append(escaped);
                            else { builder.Append('\\'); builder.Append(escaped); }
                        }
                        else builder.Append(c);
                        if (builder.Length > budget.Options.MaximumStringBytes) throw Error("fbx.budget.string", "ASCII string exceeds limit.");
                    }
                    if (!closed) throw Error("fbx.ascii.truncated", "Unclosed string.");
                    token = builder.ToString(); budget.Allocate(token.Length * 2L); return;
                }
                int start = cursor - 1;
                while (cursor < text.Length && !char.IsWhiteSpace(text[cursor]) && ":,{}*;".IndexOf(text[cursor]) < 0)
                {
                    cursor++;
                    if (cursor - start > budget.Options.MaximumStringBytes) throw Error("fbx.budget.string", "ASCII token exceeds limit.");
                }
                token = text.Substring(start, cursor - start);
            }
            private FbxFormatException Error(string code, string message) => new FbxFormatException(code, message, cursor);
        }
    }
}
