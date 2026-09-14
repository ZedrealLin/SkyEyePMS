using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using SkyEyePMS.Api.Auth;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Security;
using SkyEyePMS.Core.Services;

namespace SkyEyePMS.Api.Endpoints;

public static class AuthEndpoints
{
    /// <summary>两步验证中间态令牌（随机串 → userId，5 分钟有效）</summary>
    private static readonly ConcurrentDictionary<string, (int UserId, DateTimeOffset Expires)> PendingTotp = new();

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", async (LoginInput input, IUserRepository users, TokenService tokens,
            IConfiguration config, ILogger<Program> logger) =>
        {
            if (string.IsNullOrWhiteSpace(input.Username) || string.IsNullOrWhiteSpace(input.Password))
                return Results.BadRequest(new ApiError("用户名和密码不能为空"));

            var user = await users.GetByUsernameAsync(input.Username.Trim());
            // 校验失败统一返回“用户名或密码错误”，不区分账号是否存在
            if (user == null || !BCrypt.Net.BCrypt.Verify(input.Password, user.PasswordHash))
            {
                logger.LogWarning("登录失败：{Username}", input.Username);
                return Results.Json(new ApiError("用户名或密码错误"), statusCode: 401);
            }

            // 已启用两步验证：签发 5 分钟中间态令牌，等待验证码
            if (user.TotpEnabled && !string.IsNullOrEmpty(user.TotpSecret))
            {
                var loginToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                PendingTotp[loginToken] = (user.Id, DateTimeOffset.UtcNow.AddMinutes(5));
                return Results.Ok(new { totpRequired = true, loginToken });
            }

            return await CompleteLoginAsync(user, users, tokens, config, logger);
        }).RequireRateLimiting("login").AllowAnonymous();

        group.MapPost("/login/totp", async (TotpLoginInput input, IUserRepository users, TokenService tokens,
            IConfiguration config, ILogger<Program> logger, ISecretProtector protector) =>
        {
            var key = input.LoginToken?.Trim() ?? "";
            // 校验失败不移除会话（允许重输），防爆破由登录限流器承担
            if (!PendingTotp.TryGetValue(key, out var pending) || pending.Expires < DateTimeOffset.UtcNow)
            {
                PendingTotp.TryRemove(key, out _);
                return Results.Json(new ApiError("登录会话已过期，请重新登录"), statusCode: 401);
            }

            var user = await users.GetByIdAsync(pending.UserId);
            if (user == null || string.IsNullOrEmpty(user.TotpSecret) || !user.TotpEnabled)
            {
                PendingTotp.TryRemove(key, out _);
                return Results.Json(new ApiError("用户名或密码错误"), statusCode: 401);
            }
            var secret = protector.Unprotect(user.TotpSecret);
            if (string.IsNullOrEmpty(secret) || !Totp.Verify(secret, input.Code ?? "", DateTimeOffset.UtcNow))
                return Results.Json(new ApiError("验证码不正确"), statusCode: 401);

            PendingTotp.TryRemove(key, out _);
            return await CompleteLoginAsync(user, users, tokens, config, logger);
        }).RequireRateLimiting("login").AllowAnonymous();

        // 生成 TOTP 密钥（返回明文 secret 与 otpauth URI，验证通过前不生效）
        group.MapPost("/totp/setup", async (ClaimsPrincipal principal, IUserRepository users,
            ISecretProtector protector) =>
        {
            var userId = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = await users.GetByIdAsync(userId);
            if (user == null) return Results.Unauthorized();
            if (user.TotpEnabled) return Results.BadRequest(new ApiError("两步验证已启用，如需更换请先停用"));

            var secret = Totp.GenerateSecret();
            await users.UpdateTotpAsync(userId, protector.Protect(secret), enabled: false);
            return Results.Ok(new
            {
                secret,
                otpauthUri = Totp.BuildOtpAuthUri(secret, user.Username)
            });
        }).RequireAuthorization();

        // 验证首个验证码并启用
        group.MapPost("/totp/enable", async (TotpCodeInput input, ClaimsPrincipal principal,
            IUserRepository users, ISecretProtector protector, IAuditService audit) =>
        {
            var userId = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = await users.GetByIdAsync(userId);
            if (user == null) return Results.Unauthorized();
            var secret = string.IsNullOrEmpty(user.TotpSecret) ? null : protector.Unprotect(user.TotpSecret);
            if (secret == null) return Results.BadRequest(new ApiError("请先生成两步验证密钥"));
            if (!Totp.Verify(secret, input.Code ?? "", DateTimeOffset.UtcNow))
                return Results.BadRequest(new ApiError("验证码不正确，请重试"));
            await users.UpdateTotpAsync(userId, user.TotpSecret, enabled: true);
            await audit.RecordAsync(user.Username, "user.totp-enable", user.Username);
            return Results.Ok(new { message = "两步验证已启用" });
        }).RequireAuthorization();

        // 停用（需当前有效验证码）
        group.MapPost("/totp/disable", async (TotpCodeInput input, ClaimsPrincipal principal,
            IUserRepository users, ISecretProtector protector, IAuditService audit) =>
        {
            var userId = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = await users.GetByIdAsync(userId);
            if (user == null) return Results.Unauthorized();
            var secret = string.IsNullOrEmpty(user.TotpSecret) ? null : protector.Unprotect(user.TotpSecret);
            if (secret == null || !user.TotpEnabled) return Results.BadRequest(new ApiError("两步验证未启用"));
            if (!Totp.Verify(secret, input.Code ?? "", DateTimeOffset.UtcNow))
                return Results.BadRequest(new ApiError("验证码不正确"));
            await users.UpdateTotpAsync(userId, null, enabled: false);
            await audit.RecordAsync(user.Username, "user.totp-disable", user.Username);
            return Results.Ok(new { message = "两步验证已停用" });
        }).RequireAuthorization();

        // 当前用户 TOTP 状态
        group.MapGet("/totp/status", (ClaimsPrincipal principal, IUserRepository users) =>
        {
            var userId = int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = users.GetByIdAsync(userId).GetAwaiter().GetResult();
            if (user == null) return Results.Unauthorized();
            return Results.Ok(new { enabled = user.TotpEnabled });
        }).RequireAuthorization();

        group.MapGet("/me", (ClaimsPrincipal principal) =>
        {
            var name = principal.Identity?.Name;
            var role = principal.FindFirstValue(ClaimTypes.Role);
            return Results.Ok(new { username = name, role });
        }).RequireAuthorization();

        group.MapPost("/change-password", async (ChangePasswordInput input, ClaimsPrincipal principal,
            IUserRepository users) =>
        {
            var userIdStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out var userId)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(input.NewPassword) || input.NewPassword.Length < 8)
                return Results.BadRequest(new ApiError("新密码长度至少 8 位"));

            var user = await users.GetByIdAsync(userId);
            if (user == null) return Results.Unauthorized();
            if (!BCrypt.Net.BCrypt.Verify(input.OldPassword, user.PasswordHash))
                return Results.Json(new ApiError("原密码不正确"), statusCode: 400);

            await users.UpdatePasswordAsync(userId, BCrypt.Net.BCrypt.HashPassword(input.NewPassword, workFactor: 11));
            return Results.Ok(new { message = "密码已修改" });
        }).RequireAuthorization();

        // 周期清理过期中间态令牌
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(5));
                foreach (var kv in PendingTotp)
                    if (kv.Value.Expires < DateTimeOffset.UtcNow)
                        PendingTotp.TryRemove(kv.Key, out _);
            }
        });

        return app;
    }

    private static async Task<IResult> CompleteLoginAsync(User user, IUserRepository users,
        TokenService tokens, IConfiguration config, ILogger<Program> logger)
    {
        await users.UpdateLastLoginAsync(user.Id, DateTime.UtcNow);
        var expireHours = config.GetValue<int?>("Jwt:ExpireHours") ?? 12;
        logger.LogInformation("用户 {Username} 登录成功", user.Username);
        return Results.Ok(new LoginResult(
            Token: tokens.CreateToken(user),
            Username: user.Username,
            Role: user.Role,
            ExpiresInHours: Math.Max(1, expireHours)));
    }
}

public record TotpLoginInput(string LoginToken, string Code);
public record TotpCodeInput(string Code);
