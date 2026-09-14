using System.Reflection;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Services;
using SkyEyePMS.Infrastructure.Database;
using SkyEyePMS.Infrastructure.Polling;

namespace SkyEyePMS.Api.Endpoints;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app,
        IConfiguration configuration)
    {
        var group = app.MapGroup("/api/system").WithTags("System");

        group.MapGet("/info", async (IPrinterRepository printers, IAlertRepository alerts,
            IPollingCoordinator coordinator, IDbConnectionFactory dbf, Microsoft.Extensions.Logging.ILogger<Program> logger) =>
        {
            var printerCount = (await printers.GetAllAsync()).Count;
            var activeAlerts = await alerts.CountAsync("active");
            return Results.Ok(new
            {
                version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
                databaseProvider = dbf.Provider.ToString(),
                pollIntervalMinutes = configuration.GetValue<int?>("Polling:IntervalMinutes") ?? 2,
                pollConcurrency = configuration.GetValue<int?>("Polling:Concurrency") ?? 20,
                lastRoundAt = coordinator.LastRoundAt,
                isRoundRunning = coordinator.IsRoundRunning,
                printerCount,
                activeAlerts
            });
        }).RequireAuthorization();

        // 触发一轮完整采集（异步执行，立即返回）
        group.MapPost("/poll-now", async (IPollingCoordinator coordinator,
            Microsoft.Extensions.Logging.ILogger<Program> logger) =>
        {
            if (coordinator.IsRoundRunning) return Results.Ok(new { started = false, message = "已有采集轮次在执行" });
            _ = Task.Run(async () =>
            {
                try { await coordinator.RunRoundAsync(); }
                catch (Exception ex) { logger.LogError(ex, "手动触发的采集轮次失败"); }
            });
            return Results.Ok(new { started = true });
        }).RequireAuthorization("AdminOnly");

        // 最近收到的 SNMP Trap（诊断实时告警链路）
        group.MapGet("/traps", (SkyEyePMS.Infrastructure.Collecting.RecentTrapStore store) =>
            Results.Ok(store.Latest().Select(t => new
            {
                t.SenderIp,
                t.Community,
                t.TrapOid,
                t.GenericCode,
                t.UpTimeTicks,
                receivedAt = t.ReceivedAt
            }))).RequireAuthorization("AdminOnly");

        return app;
    }
}
