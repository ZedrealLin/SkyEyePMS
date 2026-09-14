using SkyEyePMS.Core.Models;

namespace SkyEyePMS.Core.Analysis;

/// <summary>单条耗材的耗尽预测结果（各字段可为 null = 无法预测）</summary>
public record SupplyForecast(
    /// <summary>估算的日均消耗（百分点/天，正数）</summary>
    double? DailyConsumptionPercent,
    /// <summary>预计剩余可用天数</summary>
    double? DaysLeft,
    /// <summary>预计耗尽时间</summary>
    DateTime? EstimatedEmptyAt)
{
    public bool IsPredictable => DaysLeft.HasValue;
}

/// <summary>
/// 耗材耗尽预测：对窗口期内的历史采样做最小二乘拟合求消耗斜率。
/// 采样本身按"变化≥1 个百分点才记录"存储，阶梯状下降对拟合无影响；
/// 斜率非负（平稳或刚更换回升）视为无耗尽趋势，不预测。
/// </summary>
public static class SupplyForecaster
{
    public static SupplyForecast Forecast(double? currentPercent,
        IEnumerable<SupplySample> samples, DateTime now, int windowDays = 14)
    {
        var since = now.AddDays(-windowDays);
        var pts = samples
            .Where(s => s.Percent.HasValue && s.CollectedAt >= since && s.CollectedAt <= now)
            .Select(s => (t: s.CollectedAt, v: s.Percent!.Value))
            .OrderBy(p => p.t)
            .ToList();
        if (pts.Count < 2) return new SupplyForecast(null, null, null);

        var t0 = pts[0].t;
        var spanDays = (pts[^1].t - t0).TotalDays;
        if (spanDays < 1.0 / 24) return new SupplyForecast(null, null, null); // 跨度过短，斜率不可信

        // 最小二乘：v = a + b·x，x 为距首点天数
        double n = pts.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (t, v) in pts)
        {
            var x = (t - t0).TotalDays;
            sx += x; sy += v; sxx += x * x; sxy += x * v;
        }
        var denom = n * sxx - sx * sx;
        var slope = denom <= 0 ? 0 : (n * sxy - sx * sy) / denom; // %/天，负数 = 在消耗

        if (slope >= -0.05) return new SupplyForecast(null, null, null); // 平稳/回升（刚更换）
        var cur = currentPercent ?? pts[^1].v;
        var daily = -slope;
        var daysLeft = cur <= 0 ? 0 : cur / daily;
        return new SupplyForecast(Math.Round(daily, 2), Math.Round(daysLeft, 1), now.AddDays(daysLeft));
    }
}

/// <summary>打印量与在线采样统计</summary>
public record UsageStats(long PagesPrinted, int OnlineSamples, int TotalSamples)
{
    /// <summary>时段可用率（%），无采样时为 null</summary>
    public double? AvailabilityPercent =>
        TotalSamples == 0 ? null : Math.Round(OnlineSamples * 100.0 / TotalSamples, 1);
}

public static class UsageCalculator
{
    /// <summary>
    /// 打印页数 = 相邻采样间页数计数器的正增量之和；
    /// 计数器清零/回绕（负增量）自动忽略，不影响累计。samples 无需预排序。
    /// </summary>
    public static UsageStats Compute(IEnumerable<StatusSample> samples)
    {
        long pages = 0;
        int online = 0, total = 0;
        long? prev = null;
        foreach (var s in samples.OrderBy(x => x.CollectedAt))
        {
            total++;
            if (s.Online) online++;
            if (s.PageCount.HasValue)
            {
                if (prev.HasValue && s.PageCount.Value > prev.Value)
                    pages += s.PageCount.Value - prev.Value;
                prev = s.PageCount;
            }
        }
        return new UsageStats(pages, online, total);
    }
}
