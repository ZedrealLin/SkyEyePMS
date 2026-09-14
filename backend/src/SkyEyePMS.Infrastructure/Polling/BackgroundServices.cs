using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Services;

namespace SkyEyePMS.Infrastructure.Polling;

/// <summary>定时轮询 Hosted Service：按配置间隔驱动一轮完整采集，间隔/并发可在设置页运行期调整</summary>
public class PollingService : BackgroundService
{
    private readonly IPollingCoordinator _coordinator;
    private readonly IAppSettingsService _settings;
    private readonly PollingOptions _options;
    private readonly ILogger<PollingService> _logger;

    public PollingService(IPollingCoordinator coordinator, IAppSettingsService settings,
        IOptions<PollingOptions> options, ILogger<PollingService> logger)
    {
        _coordinator = coordinator;
        _settings = settings;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("后台轮询已禁用（Polling:Enabled=false），仅支持手动触发采集");
            return;
        }
        _logger.LogInformation("SNMP 轮询服务启动，初始间隔 {Interval} 分钟（可在系统设置中运行期调整）",
            _options.IntervalMinutes);

        // 启动后稍等片刻（等 Web 宿主完成初始化），先跑一轮
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            var currentInterval = _options.IntervalMinutes;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _coordinator.RunRoundAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "轮询轮次执行失败，等待下一轮");
                }

                // 每轮结束后重读运行参数，改间隔无需重启
                int configured;
                try
                {
                    configured = (await _settings.GetSnapshotAsync()).Polling.IntervalMinutes;
                }
                catch
                {
                    configured = currentInterval; // 读取失败沿用当前值
                }
                if (configured > 0 && configured != currentInterval)
                {
                    _logger.LogInformation("轮询间隔已调整为 {Interval} 分钟", configured);
                    currentInterval = configured;
                }
                var interval = TimeSpan.FromMinutes(Math.Clamp(currentInterval, 1, 1440));
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        _logger.LogInformation("SNMP 轮询服务已停止");
    }
}

/// <summary>历史数据保留清理：定期删除超过保留期的采样/日志/已恢复告警</summary>
public class RetentionService : BackgroundService
{
    private readonly IAppSettingsService _settings;
    private readonly IMetricsRepository _metrics;
    private readonly IAlertRepository _alerts;
    private readonly SkyEyePMS.Core.Repositories.IAuditRepository _audit;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(IAppSettingsService settings, IMetricsRepository metrics,
        IAlertRepository alerts, SkyEyePMS.Core.Repositories.IAuditRepository audit,
        ILogger<RetentionService> logger)
    {
        _settings = settings;
        _metrics = metrics;
        _alerts = alerts;
        _audit = audit;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 启动 1 分钟后先清一次，之后每 6 小时一轮
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupOnceAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "历史数据清理失败");
                }
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CleanupOnceAsync()
    {
        var retention = (await _settings.GetSnapshotAsync()).Retention;
        var now = DateTime.UtcNow;
        await RollupDailyStatsAsync();
        var status = await _metrics.DeleteOldStatusSamplesAsync(now.AddDays(-retention.StatusHistoryDays));
        var supply = await _metrics.DeleteOldSupplySamplesAsync(now.AddDays(-retention.SupplyHistoryDays));
        var alerts = await _alerts.DeleteOldResolvedAsync(now.AddDays(-retention.ResolvedAlertDays));
        var logs = await _alerts.DeleteOldNotificationLogsAsync(now.AddDays(-retention.NotificationLogDays));
        var audit = await _audit.DeleteOldAsync(now.AddDays(-retention.AuditLogDays));
        if (status + supply + alerts + logs + audit > 0)
            _logger.LogInformation(
                "历史数据清理完成：状态采样 {Status} 行、耗材采样 {Supply} 行、已恢复告警 {Alerts} 条、通知日志 {Logs} 条、审计 {Audit} 条",
                status, supply, alerts, logs, audit);
    }

    /// <summary>
    /// 把原始状态采样汇总为日粒度统计（打印页数按计数器正增量、归属到较晚采样的日期）。
    /// 已有汇总的历史日期跳过，仅重算缺失日期与今天；跨日增量通过前一日末样本衔接。
    /// </summary>
    public async Task RollupDailyStatsAsync()
    {
        var today = DateTime.UtcNow.Date;
        var existing = (await _metrics.GetDailyStatsAsync(today.AddDays(-400)))
            .Select(d => d.StatDate.Date).ToHashSet();

        var todaySamples = await _metrics.GetAllStatusSamplesAsync(today);
        // 找出缺失的历史日期
        var missing = new HashSet<DateTime>();
        var probe = await _metrics.GetAllStatusSamplesAsync(today.AddDays(-400));
        foreach (var s in probe)
        {
            var date = s.CollectedAt.Date;
            if (date != today && !existing.Contains(date)) missing.Add(date);
        }
        if (missing.Count == 0 && todaySamples.Count == 0) return;

        var needDates = new HashSet<DateTime>(missing) { today };
        var pullFrom = (needDates.Count > 0 ? needDates.Min() : today).AddDays(-1);
        var samples = await _metrics.GetAllStatusSamplesAsync(pullFrom);

        // 逐打印机顺序计算正增量，归属到较晚采样所在日期
        long? prev = null;
        var byPrinterDate = new Dictionary<(int, DateTime), long>();
        var onlineByPrinterDate = new Dictionary<(int, DateTime), (int Online, int Total)>();
        foreach (var s in samples.OrderBy(x => x.CollectedAt))
        {
            var key = (s.PrinterId, s.CollectedAt.Date);
            if (!onlineByPrinterDate.TryGetValue(key, out var oc)) onlineByPrinterDate[key] = oc = (0, 0);
            onlineByPrinterDate[key] = (oc.Online + (s.Online ? 1 : 0), oc.Total + 1);

            if (!s.PageCount.HasValue) continue;
            if (prev.HasValue && s.PageCount.Value > prev.Value)
            {
                byPrinterDate[key] = byPrinterDate.TryGetValue(key, out var cur)
                    ? cur + (s.PageCount.Value - prev.Value)
                    : s.PageCount.Value - prev.Value;
            }
            prev = s.PageCount;
        }

        var rows = byPrinterDate
            .Where(kv => needDates.Contains(kv.Key.Item2))
            .Select(kv => new DailyStat
            {
                PrinterId = kv.Key.Item1,
                StatDate = kv.Key.Item2,
                PagesPrinted = kv.Value,
                OnlineSamples = onlineByPrinterDate.TryGetValue(kv.Key, out var oc) ? oc.Online : 0,
                TotalSamples = onlineByPrinterDate.TryGetValue(kv.Key, out var oc2) ? oc2.Total : 0
            }).ToList();

        foreach (var row in rows)
            await _metrics.UpsertDailyStatAsync(row);
        if (rows.Count > 0)
            _logger.LogInformation("日粒度统计已更新 {Count} 行（覆盖 {Days} 个日期）",
                rows.Count, needDates.Count);
    }
}
