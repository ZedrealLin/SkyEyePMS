namespace SkyEyePMS.Core.Models;

public enum AlertType
{
    Offline = 1,
    SupplyLow = 2,
    PaperOut = 3,
    PaperJam = 4,
    DeviceAlert = 5
}

public enum AlertSeverity
{
    Info = 1,
    Warning = 2,
    Critical = 3
}

/// <summary>active=告警中 resolved=已恢复</summary>
public enum AlertStatus
{
    Active = 1,
    Resolved = 2
}

public class Alert
{
    public int Id { get; set; }
    public int PrinterId { get; set; }
    /// <summary>同 printer_id + alert_key 仅一条 active，用于去重，如 "supply_low:2"、"offline"</summary>
    public string AlertKey { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = AlertStatus.Active.ToString().ToLowerInvariant();
    public DateTime FirstTriggeredAt { get; set; }
    public DateTime? LastNotifiedAt { get; set; }
    public int NotifyCount { get; set; }
    public DateTime? ResolvedAt { get; set; }
    /// <summary>人工确认时间（确认表示已知晓/已安排处理，不等同于恢复）</summary>
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }

    public AlertType TypeEnum => Enum.TryParse(Type, true, out AlertType t) ? t : AlertType.DeviceAlert;
    public AlertSeverity SeverityEnum => Enum.TryParse(Severity, true, out AlertSeverity s) ? s : AlertSeverity.Info;
    public AlertStatus StatusEnum => Enum.TryParse(Status, true, out AlertStatus s) ? s : AlertStatus.Active;
}

public class NotificationLogEntry
{
    public int Id { get; set; }
    public int AlertId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTime SentAt { get; set; }
}
