using SkyEyePMS.Core.Repositories;

namespace SkyEyePMS.Api.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit").WithTags("Audit");

        group.MapGet("/", async (IAuditRepository audit, string? action, string? username,
            int limit = 100, int offset = 0) =>
        {
            var l = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
            var o = Math.Max(0, offset);
            var items = await audit.QueryAsync(
                string.IsNullOrWhiteSpace(action) ? null : action.Trim(),
                string.IsNullOrWhiteSpace(username) ? null : username.Trim(), l, o);
            return Results.Ok(items);
        }).RequireAuthorization("AdminOnly");

        return app;
    }
}
