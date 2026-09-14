using SkyEyePMS.Api;
using SkyEyePMS.Core.Analysis;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Services;

namespace SkyEyePMS.Api.Endpoints;

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").WithTags("Reports");

        group.MapGet("/summary", async (IAlertRepository alerts, IMetricsRepository metrics,
            IPrinterRepository printers, IAppSettingsService settings, int days) =>
        {
            var d = Math.Clamp(days <= 0 ? 30 : days, 1, 365);
            var since = DateTime.UtcNow.AddDays(-d);
            var snapshot = await settings.GetSnapshotAsync();

            var printerList = await printers.GetAllAsync();
            var nameById = printerList.ToDictionary(p => p.Id, p => (p.Name, p.Location));

            // 故障排行：每台打印机的告警次数（离线单列）
            var alertItems = await alerts.QueryAsync(limit: 10000);
            var ranking = alertItems.Where(a => a.FirstTriggeredAt >= since)
                .GroupBy(a => a.PrinterId)
                .Select(g => new FaultRankItem(
                    PrinterId: g.Key,
                    PrinterName: nameById.TryGetValue(g.Key, out var n) ? n.Name : $"#{g.Key}",
                    Location: nameById.TryGetValue(g.Key, out var n2) ? n2.Location : null,
                    AlertCount: g.Count(),
                    OfflineCount: g.Count(a => a.TypeEnum == Core.Models.AlertType.Offline)))
                .OrderByDescending(x => x.AlertCount)
                .Take(20)
                .ToList();

            // 耗材更换频率：耗材采样中出现大幅回升（幅度可配置，默认 ≥15 个百分点）视为一次更换
            var risePercent = Math.Max(1, snapshot.Analysis.SupplyReplacementRisePercent);
            var samples = await metrics.GetAllSupplySamplesAsync(since);
            var supplies = await metrics.GetAllSuppliesAsync();
            var supplyName = supplies.ToDictionary(s => (s.PrinterId, s.SupplyIndex), s => s.Name);
            var replacements = samples
                .GroupBy(s => (s.PrinterId, s.SupplyIndex))
                .Select(g =>
                {
                    var ordered = g.OrderBy(s => s.CollectedAt).ToList();
                    var count = 0;
                    double? prev = null;
                    foreach (var s in ordered)
                    {
                        if (prev.HasValue && s.Percent.HasValue && s.Percent.Value - prev.Value >= risePercent)
                            count++;
                        if (s.Percent.HasValue) prev = s.Percent.Value;
                    }
                    return new SupplyReplaceItem(
                        PrinterId: g.Key.PrinterId,
                        PrinterName: nameById.TryGetValue(g.Key.PrinterId, out var n) ? n.Name : $"#{g.Key.PrinterId}",
                        SupplyIndex: g.Key.SupplyIndex,
                        SupplyName: supplyName.TryGetValue(g.Key, out var sn) ? sn : $"耗材 #{g.Key.SupplyIndex}",
                        Replacements: count);
                })
                .Where(x => x.Replacements > 0)
                .OrderByDescending(x => x.Replacements)
                .Take(20)
                .ToList();

            return Results.Ok(new ReportSummary(d, ranking, replacements));
        }).RequireAuthorization();

        // ---------- 使用统计与可用率 ----------
        group.MapGet("/usage", async (IPrinterRepository printers, IMetricsRepository metrics, int days = 7) =>
            Results.Ok(await BuildUsageAsync(printers, metrics, days)))
            .RequireAuthorization();

        group.MapGet("/usage/export.csv", async (HttpContext ctx,
            IPrinterRepository printers, IMetricsRepository metrics, int days = 7) =>
        {
            var report = await BuildUsageAsync(printers, metrics, days);
            return Csv.Result(ctx, $"skyeye_usage_{DateTime.UtcNow:yyyyMMdd_HHmm}.csv",
                ["打印机", "位置", "部门", "状态", "打印页数", "可用率%", "采样数", "当前页数"],
                report.Items.Select(i => new List<object?>
                {
                    i.PrinterName, i.Location, i.Department, i.Enabled ? "启用" : "停用",
                    i.PagesPrinted, i.AvailabilityPercent, i.SampleCount, i.CurrentPageCount
                }));
        }).RequireAuthorization();

        // ---------- 耗材耗尽预测 ----------
        group.MapGet("/forecast", async (IPrinterRepository printers, IMetricsRepository metrics,
            IAppSettingsService settings, int? windowDays) =>
            Results.Ok(await BuildForecastAsync(printers, metrics, settings, windowDays)))
            .RequireAuthorization();

        group.MapGet("/forecast/export.csv", async (HttpContext ctx, IPrinterRepository printers,
            IMetricsRepository metrics, IAppSettingsService settings, int? windowDays) =>
        {
            var report = await BuildForecastAsync(printers, metrics, settings, windowDays);
            return Csv.Result(ctx, $"skyeye_forecast_{DateTime.UtcNow:yyyyMMdd_HHmm}.csv",
                ["打印机", "位置", "耗材", "当前余量%", "日均消耗%/天", "预计剩余天数", "预计耗尽时间"],
                report.Items.Select(i => new List<object?>
                {
                    i.PrinterName, i.Location, i.SupplyName, i.Percent,
                    i.DailyConsumptionPercent, i.DaysLeft, i.EstimatedEmptyAt
                }));
        }).RequireAuthorization();

        // ---------- 部门抄表（计费/成本分摊） ----------
        group.MapGet("/meter", async (IPrinterRepository printers, IMetricsRepository metrics,
            IAppSettingsService settings, int days = 30) =>
            Results.Ok(await BuildMeterAsync(printers, metrics, days)))
            .RequireAuthorization();

        group.MapGet("/meter/export.csv", async (HttpContext ctx, IPrinterRepository printers,
            IMetricsRepository metrics, int days = 30) =>
        {
            var report = await BuildMeterAsync(printers, metrics, days);
            var rows = new List<IReadOnlyList<object?>>();
            foreach (var dept in report.Departments)
            {
                rows.Add(new List<object?> { $"部门：{dept.Department ?? "未分配"}", $"共 {dept.PrinterCount} 台",
                    dept.TotalPages, dept.AvgAvailabilityPercent });
                foreach (var p in report.Printers.Where(x => x.Department == dept.Department && x.Enabled))
                    rows.Add(new List<object?> { p.PrinterName, p.Location, p.PagesPrinted, p.AvailabilityPercent, p.SampleCount });
                rows.Add(new List<object?> { "", "", "", "" });
            }
            return Csv.Result(ctx, $"skyeye_meter_{DateTime.UtcNow:yyyyMMdd_HHmm}.csv",
                ["部门/打印机", "位置", "打印页数", "可用率%", "采样数"], rows);
        }).RequireAuthorization();

        // ---------- 打印量趋势（日粒度，来自每日汇总，支撑年度视图） ----------
        group.MapGet("/daily", async (IMetricsRepository metrics, int days = 90) =>
        {
            var d = Math.Clamp(days <= 0 ? 90 : days, 1, 1095);
            var since = DateTime.UtcNow.Date.AddDays(-d);
            var rows = await metrics.GetDailyStatsAsync(since);
            var items = rows.GroupBy(r => r.StatDate.Date)
                .Select(g => new
                {
                    date = g.Key.ToString("yyyy-MM-dd"),
                    totalPages = g.Sum(x => x.PagesPrinted),
                    printerCount = g.Count(),
                    avgAvailability = g.Where(x => x.TotalSamples > 0).Any()
                        ? Math.Round(g.Where(x => x.TotalSamples > 0)
                            .Average(x => x.OnlineSamples * 100.0 / x.TotalSamples), 1)
                        : (double?)null
                })
                .OrderBy(x => x.date)
                .ToList();
            return Results.Ok(new { days = d, items });
        }).RequireAuthorization();

        // ---------- 运维质量（MTTA / 确认率 / 通知成功率） ----------
        group.MapGet("/quality", async (IAlertRepository alerts, int days = 7) =>
        {
            var d = Math.Clamp(days <= 0 ? 7 : days, 1, 365);
            var since = DateTime.UtcNow.AddDays(-d);

            var items = await alerts.QueryAsync(limit: 10000);
            var window = items.Where(a => a.FirstTriggeredAt >= since).ToList();
            var acked = window.Where(a => a.AcknowledgedAt.HasValue).ToList();
            var notified = await alerts.GetNotificationLogsSinceAsync(since);
            var activeNow = items.Where(a => a.StatusEnum == Core.Models.AlertStatus.Active).ToList();

            var mtta = acked.Count == 0
                ? (double?)null
                : Math.Round(acked.Average(a => (a.AcknowledgedAt!.Value - a.FirstTriggeredAt).TotalMinutes), 1);

            var report = new QualityReport(
                Days: d,
                MttaMinutes: mtta,
                TriggeredCount: window.Count,
                AcknowledgedCount: acked.Count,
                AckRatePercent: window.Count == 0 ? null : Math.Round(acked.Count * 100.0 / window.Count, 1),
                CriticalCount: window.Count(a => a.SeverityEnum == Core.Models.AlertSeverity.Critical),
                ResolvedCount: window.Count(a => a.StatusEnum == Core.Models.AlertStatus.Resolved),
                CurrentlyActive: activeNow.Count,
                CurrentlyCriticalActive: activeNow.Count(a => a.SeverityEnum == Core.Models.AlertSeverity.Critical),
                NotifyAttempts: notified.Count,
                NotifySuccesses: notified.Count(n => n.Success),
                NotifySuccessPercent: notified.Count == 0 ? null : Math.Round(notified.Count(n => n.Success) * 100.0 / notified.Count, 1));
            return Results.Ok(report);
        }).RequireAuthorization();

        return app;
    }

    internal static async Task<MeterReport> BuildMeterAsync(
        IPrinterRepository printers, IMetricsRepository metrics, int days)
    {
        var usage = await BuildUsageAsync(printers, metrics, days);
        var departments = usage.Items
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Department) ? null : i.Department)
            .Select(g => new MeterDepartmentItem(
                Department: g.Key,
                PrinterCount: g.Count(),
                TotalPages: g.Sum(x => x.PagesPrinted),
                AvgAvailabilityPercent: g.Where(x => x.AvailabilityPercent.HasValue).Any()
                    ? Math.Round(g.Where(x => x.AvailabilityPercent.HasValue).Average(x => x.AvailabilityPercent!.Value), 1)
                    : (double?)null))
            .OrderByDescending(x => x.TotalPages)
            .ToList();
        return new MeterReport(usage.Days, departments, usage.Items);
    }

    internal static async Task<UsageReport> BuildUsageAsync(
        IPrinterRepository printers, IMetricsRepository metrics, int days)
    {
        var d = Math.Clamp(days <= 0 ? 7 : days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-d);

        var list = await printers.GetAllAsync();
        var samples = await metrics.GetAllStatusSamplesAsync(since);
        var byPrinter = samples.GroupBy(s => s.PrinterId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<StatusSample>)g.ToList());

        var items = list.Select(p =>
        {
            var stats = UsageCalculator.Compute(byPrinter.TryGetValue(p.Id, out var ss) ? ss : []);
            return new UsageReportItem(p.Id, p.Name, p.Location, p.Department, p.Enabled,
                stats.PagesPrinted, stats.AvailabilityPercent, stats.TotalSamples, p.PageCount);
        })
        .OrderByDescending(x => x.PagesPrinted)
        .ThenBy(x => x.PrinterName)
        .ToList();

        return new UsageReport(d, items);
    }

    internal static async Task<ForecastReport> BuildForecastAsync(
        IPrinterRepository printers, IMetricsRepository metrics, IAppSettingsService settings, int? windowDays)
    {
        var snapshot = await settings.GetSnapshotAsync();
        var w = Math.Clamp(windowDays ?? snapshot.Analysis.ForecastWindowDays, 2, 90);
        var now = DateTime.UtcNow;

        var list = await printers.GetAllAsync();
        var info = list.ToDictionary(p => p.Id, p => (p.Name, p.Location));
        var supplies = await metrics.GetAllSuppliesAsync();
        var samples = await metrics.GetAllSupplySamplesAsync(now.AddDays(-w));
        var byKey = samples
            .GroupBy(s => (s.PrinterId, s.SupplyIndex))
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.CollectedAt).ToList());

        var items = new List<ForecastItem>();
        foreach (var s in supplies)
        {
            byKey.TryGetValue((s.PrinterId, s.SupplyIndex), out var ss);
            var f = SupplyForecaster.Forecast(s.Percent, ss ?? [], now, w);
            items.Add(new ForecastItem(
                s.PrinterId,
                info.TryGetValue(s.PrinterId, out var i) ? i.Name : $"#{s.PrinterId}",
                info.TryGetValue(s.PrinterId, out var i2) ? i2.Location : null,
                s.SupplyIndex,
                string.IsNullOrWhiteSpace(s.Name) ? $"耗材 #{s.SupplyIndex}" : s.Name!,
                s.Percent, f.DailyConsumptionPercent, f.DaysLeft, f.EstimatedEmptyAt));
        }

        items = items
            .OrderBy(x => x.DaysLeft ?? double.MaxValue)
            .ThenBy(x => x.Percent ?? 100)
            .ThenBy(x => x.PrinterName)
            .ToList();
        return new ForecastReport(w, items);
    }
}
