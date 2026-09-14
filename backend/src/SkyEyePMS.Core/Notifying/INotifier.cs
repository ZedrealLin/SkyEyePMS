using SkyEyePMS.Core.Models;

namespace SkyEyePMS.Core.Notifying;

public enum AlertEventKind
{
    Raised,
    ReNotified,
    Resolved
}

/// <summary>推送给通知渠道的结构化告警内容</summary>
public record AlertNotification(
    AlertEventKind EventKind,
    Alert Alert,
    Printer Printer,
    string SystemName)
{
    public string EventText => EventKind switch
    {
        AlertEventKind.Raised => "新告警",
        AlertEventKind.ReNotified => "告警持续（重复提醒）",
        _ => "告警恢复"
    };

    public string SeverityText => Alert.SeverityEnum switch
    {
        AlertSeverity.Critical => "严重",
        AlertSeverity.Warning => "警告",
        _ => "提示"
    };
}

/// <summary>渠道推送路由策略（每渠道独立配置，存于渠道配置 JSON 内）</summary>
public record ChannelRouting
{
    /// <summary>推送的最低告警级别：Info / Warning / Critical，低于该级别的告警不推送到此渠道</summary>
    public string MinSeverity { get; set; } = "Info";
    /// <summary>启用免打扰时段（时段内仅推送 Critical）</summary>
    public bool QuietHoursEnabled { get; set; }
    /// <summary>免打扰开始（HH:mm，本地时间，支持跨零点）</summary>
    public string QuietHoursStart { get; set; } = "22:00";
    /// <summary>免打扰结束（HH:mm）</summary>
    public string QuietHoursEnd { get; set; } = "08:00";
}

/// <summary>通知渠道抽象：飞书/钉钉/企业微信/邮件/通用 Webhook 各实现一个，告警触发时按路由策略推送</summary>
public interface INotifier
{
    /// <summary>渠道标识：feishu / dingtalk / wecom / email / webhook</summary>
    string Channel { get; }
    bool IsConfigured { get; }
    /// <summary>该渠道的路由策略（默认全部推送、无免打扰）</summary>
    ChannelRouting Routing => new();

    Task<(bool Success, string? Error)> SendAsync(AlertNotification notification, CancellationToken ct = default);

    /// <summary>推送自由文本摘要（周报等）。默认不支持，返回失败原因。</summary>
    Task<(bool Success, string? Error)> SendDigestAsync(string title, string markdown, CancellationToken ct = default)
        => Task.FromResult<(bool Success, string? Error)>((false, "该渠道不支持摘要推送"));
}

/// <summary>告警级别与路由判定（纯函数，便于单测）</summary>
public static class NotificationRouting
{
    public static int Rank(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => 3,
        AlertSeverity.Warning => 2,
        _ => 1
    };

    public static int RankOfMin(string minSeverity) =>
        Enum.TryParse<AlertSeverity>(minSeverity, true, out var s) ? Rank(s) : 1;

    /// <summary>判断给定本地时间是否处于免打扰时段（支持跨零点窗口）</summary>
    public static bool InQuietHours(ChannelRouting routing, DateTime localNow)
    {
        if (!routing.QuietHoursEnabled) return false;
        if (!TimeSpan.TryParse(routing.QuietHoursStart, out var start) ||
            !TimeSpan.TryParse(routing.QuietHoursEnd, out var end))
            return false;
        var t = localNow.TimeOfDay;
        return start <= end
            ? t >= start && t < end
            : t >= start || t < end;
    }

    /// <summary>该告警是否应推送到采用此路由策略的渠道</summary>
    public static bool ShouldSend(ChannelRouting routing, AlertSeverity severity, DateTime localNow)
    {
        if (Rank(severity) < RankOfMin(routing.MinSeverity)) return false;
        if (InQuietHours(routing, localNow) && severity != AlertSeverity.Critical) return false;
        return true;
    }
}
