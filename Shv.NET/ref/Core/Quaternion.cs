using System;
using System.Runtime.InteropServices;

namespace GTA.Math
{
    /// <summary>Mirrors Shv.NET/source/scripting/Quaternion.hpp.</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct Quaternion : IEquatable<Quaternion>
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public Quaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public Quaternion(Vector3 value, float w)
        {
            X = value.X;
            Y = value.Y;
            Z = value.Z;
            W = w;
        }

        public static Quaternion Identity { get { return new Quaternion(0f, 0f, 0f, 1f); } }
        public Vector3 Axis { get { throw Stub.NotAvailable(); } }
        public float Angle { get { throw Stub.NotAvailable(); } }

        public float Length() { return (float)System.Math.Sqrt(X * X + Y * Y + Z * Z + W * W); }
        public float LengthSquared() { return X * X + Y * Y + Z * Z + W * W; }
        public void Normalize() { float l = Length(); if (l != 0f) { X /= l; Y /= l; Z /= l; W /= l; } }
        public void Conjugate() { X = -X; Y = -Y; Z = -Z; }
        public void Invert() { throw Stub.NotAvailable(); }

        public static Quaternion Add(Quaternion left, Quaternion right) { return new Quaternion(left.X + right.X, left.Y + right.Y, left.Z + right.Z, left.W + right.W); }
        public static Quaternion Divide(Quaternion left, Quaternion right) { throw Stub.NotAvailable(); }
        public static float Dot(Quaternion left, Quaternion right) { return left.X * right.X + left.Y * right.Y + left.Z * right.Z + left.W * right.W; }
        public static Quaternion Invert(Quaternion quaternion) { throw Stub.NotAvailable(); }
        public static Quaternion Lerp(Quaternion start, Quaternion end, float amount) { throw Stub.NotAvailable(); }
        public static Quaternion Slerp(Quaternion start, Quaternion end, float amount) { throw Stub.NotAvailable(); }
        public static Quaternion SlerpUnclamped(Quaternion a, Quaternion b, float t) { throw Stub.NotAvailable(); }
        public static Quaternion FromToRotation(Vector3 fromDirection, Vector3 toDirection) { throw Stub.NotAvailable(); }
        public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta) { throw Stub.NotAvailable(); }
        public static Quaternion Multiply(Quaternion left, Quaternion right) { throw Stub.NotAvailable(); }
        public static Quaternion Multiply(Quaternion quaternion, float scale) { return new Quaternion(quaternion.X * scale, quaternion.Y * scale, quaternion.Z * scale, quaternion.W * scale); }
        public static Quaternion Negate(Quaternion quaternion) { return new Quaternion(-quaternion.X, -quaternion.Y, -quaternion.Z, -quaternion.W); }
        public static Quaternion Normalize(Quaternion quaternion) { quaternion.Normalize(); return quaternion; }
        public static float AngleBetween(Quaternion a, Quaternion b) { throw Stub.NotAvailable(); }
        public static Quaternion Euler(float x, float y, float z) { throw Stub.NotAvailable(); }
        public static Quaternion Euler(Vector3 euler) { throw Stub.NotAvailable(); }
        public static Quaternion RotationAxis(Vector3 axis, float angle) { throw Stub.NotAvailable(); }
        public static Quaternion RotationMatrix(Matrix matrix) { throw Stub.NotAvailable(); }
        public static Quaternion RotationYawPitchRoll(float yaw, float pitch, float roll) { throw Stub.NotAvailable(); }
        public static Quaternion Subtract(Quaternion left, Quaternion right) { return new Quaternion(left.X - right.X, left.Y - right.Y, left.Z - right.Z, left.W - right.W); }

        public static Quaternion operator *(Quaternion left, Quaternion right) { return Multiply(left, right); }
        public static Vector3 operator *(Quaternion rotation, Vector3 point) { throw Stub.NotAvailable(); }
        public static Quaternion operator *(Quaternion quaternion, float scale) { return Multiply(quaternion, scale); }
        public static Quaternion operator *(float scale, Quaternion quaternion) { return Multiply(quaternion, scale); }
        public static Quaternion operator /(Quaternion left, float right) { return new Quaternion(left.X / right, left.Y / right, left.Z / right, left.W / right); }
        public static Quaternion operator +(Quaternion left, Quaternion right) { return Add(left, right); }
        public static Quaternion operator -(Quaternion left, Quaternion right) { return Subtract(left, right); }
        public static Quaternion operator -(Quaternion quaternion) { return Negate(quaternion); }
        public static bool operator ==(Quaternion left, Quaternion right) { return Equals(ref left, ref right); }
        public static bool operator !=(Quaternion left, Quaternion right) { return !Equals(ref left, ref right); }

        public override string ToString() { return string.Format("X:{0} Y:{1} Z:{2} W:{3}", X, Y, Z, W); }
        public string ToString(string numberFormat) { return string.Format("X:{0} Y:{1} Z:{2} W:{3}", X.ToString(numberFormat), Y.ToString(numberFormat), Z.ToString(numberFormat), W.ToString(numberFormat)); }
        public override int GetHashCode() { return X.GetHashCode() + Y.GetHashCode() + Z.GetHashCode() + W.GetHashCode(); }
        public override bool Equals(object obj) { return obj is Quaternion && Equals((Quaternion)obj); }
        public bool Equals(Quaternion other) { return X == other.X && Y == other.Y && Z == other.Z && W == other.W; }
        public static bool Equals(ref Quaternion value1, ref Quaternion value2) { return value1.X == value2.X && value1.Y == value2.Y && value1.Z == value2.Z && value1.W == value2.W; }
    }
}
