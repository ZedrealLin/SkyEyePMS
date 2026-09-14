using SkyEyePMS.Core.Analysis;
using SkyEyePMS.Core.Models;

namespace SkyEyePMS.Tests;

/// <summary>耗材耗尽预测与打印量/可用率统计的纯函数测试。</summary>
public class FleetAnalyticsTests
{
    private static readonly DateTime Now = new(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc);

    private static SupplySample Sample(double percent, double daysAgo) => new()
    {
        PrinterId = 1,
        SupplyIndex = 1,
        Percent = percent,
        CollectedAt = Now.AddDays(-daysAgo)
    };

    // ---------- 耗材预测 ----------

    [Fact]
    public void Forecast_ConsumingComputesDaysLeft()
    {
        // 线性消耗：每天约 -15%
        var samples = new[] { Sample(90, 2), Sample(75, 1), Sample(60, 0) };
        var f = SupplyForecaster.Forecast(60, samples, Now);

        Assert.True(f.IsPredictable);
        Assert.Equal(15, f.DailyConsumptionPercent!.Value, 1);
        Assert.Equal(4, f.DaysLeft!.Value, 1); // 60% ÷ 15%/天
        Assert.Equal(Now.AddDays(4), f.EstimatedEmptyAt!.Value, TimeSpan.FromHours(2));
    }

    [Fact]
    public void Forecast_StableIsNotPredictable()
    {
        var samples = new[] { Sample(50, 3), Sample(50, 2), Sample(50, 1), Sample(50, 0) };
        var f = SupplyForecaster.Forecast(50, samples, Now);
        Assert.False(f.IsPredictable);
        Assert.Null(f.DaysLeft);
    }

    [Fact]
    public void Forecast_RisingAfterReplacementIsNotPredictable()
    {
        // 刚更换耗材：20% → 100%
        var samples = new[] { Sample(20, 2), Sample(60, 1), Sample(100, 0) };
        var f = SupplyForecaster.Forecast(100, samples, Now);
        Assert.False(f.IsPredictable);
    }

    [Fact]
    public void Forecast_TooFewPointsIsNull()
    {
        var f = SupplyForecaster.Forecast(30, new[] { Sample(30, 0) }, Now);
        Assert.False(f.IsPredictable);
    }

    [Fact]
    public void Forecast_IgnoresSamplesOutsideWindow()
    {
        // 只有 15 天前的旧数据，窗口 14 天 → 有效点不足
        var samples = new[] { Sample(90, 20), Sample(60, 15) };
        var f = SupplyForecaster.Forecast(60, samples, Now, windowDays: 14);
        Assert.False(f.IsPredictable);
    }

    [Fact]
    public void Forecast_UsesSuppliedCurrentPercent()
    {
        // 历史斜率 -10%/天，但当前已掉到 10%
        var samples = new[] { Sample(60, 2), Sample(50, 1), Sample(40, 0) };
        var f = SupplyForecaster.Forecast(10, samples, Now);
        Assert.True(f.IsPredictable);
        Assert.Equal(10, f.DailyConsumptionPercent!.Value, 0.5);
        Assert.Equal(1, f.DaysLeft!.Value, 1);
    }

    [Fact]
    public void Forecast_AlreadyEmptyMeansZeroDays()
    {
        var samples = new[] { Sample(15, 2), Sample(8, 1), Sample(0, 0) };
        var f = SupplyForecaster.Forecast(0, samples, Now);
        Assert.True(f.IsPredictable);
        Assert.Equal(0, f.DaysLeft!.Value);
    }

    // ---------- 使用统计 ----------

    private static StatusSample Status(long? pageCount, bool online, double minutesAgo) => new()
    {
        PrinterId = 1,
        CollectedAt = Now.AddMinutes(-minutesAgo),
        Online = online,
        PageCount = pageCount
    };

    [Fact]
    public void Usage_SumsPositiveCounterDeltas()
    {
        // 时间升序：100 → 250 → 250 → 30（计数器清零）→ 130
        var samples = new[]
        {
            Status(100, true, 130), Status(250, true, 100), Status(250, true, 70), Status(30, true, 40), Status(130, false, 10)
        };
        var s = UsageCalculator.Compute(samples);

        Assert.Equal(250, s.PagesPrinted); // 150 + 0 + 0 + 100
        Assert.Equal(4, s.OnlineSamples);
        Assert.Equal(5, s.TotalSamples);
        Assert.Equal(80.0, s.AvailabilityPercent!.Value);
    }

    [Fact]
    public void Usage_NoSamplesMeansNullAvailability()
    {
        var s = UsageCalculator.Compute([]);
        Assert.Equal(0, s.PagesPrinted);
        Assert.Equal(0, s.TotalSamples);
        Assert.Null(s.AvailabilityPercent);
    }

    [Fact]
    public void Usage_NullPageCountsAreSkipped()
    {
        var s = UsageCalculator.Compute(new[]
        {
            Status(null, true, 20), Status(50, true, 10), Status(null, false, 5), Status(80, true, 1)
        });
        Assert.Equal(30, s.PagesPrinted);
        Assert.Equal(75.0, s.AvailabilityPercent!.Value); // 3/4
    }
}
