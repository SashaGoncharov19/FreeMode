using System;
using System.Threading;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace GTANetworkShared.Crypto
{
    /// <summary>AES-256-GCM with a 16-byte tag; the nonce is 12 bytes. Implemented with BouncyCastle here (any runtime) and
    /// with System.Security.Cryptography.AesGcm on .NET 10 (Server/Crypto/AesGcmNet.cs, hardware AES).</summary>
    public interface IAeadCipher : IDisposable
    {
        /// <summary>Encrypts <paramref name="count"/> bytes into <paramref name="output"/> at <paramref name="outputOffset"/>; writes count + 16 bytes.</summary>
        void Seal(byte[] nonce, byte[] input, int offset, int count, byte[] output, int outputOffset);
        /// <summary>Decrypts <paramref name="count"/> bytes (ciphertext + tag) into <paramref name="output"/>; false when the tag does not match.</summary>
        bool Open(byte[] nonce, byte[] input, int offset, int count, byte[] output, int outputOffset);
    }

    /// <summary>
    /// One direction's worth of message protection for a session (T-009): every message becomes
    /// <c>[8-byte counter][ciphertext][16-byte tag]</c>; the nonce is the direction byte and the counter, so the same key
    /// serves both directions with disjoint nonces; the receiver keeps a 128-message replay window per direction.
    /// Thread-safe: a lock per direction (the server relays from the tick thread and answers RPC from others).
    /// </summary>
    public sealed class SessionCipher : IDisposable
    {
        public const int TagBytes = 16;
        public const int CounterBytes = 8;
        public const int Overhead = CounterBytes + TagBytes;
        private const int WindowBits = 128;

        /// <summary>How an AEAD instance is made for a key: BouncyCastle by default; .NET hosts install System.Security.Cryptography.AesGcm.</summary>
        public static Func<byte[], IAeadCipher> CipherFactory = key => new BouncyCastleGcm(key);

        private readonly IAeadCipher _sealer;
        private readonly IAeadCipher _opener;
        private readonly byte[] _sendNonce = new byte[12];
        private readonly byte[] _receiveNonce = new byte[12];
        private readonly object _sendLock = new object();
        private readonly object _receiveLock = new object();
        private ulong _sendCounter;
        private ulong _receiveHighest;
        private ulong _windowLow, _windowHigh; // bits for counters highest-127 .. highest
        private bool _receivedAny;

        /// <param name="key">The 32-byte session key.</param>
        /// <param name="isServer">The server sends with direction 1 and receives direction 2; the client the other way round.</param>
        public SessionCipher(byte[] key, bool isServer)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("the session key must have 32 bytes");
            _sealer = CipherFactory(key);
            _opener = CipherFactory(key);
            _sendNonce[3] = (byte)(isServer ? 1 : 2);
            _receiveNonce[3] = (byte)(isServer ? 2 : 1);
        }

        public long MessagesSealed { get; private set; }
        public long MessagesOpened { get; private set; }
        public long MessagesRejected { get; private set; }

        /// <summary>Encrypts a message; the result holds the counter, the ciphertext and the tag.</summary>
        public byte[] Seal(byte[] plaintext, int offset, int count)
        {
            var output = new byte[CounterBytes + count + TagBytes];
            SealInto(plaintext, offset, count, output, 0);
            return output;
        }

        /// <summary>Encrypts a message into <paramref name="output"/> at <paramref name="outputOffset"/> (needs <c>count + Overhead</c> bytes there); returns the bytes written. No allocation: the relay's per-recipient path (T-023).</summary>
        public int SealInto(byte[] plaintext, int offset, int count, byte[] output, int outputOffset)
        {
            if (output == null || output.Length - outputOffset < CounterBytes + count + TagBytes) throw new ArgumentException("the output buffer is too small for the sealed message");
            lock (_sendLock)
            {
                var counter = ++_sendCounter;
                WriteCounter(_sendNonce, counter);
                for (var i = 0; i < CounterBytes; i++) output[outputOffset + i] = _sendNonce[4 + i];
                _sealer.Seal(_sendNonce, plaintext, offset, count, output, outputOffset + CounterBytes);
                MessagesSealed++;
                return CounterBytes + count + TagBytes;
            }
        }

        /// <summary>Decrypts a message made by <see cref="Seal"/>; null when it is too short, replayed, or the tag does not match.</summary>
        public byte[] Open(byte[] data, int offset, int count)
        {
            if (data == null || count < Overhead) return null;
            lock (_receiveLock)
            {
                ulong counter = 0;
                for (var i = 0; i < CounterBytes; i++) counter = (counter << 8) | data[offset + i];
                if (counter == 0 || IsReplay(counter)) { MessagesRejected++; return null; }
                WriteCounter(_receiveNonce, counter);
                var plain = new byte[count - Overhead];
                if (!_opener.Open(_receiveNonce, data, offset + CounterBytes, count - CounterBytes, plain, 0)) { MessagesRejected++; return null; }
                MarkReceived(counter);
                MessagesOpened++;
                return plain;
            }
        }

        private static void WriteCounter(byte[] nonce, ulong counter)
        {
            for (var i = 7; i >= 0; i--) { nonce[4 + i] = (byte)counter; counter >>= 8; }
        }

        private bool IsReplay(ulong counter)
        {
            if (!_receivedAny || counter > _receiveHighest) return false;
            var back = _receiveHighest - counter;
            if (back >= WindowBits) return true; // older than the window: refused
            return (back < 64 ? (_windowLow >> (int)back) & 1 : (_windowHigh >> (int)(back - 64)) & 1) == 1;
        }

        private void MarkReceived(ulong counter)
        {
            if (!_receivedAny || counter > _receiveHighest)
            {
                var shift = _receivedAny ? counter - _receiveHighest : 0;
                if (shift >= WindowBits) { _windowLow = 0; _windowHigh = 0; }
                else
                {
                    for (ulong i = 0; i < shift; i++)
                    {
                        _windowHigh = (_windowHigh << 1) | (_windowLow >> 63);
                        _windowLow <<= 1;
                    }
                }
                _windowLow |= 1;
                _receiveHighest = counter;
                _receivedAny = true;
                return;
            }
            var back = _receiveHighest - counter;
            if (back < 64) _windowLow |= 1UL << (int)back; else _windowHigh |= 1UL << (int)(back - 64);
        }

        public void Dispose()
        {
            _sealer.Dispose();
            _opener.Dispose();
        }

        /// <summary>AES-GCM through BouncyCastle: works on .NET Framework 4.8 (the in-game client) and everywhere else.</summary>
        private sealed class BouncyCastleGcm : IAeadCipher
        {
            private readonly KeyParameter _key;

            public BouncyCastleGcm(byte[] key) { _key = new KeyParameter(key); }

            public void Seal(byte[] nonce, byte[] input, int offset, int count, byte[] output, int outputOffset)
            {
                var gcm = new GcmBlockCipher(Org.BouncyCastle.Crypto.AesUtilities.CreateEngine());
                gcm.Init(true, new AeadParameters(_key, TagBytes * 8, nonce));
                var n = gcm.ProcessBytes(input, offset, count, output, outputOffset);
                gcm.DoFinal(output, outputOffset + n);
            }

            public bool Open(byte[] nonce, byte[] input, int offset, int count, byte[] output, int outputOffset)
            {
                var gcm = new GcmBlockCipher(Org.BouncyCastle.Crypto.AesUtilities.CreateEngine());
                gcm.Init(false, new AeadParameters(_key, TagBytes * 8, nonce));
                try
                {
                    var n = gcm.ProcessBytes(input, offset, count, output, outputOffset);
                    gcm.DoFinal(output, outputOffset + n);
                    return true;
                }
                catch (InvalidCipherTextException)
                {
                    return false;
                }
            }

            public void Dispose() { }
        }
    }
}
