using System;

namespace GTANetworkShared
{
    /// <summary>Error codes of the RPC layer (the <c>code</c> of the rejected promise / the <see cref="RpcException"/>) and its limits.</summary>
    public static class RpcCodes
    {
        /// <summary>No answer within the timeout.</summary>
        public const string Timeout = "timeout";
        /// <summary>The handler's allow check refused the caller.</summary>
        public const string Denied = "denied";
        /// <summary>No handler of that name on the receiving side.</summary>
        public const string Unknown = "unknown";
        /// <summary>The caller sent more requests per second than the receiver allows.</summary>
        public const string Rate = "rate";
        /// <summary>The handler threw; the message is the exception's message (or what the script threw).</summary>
        public const string Handler = "handler";
        /// <summary>The payload is larger than <see cref="MaxPayloadBytes"/>.</summary>
        public const string Size = "size";
        /// <summary>The payload is not valid JSON.</summary>
        public const string Invalid = "invalid";
        /// <summary>The other side went away before answering (or the call was made while not connected).</summary>
        public const string Disconnected = "disconnected";

        public const int MaxPayloadBytes = 64 * 1024;
        public const int DefaultTimeoutMs = 10000;
        public const int MaxTimeoutMs = 60000;

        /// <summary>0 or less = the default; anything above the maximum is cut to it.</summary>
        public static int ClampTimeout(int timeoutMs)
        {
            if (timeoutMs <= 0) return DefaultTimeoutMs;
            return timeoutMs > MaxTimeoutMs ? MaxTimeoutMs : timeoutMs;
        }
    }

    /// <summary>A failed RPC: <see cref="Code"/> is one of <see cref="RpcCodes"/>. Thrown by a handler it sets the code the caller sees.</summary>
    public sealed class RpcException : Exception
    {
        public string Code { get; }

        public RpcException(string code, string message) : base(message ?? code)
        {
            Code = string.IsNullOrEmpty(code) ? RpcCodes.Handler : code;
        }
    }
}
