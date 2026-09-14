using System.Security.Cryptography;

namespace SkyEyePMS.Core.Security;

/// <summary>
/// RFC 6238 TOTP（SHA-1 / 6 位 / 30 秒步长，与主流验证器 App 兼容）。
/// 密钥为 Base32 字符串；验证允许 ±1 步时钟偏移。
/// </summary>
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string GenerateSecret(int byteCount = 20)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Base32Encode(bytes);
    }

    public static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(value >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(value << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string encoded)
    {
        var clean = encoded.TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        var bytes = new List<byte>(clean.Length * 5 / 8);
        int bits = 0, value = 0;
        foreach (var c in clean)
        {
            var idx = Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException($"非法 Base32 字符：{c}");
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return bytes.ToArray();
    }

    /// <summary>计算指定时间步的 6 位验证码</summary>
    public static string ComputeCode(string base32Secret, DateTimeOffset timestamp)
    {
        var step = timestamp.ToUnixTimeSeconds() / 30;
        return ComputeCode(base32Secret, step);
    }

    public static string ComputeCode(string base32Secret, long step)
    {
        var key = Base32Decode(base32Secret);
        var counter = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16)
                   | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>校验验证码，允许 ±window 步偏移（默认 ±1 = ±30 秒）</summary>
    public static bool Verify(string base32Secret, string code, DateTimeOffset timestamp, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length != 6 || !long.TryParse(code.Trim(), out _))
            return false;
        var currentStep = timestamp.ToUnixTimeSeconds() / 30;
        for (var i = -window; i <= window; i++)
        {
            if (ComputeCode(base32Secret, currentStep + i) == code.Trim()) return true;
        }
        return false;
    }

    /// <summary>otpauth:// 统一 URI（验证器 App 扫码/手动录入用）</summary>
    public static string BuildOtpAuthUri(string secret, string account, string issuer = "SkyEyePMS") =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";
}
