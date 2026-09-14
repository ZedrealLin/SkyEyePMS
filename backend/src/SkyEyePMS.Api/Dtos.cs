using SkyEyePMS.Core.Models;

namespace SkyEyePMS.Api;

// ---------- 看板 / 打印机 ----------

public record SupplyDto(
    int SupplyIndex,
    string? Name,
    int? Type,
    double? Percent,
    long? Level,
    long? MaxCapacity,
    int? LowPercentThreshold);

public record PrinterOverviewItem(
    int Id,
    string Name,
    string Ip,
    int Port,
    string? Location,
    string? Department,
    bool Enabled,
    /// <summary>单台耗材低阈值覆盖（%），null 用全局</summary>
    int? SupplyLowPercentOverride,
    /// <summary>ok / warn / error / offline / unknown</summary>
    string State,
    string HrStatusText,
    int? HrStatus,
    long? PageCount,
    DateTime? LastPollAt,
    DateTime? LastSuccessAt,
    int FailureCount,
    bool Online,
    IReadOnlyList<SupplyDto> Supplies,
    int ActiveAlertCount,
    int CriticalAlertCount,
    string? Model = null,
    string? SerialNumber = null,
    string? AssetTag = null,
    DateTime? InstalledAt = null);

/// <summary>对外输出的打印机视图（不含加密后的 Community 密文）</summary>
public record PrinterView(
    int Id,
    string Name,
    string Ip,
    int Port,
    int SnmpVersion,
    string? Location,
    string? Department,
    bool Enabled,
    int? SupplyLowPercentOverride,
    string? Model,
    string? SerialNumber,
    string? AssetTag,
    DateTime? InstalledAt,
    int ConsecutiveFailures,
    DateTime? LastPollAt,
    DateTime? LastSuccessAt,
    bool? LastOnline,
    int? HrStatus,
    string HrStatusText,
    long? PageCount,
    string? SysName,
    string? SysDescr,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static PrinterView From(Printer p) => new(
        p.Id, p.Name, p.Ip, p.Port, p.SnmpVersion, p.Location, p.Department, p.Enabled,
        p.SupplyLowPercentOverride,
        p.Model, p.SerialNumber, p.AssetTag, p.InstalledAt,
        p.ConsecutiveFailures, p.LastPollAt, p.LastSuccessAt, p.LastOnline, p.HrStatus,
        Core.Reading.HrPrinterStatusValues.Describe(p.HrStatus),
        p.PageCount, p.SysName, p.SysDescr, p.CreatedAt, p.UpdatedAt);
}

public record PrinterDetail(
    PrinterView Printer,
    string CommunityMasked,
    IReadOnlyList<SupplyDto> Supplies,
    IReadOnlyList<StatusSample> RecentSamples,
    IReadOnlyList<Alert> ActiveAlerts,
    IReadOnlyList<Alert> RecentAlerts);

// ---------- 通用 ----------

public record PagedResult<T>(IReadOnlyList<T> Items, int Total);

public record ApiError(string Message);

// ---------- 设置 ----------

public record SettingsView(AlertThresholds Thresholds, RetentionOptions Retention,
    AnalysisOptions Analysis, SamplingOptions Sampling, PollingSettings Polling,
    ReportPushOptions ReportPush, object Notifiers);

// ---------- 报表 ----------

public record FaultRankItem(int PrinterId, string PrinterName, string? Location, int AlertCount, int OfflineCount);

public record SupplyReplaceItem(int PrinterId, string PrinterName, int SupplyIndex, string? SupplyName, int Replacements);

public record ReportSummary(int Days, IReadOnlyList<FaultRankItem> FaultRanking, IReadOnlyList<SupplyReplaceItem> SupplyReplacements);

public record UsageReportItem(
    int PrinterId,
    string PrinterName,
    string? Location,
    string? Department,
    bool Enabled,
    /// <summary>时段内累计打印页数（计数器正增量之和）</summary>
    long PagesPrinted,
    /// <summary>时段可用率（%），无采样为 null</summary>
    double? AvailabilityPercent,
    int SampleCount,
    long? CurrentPageCount);

public record ForecastItem(
    int PrinterId,
    string PrinterName,
    string? Location,
    int SupplyIndex,
    string SupplyName,
    double? Percent,
    double? DailyConsumptionPercent,
    double? DaysLeft,
    DateTime? EstimatedEmptyAt);

public record UsageReport(int Days, IReadOnlyList<UsageReportItem> Items);

public record ForecastReport(int WindowDays, IReadOnlyList<ForecastItem> Items);

// ---------- 运维质量 / 部门抄表 ----------

public record QualityReport(
    int Days,
    /// <summary>平均确认响应时长（分钟，已确认告警的 ack 与首次触发差值均值）</summary>
    double? MttaMinutes,
    int TriggeredCount,
    int AcknowledgedCount,
    /// <summary>确认率（%，已确认/期间触发）</summary>
    double? AckRatePercent,
    int CriticalCount,
    int ResolvedCount,
    int CurrentlyActive,
    int CurrentlyCriticalActive,
    long NotifyAttempts,
    long NotifySuccesses,
    /// <summary>通知成功率（%，期间内所有渠道推送）</summary>
    double? NotifySuccessPercent);

public record MeterDepartmentItem(
    string? Department,
    int PrinterCount,
    long TotalPages,
    double? AvgAvailabilityPercent);

public record MeterReport(int Days, IReadOnlyList<MeterDepartmentItem> Departments,
    IReadOnlyList<UsageReportItem> Printers);

// ---------- 用户 ----------

public record UserView(int Id, string Username, string Role, DateTime CreatedAt, DateTime? LastLoginAt);

public record CreateUserInput(string Username, string Password, string Role);

public record ChangePasswordInput(string OldPassword, string NewPassword);

public record ResetPasswordInput(string NewPassword);

public record LoginInput(string Username, string Password);

public record LoginResult(string Token, string Username, string Role, int ExpiresInHours);
