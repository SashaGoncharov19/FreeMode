using System;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace GTANetworkShared.Crypto
{
    /// <summary>An X25519 key pair: 32 private bytes, 32 public bytes.</summary>
    public sealed class KeyPair
    {
        public byte[] PrivateKey { get; }
        public byte[] PublicKey { get; }

        public KeyPair(byte[] privateKey)
        {
            if (privateKey == null || privateKey.Length != 32) throw new ArgumentException("an X25519 private key has 32 bytes");
            PrivateKey = privateKey;
            PublicKey = new X25519PrivateKeyParameters(privateKey, 0).GeneratePublicKey().GetEncoded();
        }

        public static KeyPair Generate()
        {
            var key = new X25519PrivateKeyParameters(new SecureRandom());
            return new KeyPair(key.GetEncoded());
        }
    }

    /// <summary>
    /// The session handshake (T-009): the client sends an ephemeral X25519 public key in its hail, the server answers with its
    /// static public key in the approval hail; both derive the same 32-byte session key with HKDF-SHA256 over the shared
    /// secret (salt = client public key || server public key, info = <see cref="Info"/>). The client pins the server's key
    /// through the master list or the connect string, so a man in the middle cannot impersonate a listed server.
    /// </summary>
    public static class SessionHandshake
    {
        public const string Info = "gtan-session-v1";
        public const int KeyBytes = 32;

        public static byte[] DeriveSessionKey(byte[] ownPrivateKey, byte[] peerPublicKey, byte[] clientPublicKey, byte[] serverPublicKey)
        {
            if (peerPublicKey == null || peerPublicKey.Length != 32) throw new ArgumentException("the peer's X25519 public key must have 32 bytes");
            var agreement = new X25519Agreement();
            agreement.Init(new X25519PrivateKeyParameters(ownPrivateKey, 0));
            var shared = new byte[agreement.AgreementSize];
            agreement.CalculateAgreement(new X25519PublicKeyParameters(peerPublicKey, 0), shared, 0);
            if (IsAllZero(shared)) throw new InvalidOperationException("the X25519 agreement produced a zero secret (low-order public key)");

            var salt = new byte[clientPublicKey.Length + serverPublicKey.Length];
            Buffer.BlockCopy(clientPublicKey, 0, salt, 0, clientPublicKey.Length);
            Buffer.BlockCopy(serverPublicKey, 0, salt, clientPublicKey.Length, serverPublicKey.Length);
            var hkdf = new HkdfBytesGenerator(new Sha256Digest());
            hkdf.Init(new HkdfParameters(shared, salt, System.Text.Encoding.ASCII.GetBytes(Info)));
            var key = new byte[KeyBytes];
            hkdf.GenerateBytes(key, 0, key.Length);
            Array.Clear(shared, 0, shared.Length);
            return key;
        }

        /// <summary>The first 8 bytes of SHA-256 over a public key, as 16 hex characters: what the banner and the logs show.</summary>
        public static string Fingerprint(byte[] publicKey)
        {
            if (publicKey == null) return "(none)";
            var digest = new Sha256Digest();
            digest.BlockUpdate(publicKey, 0, publicKey.Length);
            var hash = new byte[digest.GetDigestSize()];
            digest.DoFinal(hash, 0);
            return ToHex(hash, 8);
        }

        public static string ToHex(byte[] bytes, int count = -1)
        {
            if (bytes == null) return "";
            if (count < 0 || count > bytes.Length) count = bytes.Length;
            var chars = new char[count * 2];
            for (var i = 0; i < count; i++)
            {
                chars[i * 2] = "0123456789abcdef"[bytes[i] >> 4];
                chars[i * 2 + 1] = "0123456789abcdef"[bytes[i] & 15];
            }
            return new string(chars);
        }

        /// <summary>Parses hex (case-insensitive, optional spaces or colons); null when it is not hex of the expected length.</summary>
        public static byte[] FromHex(string text, int expectedBytes = -1)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var clean = text.Replace(" ", "").Replace(":", "").Trim();
            if (clean.Length % 2 != 0) return null;
            if (expectedBytes >= 0 && clean.Length != expectedBytes * 2) return null;
            var bytes = new byte[clean.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                var hi = HexValue(clean[i * 2]);
                var lo = HexValue(clean[i * 2 + 1]);
                if (hi < 0 || lo < 0) return null;
                bytes[i] = (byte)(hi * 16 + lo);
            }
            return bytes;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static bool IsAllZero(byte[] bytes)
        {
            foreach (var b in bytes) if (b != 0) return false;
            return true;
        }
    }
}
