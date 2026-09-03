using System;
using System.Runtime.InteropServices;

namespace GTA.Math
{
    /// <summary>Mirrors Shv.NET/source/scripting/Vector3.hpp.</summary>
    [Serializable]
    [StructLayout(LayoutKind.Explicit, Pack = 4)]
    public struct Vector3 : IEquatable<Vector3>
    {
        [FieldOffset(0)] public float X;
        [FieldOffset(4)] public float Y;
        [FieldOffset(8)] public float Z;
        [FieldOffset(12)] public float _padding;

        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
            _padding = 0f;
        }

        public Vector3 Normalized { get { return Normalize(this); } }
        public static Vector3 Zero { get { return new Vector3(0f, 0f, 0f); } }
        public static Vector3 WorldUp { get { return new Vector3(0f, 0f, 1f); } }
        public static Vector3 WorldDown { get { return new Vector3(0f, 0f, -1f); } }
        public static Vector3 WorldNorth { get { return new Vector3(0f, 1f, 0f); } }
        public static Vector3 WorldSouth { get { return new Vector3(0f, -1f, 0f); } }
        public static Vector3 WorldEast { get { return new Vector3(1f, 0f, 0f); } }
        public static Vector3 WorldWest { get { return new Vector3(-1f, 0f, 0f); } }
        public static Vector3 RelativeRight { get { return new Vector3(1f, 0f, 0f); } }
        public static Vector3 RelativeLeft { get { return new Vector3(-1f, 0f, 0f); } }
        public static Vector3 RelativeFront { get { return new Vector3(0f, 1f, 0f); } }
        public static Vector3 RelativeBack { get { return new Vector3(0f, -1f, 0f); } }
        public static Vector3 RelativeTop { get { return new Vector3(0f, 0f, 1f); } }
        public static Vector3 RelativeBottom { get { return new Vector3(0f, 0f, -1f); } }

        public float this[int index]
        {
            get
            {
                switch (index)
                {
                    case 0: return X;
                    case 1: return Y;
                    case 2: return Z;
                }
                throw new ArgumentOutOfRangeException("index", "Indices for Vector3 run from 0 to 2, inclusive.");
            }
            set
            {
                switch (index)
                {
                    case 0: X = value; return;
                    case 1: Y = value; return;
                    case 2: Z = value; return;
                }
                throw new ArgumentOutOfRangeException("index", "Indices for Vector3 run from 0 to 2, inclusive.");
            }
        }

        public float Length() { return (float)System.Math.Sqrt(X * X + Y * Y + Z * Z); }
        public float LengthSquared() { return X * X + Y * Y + Z * Z; }
        public void Normalize() { float l = Length(); if (l != 0f) { X /= l; Y /= l; Z /= l; } }
        public float DistanceTo(Vector3 position) { return (position - this).Length(); }
        public float DistanceToSquared(Vector3 position) { return (position - this).LengthSquared(); }
        public float DistanceTo2D(Vector3 position) { return Distance2D(this, position); }
        public float DistanceToSquared2D(Vector3 position) { return DistanceSquared2D(this, position); }
        public static float Distance(Vector3 position1, Vector3 position2) { return (position1 - position2).Length(); }
        public static float DistanceSquared(Vector3 position1, Vector3 position2) { return (position1 - position2).LengthSquared(); }
        public static float Distance2D(Vector3 position1, Vector3 position2) { return Distance(new Vector3(position1.X, position1.Y, 0f), new Vector3(position2.X, position2.Y, 0f)); }
        public static float DistanceSquared2D(Vector3 position1, Vector3 position2) { return DistanceSquared(new Vector3(position1.X, position1.Y, 0f), new Vector3(position2.X, position2.Y, 0f)); }
        public static float Angle(Vector3 from, Vector3 to) { return (float)(System.Math.Acos(Clamp01(Dot(from.Normalized, to.Normalized))) * (180.0 / System.Math.PI)); }
        public static float SignedAngle(Vector3 from, Vector3 to, Vector3 planeNormal) { throw Stub.NotAvailable(); }
        public float ToHeading() { return (float)(System.Math.Atan2(X, -Y) * (180.0 / System.Math.PI)); }
        public Vector3 Around(float distance) { throw Stub.NotAvailable(); }
        public static Vector3 RandomXY() { throw Stub.NotAvailable(); }
        public static Vector3 RandomXYZ() { throw Stub.NotAvailable(); }

