namespace SkyEyePMS.Core.Security;

/// <summary>对称加密保护敏感配置（SNMP Community、Webhook Secret、SMTP 密码等）</summary>
public interface ISecretProtector
{
    /// <summary>加密，返回 "enc1:" 前缀密文</summary>
    string Protect(string plain);
    /// <summary>解密；无前缀的输入按明文原样返回（兼容手工配置）</summary>
    string Unprotect(string cipher);
    bool IsEncrypted(string value);
}
