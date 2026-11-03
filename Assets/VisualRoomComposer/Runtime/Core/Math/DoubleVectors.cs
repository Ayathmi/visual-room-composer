using System;
using System.Globalization;

namespace VisualRoomComposer.Core
{
    /// <summary>Finite-number helpers shared by the engine-independent data model.</summary>
    public static class Numeric
    {
        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        public static void RequireFinite(double value, string name)
        {
            if (!IsFinite(value)) throw new ArgumentOutOfRangeException(name, "A finite number is required.");
        }
    }

    /// <summary>A double-precision two-component value.</summary>
    public readonly struct Double2 : IEquatable<Double2>
    {
        public double X { get; }
        public double Y { get; }
        public Double2(double x, double y) { X = x; Y = y; }
        public bool IsFinite => Numeric.IsFinite(X) && Numeric.IsFinite(Y);
        public bool Equals(Double2 other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object obj) => obj is Double2 other && Equals(other);
        public override int GetHashCode() => unchecked(X.GetHashCode() * 397 ^ Y.GetHashCode());
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:R}, {1:R})", X, Y);
    }

    /// <summary>A double-precision vector with conventional algebraic cross products.</summary>
    public readonly struct Double3 : IEquatable<Double3>
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public Double3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Double3 Zero => new Double3(0, 0, 0);
        public static Double3 One => new Double3(1, 1, 1);
        public static Double3 UnitX => new Double3(1, 0, 0);
        public static Double3 UnitY => new Double3(0, 1, 0);
        public static Double3 UnitZ => new Double3(0, 0, 1);
        public bool IsFinite => Numeric.IsFinite(X) && Numeric.IsFinite(Y) && Numeric.IsFinite(Z);
        public double LengthSquared => Dot(this, this);
        public double Length
        {
            get
            {
                double scale = Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
                if (scale == 0) return 0;
                var reduced = this / scale;
                return scale * Math.Sqrt(reduced.LengthSquared);
            }
        }
        public Double3 Normalized()
        {
            if (!IsFinite) throw new ArgumentException("Cannot normalize a non-finite vector.");
            double scale = Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
            if (scale == 0) throw new ArgumentException("Cannot normalize a zero vector.");
            var reduced = this / scale;
            return reduced / Math.Sqrt(reduced.LengthSquared);
        }
        public static double Dot(Double3 a, Double3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Double3 Cross(Double3 a, Double3 b) => new Double3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static Double3 operator +(Double3 a, Double3 b) => new Double3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Double3 operator -(Double3 a, Double3 b) => new Double3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Double3 operator -(Double3 value) => new Double3(-value.X, -value.Y, -value.Z);
        public static Double3 operator *(Double3 value, double scale) => new Double3(value.X * scale, value.Y * scale, value.Z * scale);
        public static Double3 operator *(double scale, Double3 value) => value * scale;
        public static Double3 operator /(Double3 value, double divisor) => new Double3(value.X / divisor, value.Y / divisor, value.Z / divisor);
        public bool Equals(Double3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        public override bool Equals(object obj) => obj is Double3 other && Equals(other);
        public override int GetHashCode() => unchecked((X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode());
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:R}, {1:R}, {2:R})", X, Y, Z);
    }

    /// <summary>A color, quaternion, or tangent whose fourth component preserves handedness.</summary>
    public readonly struct Double4 : IEquatable<Double4>
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public double W { get; }
        public Double4(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
        public Double3 Xyz => new Double3(X, Y, Z);
        public bool IsFinite => Xyz.IsFinite && Numeric.IsFinite(W);
        public bool Equals(Double4 other) => Xyz.Equals(other.Xyz) && W.Equals(other.W);
        public override bool Equals(object obj) => obj is Double4 other && Equals(other);
        public override int GetHashCode() => unchecked(Xyz.GetHashCode() * 397 ^ W.GetHashCode());
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:R}, {1:R}, {2:R}, {3:R})", X, Y, Z, W);
    }
}
