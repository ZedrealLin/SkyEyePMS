using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Services;

namespace SkyEyePMS.Api.Endpoints;

public static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alerts").WithTags("Alerts");

        group.MapGet("/", async (IAlertRepository alerts, string? status, int? printerId,
            int limit = 100, int offset = 0) =>
        {
            var effectiveStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
            var l = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
            var o = Math.Max(0, offset);
            var items = await alerts.QueryAsync(effectiveStatus, printerId, l, o);
            var total = await alerts.CountAsync(effectiveStatus);
            return Results.Ok(new PagedResult<Alert>(items, total));
        }).RequireAuthorization();

        group.MapGet("/{id:int}/notifications", async (int id, IAlertRepository alerts) =>
        {
            var alert = await alerts.GetByIdAsync(id);
            if (alert == null) return Results.NotFound(new ApiError("告警不存在"));
            var logs = await alerts.GetNotificationLogsAsync(id);
            return Results.Ok(logs);
        }).RequireAuthorization();

        // 确认告警（表示已知晓/已安排处理，不改变告警状态）
        group.MapPost("/{id:int}/ack", async (int id, IAlertRepository alerts, HttpContext ctx,
            IAuditService audit) =>
        {
            var alert = await alerts.GetByIdAsync(id);
            if (alert == null) return Results.NotFound(new ApiError("告警不存在"));
            var user = ctx.User.Identity?.Name;
            if (string.IsNullOrEmpty(user)) return Results.Unauthorized();
            await alerts.AcknowledgeAsync(id, user, DateTime.UtcNow);
            await audit.RecordAsync(user, "alert.ack", $"#{id}", alert.Message);
            return Results.Ok(new { message = "已确认" });
        }).RequireAuthorization("AdminOnly");

        // 手动恢复
        group.MapPost("/{id:int}/resolve", async (int id, IAlertRepository alerts, HttpContext ctx,
            IAuditService audit) =>
        {
            var alert = await alerts.GetByIdAsync(id);
            if (alert == null) return Results.NotFound(new ApiError("告警不存在"));
            if (alert.StatusEnum == Core.Models.AlertStatus.Resolved)
                return Results.Ok(new { message = "已是恢复状态" });
            await alerts.ResolveAsync(id, DateTime.UtcNow);
            await audit.RecordAsync(ctx.User.Identity?.Name, "alert.resolve", $"#{id}", alert.Message);
            return Results.Ok(new { message = "已恢复" });
        }).RequireAuthorization("AdminOnly");

        // 告警记录导出 CSV
        group.MapGet("/export.csv", async (HttpContext ctx, IAlertRepository alerts,
            IPrinterRepository printers, string? status) =>
        {
            var effective = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
            var items = await alerts.QueryAsync(effective, null, 5000, 0);
            var printerList = await printers.GetAllAsync();
            var nameById = printerList.ToDictionary(p => p.Id, p => p.Name);

            return Csv.Result(ctx, $"skyeye_alerts_{DateTime.UtcNow:yyyyMMdd_HHmm}.csv",
                ["ID", "打印机", "类型", "级别", "内容", "状态", "首次触发", "最近推送", "推送次数",
                 "恢复时间", "确认人", "确认时间"],
                items.Select(a => new List<object?>
                {
                    a.Id,
                    nameById.TryGetValue(a.PrinterId, out var n) ? n : $"#{a.PrinterId}",
                    a.Type, a.Severity, a.Message, a.Status,
                    a.FirstTriggeredAt, a.LastNotifiedAt, a.NotifyCount,
                    a.ResolvedAt, a.AcknowledgedBy, a.AcknowledgedAt
                }));
        }).RequireAuthorization();

        return app;
    }
}
