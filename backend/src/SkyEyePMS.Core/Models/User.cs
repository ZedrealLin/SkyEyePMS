namespace SkyEyePMS.Core.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    /// <summary>admin（管理员）/ viewer（只读）</summary>
    public string Role { get; set; } = "viewer";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    /// <summary>TOTP 密钥密文（AES-GCM，启用两步验证后存在）</summary>
    public string? TotpSecret { get; set; }
    public bool TotpEnabled { get; set; }
}

public static class UserRoles
{
    public const string Admin = "admin";
    public const string Viewer = "viewer";
}
