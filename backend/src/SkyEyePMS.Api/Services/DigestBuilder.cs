using System.Text;
using SkyEyePMS.Api.Endpoints;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Services;

namespace SkyEyePMS.Api.Services;

/// <summary>周期报表摘要：使用概况 + 耗材预警 + 故障排行 + 待处理告警，输出 Markdown</summary>
public static class DigestBuilder
{
    public record DigestContent(string Title, string Markdown);

    public static async Task<DigestContent> BuildAsync(
        IPrinterRepository printers, IMetricsRepository metrics, IAlertRepository alerts,
        IAppSettingsService settings, int days = 7)
    {
        var usage = await ReportEndpoints.BuildUsageAsync(printers, metrics, days);
        var forecast = await ReportEndpoints.BuildForecastAsync(printers, metrics, settings, null);

        var enabled = usage.Items.Where(i => i.Enabled).ToList();
        var totalPages = enabled.Sum(i => i.PagesPrinted);
        var avail = enabled.Where(i => i.AvailabilityPercent.HasValue)
            .Select(i => i.AvailabilityPercent!.Value).ToList();
        var avgAvail = avail.Count == 0 ? (double?)null : Math.Round(avail.Average(), 1);

        var dueSoon = forecast.Items
            .Where(f => f.DaysLeft.HasValue && f.DaysLeft.Value <= 7)
            .OrderBy(f => f.DaysLeft)
            .Take(8).ToList();

        var now = DateTime.UtcNow;
        var allAlerts = await alerts.QueryAsync(limit: 5000);
        var since = now.AddDays(-days);
        var faults = allAlerts.Where(a => a.FirstTriggeredAt >= since)
            .GroupBy(a => a.PrinterId)
            .Select(g => (PrinterId: g.Key, Count: g.Count(),
                Offline: g.Count(a => a.TypeEnum == AlertType.Offline)))
            .OrderByDescending(x => x.Count).Take(5).ToList();
        var nameById = (await printers.GetAllAsync()).ToDictionary(p => p.Id, p => p.Name);
        var activeNow = allAlerts.Count(a => a.StatusEnum == AlertStatus.Active);
        var activeCritical = allAlerts.Count(a => a.StatusEnum == AlertStatus.Active
                                                  && a.SeverityEnum == AlertSeverity.Critical);

        var sb = new StringBuilder();
        var to = now.ToLocalTime();
        var from = to.AddDays(-days);
        sb.AppendLine($"**统计周期**：{from:MM-dd} ~ {to:MM-dd}（近 {days} 天）");
        sb.AppendLine($"**使用概况**：{enabled.Count} 台启用设备，累计打印 {totalPages} 页" +
                      (avgAvail.HasValue ? $"，平均可用率 {avgAvail}%" : ""));
        sb.AppendLine();
        sb.AppendLine("### 耗材预警（7 天内预计耗尽）");
        if (dueSoon.Count == 0)
        {
            sb.AppendLine("- 无，近期无需更换耗材");
        }
        else
        {
            foreach (var f in dueSoon)
                sb.AppendLine(
                    $"- **{f.PrinterName}** · {f.SupplyName}：余 {f.Percent:0.#}%，预计 {f.DaysLeft:0.#} 天后耗尽（{f.EstimatedEmptyAt?.ToLocalTime():MM-dd}）");
        }
        sb.AppendLine();
        sb.AppendLine("### 故障排行（Top 5）");
        if (faults.Count == 0)
        {
            sb.AppendLine("- 所选时段无故障记录");
        }
        else
        {
            foreach (var f in faults)
            {
                var name = nameById.TryGetValue(f.PrinterId, out var n) ? n : $"#{f.PrinterId}";
                sb.AppendLine($"- **{name}**：{f.Count} 次" + (f.Offline > 0 ? $"（其中离线 {f.Offline} 次）" : ""));
            }
        }
        sb.AppendLine();
        sb.AppendLine("### 当前待处理");
        sb.AppendLine($"- 告警中 {activeNow} 条" + (activeCritical > 0 ? $"（其中严重 {activeCritical} 条）" : ""));

        var title = $"SkyEyePMS 打印机周报 · {to:MM-dd}";
        return new DigestContent(title, sb.ToString());
    }
}
