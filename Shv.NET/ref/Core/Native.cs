using System;
using System.Collections.Generic;

namespace GTA.Native
{
    public interface INativeValue
    {
        ulong NativeValue { get; set; }
    }

    /// <summary>Mirrors InputArgument from Shv.NET/source/core/Native.hpp.</summary>
    public class InputArgument
    {
        internal ulong _data;

        public InputArgument(object value)
        {
            throw Stub.NotAvailable();
        }

        public override string ToString()
        {
            return _data.ToString();
        }

        // Value types (C++/CLI "char" is System.SByte, "unsigned char" is System.Byte)
        public static implicit operator InputArgument(bool value) { return new InputArgument((object)value); }
        public static implicit operator InputArgument(sbyte value) { return new InputArgument((object)(int)value); }
        public static implicit operator InputArgument(byte value) { return new InputArgument((object)(int)value); }
        public static implicit operator InputArgument(short value) { return new InputArgument((object)(int)value); }
        public static implicit operator InputArgument(ushort value) { return new InputArgument((object)(int)value); }
        public static implicit operator InputArgument(int value) { return new InputArgument((object)value); }
        public static implicit operator InputArgument(uint value) { return new InputArgument((object)value); }
        public static implicit operator InputArgument(long value) { return new InputArgument((object)value); }
        public static implicit operator InputArgument(ulong value) { return new InputArgument((object)value); }
        public static implicit operator InputArgument(float value) { return new InputArgument((object)value); }
        public static implicit operator InputArgument(double value) { return new InputArgument((object)(float)value); }
        public static implicit operator InputArgument(Enum value) { return new InputArgument((object)value); }
        // C++/CLI declares a single conversion from INativeValue; C# cannot declare conversions from an
        // interface, so the stub declares one per root implementer instead (same effect for callers).
        public static implicit operator InputArgument(PoolObject value) { return new InputArgument((object)value); }
        public static implicit operator InputArgument(Model value) { return new InputArgument((object)value); }
        public static implicit operator InputArgument(Player value) { return new InputArgument((object)value); }
        public static implicit operator InputArgument(RelationshipGroup value) { return new InputArgument((object)value); }
        public static implicit operator InputArgument(Scaleform value) { return new InputArgument((object)value); }
        public static implicit operator InputArgument(WeaponAsset value) { return new InputArgument((object)value); }

        // String types
        public static implicit operator InputArgument(string value) { return new InputArgument((object)value); }

        // Pointer types (any T* converts implicitly to void*, exactly like the C++/CLI original)
        public static implicit operator InputArgument(IntPtr value) { return new InputArgument((object)value); }
        public static unsafe implicit operator InputArgument(void* value) { return new InputArgument((object)new IntPtr(value)); }
    }

    /// <summary>Mirrors OutputArgument from Shv.NET/source/core/Native.hpp.</summary>
    public class OutputArgument : InputArgument, IDisposable
    {
        public OutputArgument() : base(IntPtr.Zero)
        {
        }

        public OutputArgument(object initvalue) : base(IntPtr.Zero)
        {
        }

        ~OutputArgument()
        {
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        public T GetResult<T>()
        {
            throw Stub.NotAvailable();
        }
    }

    /// <summary>Mirrors Function from Shv.NET/source/core/Native.hpp.</summary>
    public static class Function
    {
        public static T Call<T>(Hash hash, params InputArgument[] arguments)
        {
            throw Stub.NotAvailable();
        }

        public static void Call(Hash hash, params InputArgument[] arguments)
        {
            throw Stub.NotAvailable();
        }

        internal static T Call<T>(ulong hash, params InputArgument[] arguments)
        {
            throw Stub.NotAvailable();
        }
    }

    /// <summary>Mirrors CallCollection from Shv.NET/source/core/CallCollection.hpp.</summary>
    public class CallCollection
    {
        private readonly List<object> _tasks = new List<object>();

        public CallCollection()
        {
        }

        public void Call(Hash hash, params InputArgument[] arguments)
        {
            throw Stub.NotAvailable();
        }

        public int Execute()
        {
            throw Stub.NotAvailable();
        }
    }

    /// <summary>Mirrors GlobalVariable from Shv.NET/source/core/Native.hpp.</summary>
    public struct GlobalVariable
    {
        private IntPtr _address;

        private GlobalVariable(IntPtr address)
        {
            _address = address;
        }

        public static GlobalVariable Get(int index)
        {
            throw Stub.NotAvailable();
        }

        public IntPtr MemoryAddress
        {
            get { return _address; }
        }

        public T Read<T>() { throw Stub.NotAvailable(); }
        public void Write<T>(T value) { throw Stub.NotAvailable(); }
        public void WriteString(string value, int maxSize) { throw Stub.NotAvailable(); }
        public void SetBit(int index) { throw Stub.NotAvailable(); }
        public void ClearBit(int index) { throw Stub.NotAvailable(); }
        public bool IsBitSet(int index) { throw Stub.NotAvailable(); }
        public GlobalVariable GetStructField(int index) { throw Stub.NotAvailable(); }
        public GlobalVariable[] GetArray(int itemSize) { throw Stub.NotAvailable(); }
        public GlobalVariable GetArrayItem(int index, int itemSize) { throw Stub.NotAvailable(); }
    }
}
