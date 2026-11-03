using System;
using System.Collections.Generic;

namespace VisualRoomComposer.Core
{
    /// <summary>A full double-precision matrix. Column vectors use parent * local, with translation in M14/M24/M34.</summary>
    public readonly struct Matrix4d
    {
        public double M11 { get; } public double M12 { get; } public double M13 { get; } public double M14 { get; }
        public double M21 { get; } public double M22 { get; } public double M23 { get; } public double M24 { get; }
        public double M31 { get; } public double M32 { get; } public double M33 { get; } public double M34 { get; }
        public double M41 { get; } public double M42 { get; } public double M43 { get; } public double M44 { get; }

        public Matrix4d(double m11, double m12, double m13, double m14,
            double m21, double m22, double m23, double m24,
            double m31, double m32, double m33, double m34,
            double m41, double m42, double m43, double m44)
        {
            M11 = m11; M12 = m12; M13 = m13; M14 = m14;
            M21 = m21; M22 = m22; M23 = m23; M24 = m24;
            M31 = m31; M32 = m32; M33 = m33; M34 = m34;
            M41 = m41; M42 = m42; M43 = m43; M44 = m44;
        }

        public static Matrix4d Identity => new Matrix4d(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        public double this[int row, int column]
        {
            get
            {
                if (row < 0 || row > 3 || column < 0 || column > 3) throw new ArgumentOutOfRangeException();
                switch (row * 4 + column)
                {
                    case 0: return M11; case 1: return M12; case 2: return M13; case 3: return M14;
                    case 4: return M21; case 5: return M22; case 6: return M23; case 7: return M24;
                    case 8: return M31; case 9: return M32; case 10: return M33; case 11: return M34;
                    case 12: return M41; case 13: return M42; case 14: return M43; default: return M44;
                }
            }
        }
        public bool IsFinite
        {
            get
            {
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) if (!Numeric.IsFinite(this[r, c])) return false;
                return true;
            }
        }
        public bool IsAffine => IsFinite && M41 == 0 && M42 == 0 && M43 == 0 && M44 == 1;
        public double LinearDeterminant => M11 * (M22 * M33 - M23 * M32) - M12 * (M21 * M33 - M23 * M31) + M13 * (M21 * M32 - M22 * M31);
        public Double3 TranslationPart => new Double3(M14, M24, M34);
        public static Matrix4d Translation(Double3 value) => new Matrix4d(1, 0, 0, value.X, 0, 1, 0, value.Y, 0, 0, 1, value.Z, 0, 0, 0, 1);
        public static Matrix4d Scale(Double3 value) => new Matrix4d(value.X, 0, 0, 0, 0, value.Y, 0, 0, 0, 0, value.Z, 0, 0, 0, 0, 1);
        public static Matrix4d RotationX(double radians)
        {
            double c = Math.Cos(radians), s = Math.Sin(radians);
            return new Matrix4d(1, 0, 0, 0, 0, c, -s, 0, 0, s, c, 0, 0, 0, 0, 1);
        }
        public static Matrix4d RotationY(double radians)
        {
            double c = Math.Cos(radians), s = Math.Sin(radians);
            return new Matrix4d(c, 0, s, 0, 0, 1, 0, 0, -s, 0, c, 0, 0, 0, 0, 1);
        }
        public static Matrix4d RotationZ(double radians)
        {
            double c = Math.Cos(radians), s = Math.Sin(radians);
            return new Matrix4d(c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        }
        public static Matrix4d RotationYQuarterTurns(int turns)
        {
            switch ((turns % 4 + 4) % 4)
            {
                case 0: return Identity;
                case 1: return new Matrix4d(0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 0, 1);
                case 2: return Scale(new Double3(-1, 1, -1));
                default: return new Matrix4d(0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1);
            }
        }
        public static Matrix4d RotationQuaternion(Double4 quaternion)
        {
            if (!quaternion.IsFinite) throw new ArgumentException("Quaternion must be finite.");
            double length = Math.Sqrt(quaternion.X * quaternion.X + quaternion.Y * quaternion.Y + quaternion.Z * quaternion.Z + quaternion.W * quaternion.W);
            if (!Numeric.IsFinite(length) || length < 1e-15) throw new ArgumentException("Quaternion must be nonzero.");
            double x = quaternion.X / length, y = quaternion.Y / length, z = quaternion.Z / length, w = quaternion.W / length;
            return new Matrix4d(1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w), 0,
                2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w), 0,
                2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y), 0, 0, 0, 0, 1);
        }
        public Double3 TransformPoint(Double3 point)
        {
            if (!IsAffine) throw new InvalidOperationException("A finite affine matrix is required.");
            return new Double3(M11 * point.X + M12 * point.Y + M13 * point.Z + M14,
                M21 * point.X + M22 * point.Y + M23 * point.Z + M24,
                M31 * point.X + M32 * point.Y + M33 * point.Z + M34);
        }
        public Double3 TransformVector(Double3 vector) => new Double3(M11 * vector.X + M12 * vector.Y + M13 * vector.Z,
            M21 * vector.X + M22 * vector.Y + M23 * vector.Z, M31 * vector.X + M32 * vector.Y + M33 * vector.Z);
        public Double3 TransformNormal(Double3 normal)
        {
            var inverse = InverseAffine();
            return new Double3(inverse.M11 * normal.X + inverse.M21 * normal.Y + inverse.M31 * normal.Z,
                inverse.M12 * normal.X + inverse.M22 * normal.Y + inverse.M32 * normal.Z,
                inverse.M13 * normal.X + inverse.M23 * normal.Y + inverse.M33 * normal.Z).Normalized();
        }
        public bool TryInverseAffine(out Matrix4d inverse, double relativeTolerance = 1e-12)
        {
            inverse = default;
            if (!IsAffine || !Numeric.IsFinite(relativeTolerance) || relativeTolerance <= 0) return false;
            double scale = 0;
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) scale = Math.Max(scale, Math.Abs(this[r, c]));
            if (scale == 0) return false;
            double a = M11 / scale, b = M12 / scale, c0 = M13 / scale;
            double d = M21 / scale, e = M22 / scale, f = M23 / scale;
            double g = M31 / scale, h = M32 / scale, i = M33 / scale;
            double determinant = a * (e * i - f * h) - b * (d * i - f * g) + c0 * (d * h - e * g);
            if (Math.Abs(determinant) <= relativeTolerance) return false;
            double factor = 1 / determinant / scale;
            var linear = new Matrix4d((e * i - f * h) * factor, (c0 * h - b * i) * factor, (b * f - c0 * e) * factor, 0,
                (f * g - d * i) * factor, (a * i - c0 * g) * factor, (c0 * d - a * f) * factor, 0,
                (d * h - e * g) * factor, (b * g - a * h) * factor, (a * e - b * d) * factor, 0, 0, 0, 0, 1);
            var offset = -linear.TransformVector(TranslationPart);
            inverse = new Matrix4d(linear.M11, linear.M12, linear.M13, offset.X,
                linear.M21, linear.M22, linear.M23, offset.Y, linear.M31, linear.M32, linear.M33, offset.Z, 0, 0, 0, 1);
            return inverse.IsFinite;
        }
        public Matrix4d InverseAffine()
        {
            if (!TryInverseAffine(out var inverse)) throw new ArgumentException("Transform is singular, non-affine, non-finite, or numerically unsafe.");
            return inverse;
        }
        public int OrientationSign
        {
            get
            {
                InverseAffine();
                var x = new Double3(M11, M21, M31).Normalized();
                var y = new Double3(M12, M22, M32).Normalized();
                var z = new Double3(M13, M23, M33).Normalized();
                return Double3.Dot(Double3.Cross(x, y), z) < 0 ? -1 : 1;
            }
        }
        public bool HasShear(double tolerance = 1e-10)
        {
            InverseAffine();
            var x = new Double3(M11, M21, M31).Normalized();
            var y = new Double3(M12, M22, M32).Normalized();
            var z = new Double3(M13, M23, M33).Normalized();
            return Math.Abs(Double3.Dot(x, y)) > tolerance || Math.Abs(Double3.Dot(x, z)) > tolerance || Math.Abs(Double3.Dot(y, z)) > tolerance;
        }
        public static Matrix4d FromRowMajor(IReadOnlyList<double> values)
        {
            if (values == null || values.Count != 16) throw new ArgumentException("Exactly sixteen values are required.");
            return new Matrix4d(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
                values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
        }
        public double[] ToRowMajorArray() => new[] { M11, M12, M13, M14, M21, M22, M23, M24, M31, M32, M33, M34, M41, M42, M43, M44 };
        public static Matrix4d operator *(Matrix4d left, Matrix4d right)
        {
            var values = new double[16];
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++)
                values[r * 4 + c] = left[r, 0] * right[0, c] + left[r, 1] * right[1, c] + left[r, 2] * right[2, c] + left[r, 3] * right[3, c];
            return FromRowMajor(values);
        }
    }
}
