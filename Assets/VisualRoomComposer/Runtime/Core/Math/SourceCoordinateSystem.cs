using System;

namespace VisualRoomComposer.Core
{
    /// <summary>Source basis vectors expressed in canonical Unity axes. One source unit has MetersPerUnit meters.</summary>
    public sealed class SourceCoordinateSystem
    {
        public string Name { get; }
        public double MetersPerUnit { get; }
        public Double3 SourceXInCanonical { get; }
        public Double3 SourceYInCanonical { get; }
        public Double3 SourceZInCanonical { get; }
        public bool SourceFrontFaceClockwise { get; }
        public Matrix4d ToCanonicalMatrix { get; }
        public static SourceCoordinateSystem UnityMeters => new SourceCoordinateSystem("Unity meters Y-up left-handed", 1,
            Double3.UnitX, Double3.UnitY, Double3.UnitZ, true);
        public static SourceCoordinateSystem GltfMeters => new SourceCoordinateSystem("glTF meters Y-up right-handed", 1,
            Double3.UnitX, Double3.UnitY, -Double3.UnitZ, false);
        public SourceCoordinateSystem(string name, double metersPerUnit, Double3 sourceXInCanonical,
            Double3 sourceYInCanonical, Double3 sourceZInCanonical, bool sourceFrontFaceClockwise)
        {
            if (!Numeric.IsFinite(metersPerUnit) || metersPerUnit <= 0) throw new ArgumentOutOfRangeException(nameof(metersPerUnit));
            RequireAxis(sourceXInCanonical); RequireAxis(sourceYInCanonical); RequireAxis(sourceZInCanonical);
            if (Math.Abs(Double3.Dot(sourceXInCanonical, sourceYInCanonical)) > 1e-12 ||
                Math.Abs(Double3.Dot(sourceXInCanonical, sourceZInCanonical)) > 1e-12 ||
                Math.Abs(Double3.Dot(sourceYInCanonical, sourceZInCanonical)) > 1e-12)
                throw new ArgumentException("Source coordinate axes must be orthogonal.");
            Name = name ?? "Explicit source coordinates"; MetersPerUnit = metersPerUnit;
            SourceXInCanonical = sourceXInCanonical; SourceYInCanonical = sourceYInCanonical; SourceZInCanonical = sourceZInCanonical;
            SourceFrontFaceClockwise = sourceFrontFaceClockwise;
            ToCanonicalMatrix = new Matrix4d(sourceXInCanonical.X * metersPerUnit, sourceYInCanonical.X * metersPerUnit, sourceZInCanonical.X * metersPerUnit, 0,
                sourceXInCanonical.Y * metersPerUnit, sourceYInCanonical.Y * metersPerUnit, sourceZInCanonical.Y * metersPerUnit, 0,
                sourceXInCanonical.Z * metersPerUnit, sourceYInCanonical.Z * metersPerUnit, sourceZInCanonical.Z * metersPerUnit, 0, 0, 0, 0, 1);
            ToCanonicalMatrix.InverseAffine();
        }
        private static void RequireAxis(Double3 value)
        {
            if (!value.IsFinite || Math.Abs(value.LengthSquared - 1) > 1e-12) throw new ArgumentException("Each source basis axis must have unit length.");
        }
    }
}
