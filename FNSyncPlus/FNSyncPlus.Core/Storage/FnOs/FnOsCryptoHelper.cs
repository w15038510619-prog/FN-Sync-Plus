using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FNSyncPlus.Core.Storage.FnOs
{
    /// <summary>
    /// 飞牛 fnOS 专有加密算法与哈希签名助手
    /// 严格实现 RSA PKCS#1 v1.5 + AES-256-CBC PKCS7 + HMAC-SHA256 签名体系
    /// </summary>
    public static class FnOsCryptoHelper
    {
        /// <summary>
        /// 使用服务端下发的 RSA 公钥加密客户端生成的 AES 密钥
        /// </summary>
        public static byte[] EncryptAesKeyWithRsa(string pemPubKey, byte[] aesKey)
        {
            if (string.IsNullOrWhiteSpace(pemPubKey))
                throw new ArgumentException("RSA public key cannot be empty", nameof(pemPubKey));

            using var rsa = RSA.Create();
            rsa.ImportFromPem(pemPubKey.Trim());
            return rsa.Encrypt(aesKey, RSAEncryptionPadding.Pkcs1);
        }

        /// <summary>
        /// 使用 AES-256-CBC 算法加密数据 (PKCS7 填充)
        /// </summary>
        public static byte[] EncryptAesCbc(byte[] plainData, byte[] key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            return encryptor.TransformFinalBlock(plainData, 0, plainData.Length);
        }

        /// <summary>
        /// 使用 AES-256-CBC 算法解密数据 (PKCS7 填充)
        /// </summary>
        public static byte[] DecryptAesCbc(byte[] cipherData, byte[] key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(cipherData, 0, cipherData.Length);
        }

        /// <summary>
        /// 计算 HMAC-SHA256 摘要
        /// </summary>
        public static byte[] ComputeHmacSha256(byte[] hmacKey, byte[] data)
        {
            using var hmac = new HMACSHA256(hmacKey);
            return hmac.ComputeHash(data);
        }

        /// <summary>
        /// 生成 WebSocket RPC 带 44 字节前置 HMAC-SHA256 签名的传输报文 (Signature + JSON)
        /// </summary>
        public static string FormatSignedRpcMessage(byte[] hmacKey, string jsonStr)
        {
            byte[] dataBytes = Encoding.UTF8.GetBytes(jsonStr);
            byte[] hash = ComputeHmacSha256(hmacKey, dataBytes);
            string sigBase64 = Convert.ToBase64String(hash);
            return sigBase64 + jsonStr;
        }

        /// <summary>
        /// 计算 HTTP POST /upload 头部的 Trim-Sign 签名 (Base64(HMAC-SHA256(SecretKey, Trim-Path)))
        /// </summary>
        public static string ComputeTrimUploadSign(byte[] hmacKey, string trimPath)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(trimPath);
            byte[] hash = ComputeHmacSha256(hmacKey, pathBytes);
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// 生成随机字节数组
        /// </summary>
        public static byte[] GenerateRandomBytes(int length)
        {
            byte[] buffer = new byte[length];
            RandomNumberGenerator.Fill(buffer);
            return buffer;
        }

        /// <summary>
        /// 生成唯一的设备标识 (did)
        /// </summary>
        public static string GenerateDeviceId()
        {
            string t = Convert.ToBase64String(Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()))
                .Replace("+", "").Replace("/", "").Replace("=", "").ToLowerInvariant();
            string r1 = Guid.NewGuid().ToString("N")[..12];
            string r2 = Guid.NewGuid().ToString("N")[..12];
            return $"{t}-{r1}-{r2}";
        }

        /// <summary>
        /// 生成请求标识符 (不超过 25 字符)
        /// </summary>
        public static string GenerateReqId()
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string rand = Guid.NewGuid().ToString("N")[..10];
            return $"{ts}{rand}";
        }

        /// <summary>
        /// 敏感字符串脱敏 (日志安全打印)
        /// </summary>
        public static string MaskSensitive(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "<empty>";
            if (value.Length <= 6) return "***";
            return $"{value[..2]}***{value[^2..]}";
        }
    }
}
