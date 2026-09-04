using System;
using System.Runtime.InteropServices;

namespace GTA.Math
{
    /// <summary>Mirrors Shv.NET/source/scripting/Vector2.hpp.</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Vector2 : IEquatable<Vector2>
    {
        public float X;
        public float Y;

        public Vector2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public Vector2 Normalized { get { return Normalize(this); } }
        public static Vector2 Zero { get { return new Vector2(0f, 0f); } }
        public static Vector2 Up { get { return new Vector2(0f, 1f); } }
        public static Vector2 Down { get { return new Vector2(0f, -1f); } }
        public static Vector2 Right { get { return new Vector2(1f, 0f); } }
        public static Vector2 Left { get { return new Vector2(-1f, 0f); } }

        public float this[int index]
        {
            get
            {
                switch (index)
                {
                    case 0: return X;
                    case 1: return Y;
                }
                throw new ArgumentOutOfRangeException("index", "Indices for Vector2 run from 0 to 1, inclusive.");
            }
            set
            {
                switch (index)
                {
                    case 0: X = value; return;
                    case 1: Y = value; return;
                }
                throw new ArgumentOutOfRangeException("index", "Indices for Vector2 run from 0 to 1, inclusive.");
            }
        }

        public float Length() { return (float)System.Math.Sqrt(X * X + Y * Y); }
        public float LengthSquared() { return X * X + Y * Y; }
        public void Normalize() { float l = Length(); if (l != 0f) { X /= l; Y /= l; } }
        public float DistanceTo(Vector2 position) { return (position - this).Length(); }
        public float DistanceToSquared(Vector2 position) { return (position - this).LengthSquared(); }
        public static float Distance(Vector2 position1, Vector2 position2) { return (position1 - position2).Length(); }
        public static float DistanceSquared(Vector2 position1, Vector2 position2) { return (position1 - position2).LengthSquared(); }
        public static float Angle(Vector2 from, Vector2 to) { return (float)(System.Math.Acos(Clamp01(Dot(from.Normalized, to.Normalized))) * (180.0 / System.Math.PI)); }
        public static float SignedAngle(Vector2 from, Vector2 to) { throw Stub.NotAvailable(); }
        public float ToHeading() { return (float)(System.Math.Atan2(X, -Y) * (180.0 / System.Math.PI)); }
        public static Vector2 RandomXY() { throw Stub.NotAvailable(); }

        public static Vector2 Add(Vector2 left, Vector2 right) { return new Vector2(left.X + right.X, left.Y + right.Y); }
        public static Vector2 Subtract(Vector2 left, Vector2 right) { return new Vector2(left.X - right.X, left.Y - right.Y); }
        public static Vector2 Multiply(Vector2 value, float scale) { return new Vector2(value.X * scale, value.Y * scale); }
        public static Vector2 Modulate(Vector2 left, Vector2 right) { return new Vector2(left.X * right.X, left.Y * right.Y); }
        public static Vector2 Divide(Vector2 value, float scale) { return new Vector2(value.X / scale, value.Y / scale); }
        public static Vector2 Negate(Vector2 value) { return new Vector2(-value.X, -value.Y); }
        public static Vector2 Clamp(Vector2 value, Vector2 min, Vector2 max)
        {
            return new Vector2(System.Math.Max(min.X, System.Math.Min(max.X, value.X)), System.Math.Max(min.Y, System.Math.Min(max.Y, value.Y)));
        }
        public static Vector2 Lerp(Vector2 start, Vector2 end, float amount) { return new Vector2(start.X + (end.X - start.X) * amount, start.Y + (end.Y - start.Y) * amount); }
        public static Vector2 Normalize(Vector2 vector) { vector.Normalize(); return vector; }
        public static float Dot(Vector2 left, Vector2 right) { return left.X * right.X + left.Y * right.Y; }
        public static Vector2 Reflect(Vector2 vector, Vector2 normal) { float d = 2f * Dot(vector, normal); return new Vector2(vector.X - d * normal.X, vector.Y - d * normal.Y); }
        public static Vector2 Minimize(Vector2 value1, Vector2 value2) { return new Vector2(System.Math.Min(value1.X, value2.X), System.Math.Min(value1.Y, value2.Y)); }
        public static Vector2 Maximize(Vector2 value1, Vector2 value2) { return new Vector2(System.Math.Max(value1.X, value2.X), System.Math.Max(value1.Y, value2.Y)); }

        public static Vector2 operator +(Vector2 left, Vector2 right) { return Add(left, right); }
        public static Vector2 operator -(Vector2 left, Vector2 right) { return Subtract(left, right); }
        public static Vector2 operator -(Vector2 value) { return Negate(value); }
        public static Vector2 operator *(Vector2 vector, float scale) { return Multiply(vector, scale); }
        public static Vector2 operator *(float scale, Vector2 vector) { return Multiply(vector, scale); }
        public static Vector2 operator /(Vector2 vector, float scale) { return Divide(vector, scale); }
        public static bool operator ==(Vector2 left, Vector2 right) { return Equals(ref left, ref right); }
        public static bool operator !=(Vector2 left, Vector2 right) { return !Equals(ref left, ref right); }

        public override string ToString() { return string.Format("X:{0} Y:{1}", X, Y); }
        public string ToString(string numberFormat) { return string.Format("X:{0} Y:{1}", X.ToString(numberFormat), Y.ToString(numberFormat)); }
        public override int GetHashCode() { return X.GetHashCode() + Y.GetHashCode(); }
        public override bool Equals(object obj) { return obj is Vector2 && Equals((Vector2)obj); }
        public bool Equals(Vector2 other) { return X == other.X && Y == other.Y; }
        public static bool Equals(ref Vector2 value1, ref Vector2 value2) { return value1.X == value2.X && value1.Y == value2.Y; }

        private static float Clamp01(float v) { return v < -1f ? -1f : (v > 1f ? 1f : v); }
    }
}
