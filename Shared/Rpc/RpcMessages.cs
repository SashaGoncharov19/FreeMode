using ProtoBuf;

namespace GTANetworkShared
{
    /// <summary>Who sent an <see cref="RpcRequest"/>: the server, a client script, or a CEF page (through its client script).</summary>
    public static class RpcOrigin
    {
        public const int Server = 0;
        public const int Client = 1;
        public const int Cef = 2;
    }

    /// <summary>
    /// One request of the typed RPC layer (T-008): <c>API.rpc.call(name, args)</c> on the client, <c>API.callClient(player, name, args)</c>
    /// on the server. <see cref="Id"/> is unique per sender and direction; the answer is an <see cref="RpcResponse"/> with the same id.
    /// The arguments travel as one JSON value (<see cref="Payload"/>, at most <see cref="RpcCodes.MaxPayloadBytes"/>), so any
    /// JSON-serialisable shape works on both sides without a per-call schema.
    /// </summary>
    [ProtoContract]
    public class RpcRequest
    {
        [ProtoMember(1)] public uint Id { get; set; }
        /// <summary>Handler name; global on the server, so resources prefix theirs ("auth:login").</summary>
        [ProtoMember(2)] public string Name { get; set; }
        /// <summary>The resource of the sending script (client → server) or of the calling server script (server → client).</summary>
        [ProtoMember(3)] public string Resource { get; set; }
        /// <summary>The arguments as JSON; null or empty = no arguments.</summary>
        [ProtoMember(4)] public string Payload { get; set; }
        /// <summary>How long the sender waits for the answer (ms); the receiver may drop work that cannot finish in time.</summary>
        [ProtoMember(5)] public int TimeoutMs { get; set; }
        /// <summary>One of <see cref="RpcOrigin"/>.</summary>
        [ProtoMember(6)] public int Origin { get; set; }
    }

    /// <summary>The answer to an <see cref="RpcRequest"/>: the handler's return value as JSON, or an error code and message (never a stack trace).</summary>
    [ProtoContract]
    public class RpcResponse
    {
        [ProtoMember(1)] public uint Id { get; set; }
        [ProtoMember(2)] public bool Ok { get; set; }
        /// <summary>The handler's return value as JSON when <see cref="Ok"/>; "null" or empty for no value.</summary>
        [ProtoMember(3)] public string Payload { get; set; }
        /// <summary>One of <see cref="RpcCodes"/> when not <see cref="Ok"/>.</summary>
        [ProtoMember(4)] public string ErrorCode { get; set; }
        [ProtoMember(5)] public string ErrorMessage { get; set; }
    }
}
