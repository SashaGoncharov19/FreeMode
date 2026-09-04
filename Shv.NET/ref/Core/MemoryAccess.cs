using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using GTA.Math;

namespace GTA.Native
{
    /// <summary>Mirrors MemoryAccess from Shv.NET/source/core/NativeMemory.hpp.</summary>
    public static unsafe class MemoryAccess
    {
        public static ulong modelHashTable, modelNum2, modelNum3, modelNum4;
        public static int modelNum1;
        public static ushort modelHashEntries;
        public static ReadOnlyCollection<ReadOnlyCollection<int>> vehicleModels;
        public static int* _cursorSpriteAddr;
        public static IntPtr _cellEmailBconPtr;
        public static IntPtr _stringPtr;
        public static IntPtr _nullString;

        public static int GetGameVersion() { throw Stub.NotAvailable(); }

        public static sbyte ReadSByte(IntPtr address) { throw Stub.NotAvailable(); }
        public static byte ReadByte(IntPtr address) { throw Stub.NotAvailable(); }
        public static short ReadShort(IntPtr address) { throw Stub.NotAvailable(); }
        public static ushort ReadUShort(IntPtr address) { throw Stub.NotAvailable(); }
        public static int ReadInt(IntPtr address) { throw Stub.NotAvailable(); }
        public static uint ReadUInt(IntPtr address) { throw Stub.NotAvailable(); }
        public static float ReadFloat(IntPtr address) { throw Stub.NotAvailable(); }
        public static Vector3 ReadVector3(IntPtr address) { throw Stub.NotAvailable(); }
        public static string ReadString(IntPtr address) { throw Stub.NotAvailable(); }
        public static IntPtr ReadPtr(IntPtr address) { throw Stub.NotAvailable(); }
        public static Matrix ReadMatrix(IntPtr address) { throw Stub.NotAvailable(); }
        public static long ReadLong(IntPtr address) { throw Stub.NotAvailable(); }
        public static ulong ReadULong(IntPtr address) { throw Stub.NotAvailable(); }
        public static void WriteSByte(IntPtr address, sbyte value) { throw Stub.NotAvailable(); }
        public static void WriteByte(IntPtr address, byte value) { throw Stub.NotAvailable(); }
        public static void WriteShort(IntPtr address, short value) { throw Stub.NotAvailable(); }
        public static void WriteUShort(IntPtr address, ushort value) { throw Stub.NotAvailable(); }
        public static void WriteInt(IntPtr address, int value) { throw Stub.NotAvailable(); }
        public static void WriteUInt(IntPtr address, uint value) { throw Stub.NotAvailable(); }
        public static void WriteFloat(IntPtr address, float value) { throw Stub.NotAvailable(); }
        public static void WriteVector3(IntPtr address, Vector3 value) { throw Stub.NotAvailable(); }
        public static void WriteMatrix(IntPtr address, Matrix value) { throw Stub.NotAvailable(); }
        public static void WriteLong(IntPtr address, long value) { throw Stub.NotAvailable(); }
        public static void WriteULong(IntPtr address, ulong value) { throw Stub.NotAvailable(); }
        public static void SetBit(IntPtr address, int bit) { throw Stub.NotAvailable(); }
        public static void ClearBit(IntPtr address, int bit) { throw Stub.NotAvailable(); }
        public static bool IsBitSet(IntPtr address, int bit) { throw Stub.NotAvailable(); }
        public static uint GetHashKey(string toHash) { throw Stub.NotAvailable(); }
        public static string GetGXTEntryByHash(int Hash) { throw Stub.NotAvailable(); }

        public static IntPtr GetEntityAddress(int handle) { throw Stub.NotAvailable(); }
        public static IntPtr GetPlayerAddress(int handle) { throw Stub.NotAvailable(); }
        public static IntPtr GetCheckpointAddress(int handle) { throw Stub.NotAvailable(); }
        public static IntPtr GetEntityBoneMatrixAddress(int handle, int boneIndex) { throw Stub.NotAvailable(); }
        public static IntPtr GetEntityBonePoseAddress(int handle, int boneIndex) { throw Stub.NotAvailable(); }
        public static IntPtr GetPtfxAddress(int handle) { throw Stub.NotAvailable(); }
        public static int GetEntityBoneCount(int handle) { throw Stub.NotAvailable(); }
        public static float ReadWorldGravity() { throw Stub.NotAvailable(); }
        public static void WriteWorldGravity(float value) { throw Stub.NotAvailable(); }
        public static int ReadCursorSprite() { throw Stub.NotAvailable(); }
        public static IntPtr GetGameplayCameraAddress() { throw Stub.NotAvailable(); }
        public static IntPtr GetCameraAddress(int handle) { throw Stub.NotAvailable(); }

        public static int[] GetEntityHandles() { throw Stub.NotAvailable(); }
        public static int[] GetEntityHandles(Vector3 position, float radius) { throw Stub.NotAvailable(); }
        public static int[] GetVehicleHandles(int[] modelhashes) { throw Stub.NotAvailable(); }
        public static int[] GetVehicleHandles(Vector3 position, float radius, int[] modelhashes) { throw Stub.NotAvailable(); }
        public static int[] GetPedHandles(int[] modelhashes) { throw Stub.NotAvailable(); }
        public static int[] GetPedHandles(Vector3 position, float radius, int[] modelhashes) { throw Stub.NotAvailable(); }
        public static int[] GetPropHandles(int[] modelhashes) { throw Stub.NotAvailable(); }
        public static int[] GetPropHandles(Vector3 position, float radius, int[] modelhashes) { throw Stub.NotAvailable(); }
        public static int[] GetCheckpointHandles() { throw Stub.NotAvailable(); }
        public static int[] GetPickupObjectHandles() { throw Stub.NotAvailable(); }
        public static int[] GetPickupObjectHandles(Vector3 position, float radius) { throw Stub.NotAvailable(); }
        public static int GetNumberOfVehicles() { throw Stub.NotAvailable(); }

        public static void SendEuphoriaMessage(int targetHandle, string message, Dictionary<string, object> _arguments) { throw Stub.NotAvailable(); }

        public static int CreateTexture(string filename) { throw Stub.NotAvailable(); }
        public static void DrawTexture(int id, int index, int level, int time, float sizeX, float sizeY, float centerX, float centerY, float posX, float posY, float rotation, float scaleFactor, Color color) { throw Stub.NotAvailable(); }

        public static ReadOnlyCollection<ReadOnlyCollection<int>> VehicleModels
        {
            get { return vehicleModels; }
        }

        public static bool IsModelAPed(int modelHash) { throw Stub.NotAvailable(); }

        /// <summary>Number of memory patterns that were not found in this game build (0 = everything is fine).</summary>
        public static int MissingPatternCount { get { return _missingPatterns.Count; } }
        /// <summary>Names of the memory patterns that were not found in this game build.</summary>
        public static string[] MissingPatterns { get { return _missingPatterns.ToArray(); } }
        public static List<string> _missingPatterns = new List<string>();

        public static IntPtr CellEmailBcon { get { return _cellEmailBconPtr; } }
        public static IntPtr StringPtr { get { return _stringPtr; } }
        public static IntPtr NullString { get { return _nullString; } }

        public static ulong GetEntitySkeletonData(int handle) { throw Stub.NotAvailable(); }
        public static void GenerateVehicleModelList() { throw Stub.NotAvailable(); }
        public static ulong FindPattern(sbyte* pattern, sbyte* mask) { throw Stub.NotAvailable(); }
    }
}
