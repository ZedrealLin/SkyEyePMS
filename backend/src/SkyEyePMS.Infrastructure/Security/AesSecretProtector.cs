using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SkyEyePMS.Core.Security;

namespace SkyEyePMS.Infrastructure.Security;

/// <summary>安全相关配置（appsettings "Security" 节）</summary>
public class SecurityOptions
{
    /// <summary>
    /// 敏感字段加密密钥（任意长字符串，取 SHA-256 派生 AES-256 密钥）。
    /// 生产环境务必通过环境变量/配置文件覆盖默认值；更换密钥将导致已加密数据无法解密。
    /// </summary>
    public string EncryptionKey { get; set; } = "change-me-in-production";
}

/// <summary>AES-256-GCM 加密，密文格式 "enc1:" + base64(nonce[12] + tag[16] + cipher)</summary>
public class AesSecretProtector : ISecretProtector
{
    public const string Prefix = "enc1:";
    private readonly byte[] _key;

    public AesSecretProtector(IOptions<SecurityOptions> options)
    {
        var raw = string.IsNullOrWhiteSpace(options.Value.EncryptionKey)
            ? "change-me-in-production"
            : options.Value.EncryptionKey;
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    public string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length + tag.Length, cipher.Length);
        return Prefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(string cipher)
    {
        if (!IsEncrypted(cipher)) return cipher; // 手工配置的明文原样返回
        try
        {
            var payload = Convert.FromBase64String(cipher[Prefix.Length..]);
            var nonce = payload.AsSpan(0, 12);
            var tag = payload.AsSpan(12, 16);
            var data = payload.AsSpan(28);
            var plain = new byte[data.Length];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, data, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("解密失败：加密密钥与数据不匹配（更换过 Security:EncryptionKey？）");
        }
    }

    public bool IsEncrypted(string value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
}
