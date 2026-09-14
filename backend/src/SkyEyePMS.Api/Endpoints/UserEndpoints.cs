using System.Security.Claims;
using SkyEyePMS.Api.Auth;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Services;

namespace SkyEyePMS.Api.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (IUserRepository users) =>
        {
            var all = await users.GetAllAsync();
            return Results.Ok(all.Select(u => new UserView(u.Id, u.Username, u.Role, u.CreatedAt, u.LastLoginAt)));
        });

        group.MapPost("/", async (CreateUserInput input, IUserRepository users, IAuditService audit,
            ClaimsPrincipal principal) =>
        {
            var username = input.Username?.Trim() ?? "";
            if (username.Length is < 2 or > 64) return Results.BadRequest(new ApiError("用户名长度 2-64 个字符"));
            if (string.IsNullOrWhiteSpace(input.Password) || input.Password.Length < 8)
                return Results.BadRequest(new ApiError("密码长度至少 8 位"));
            var role = string.Equals(input.Role, UserRoles.Admin, StringComparison.OrdinalIgnoreCase)
                ? UserRoles.Admin : UserRoles.Viewer;
            if (await users.GetByUsernameAsync(username) != null)
                return Results.Conflict(new ApiError("用户名已存在"));

            var user = new User
            {
                Username = username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(input.Password, workFactor: 11),
                Role = role,
                CreatedAt = DateTime.UtcNow
            };
            user.Id = await users.CreateAsync(user);
            await audit.RecordAsync(principal.Identity?.Name, "user.create", user.Username, role);
            return Results.Created($"/api/users/{user.Id}", new UserView(user.Id, user.Username, user.Role, user.CreatedAt, null));
        });

        group.MapPut("/{id:int}/password", async (int id, ResetPasswordInput input, IUserRepository users,
            IAuditService audit, ClaimsPrincipal principal) =>
        {
            var user = await users.GetByIdAsync(id);
            if (user == null) return Results.NotFound(new ApiError("用户不存在"));
            if (string.IsNullOrWhiteSpace(input.NewPassword) || input.NewPassword.Length < 8)
                return Results.BadRequest(new ApiError("密码长度至少 8 位"));
            await users.UpdatePasswordAsync(id, BCrypt.Net.BCrypt.HashPassword(input.NewPassword, workFactor: 11));
            await audit.RecordAsync(principal.Identity?.Name, "user.reset-password", user.Username);
            return Results.Ok(new { message = "密码已重置" });
        });

        group.MapDelete("/{id:int}", async (int id, ClaimsPrincipal principal, IUserRepository users,
            IAuditService audit) =>
        {
            var user = await users.GetByIdAsync(id);
            if (user == null) return Results.NotFound(new ApiError("用户不存在"));
            var currentId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (currentId == id.ToString()) return Results.BadRequest(new ApiError("不能删除当前登录账号"));
            if (user.Role == UserRoles.Admin)
            {
                var all = await users.GetAllAsync();
                if (all.Count(u => u.Role == UserRoles.Admin) <= 1)
                    return Results.BadRequest(new ApiError("至少保留一个管理员账号"));
            }
            await users.DeleteAsync(id);
            await audit.RecordAsync(principal.Identity?.Name, "user.delete", user.Username);
            return Results.Ok(new { message = "已删除" });
        });

        return app;
    }
}
