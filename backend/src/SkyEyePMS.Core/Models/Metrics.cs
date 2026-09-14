namespace SkyEyePMS.Core.Models;

public class StatusSample
{
    public int Id { get; set; }
    public int PrinterId { get; set; }
    public DateTime CollectedAt { get; set; }
    public bool Online { get; set; }
    public int? HrStatus { get; set; }
    public long? PageCount { get; set; }
}

/// <summary>耗材当前状态（每个 printer_id + supply_index 一行，upsert 更新）</summary>
public class Supply
{
    public int Id { get; set; }
    public int PrinterId { get; set; }
    public int SupplyIndex { get; set; }
    public string? Name { get; set; }
    public int? SupplyType { get; set; }
    public int? Unit { get; set; }
    public long? MaxCapacity { get; set; }
    public long? Level { get; set; }
    public double? Percent { get; set; }
    /// <summary>低耗材阈值覆盖（%），null 使用全局</summary>
    public int? LowPercentThreshold { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>耗材历史采样（仅在百分比变化 ≥1 个点时写入，控制表膨胀）</summary>
public class SupplySample
{
    public int Id { get; set; }
    public int PrinterId { get; set; }
    public int SupplyIndex { get; set; }
    public double? Percent { get; set; }
    public long? Level { get; set; }
    public DateTime CollectedAt { get; set; }
}

/// <summary>日粒度统计（每日汇总作业写入，支撑年度趋势；原始采样被保留策略清理后仍可查）</summary>
public class DailyStat
{
    public int PrinterId { get; set; }
    /// <summary>统计日期（UTC 日期，0 点）</summary>
    public DateTime StatDate { get; set; }
    /// <summary>当日打印页数（计数器正增量）</summary>
    public long PagesPrinted { get; set; }
    public int OnlineSamples { get; set; }
    public int TotalSamples { get; set; }
}
