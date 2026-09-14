using SkyEyePMS.Core.Alerting;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Reading;

namespace SkyEyePMS.Tests;

/// <summary>告警引擎状态机：触发/去重/静默/恢复</summary>
public class AlertEngineTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    private static AlertThresholds Thresholds(int offlineFailures = 3, int silenceMinutes = 60, int supplyLow = 20) =>
        new() { OfflineConsecutiveFailures = offlineFailures, AlertSilenceMinutes = silenceMinutes, SupplyLowPercent = supplyLow };

    private static Printer Printer(int failures = 0) => new()
    {
        Id = 1, Name = "PRN-1", Ip = "10.0.0.8", Port = 161,
        ConsecutiveFailures = failures
    };

    private static Alert ActiveAlert(string key, AlertType type, AlertSeverity severity,
        DateTime? lastNotifiedAt = null, string message = "msg") => new()
    {
        Id = 100, PrinterId = 1, AlertKey = key, Type = type.ToString(), Severity = severity.ToString(),
        Message = message, Status = "active", FirstTriggeredAt = Now.AddMinutes(-30),
        LastNotifiedAt = lastNotifiedAt ?? Now.AddMinutes(-30), NotifyCount = 1
    };

    // ---------- 耗材低 ----------

    [Fact]
    public void LowSupply_FirstTime_Raises()
    {
        var reading = new DeviceReading
        {
            Supplies = { new SupplyReading(1, 3, "Black Toner", 7, 10000, 1500) } // 15%
        };
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), Array.Empty<Alert>(), Now);
        var raise = actions.Single(a => a.AlertKey == "supply_low:1");
        Assert.Equal(AlertActionType.Raise, raise.ActionType);
        Assert.Equal(AlertSeverity.Warning, raise.Severity);
        Assert.Contains("15%", raise.Message);
    }

    [Fact]
    public void LowSupply_Below5Percent_IsCritical()
    {
        var reading = new DeviceReading
        {
            Supplies = { new SupplyReading(1, 3, "Black Toner", 7, 10000, 300) } // 3%
        };
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), Array.Empty<Alert>(), Now);
        Assert.Equal(AlertSeverity.Critical, actions.Single().Severity);
    }

    [Fact]
    public void LowSupply_WithinSilence_NoNotify()
    {
        var existing = ActiveAlert("supply_low:1", AlertType.SupplyLow, AlertSeverity.Warning);
        var reading = new DeviceReading
        {
            Supplies = { new SupplyReading(1, 3, "Black Toner", 7, 10000, 1500) }
        };
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), new[] { existing }, Now);
        var action = actions.Single(a => a.AlertKey == "supply_low:1");
        Assert.Equal(AlertActionType.ReNotify, action.ActionType);
        Assert.False(AlertEngine.ShouldNotify(action, Thresholds(), Now)); // 静默期内
    }

    [Fact]
    public void LowSupply_AfterSilence_ReNotifies()
    {
        var existing = ActiveAlert("supply_low:1", AlertType.SupplyLow, AlertSeverity.Warning,
            lastNotifiedAt: Now.AddMinutes(-61)); // 已过 60 分钟静默
        var reading = new DeviceReading
        {
            Supplies = { new SupplyReading(1, 3, "Black Toner", 7, 10000, 1500) }
        };
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), new[] { existing }, Now);
        var action = actions.Single(a => a.AlertKey == "supply_low:1");
        Assert.True(AlertEngine.ShouldNotify(action, Thresholds(), Now));
    }

    [Fact]
    public void SupplyRecovered_Resolves()
    {
        var existing = ActiveAlert("supply_low:1", AlertType.SupplyLow, AlertSeverity.Warning);
        var reading = new DeviceReading
        {
            Supplies = { new SupplyReading(1, 3, "Black Toner", 7, 10000, 9000) } // 90%
        };
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), new[] { existing }, Now);
        var resolve = actions.Single(a => a.AlertKey == "supply_low:1");
        Assert.Equal(AlertActionType.Resolve, resolve.ActionType);
    }

    [Fact]
    public void WasteSupplies_NeverAlert()
    {
        var reading = new DeviceReading
        {
            Supplies =
            {
                new SupplyReading(1, 4, "Waste Toner", 7, 10000, 9900),  // wasteToner
                new SupplyReading(2, 8, "Waste Ink", 7, 10000, 9900)     // wasteInk
            }
        };
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), Array.Empty<Alert>(), Now);
        Assert.Empty(actions);
    }

    [Fact]
    public void SupplyWithUnknownPercent_NoAlert()
    {
        var reading = new DeviceReading
        {
            Supplies = { new SupplyReading(1, 3, "Black Toner", 7, -2, -2) } // max/level 未知
        };
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), Array.Empty<Alert>(), Now);
        Assert.Empty(actions);
    }

    // ---------- 离线 ----------

    [Fact]
    public void ConsecutiveFailures_BelowThreshold_NoAlert()
    {
        var actions = AlertEngine.Evaluate(Printer(failures: 2), null, false, Thresholds(offlineFailures: 3),
            Array.Empty<Alert>(), Now);
        Assert.Empty(actions);
    }

    [Fact]
    public void ConsecutiveFailures_AtThreshold_RaisesOffline()
    {
        var actions = AlertEngine.Evaluate(Printer(failures: 3), null, false, Thresholds(offlineFailures: 3),
            Array.Empty<Alert>(), Now);
        var raise = actions.Single();
        Assert.Equal(AlertActionType.Raise, raise.ActionType);
        Assert.Equal(AlertType.Offline, raise.AlertType);
        Assert.Equal(AlertSeverity.Critical, raise.Severity);
    }

    [Fact]
    public void OfflineRecovered_Resolves()
    {
        var existing = ActiveAlert("offline", AlertType.Offline, AlertSeverity.Critical);
        var reading = new DeviceReading();
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), new[] { existing }, Now);
        Assert.Contains(actions, a => a.AlertKey == "offline" && a.ActionType == AlertActionType.Resolve);
    }

    // ---------- 纸张 ----------

    [Fact]
    public void NoPaperBit_RaisesPaperOut_ThenResolves()
    {
        var reading = new DeviceReading { DetectedErrorState = new byte[] { 0b0100_0000 } }; // bit1 noPaper（MSB-first）
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), Array.Empty<Alert>(), Now);
        Assert.Contains(actions, a => a.AlertKey == "paper_out" && a.ActionType == AlertActionType.Raise);

        var existing = ActiveAlert("paper_out", AlertType.PaperOut, AlertSeverity.Warning);
        var ok = new DeviceReading { DetectedErrorState = new byte[] { 0x00 } };
        var resolve = AlertEngine.Evaluate(Printer(), ok, true, Thresholds(), new[] { existing }, Now);
        Assert.Contains(resolve, a => a.AlertKey == "paper_out" && a.ActionType == AlertActionType.Resolve);
    }

    [Fact]
    public void JamBit_RaisesPaperJam()
    {
        var reading = new DeviceReading { DetectedErrorState = new byte[] { 0b0000_0100 } }; // bit5 jammed（MSB-first）
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), Array.Empty<Alert>(), Now);
        Assert.Contains(actions, a => a.AlertKey == "paper_jam" && a.ActionType == AlertActionType.Raise);
    }

    [Fact]
    public void EmptyTray_RaisesPaperOutPerTray()
    {
        var reading = new DeviceReading
        {
            InputTrays = { new InputTrayReading(2, "Tray 2", 500, 0) }
        };
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), Array.Empty<Alert>(), Now);
        Assert.Contains(actions, a => a.AlertKey == "paper_out:tray:2" && a.ActionType == AlertActionType.Raise);
    }

    // ---------- 设备告警表 ----------

    [Fact]
    public void CriticalAlertTableEntry_RaisesDeviceAlert_AndResolvesWhenGone()
    {
        var reading = new DeviceReading
        {
            AlertTableEntries = { new DeviceAlertReading(1, 3, 8, 1712, "Fuser unit failure") }
        };
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), Array.Empty<Alert>(), Now);
        Assert.Contains(actions, a => a.AlertKey == "device_alert:8:1712" && a.ActionType == AlertActionType.Raise);

        var existing = ActiveAlert("device_alert:8:1712", AlertType.DeviceAlert, AlertSeverity.Critical);
        var clean = new DeviceReading();
        var resolved = AlertEngine.Evaluate(Printer(), clean, true, Thresholds(), new[] { existing }, Now);
        Assert.Contains(resolved, a => a.AlertKey == "device_alert:8:1712" && a.ActionType == AlertActionType.Resolve);
    }

    [Fact]
    public void JamDescriptionInAlertTable_ClassifiedAsJam()
    {
        var reading = new DeviceReading
        {
            AlertTableEntries = { new DeviceAlertReading(1, 4, 4, 0, "Paper jam in Tray 1") }
        };
        var actions = AlertEngine.Evaluate(Printer(), reading, true, Thresholds(), Array.Empty<Alert>(), Now);
        Assert.Contains(actions, a => a.AlertKey == "paper_jam:alert" && a.AlertType == AlertType.PaperJam);
    }
}
