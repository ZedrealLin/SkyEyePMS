using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SkyEyePMS.Core.Models;

namespace SkyEyePMS.Api.Auth;

/// <summary>JWT 配置（appsettings "Jwt" 节）</summary>
public class JwtOptions
{
    public string Issuer { get; set; } = "SkyEyePMS";
    public string Audience { get; set; } = "SkyEyePMS";
    /// <summary>签名密钥（生产必须设置 32+ 位随机串；未设置时每次启动随机生成，重启后需重新登录）</summary>
    public string SecretKey { get; set; } = string.Empty;
    public int ExpireHours { get; set; } = 12;
}

public class TokenService
{
    private readonly JwtOptions _options;
    private readonly byte[] _key;

    public TokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        var secret = _options.SecretKey;
        if (string.IsNullOrWhiteSpace(secret))
        {
            // 未配置密钥：随机生成（重启后所有 token 失效，仅适合试用）
            secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        }
        _key = Encoding.UTF8.GetBytes(secret);
    }

    public string CreateToken(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role)
        };
        var creds = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(Math.Max(1, _options.ExpireHours)),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>首次启动种子管理员账号</summary>
public static class AuthSeeder
{
    /// <summary>种子管理员密码（appsettings "Seed:AdminPassword"；未设置则随机生成并打印到控制台）</summary>
    public static async Task SeedAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        var users = services.GetRequiredService<Core.Repositories.IUserRepository>();
        if (await users.CountAsync() > 0) return;

        var username = config["Seed:AdminUsername"];
        if (string.IsNullOrWhiteSpace(username)) username = "admin";
        var password = config["Seed:AdminPassword"];
        var generated = false;
        if (string.IsNullOrWhiteSpace(password))
        {
            password = GeneratePassword();
            generated = true;
        }
        await users.CreateAsync(new User
        {
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11),
            Role = UserRoles.Admin,
            CreatedAt = DateTime.UtcNow
        });
        if (generated)
        {
            logger.LogWarning("""
                ============ SkyEyePMS 初始管理员账号 ============
                用户名：{Username}
                密码：{Password}
                （来自 Seed:AdminPassword 未配置时的随机生成，请尽快登录修改密码，
                  或在配置文件中显式设置 Seed:AdminPassword 后重新初始化数据库）
                =================================================
                """, username, password);
        }
        else
        {
            logger.LogInformation("已创建初始管理员账号 {Username}（密码来自 Seed:AdminPassword）", username);
        }
    }

    private static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%=";
        var all = upper + lower + digits + symbols;
        int Rng(int maxInclusive) => System.Security.Cryptography.RandomNumberGenerator.GetInt32(maxInclusive);
        var chars = new List<char>
        {
            upper[Rng(upper.Length)], lower[Rng(lower.Length)], digits[Rng(digits.Length)], symbols[Rng(symbols.Length)]
        };
        chars.AddRange(Enumerable.Range(0, 10).Select(_ => all[Rng(all.Length)]));
        // 洗牌
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = Rng(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }
}
