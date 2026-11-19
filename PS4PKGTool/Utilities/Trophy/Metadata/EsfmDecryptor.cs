#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PS4PKGTool.Utilities.TrophyMetadata
{
    public sealed class EsfmDecryptor
    {
        private static readonly byte[] TrophyMasterKey =
        {
            0x21, 0xF4, 0x1A, 0x6B, 0xAD, 0x8A, 0x1D, 0x3E,
            0xCA, 0x7A, 0xD5, 0x86, 0xC1, 0x01, 0xB7, 0xA9
        };

        private readonly NpCommunicationIdResolver _idResolver = new();

        public byte[] Decrypt(ReadOnlySpan<byte> encryptedData, string npCommunicationId)
        {
            if (!_idResolver.IsValid(npCommunicationId))
                throw new ArgumentException("NP Communication ID 必须匹配 NPWRxxxxx_00。", nameof(npCommunicationId));
            if (encryptedData.Length < 32 || encryptedData.Length % 16 != 0)
                throw new InvalidDataException("ESFM 数据必须至少包含两个完整的 AES 块。");

            byte[] titleKey = DeriveTitleKey(npCommunicationId);
            byte[] decrypted;
            try
            {
                using Aes aes = CreateAes(titleKey, PaddingMode.PKCS7);
                using ICryptoTransform transform = aes.CreateDecryptor();
                byte[] encrypted = encryptedData.ToArray();
                decrypted = transform.TransformFinalBlock(encrypted, 0, encrypted.Length);
            }
            catch (CryptographicException ex)
            {
                throw new CryptographicException("ESFM 解密失败。NP Communication ID 可能不正确，或数据已损坏。", ex);
            }

            if (decrypted.Length < 17 || !decrypted.AsSpan(0, 16).SequenceEqual(new byte[16]))
                throw new CryptographicException("ESFM 解密后未出现要求的 16 字节零前缀。NP Communication ID 可能不正确，或不支持该 ESFM 变体。");

            byte[] xml = decrypted.AsSpan(16).ToArray();
            try
            {
                _ = new UTF8Encoding(false, true).GetString(xml);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("解密后的 ESFM 数据不是有效的 UTF-8。", ex);
            }
            return xml;
        }

        public byte[] DeriveTitleKey(string npCommunicationId)
        {
            if (!_idResolver.IsValid(npCommunicationId))
                throw new ArgumentException("NP Communication ID 必须匹配 NPWRxxxxx_00。", nameof(npCommunicationId));
            byte[] input = new byte[16];
            int written = Encoding.ASCII.GetBytes(npCommunicationId, input);
            if (written != 12)
                throw new ArgumentException("NP Communication ID 必须正好是 12 个 ASCII 字节。", nameof(npCommunicationId));

            using Aes aes = CreateAes(TrophyMasterKey, PaddingMode.None);
            using ICryptoTransform transform = aes.CreateEncryptor();
            return transform.TransformFinalBlock(input, 0, input.Length);
        }

        private static Aes CreateAes(byte[] key, PaddingMode padding)
        {
            if (key.Length != 16)
                throw new ArgumentException("AES-128 需要 16 字节密钥。", nameof(key));
            Aes aes = Aes.Create();
            aes.KeySize = 128;
            aes.BlockSize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = padding;
            aes.Key = key;
            aes.IV = new byte[16];
            return aes;
        }
    }
}
