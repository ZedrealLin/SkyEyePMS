using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Reading;

namespace SkyEyePMS.Core.Alerting;

public enum AlertActionType
{
    /// <summary>新建告警并推送</summary>
    Raise,
    /// <summary>告警仍活跃且已过静默期，再次推送</summary>
    ReNotify,
    /// <summary>条件解除，告警恢复</summary>
    Resolve
}

public record AlertAction(
    AlertActionType ActionType,
    AlertType AlertType,
    string AlertKey,
    AlertSeverity Severity,
    string Message,
    Alert? Existing)
{
    public int? ExistingId => Existing?.Id;
}

/// <summary>
/// 告警判定引擎（纯函数，不访问数据库/网络）：
/// 输入某台打印机的一次采集结果 + 现有告警 + 阈值配置，输出应执行的告警动作列表。
/// 去重规则：同 printer+alert_key 仅一条 active；静默期内不重复推送；
/// 恢复规则：条件解除即 Resolve（不再推送恢复消息以外的内容）。
/// </summary>
public static class AlertEngine
{
    public static IReadOnlyList<AlertAction> Evaluate(
        Printer printer,
        DeviceReading? reading,
        bool pollSucceeded,
        AlertThresholds thresholds,
        IReadOnlyList<Alert> existingAlerts,
        DateTime now)
    {
        var actions = new List<AlertAction>();
        var activeByKey = existingAlerts
            .Where(a => a.StatusEnum == AlertStatus.Active)
            .GroupBy(a => a.AlertKey)
            .ToDictionary(g => g.Key, g => g.First());

        // ---- 离线判定 ----
        if (!pollSucceeded)
        {
            if (printer.ConsecutiveFailures >= Math.Max(1, thresholds.OfflineConsecutiveFailures))
            {
                actions.Add(Upsert(
                    activeByKey, AlertType.Offline, "offline", AlertSeverity.Critical,
                    $"设备连续 {printer.ConsecutiveFailures} 次采集无响应，已标记为离线（{printer.Ip}）"));
            }
            return actions;
        }

        // 采集成功 → 离线告警恢复
        if (activeByKey.TryGetValue("offline", out var offlineAlert))
            actions.Add(new AlertAction(AlertActionType.Resolve, AlertType.Offline, "offline",
                offlineAlert.SeverityEnum, offlineAlert.Message, offlineAlert));

        reading ??= new DeviceReading();

        // ---- 缺纸 / 卡纸（error bits）----
        var flags = reading.ErrorFlags;
        if (flags.HasFlag(PrinterErrorFlag.NoPaper))
            actions.Add(Upsert(activeByKey, AlertType.PaperOut, "paper_out", AlertSeverity.Warning,
                "检测到缺纸（noPaper）"));
        else if (flags.HasFlag(PrinterErrorFlag.LowPaper))
            actions.Add(Upsert(activeByKey, AlertType.PaperOut, "paper_low", AlertSeverity.Info,
                "纸张余量不足（lowPaper）"));

        if (flags.HasFlag(PrinterErrorFlag.Jammed))
            actions.Add(Upsert(activeByKey, AlertType.PaperJam, "paper_jam", AlertSeverity.Warning,
                "检测到卡纸（jammed）"));

        // ---- 纸盒空（prtInputTable）----
        foreach (var tray in reading.InputTrays.Where(t => t.IsEmpty))
        {
            actions.Add(Upsert(activeByKey, AlertType.PaperOut, $"paper_out:tray:{tray.Index}",
                AlertSeverity.Warning,
                $"纸盒 {tray.Index}（{tray.Name ?? "未知"}）已无纸"));
        }

        // ---- 耗材低（prtMarkerSuppliesTable）----
        foreach (var supply in reading.Supplies)
        {
            if (!supply.IsAlertableSupply || supply.Percent == null) continue;
            if (supply.Percent.Value > thresholds.SupplyLowPercent) continue;
            var critical = supply.Percent.Value <= thresholds.SupplyCriticalPercent;
            actions.Add(Upsert(activeByKey, AlertType.SupplyLow, $"supply_low:{supply.Index}",
                critical ? AlertSeverity.Critical : AlertSeverity.Warning,
                $"{DescribeSupply(supply)} 余量仅 {supply.Percent.Value:0.#}%（低于阈值 {thresholds.SupplyLowPercent}%）"));
        }

        // ---- 设备告警表（prtAlertTable，critical 条目）----
        foreach (var entry in reading.AlertTableEntries)
        {
            var isJam = entry.MentionsJam();
            if (!entry.IsCritical && !isJam) continue;
            var key = isJam ? "paper_jam:alert" : $"device_alert:{entry.Group}:{entry.Code}";
            var type = isJam ? AlertType.PaperJam : AlertType.DeviceAlert;
            actions.Add(Upsert(activeByKey, type, key,
                isJam ? AlertSeverity.Warning : AlertSeverity.Critical,
                $"设备报告告警：{entry.Description ?? $"group={entry.Group} code={entry.Code}"}"));
        }

        // ---- 条件已解除的 active 告警 → Resolve ----
        foreach (var (key, alert) in activeByKey)
        {
            if (!NeedsResolve(key, actions, reading, thresholds)) continue;
            actions.Add(new AlertAction(AlertActionType.Resolve, alert.TypeEnum, key,
                alert.SeverityEnum, alert.Message, alert));
        }

        return actions;
    }

