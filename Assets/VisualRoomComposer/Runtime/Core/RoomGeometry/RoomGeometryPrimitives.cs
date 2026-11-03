using System;
using System.Collections.Generic;
using System.Linq;
using VisualRoomComposer.Core.Documents;

namespace VisualRoomComposer.Core.RoomGeometry
{
    /// <summary>World-space bounds. Touching faces do not imply volume penetration.</summary>
    public readonly struct GeometryBox
    {
        public const double Epsilon = 0.00001;
        public Double3 Min { get; }
        public Double3 Max { get; }
        public Double3 Center => (Min + Max) * 0.5;
        public Double3 Size => Max - Min;
        public GeometryBox(Double3 min, Double3 max)
        {
            if (!min.IsFinite || !max.IsFinite || min.X > max.X || min.Y > max.Y || min.Z > max.Z)
                throw new ArgumentException("Bounds must be finite and ordered.");
            Min = min; Max = max;
        }
        public bool Intersects(GeometryBox other) => Min.X < other.Max.X - Epsilon && Max.X > other.Min.X + Epsilon &&
            Min.Y < other.Max.Y - Epsilon && Max.Y > other.Min.Y + Epsilon && Min.Z < other.Max.Z - Epsilon && Max.Z > other.Min.Z + Epsilon;
        public bool Contains(GeometryBox other) => Contains(other.Min) && Contains(other.Max);
        public bool Contains(Double3 p, double epsilon = Epsilon) => p.X >= Min.X - epsilon && p.X <= Max.X + epsilon &&
            p.Y >= Min.Y - epsilon && p.Y <= Max.Y + epsilon && p.Z >= Min.Z - epsilon && p.Z <= Max.Z + epsilon;
        public bool IntersectsProjection(GridRect region) => Min.X < region.MaxX * .25 - Epsilon && Max.X > region.MinX * .25 + Epsilon &&
            Min.Z < region.MaxZ * .25 - Epsilon && Max.Z > region.MinZ * .25 + Epsilon;
    }

    /// <summary>A canonical quarter-meter edge. Direction-independent identity supports shared topology.</summary>
    public readonly struct GridEdge : IEquatable<GridEdge>
    {
        public int X { get; }
        public int Z { get; }
        public bool AlongX { get; }
        public GridEdge(int x, int z, bool alongX) { X = x; Z = z; AlongX = alongX; }
        public GridCell2 SideA => new GridCell2(X, Z);
        public GridCell2 SideB => AlongX ? new GridCell2(X, checked(Z - 1)) : new GridCell2(checked(X - 1), Z);
        public GridCell2 Start => new GridCell2(X, Z);
        public GridCell2 End => new GridCell2(checked(X + (AlongX ? 1 : 0)), checked(Z + (AlongX ? 0 : 1)));
        public bool Equals(GridEdge other) => X == other.X && Z == other.Z && AlongX == other.AlongX;
        public override bool Equals(object obj) => obj is GridEdge edge && Equals(edge);
        public override int GetHashCode() => unchecked((X * 397 ^ Z) * 31 + (AlongX ? 1 : 0));
    }

    /// <summary>Half-open grid rectangle; all edit paths share the same boundary semantics.</summary>
    public readonly struct GridRect
    {
        public int MinX { get; }
        public int MinZ { get; }
        public int MaxX { get; }
        public int MaxZ { get; }
        public long CellCount => ((long)MaxX - MinX) * ((long)MaxZ - MinZ);
        public GridRect(int minX, int minZ, int maxX, int maxZ)
        {
            if (maxX <= minX || maxZ <= minZ || minX < -65536 || minZ < -65536 || maxX > 65536 || maxZ > 65536)
                throw new ArgumentOutOfRangeException(nameof(maxX), "Selection must be ordered and within the grid budget.");
            MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ;
        }
        public bool Contains(int x, int z) => x >= MinX && x < MaxX && z >= MinZ && z < MaxZ;
        public static GridRect Between(GridCell2 a, GridCell2 b) => new GridRect(Math.Min(a.X, b.X), Math.Min(a.Z, b.Z),
            checked(Math.Max(a.X, b.X) + 1), checked(Math.Max(a.Z, b.Z) + 1));
    }

