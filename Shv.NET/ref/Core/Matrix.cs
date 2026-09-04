using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GTA.Math
{
    /// <summary>Mirrors Shv.NET/source/scripting/Matrix.hpp.</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Matrix : IEquatable<Matrix>
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;

        [Browsable(false)]
        public float this[int row, int column]
        {
            get { return ToArray()[row * 4 + column]; }
            set
            {
                var a = ToArray();
                a[row * 4 + column] = value;
                this = FromArray(a);
            }
        }

        public static Matrix Identity
        {
            get
            {
                Matrix result = new Matrix();
                result.M11 = 1f; result.M22 = 1f; result.M33 = 1f; result.M44 = 1f;
                return result;
            }
        }

        [Browsable(false)]
        public bool IsIdentity { get { return Equals(Identity); } }
        public bool HasInverse { get { return Determinant() != 0f; } }

        public static Matrix FromArray(float[] floatArray)
        {
            if (floatArray == null || floatArray.Length != 16) throw new ArgumentException("floatArray");
            var m = new Matrix();
            m.M11 = floatArray[0]; m.M12 = floatArray[1]; m.M13 = floatArray[2]; m.M14 = floatArray[3];
            m.M21 = floatArray[4]; m.M22 = floatArray[5]; m.M23 = floatArray[6]; m.M24 = floatArray[7];
            m.M31 = floatArray[8]; m.M32 = floatArray[9]; m.M33 = floatArray[10]; m.M34 = floatArray[11];
            m.M41 = floatArray[12]; m.M42 = floatArray[13]; m.M43 = floatArray[14]; m.M44 = floatArray[15];
            return m;
        }

        public float Determinant() { throw Stub.NotAvailable(); }
        public void Inverse() { throw Stub.NotAvailable(); }
        public Vector3 TransformPoint(Vector3 point) { throw Stub.NotAvailable(); }
        public Vector3 InverseTransformPoint(Vector3 point) { throw Stub.NotAvailable(); }

        public static Matrix Add(Matrix left, Matrix right) { throw Stub.NotAvailable(); }
        public static Matrix Subtract(Matrix left, Matrix right) { throw Stub.NotAvailable(); }
        public static Matrix Multiply(Matrix left, Matrix right) { throw Stub.NotAvailable(); }
        public static Matrix Multiply(Matrix left, float right) { throw Stub.NotAvailable(); }
        public static Matrix Divide(Matrix left, Matrix right) { throw Stub.NotAvailable(); }
        public static Matrix Divide(Matrix left, float right) { throw Stub.NotAvailable(); }
        public static Matrix Negate(Matrix matrix) { throw Stub.NotAvailable(); }
        public static Matrix Inverse(Matrix matrix) { throw Stub.NotAvailable(); }
        public static Matrix Lerp(Matrix start, Matrix end, float amount) { throw Stub.NotAvailable(); }
        public static Matrix RotationX(float angle) { throw Stub.NotAvailable(); }
        public static Matrix RotationY(float angle) { throw Stub.NotAvailable(); }
        public static Matrix RotationZ(float angle) { throw Stub.NotAvailable(); }
        public static Matrix RotationAxis(Vector3 axis, float angle) { throw Stub.NotAvailable(); }
        public static Matrix RotationQuaternion(Quaternion rotation) { throw Stub.NotAvailable(); }
        public static Matrix RotationYawPitchRoll(float yaw, float pitch, float roll) { throw Stub.NotAvailable(); }
        public static Matrix Scaling(float x, float y, float z) { throw Stub.NotAvailable(); }
        public static Matrix Scaling(Vector3 scale) { throw Stub.NotAvailable(); }
        public static Matrix Translation(float x, float y, float z) { throw Stub.NotAvailable(); }
        public static Matrix Translation(Vector3 amount) { throw Stub.NotAvailable(); }
        public static Matrix Transpose(Matrix matrix) { throw Stub.NotAvailable(); }

        public static Matrix operator -(Matrix matrix) { return Negate(matrix); }
        public static Matrix operator +(Matrix left, Matrix right) { return Add(left, right); }
        public static Matrix operator -(Matrix left, Matrix right) { return Subtract(left, right); }
        public static Matrix operator /(Matrix left, Matrix right) { return Divide(left, right); }
        public static Matrix operator /(Matrix left, float right) { return Divide(left, right); }
        public static Matrix operator *(Matrix left, Matrix right) { return Multiply(left, right); }
        public static Matrix operator *(Matrix left, float right) { return Multiply(left, right); }
        public static Matrix operator *(float left, Matrix right) { return Multiply(right, left); }
        public static bool operator ==(Matrix left, Matrix right) { return Equals(ref left, ref right); }
        public static bool operator !=(Matrix left, Matrix right) { return !Equals(ref left, ref right); }

        public float[] ToArray()
        {
            return new[] { M11, M12, M13, M14, M21, M22, M23, M24, M31, M32, M33, M34, M41, M42, M43, M44 };
        }

        public override string ToString()
        {
            return string.Format("[M11:{0} M12:{1} M13:{2} M14:{3}] [M21:{4} M22:{5} M23:{6} M24:{7}] [M31:{8} M32:{9} M33:{10} M34:{11}] [M41:{12} M42:{13} M43:{14} M44:{15}]",
                M11, M12, M13, M14, M21, M22, M23, M24, M31, M32, M33, M34, M41, M42, M43, M44);
        }

        public string ToString(string numberFormat) { return ToString(); }

        public override int GetHashCode()
        {
            int h = 0;
            foreach (var f in ToArray()) h += f.GetHashCode();
            return h;
        }

        public override bool Equals(object obj) { return obj is Matrix && Equals((Matrix)obj); }

        public bool Equals(Matrix other)
        {
            var a = ToArray();
            var b = other.ToArray();
            for (int i = 0; i < 16; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static bool Equals(ref Matrix value1, ref Matrix value2) { return value1.Equals(value2); }
    }
}
