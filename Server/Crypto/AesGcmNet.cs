using System;
using System.Security.Cryptography;
using GTANetworkShared.Crypto;

namespace GTANetworkServer.Crypto
{
    /// <summary>
    /// AES-256-GCM through System.Security.Cryptography.AesGcm (AES-NI on .NET 10). The server relays every sync packet once
    /// per recipient, so the cipher must be cheap; the client on .NET Framework uses the BouncyCastle implementation in
    /// GTANetworkShared instead (same wire format). Also compiled into the bot.
    /// </summary>
    public sealed class AesGcmNet : IAeadCipher
    {
        private readonly AesGcm _aes;

        public AesGcmNet(byte[] key)
        {
            _aes = new AesGcm(key, SessionCipher.TagBytes);
        }

        /// <summary>Makes every SessionCipher of this process use AesGcm.</summary>
        public static void Install()
        {
            SessionCipher.CipherFactory = key => new AesGcmNet(key);
        }

        public void Seal(byte[] nonce, byte[] input, int offset, int count, byte[] output, int outputOffset)
        {
            _aes.Encrypt(nonce, new ReadOnlySpan<byte>(input, offset, count), new Span<byte>(output, outputOffset, count),
                new Span<byte>(output, outputOffset + count, SessionCipher.TagBytes));
        }

        public bool Open(byte[] nonce, byte[] input, int offset, int count, byte[] output, int outputOffset)
        {
            var plainLength = count - SessionCipher.TagBytes;
            if (plainLength < 0) return false;
            try
            {
                _aes.Decrypt(nonce, new ReadOnlySpan<byte>(input, offset, plainLength), new ReadOnlySpan<byte>(input, offset + plainLength, SessionCipher.TagBytes),
                    new Span<byte>(output, outputOffset, plainLength));
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _aes.Dispose();
        }
    }
}