    /// <summary>返回 true 表示该 active 告警的条件已解除，应添加 Resolve 动作</summary>
    private static bool NeedsResolve(string key, List<AlertAction> actions,
        DeviceReading reading, AlertThresholds thresholds)
    {
        // 本次评估已对该 key 产生动作（Raise/ReNotify）→ 保持活跃
        if (actions.Any(a => a.AlertKey == key && a.ActionType != AlertActionType.Resolve)) return false;

        if (key == "offline") return false; // 采集成功时已在上方显式添加 Resolve，避免重复
        if (key.StartsWith("supply_low:"))
        {
            var idxStr = key["supply_low:".Length..];
            if (int.TryParse(idxStr, out var idx))
            {
                var s = reading.Supplies.FirstOrDefault(x => x.Index == idx);
                // 供应消失或恢复到阈值以上 → 解除
                return s == null || !s.IsAlertableSupply ||
                       (s.Percent != null && s.Percent.Value > thresholds.SupplyLowPercent);
            }
            return true;
        }
        if (key == "paper_out") return !reading.ErrorFlags.HasFlag(PrinterErrorFlag.NoPaper);
        if (key == "paper_low") return !reading.ErrorFlags.HasFlag(PrinterErrorFlag.LowPaper);
        if (key == "paper_jam" || key == "paper_jam:alert") return !reading.ErrorFlags.HasFlag(PrinterErrorFlag.Jammed);
        if (key.StartsWith("paper_out:tray:"))
        {
            var idxStr = key["paper_out:tray:".Length..];
            if (int.TryParse(idxStr, out var idx))
            {
                var t = reading.InputTrays.FirstOrDefault(x => x.Index == idx);
                return t == null || !t.IsEmpty;
            }
            return true;
        }
        if (key.StartsWith("device_alert:"))
        {
            var parts = key["device_alert:".Length..].Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var g) && int.TryParse(parts[1], out var c))
            {
                var e = reading.AlertTableEntries.FirstOrDefault(x => x.Group == g && x.Code == c);
                return e == null; // 告警表里还在 → 仍活跃
            }
            return true;
        }
        return true;
    }

    private static AlertAction Upsert(Dictionary<string, Alert> activeByKey, AlertType type, string key,
        AlertSeverity severity, string message)
    {
        // 已存在 active 告警 → ReNotify；是否真的推送由执行器用 ShouldNotify 按静默期判断
        if (activeByKey.TryGetValue(key, out var existing))
            return new AlertAction(AlertActionType.ReNotify, type, key, severity, message, existing);
        return new AlertAction(AlertActionType.Raise, type, key, severity, message, null);
    }

    private static string DescribeSupply(SupplyReading s)
    {
        var name = string.IsNullOrWhiteSpace(s.Description) ? $"耗材 #{s.Index}" : s.Description!.Trim();
        var typeName = s.Type switch
        {
            3 => "硒鼓/碳粉",
            5 => "墨水",
            6 => "墨盒",
            7 => "色带",
            9 => "感光鼓",
            10 => "显影剂",
            _ => null
        };
        return typeName != null && !name.Contains(typeName, StringComparison.OrdinalIgnoreCase)
            ? $"{name}（{typeName}）"
            : name;
    }

    /// <summary>判断动作是否需要推送（Raise 需要；ReNotify 需检查静默）</summary>
    public static bool ShouldNotify(AlertAction action, AlertThresholds thresholds, DateTime now)
    {
        if (action.ActionType == AlertActionType.Raise) return true;
        if (action.ActionType == AlertActionType.ReNotify && action.Existing != null)
        {
            return action.Existing.LastNotifiedAt == null ||
                   now - action.Existing.LastNotifiedAt.Value >= TimeSpan.FromMinutes(Math.Max(1, thresholds.AlertSilenceMinutes));
        }
        return false;
    }
}
