using System;
using GTANetworkShared.Crypto;
using Lidgren.Network;

// Compiled into the server, the client and the bot (a linked source file): GTANetworkShared does not reference Lidgren.
namespace GTANetworkShared.Crypto
{
    /// <summary>
    /// Lidgren's per-message encryption hook over a <see cref="SessionCipher"/>: <c>message.Encrypt(this)</c> before a send
    /// replaces the payload with counter + ciphertext + tag, <c>message.Decrypt(this)</c> after a read restores it (false = drop).
    /// One instance per connection; the counter and replay window live in the cipher.
    /// </summary>
    public sealed class NetSessionEncryption : NetEncryption
    {
        public SessionCipher Cipher { get; }
        public string PeerFingerprint { get; }

        public NetSessionEncryption(NetPeer peer, SessionCipher cipher, string peerFingerprint) : base(peer)
        {
            Cipher = cipher;
            PeerFingerprint = peerFingerprint;
        }

        public override bool Encrypt(NetOutgoingMessage msg)
        {
            var sealedBytes = Cipher.Seal(msg.Data, 0, msg.LengthBytes);
            msg.Data = sealedBytes;
            msg.LengthBytes = sealedBytes.Length;
            return true;
        }

        public override bool Decrypt(NetIncomingMessage msg)
        {
            var plain = Cipher.Open(msg.Data, 0, msg.LengthBytes);
            if (plain == null) return false;
            msg.Data = plain;
            msg.LengthBytes = plain.Length;
            msg.Position = 0;
            return true;
        }

        public override void SetKey(byte[] data, int offset, int count)
        {
            throw new NotSupportedException("the session key is derived by the handshake");
        }
    }
}