        public static Vector3 Add(Vector3 left, Vector3 right) { return new Vector3(left.X + right.X, left.Y + right.Y, left.Z + right.Z); }
        public static Vector3 Subtract(Vector3 left, Vector3 right) { return new Vector3(left.X - right.X, left.Y - right.Y, left.Z - right.Z); }
        public static Vector3 Multiply(Vector3 value, float scale) { return new Vector3(value.X * scale, value.Y * scale, value.Z * scale); }
        public static Vector3 Modulate(Vector3 left, Vector3 right) { return new Vector3(left.X * right.X, left.Y * right.Y, left.Z * right.Z); }
        public static Vector3 Divide(Vector3 value, float scale) { return new Vector3(value.X / scale, value.Y / scale, value.Z / scale); }
        public static Vector3 Negate(Vector3 value) { return new Vector3(-value.X, -value.Y, -value.Z); }
        public static Vector3 Clamp(Vector3 value, Vector3 min, Vector3 max)
        {
            return new Vector3(
                System.Math.Max(min.X, System.Math.Min(max.X, value.X)),
                System.Math.Max(min.Y, System.Math.Min(max.Y, value.Y)),
                System.Math.Max(min.Z, System.Math.Min(max.Z, value.Z)));
        }
        public static Vector3 Lerp(Vector3 start, Vector3 end, float amount)
        {
            return new Vector3(start.X + (end.X - start.X) * amount, start.Y + (end.Y - start.Y) * amount, start.Z + (end.Z - start.Z) * amount);
        }
        public static Vector3 Normalize(Vector3 vector) { vector.Normalize(); return vector; }
        public static float Dot(Vector3 left, Vector3 right) { return left.X * right.X + left.Y * right.Y + left.Z * right.Z; }
        public static Vector3 Cross(Vector3 left, Vector3 right)
        {
            return new Vector3(left.Y * right.Z - left.Z * right.Y, left.Z * right.X - left.X * right.Z, left.X * right.Y - left.Y * right.X);
        }
        public static Vector3 Project(Vector3 vector, Vector3 onNormal) { return onNormal * Dot(vector, onNormal) / Dot(onNormal, onNormal); }
        public static Vector3 ProjectOnPlane(Vector3 vector, Vector3 planeNormal) { return vector - Project(vector, planeNormal); }
        public static Vector3 Reflect(Vector3 vector, Vector3 normal)
        {
            float d = 2f * ((vector.X * normal.X) + (vector.Y * normal.Y)) + (vector.Z * normal.Z);
            return new Vector3(vector.X - d * normal.X, vector.Y - d * normal.Y, vector.Z - d * normal.Z);
        }
        public static Vector3 Minimize(Vector3 value1, Vector3 value2)
        {
            return new Vector3(System.Math.Min(value1.X, value2.X), System.Math.Min(value1.Y, value2.Y), System.Math.Min(value1.Z, value2.Z));
        }
        public static Vector3 Maximize(Vector3 value1, Vector3 value2)
        {
            return new Vector3(System.Math.Max(value1.X, value2.X), System.Math.Max(value1.Y, value2.Y), System.Math.Max(value1.Z, value2.Z));
        }

        public static Vector3 operator +(Vector3 left, Vector3 right) { return Add(left, right); }
        public static Vector3 operator -(Vector3 left, Vector3 right) { return Subtract(left, right); }
        public static Vector3 operator -(Vector3 value) { return Negate(value); }
        public static Vector3 operator *(Vector3 vector, float scale) { return Multiply(vector, scale); }
        public static Vector3 operator *(float scale, Vector3 vector) { return Multiply(vector, scale); }
        public static Vector3 operator /(Vector3 vector, float scale) { return Divide(vector, scale); }
        public static bool operator ==(Vector3 left, Vector3 right) { return Equals(ref left, ref right); }
        public static bool operator !=(Vector3 left, Vector3 right) { return !Equals(ref left, ref right); }

        public override string ToString() { return string.Format("X:{0} Y:{1} Z:{2}", X, Y, Z); }
        public string ToString(string numberFormat) { return string.Format("X:{0} Y:{1} Z:{2}", X.ToString(numberFormat), Y.ToString(numberFormat), Z.ToString(numberFormat)); }
        public override int GetHashCode() { return X.GetHashCode() + Y.GetHashCode() + Z.GetHashCode(); }
        public override bool Equals(object obj) { return obj is Vector3 && Equals((Vector3)obj); }
        public bool Equals(Vector3 other) { return X == other.X && Y == other.Y && Z == other.Z; }
        public static bool Equals(ref Vector3 value1, ref Vector3 value2) { return value1.X == value2.X && value1.Y == value2.Y && value1.Z == value2.Z; }

        private static float Clamp01(float v) { return v < -1f ? -1f : (v > 1f ? 1f : v); }
    }

    /// <summary>For natives that require pointers to vectors (used internally by the scripting section).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
    internal struct NativeVector3
    {
        [FieldOffset(0x00)] public float X;
        [FieldOffset(0x08)] public float Y;
        [FieldOffset(0x10)] public float Z;

        public static implicit operator Vector3(NativeVector3 value) { return new Vector3(value.X, value.Y, value.Z); }
        public static implicit operator NativeVector3(Vector3 value) { return new NativeVector3 { X = value.X, Y = value.Y, Z = value.Z }; }
    }
}