    public enum RoomIssueKind { DraftDiagnostic, BlockingDataError }

    /// <summary>Draft diagnostics never make an otherwise well-formed document unsavable.</summary>
    public sealed class RoomGeometryIssue
    {
        public string Code { get; }
        public string PlacementId { get; }
        public string RelatedId { get; }
        public RoomIssueKind Kind { get; }
        public RoomGeometryIssue(string code, string placementId = null, string relatedId = null, RoomIssueKind kind = RoomIssueKind.DraftDiagnostic)
        { Code = code; PlacementId = placementId; RelatedId = relatedId; Kind = kind; }
    }

    /// <summary>Bounded broad phase for immutable entries. Exact boxes determine actual intersection.</summary>
    public sealed class RoomSpatialIndex
    {
        private readonly Dictionary<(int, int, int), HashSet<string>> buckets = new Dictionary<(int, int, int), HashSet<string>>();
        private readonly HashSet<string> large = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<GeometryBox>> entries = new Dictionary<string, IReadOnlyList<GeometryBox>>(StringComparer.Ordinal);
        public RoomSpatialIndex(IEnumerable<KeyValuePair<string, IReadOnlyList<GeometryBox>>> source)
        {
            foreach (var row in source)
            {
                if (entries.ContainsKey(row.Key)) throw new ArgumentException("Duplicate spatial identity.");
                var parts = Array.AsReadOnly(row.Value.ToArray()); entries.Add(row.Key, parts);
                foreach (var box in parts)
                {
                    var span = Span(box);
                    if (Count(span) > 4096) { large.Add(row.Key); continue; }
                    for (int x = span.x1; x <= span.x2; x++) for (int y = span.y1; y <= span.y2; y++) for (int z = span.z1; z <= span.z2; z++)
                    {
                        if (!buckets.TryGetValue((x, y, z), out var values)) buckets.Add((x, y, z), values = new HashSet<string>(StringComparer.Ordinal));
                        values.Add(row.Key);
                    }
                }
            }
        }
        public IReadOnlyList<string> Query(GeometryBox box, bool exact = true)
        {
            var span = Span(box); var found = new HashSet<string>(large, StringComparer.Ordinal);
            if (Count(span) > 4096) found.UnionWith(entries.Keys);
            else for (int x = span.x1; x <= span.x2; x++) for (int y = span.y1; y <= span.y2; y++) for (int z = span.z1; z <= span.z2; z++)
                if (buckets.TryGetValue((x, y, z), out var values)) found.UnionWith(values);
            return Array.AsReadOnly(found.Where(id => !exact || entries[id].Any(box.Intersects)).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        }
        private static (int x1, int y1, int z1, int x2, int y2, int z2) Span(GeometryBox b) =>
            (Cell(b.Min.X), Cell(b.Min.Y), Cell(b.Min.Z), Cell(b.Max.X), Cell(b.Max.Y), Cell(b.Max.Z));
        private static int Cell(double n)
        {
            if (Math.Abs(n) > 1000000) throw new ArgumentOutOfRangeException(nameof(n), "Spatial coordinate budget exceeded.");
            return checked((int)Math.Floor(n / 2));
        }
        private static long Count((int x1, int y1, int z1, int x2, int y2, int z2) s)
        {
            long x = (long)s.x2 - s.x1 + 1, y = (long)s.y2 - s.y1 + 1, z = (long)s.z2 - s.z1 + 1;
            if (x > 4096 || y > 4096 || z > 4096) return 4097;
            return Math.Min(4097, x * y * z);
        }
    }
}
