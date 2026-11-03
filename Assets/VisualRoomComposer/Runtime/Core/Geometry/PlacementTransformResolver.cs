using System;

namespace VisualRoomComposer.Core
{
    /// <summary>Integer quarter-meter placement coordinates.</summary>
    public readonly struct GridPosition
    {
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public GridPosition(int x, int y, int z) { X = x; Y = y; Z = z; }
    }

    /// <summary>Authored placement transform inputs, separate from category and occupancy validation.</summary>
    public readonly struct PlacementTransform
    {
        public GridPosition Position { get; }
        public int QuarterTurns { get; }
        public Double3 Scale { get; }
        public Double3 PivotOffset { get; }
        public double GroundPivotOffset { get; }
        public Matrix4d MeshTransform { get; }
        public PlacementTransform(GridPosition position, int quarterTurns, Double3 scale, Double3 pivotOffset,
            double groundPivotOffset, Matrix4d meshTransform)
        {
            if (quarterTurns < 0 || quarterTurns > 3) throw new ArgumentOutOfRangeException(nameof(quarterTurns));
            if (!scale.IsFinite || scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0) throw new ArgumentException("Placement scale must be finite and positive.", nameof(scale));
            if (!pivotOffset.IsFinite || pivotOffset.Y != 0) throw new ArgumentException("Placement pivot offset is horizontal; use grid height for Y.", nameof(pivotOffset));
            Numeric.RequireFinite(groundPivotOffset, nameof(groundPivotOffset)); meshTransform.InverseAffine();
            Position = position; QuarterTurns = quarterTurns; Scale = scale; PivotOffset = pivotOffset;
            GroundPivotOffset = groundPivotOffset; MeshTransform = meshTransform;
        }
    }

    /// <summary>The shared placement rule preserves SDD offsets without importing its game assemblies.</summary>
    public static class PlacementTransformResolver
    {
        public const string RuleVersion = "vrc-placement-1";
        public const double GridMeters = 0.25;
        public static Matrix4d ResolvePlacementRoot(PlacementTransform placement)
        {
            if (!placement.Scale.IsFinite || placement.Scale.X <= 0 || placement.Scale.Y <= 0 || placement.Scale.Z <= 0 ||
                placement.QuarterTurns < 0 || placement.QuarterTurns > 3 || !placement.PivotOffset.IsFinite || placement.PivotOffset.Y != 0 || !Numeric.IsFinite(placement.GroundPivotOffset))
                throw new ArgumentException("Placement transform is invalid.", nameof(placement));
            var origin = new Double3(placement.Position.X * GridMeters + placement.PivotOffset.X,
                placement.Position.Y * GridMeters + placement.GroundPivotOffset,
                placement.Position.Z * GridMeters + placement.PivotOffset.Z);
            return Matrix4d.Translation(origin) * Matrix4d.RotationYQuarterTurns(placement.QuarterTurns) * Matrix4d.Scale(placement.Scale);
        }
        public static Matrix4d ResolveModelRoot(PlacementTransform placement) => ResolvePlacementRoot(placement) * placement.MeshTransform;
        public static Matrix4d ResolveNodeWorld(PlacementTransform placement, Matrix4d modelNodeWorld)
        {
            modelNodeWorld.InverseAffine();
            var result = ResolveModelRoot(placement) * modelNodeWorld;
            result.InverseAffine();
            return result;
        }
        public static double GroundOffsetFromHitbox(Double3 center, Double3 size, bool grounded)
        {
            if (!center.IsFinite || !size.IsFinite || size.X < 0 || size.Y < 0 || size.Z < 0) throw new ArgumentException("Hitbox must be finite with nonnegative size.");
            return grounded ? size.Y * 0.5 - center.Y : 0;
        }
    }
}
