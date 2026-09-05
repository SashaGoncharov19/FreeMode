using System;
using System.IO;

namespace GTANetworkShared.Crypto
{
    /// <summary>
    /// The server's static X25519 key: <c>server.key</c> next to the server (the private key as 64 hex characters), created at
    /// the first start. Its public key is what clients pin (master list entry, or <c>host:port#&lt;public key hex&gt;</c> in the
    /// connect string); losing the file means every pin breaks, so operators back it up like a certificate.
    /// </summary>
    public sealed class ServerKey
    {
        public KeyPair Pair { get; }
        public string Path { get; }
        public bool Created { get; }

        private ServerKey(KeyPair pair, string path, bool created)
        {
            Pair = pair;
            Path = path;
            Created = created;
        }

        public byte[] PublicKey => Pair.PublicKey;
        public string PublicKeyHex => SessionHandshake.ToHex(Pair.PublicKey);
        public string Fingerprint => SessionHandshake.Fingerprint(Pair.PublicKey);

        public static ServerKey LoadOrCreate(string path)
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                var privateKey = SessionHandshake.FromHex(text, 32);
                if (privateKey == null) throw new InvalidDataException(path + " does not contain a 32-byte X25519 private key in hex; delete it to create a new key (clients that pinned the old public key will refuse to connect)");
                return new ServerKey(new KeyPair(privateKey), path, false);
            }
            var pair = KeyPair.Generate();
            File.WriteAllText(path, SessionHandshake.ToHex(pair.PrivateKey) + Environment.NewLine);
            return new ServerKey(pair, path, true);
        }
    }
}
