using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Models;
using System.Text.Json;
using SkyEyePMS.Core.Notifying;
using SkyEyePMS.Core.Services;
using SkyEyePMS.Infrastructure.Notifying;

namespace SkyEyePMS.Api.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");

        group.MapGet("/", async (IAppSettingsService settings, NotifierConfigService notifierConfig) =>
        {
            var snapshot = await settings.GetSnapshotAsync();
            var options = await notifierConfig.GetEffectiveAsync();
            var view = new
            {
                feishu = new
                {
                    options.Feishu.Enabled,
                    WebhookUrl = Mask(options.Feishu.WebhookUrl),
                    Secret = Mask(options.Feishu.Secret),
                    options.Feishu.MinSeverity,
                    options.Feishu.QuietHoursEnabled,
                    options.Feishu.QuietHoursStart,
                    options.Feishu.QuietHoursEnd,
                    Configured = options.Feishu.Enabled && !string.IsNullOrEmpty(options.Feishu.WebhookUrl)
                },
                dingtalk = new
                {
                    options.DingTalk.Enabled,
                    WebhookUrl = Mask(options.DingTalk.WebhookUrl),
                    Secret = Mask(options.DingTalk.Secret),
                    options.DingTalk.MinSeverity,
                    options.DingTalk.QuietHoursEnabled,
                    options.DingTalk.QuietHoursStart,
                    options.DingTalk.QuietHoursEnd,
                    Configured = options.DingTalk.Enabled && !string.IsNullOrEmpty(options.DingTalk.WebhookUrl)
                },
                wecom = new
                {
                    options.WeCom.Enabled,
                    WebhookUrl = Mask(options.WeCom.WebhookUrl),
                    options.WeCom.MinSeverity,
                    options.WeCom.QuietHoursEnabled,
                    options.WeCom.QuietHoursStart,
                    options.WeCom.QuietHoursEnd,
                    Configured = options.WeCom.Enabled && !string.IsNullOrEmpty(options.WeCom.WebhookUrl)
                },
                webhook = new
                {
                    options.Webhook.Enabled,
                    Url = Mask(options.Webhook.Url),
                    Secret = Mask(options.Webhook.Secret),
                    Configured = options.Webhook.Enabled && !string.IsNullOrEmpty(options.Webhook.Url)
                },
                email = new
                {
                    options.Email.Enabled,
                    options.Email.SmtpHost,
                    options.Email.SmtpPort,
                    options.Email.UseSsl,
                    options.Email.Username,
                    Password = Mask(options.Email.Password),
                    options.Email.FromAddress,
                    options.Email.FromName,
                    options.Email.ToAddresses,
                    options.Email.MinSeverity,
                    options.Email.QuietHoursEnabled,
                    options.Email.QuietHoursStart,
                    options.Email.QuietHoursEnd,
                    Configured = options.Email.Enabled && !string.IsNullOrEmpty(options.Email.SmtpHost)
                                 && !string.IsNullOrEmpty(options.Email.FromAddress)
                                 && options.Email.ToAddresses.Count > 0
                }
            };
            return Results.Ok(new SettingsView(snapshot.Thresholds, snapshot.Retention,
                snapshot.Analysis, snapshot.Sampling, snapshot.Polling, snapshot.ReportPush, view));
        }).RequireAuthorization();

        group.MapPut("/thresholds", async (AlertThresholds thresholds, IAppSettingsService settings) =>
        {
            if (thresholds.SupplyLowPercent is < 1 or > 99) return Results.BadRequest(new ApiError("耗材阈值应在 1-99 之间"));
            if (thresholds.SupplyCriticalPercent is < 0 or > 99) return Results.BadRequest(new ApiError("耗材严重阈值应在 0-99 之间"));
            if (thresholds.OfflineConsecutiveFailures is < 1 or > 60) return Results.BadRequest(new ApiError("离线连续失败次数应在 1-60 之间"));
            if (thresholds.AlertSilenceMinutes is < 1 or > 1440) return Results.BadRequest(new ApiError("静默期应在 1-1440 分钟之间"));
            if (thresholds.SupplyCriticalPercent > thresholds.SupplyLowPercent) return Results.BadRequest(new ApiError("严重阈值不能高于低告警阈值"));
            await settings.SaveThresholdsAsync(thresholds);
            return Results.Ok(thresholds);
        }).RequireAuthorization("AdminOnly");

        group.MapPut("/analysis", async (AnalysisOptions analysis, IAppSettingsService settings) =>
        {
            if (analysis.ForecastWindowDays is < 2 or > 90) return Results.BadRequest(new ApiError("预测窗口应在 2-90 天之间"));
            if (analysis.SupplyReplacementRisePercent is < 2 or > 100) return Results.BadRequest(new ApiError("更换判定回升幅度应在 2-100 个百分点之间"));
            await settings.SaveAnalysisAsync(analysis);
            return Results.Ok(analysis);
        }).RequireAuthorization("AdminOnly");

        group.MapPut("/sampling", async (SamplingOptions sampling, IAppSettingsService settings) =>
        {
            if (sampling.SupplySampleMinChangePercent is < 1 or > 50) return Results.BadRequest(new ApiError("耗材采样最小变化应在 1-50 个百分点之间"));
            await settings.SaveSamplingAsync(sampling);
            return Results.Ok(sampling);
        }).RequireAuthorization("AdminOnly");

        group.MapPut("/polling", async (PollingSettings polling, IAppSettingsService settings,
            IAuditService audit, System.Security.Claims.ClaimsPrincipal user) =>
        {
            if (polling.IntervalMinutes is < 1 or > 1440) return Results.BadRequest(new ApiError("轮询间隔应在 1-1440 分钟之间"));
            if (polling.Concurrency is < 1 or > 100) return Results.BadRequest(new ApiError("并发上限应在 1-100 之间"));
            await settings.SavePollingAsync(polling);
            await audit.RecordAsync(user.Identity?.Name, "settings.save", "polling",
                $"间隔={polling.IntervalMinutes}min 并发={polling.Concurrency}");
            return Results.Ok(polling);
        }).RequireAuthorization("AdminOnly");

        group.MapPut("/reportpush", async (ReportPushOptions push, IAppSettingsService settings,
            IAuditService audit, System.Security.Claims.ClaimsPrincipal user) =>
        {
            if (push.DayOfWeek is < 0 or > 6) return Results.BadRequest(new ApiError("推送日应在 0-6 之间（0=周日）"));
            if (push.Hour is < 0 or > 23) return Results.BadRequest(new ApiError("推送小时应在 0-23 之间"));
            if (push.Minute is < 0 or > 59) return Results.BadRequest(new ApiError("推送分钟应在 0-59 之间"));
            if (push.Channels.Count == 0) return Results.BadRequest(new ApiError("至少选择一个推送渠道"));
            var validChannels = new[] { "feishu", "dingtalk", "wecom", "email", "webhook" };
            if (push.Channels.Any(c => !validChannels.Contains(c.ToLowerInvariant())))
                return Results.BadRequest(new ApiError("渠道标识无效"));
            // LastSentAt 由推送服务维护，不接受外部改写（保留现值）
            var current = (await settings.GetSnapshotAsync()).ReportPush.LastSentAt;
            push.LastSentAt = current;
            await settings.SaveReportPushAsync(push);
            await audit.RecordAsync(user.Identity?.Name, "settings.save", "reportPush",
                $"周{(DayOfWeek)push.DayOfWeek} {push.Hour:00}:{push.Minute:00} 渠道=[{string.Join(",", push.Channels)}]");
            return Results.Ok(push);
        }).RequireAuthorization("AdminOnly");

        group.MapPut("/retention", async (RetentionOptions retention, IAppSettingsService settings,
            IAuditService audit, System.Security.Claims.ClaimsPrincipal user) =>
        {
            if (retention.StatusHistoryDays is < 1 or > 3650) return Results.BadRequest(new ApiError("状态保留天数应在 1-3650 之间"));
            if (retention.SupplyHistoryDays is < 1 or > 3650) return Results.BadRequest(new ApiError("耗材保留天数应在 1-3650 之间"));
            if (retention.ResolvedAlertDays is < 1 or > 3650) return Results.BadRequest(new ApiError("告警保留天数应在 1-3650 之间"));
            if (retention.NotificationLogDays is < 1 or > 3650) return Results.BadRequest(new ApiError("通知日志保留天数应在 1-3650 之间"));
            if (retention.AuditLogDays is < 7 or > 3650) return Results.BadRequest(new ApiError("审计保留天数应在 7-3650 之间"));
            await settings.SaveRetentionAsync(retention);
            await audit.RecordAsync(user.Identity?.Name, "settings.save", "retention",
                $"状态={retention.StatusHistoryDays}d 耗材={retention.SupplyHistoryDays}d 审计={retention.AuditLogDays}d");
            return Results.Ok(retention);
        }).RequireAuthorization("AdminOnly");

        // 保存通知渠道配置（channel: feishu / dingtalk / wecom / email）
        group.MapPut("/notifiers/{channel}", async (string channel, JsonElement config,
            NotifierConfigService notifierConfig) =>
        {
            try
            {
                await notifierConfig.SaveChannelAsync(channel, config);
                return Results.Ok(new { message = "已保存" });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ApiError(ex.Message));
            }
        }).RequireAuthorization("AdminOnly");

        // 发送测试消息到指定渠道
        group.MapPost("/notifiers/{channel}/test", async (string channel, INotifierRegistry registry,
            IPrinterRepository printers) =>
        {
            var notifier = await registry.GetAsync(channel);
            if (notifier == null) return Results.NotFound(new ApiError("渠道不存在"));
            if (!notifier.IsConfigured) return Results.BadRequest(new ApiError("该渠道未启用或配置不完整"));

            var printer = await printers.GetAllAsync() switch
            {
                { Count: > 0 } list => list[0],
                _ => new Core.Models.Printer { Id = 0, Name = "示例打印机", Ip = "192.0.2.1", Port = 161, Location = "示例位置" }
            };
            var alert = new Core.Models.Alert
            {
                Id = 0,
                PrinterId = printer.Id,
                AlertKey = "test",
                Type = "DeviceAlert",
                Severity = "Warning",
                Message = "这是一条测试消息，收到说明该通知渠道配置正确。",
                FirstTriggeredAt = DateTime.UtcNow
            };
            var notification = new AlertNotification(AlertEventKind.Raised, alert, printer, "SkyEyePMS");
            var (success, error) = await notifier.SendAsync(notification);
            return success ? Results.Ok(new { message = "测试消息已发送" }) : Results.Json(new ApiError(error ?? "发送失败"), statusCode: 502);
        }).RequireAuthorization("AdminOnly");

        return app;
    }

    private static string? Mask(string? value) =>
        string.IsNullOrEmpty(value) ? null : NotifierConfigService.Mask;
}
