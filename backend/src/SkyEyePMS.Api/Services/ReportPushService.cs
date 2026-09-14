using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkyEyePMS.Core.Notifying;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Services;

namespace SkyEyePMS.Api.Services;

/// <summary>
/// 周期报表推送：按设置（星期/时刻/渠道）把打印机周报摘要推送到指定渠道。
/// 每 10 分钟自检一次；上次成功推送时间持久化在报表推送配置中，重启不重复发送。
/// </summary>
public class ReportPushService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReportPushService> _logger;

    public ReportPushService(IServiceScopeFactory scopeFactory, ILogger<ReportPushService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("报表推送服务启动（每 10 分钟检查一次计划）");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndPushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "报表推送检查失败");
            }
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }

    internal async Task CheckAndPushAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        var snapshot = await settings.GetSnapshotAsync();
        var push = snapshot.ReportPush;
        if (!push.Enabled) return;

        var now = DateTime.Now;
        if (now.DayOfWeek != (DayOfWeek)Math.Clamp(push.DayOfWeek, 0, 6)) return;
        if (now.Hour != Math.Clamp(push.Hour, 0, 23) || now.Minute < Math.Clamp(push.Minute, 0, 59)) return;
        // 当前 10 分钟窗口内已经发过（含跨重启），跳过
        if (push.LastSentAt.HasValue &&
            (DateTime.UtcNow - push.LastSentAt.Value).TotalMinutes < 60) return;

        var printers = scope.ServiceProvider.GetRequiredService<IPrinterRepository>();
        var metrics = scope.ServiceProvider.GetRequiredService<IMetricsRepository>();
        var alerts = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
        var registry = scope.ServiceProvider.GetRequiredService<INotifierRegistry>();

        var digest = await DigestBuilder.BuildAsync(printers, metrics, alerts, settings, days: 7);
        var channels = push.Channels.Count == 0 ? ["feishu"] : push.Channels;
        var results = new List<string>();
        foreach (var channel in channels.Distinct())
        {
            var notifier = await registry.GetAsync(channel);
            if (notifier == null || !notifier.IsConfigured)
            {
                results.Add($"{channel}:未配置");
                continue;
            }
            var (success, error) = await notifier.SendDigestAsync(digest.Title, digest.Markdown, ct);
            results.Add($"{channel}:{(success ? "成功" : $"失败（{error}）")}");
            _logger.LogInformation("报表推送到 {Channel}: {Result}", channel, success ? "成功" : $"失败 {error}");
        }

        push.LastSentAt = DateTime.UtcNow;
        await settings.SaveReportPushAsync(push);
        _logger.LogInformation("本周报表推送完成：{Results}", string.Join("；", results));
    }
}
