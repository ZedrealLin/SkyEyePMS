namespace SkyEyePMS.Core.Models;

/// <summary>告警阈值（看板/告警引擎使用，管理员可改，存 app_settings）</summary>
public class AlertThresholds
{
    /// <summary>耗材低于该百分比触发告警（%）；可被单台打印机覆盖</summary>
    public int SupplyLowPercent { get; set; } = 20;
    /// <summary>耗材低于该百分比时告警升级为严重（%）</summary>
    public int SupplyCriticalPercent { get; set; } = 5;
    /// <summary>连续 N 次采集失败判定离线并告警</summary>
    public int OfflineConsecutiveFailures { get; set; } = 3;
    /// <summary>同一告警静默期（分钟），期内不重复推送</summary>
    public int AlertSilenceMinutes { get; set; } = 60;

    /// <summary>复制一份（轮询服务为单台打印机套用覆盖阈值时使用）</summary>
    public AlertThresholds Clone() => new()
    {
        SupplyLowPercent = SupplyLowPercent,
        SupplyCriticalPercent = SupplyCriticalPercent,
        OfflineConsecutiveFailures = OfflineConsecutiveFailures,
        AlertSilenceMinutes = AlertSilenceMinutes
    };
}

/// <summary>分析与报表参数</summary>
public class AnalysisOptions
{
    /// <summary>耗材耗尽预测的历史窗口（天）</summary>
    public int ForecastWindowDays { get; set; } = 14;
    /// <summary>余量回升超过该百分点判定为一次耗材更换</summary>
    public int SupplyReplacementRisePercent { get; set; } = 15;
}

/// <summary>采集采样参数</summary>
public class SamplingOptions
{
    /// <summary>耗材余量变化达到该百分点才记录历史采样（控制表膨胀）</summary>
    public int SupplySampleMinChangePercent { get; set; } = 1;
}

/// <summary>轮询运行参数（运行期可改，下一轮生效，无需重启）</summary>
public class PollingSettings
{
    /// <summary>轮询间隔（分钟）</summary>
    public int IntervalMinutes { get; set; } = 2;
    /// <summary>并发采集台数上限</summary>
    public int Concurrency { get; set; } = 20;
}

/// <summary>周期报表推送（如每周使用摘要；本地时间判定）</summary>
public class ReportPushOptions
{
    public bool Enabled { get; set; }
    /// <summary>推送日（0=周日 … 6=周六，默认每周一）</summary>
    public int DayOfWeek { get; set; } = 1;
    /// <summary>推送时刻（本地时间小时，0-23）</summary>
    public int Hour { get; set; } = 8;
    /// <summary>推送分钟（0-59）</summary>
    public int Minute { get; set; } = 0;
    /// <summary>推送渠道（feishu / dingtalk / wecom / email / webhook 的子集）</summary>
    public List<string> Channels { get; set; } = ["feishu"];
    /// <summary>上次成功推送时间（UTC，防重复）</summary>
    public DateTime? LastSentAt { get; set; }
}

/// <summary>历史数据保留策略（天）</summary>
public class RetentionOptions
{
    public int StatusHistoryDays { get; set; } = 30;
    public int SupplyHistoryDays { get; set; } = 60;
    public int ResolvedAlertDays { get; set; } = 90;
    public int NotificationLogDays { get; set; } = 30;
    public int AuditLogDays { get; set; } = 180;
}

/// <summary>设置快照（轮询服务每次循环读取）</summary>
public class SettingsSnapshot
{
    public AlertThresholds Thresholds { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
    public AnalysisOptions Analysis { get; set; } = new();
    public SamplingOptions Sampling { get; set; } = new();
    public PollingSettings Polling { get; set; } = new();
    public ReportPushOptions ReportPush { get; set; } = new();
}
