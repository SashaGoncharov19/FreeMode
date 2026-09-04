using System;

namespace GTA
{
    internal static class Stub
    {
        internal static Exception NotAvailable()
        {
            return new PlatformNotSupportedException(
                "This is the ScriptHookVDotNet reference stub. It exists only so that GTANetwork.dll can be compiled " +
                "without MSVC; the real ScriptHookVDotNet.dll (built from Shv.NET on Windows) must be used in game.");
        }
    }
}
