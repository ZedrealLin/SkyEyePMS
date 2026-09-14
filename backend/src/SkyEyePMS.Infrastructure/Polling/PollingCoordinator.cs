using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyEyePMS.Core.Alerting;
using SkyEyePMS.Core.Collecting;
using SkyEyePMS.Infrastructure.Collecting;
using SkyEyePMS.Infrastructure.Notifying;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Notifying;
using SkyEyePMS.Core.Reading;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Security;
using SkyEyePMS.Core.Services;

namespace SkyEyePMS.Infrastructure.Polling;

/// <summary>轮询参数（appsettings "Polling" 节）</summary>
public class PollingOptions
{
    /// <summary>是否启用后台轮询（测试/特殊部署时可关闭，仍可手动触发）</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>轮询间隔（分钟），1-5 分钟为宜</summary>
    public int IntervalMinutes { get; set; } = 2;
    /// <summary>并发采集台数上限（100+ 台规模建议 20-30）</summary>
    public int Concurrency { get; set; } = 20;
}

public record PollOutcome(bool Success, string? Error, Core.Reading.DeviceReading? Reading);

/// <summary>供 API 手动触发采集（立即刷新单台/全部）</summary>
public interface IPollingCoordinator
{
    DateTime? LastRoundAt { get; }
    bool IsRoundRunning { get; }
    Task<PollOutcome?> PollPrinterNowAsync(int printerId, CancellationToken ct = default);
    /// <summary>触发一轮完整采集；若已有轮次在执行返回 -1</summary>
    Task<int> RunRoundAsync(CancellationToken ct = default);
}

/// <summary>
/// 轮询编排器：一轮 = 并发采集所有启用打印机（信号量限流）→ 运行状态/采样/耗材落库 →
/// 告警引擎评估 → 告警动作落库 → 通知分发。
/// </summary>
public class PollingCoordinator : IPollingCoordinator
{
    private readonly IPrinterRepository _printers;
    private readonly IAlertRepository _alerts;
    private readonly IMetricsRepository _metrics;
    private readonly IAppSettingsService _settings;
    private readonly ISnmpCollector _collector;
    private readonly ISecretProtector _protector;
    private readonly AlertDispatcher _dispatcher;
    private readonly PollingOptions _options;
    private readonly ILogger<PollingCoordinator> _logger;
    private readonly SemaphoreSlim _roundGate = new(1, 1);

    public DateTime? LastRoundAt { get; private set; }
    public bool IsRoundRunning => _roundGate.CurrentCount == 0;

    public PollingCoordinator(IPrinterRepository printers, IAlertRepository alerts, IMetricsRepository metrics,
        IAppSettingsService settings, ISnmpCollector collector, ISecretProtector protector,
        AlertDispatcher dispatcher, IOptions<PollingOptions> options, ILogger<PollingCoordinator> logger)
    {
        _printers = printers;
        _alerts = alerts;
        _metrics = metrics;
        _settings = settings;
        _collector = collector;
        _protector = protector;
        _dispatcher = dispatcher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PollOutcome?> PollPrinterNowAsync(int printerId, CancellationToken ct = default)
    {
        var printer = await _printers.GetByIdAsync(printerId);
        if (printer == null) return null;
        var snapshot = await _settings.GetSnapshotAsync();
        return await PollOneAsync(printer, snapshot, ct);
    }

    public async Task<int> RunRoundAsync(CancellationToken ct = default)
    {
        if (!await _roundGate.WaitAsync(0, ct)) return -1;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = await _settings.GetSnapshotAsync();
            var printers = await _printers.GetAllAsync(onlyEnabled: true);
            using var gate = new SemaphoreSlim(Math.Max(1, snapshot.Polling.Concurrency > 0
                ? snapshot.Polling.Concurrency : _options.Concurrency));

            var tasks = printers.Select(async printer =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    await PollOneAsync(printer, snapshot, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "采集打印机 {Printer}（{Ip}）时发生未处理异常", printer.Name, printer.Ip);
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks);
            LastRoundAt = DateTime.UtcNow;
            _logger.LogInformation("本轮采集完成：{Count} 台，耗时 {Elapsed:F1}s", printers.Count, sw.Elapsed.TotalSeconds);
            return printers.Count;
        }
        finally
        {
            _roundGate.Release();
        }
    }

    private async Task<PollOutcome> PollOneAsync(Printer printer, SettingsSnapshot snapshot, CancellationToken ct)
    {
        var community = _protector.Unprotect(printer.CommunityCipher);
        var endpoint = new PrinterEndpoint(printer.Ip, printer.Port, community, printer.SnmpVersion);

        var now = DateTime.UtcNow;
        DeviceReading? reading = null;
        var success = true;
        string? error = null;
        try
        {
            reading = await _collector.CollectAsync(endpoint, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            success = false;
            error = ex.Message;
            _logger.LogDebug("采集 {Printer}（{Ip}）失败：{Error}", printer.Name, printer.Ip, ex.Message);
        }

        printer.LastPollAt = now;
        if (success)
        {
            printer.ConsecutiveFailures = 0;
            printer.LastSuccessAt = now;
            printer.LastOnline = true;
            printer.SysName = reading!.SysName;
            printer.SysDescr = reading.SysDescr;
            if (reading.PageCount.HasValue) printer.PageCount = reading.PageCount;
            if (reading.HrPrinterStatus != printer.HrStatus)
            {
                printer.HrStatus = reading.HrPrinterStatus;
                printer.StatusChangedAt = now;
            }
            // 序列号以设备为准自动回填（设备未上报时保留原值）
            if (!string.IsNullOrWhiteSpace(reading.SerialNumber))
                printer.SerialNumber = reading.SerialNumber;
            await _printers.UpdateRuntimeAsync(printer);
            await _metrics.InsertStatusSampleAsync(new StatusSample
            {
                PrinterId = printer.Id,
                CollectedAt = now,
                Online = true,
                HrStatus = reading.HrPrinterStatus,
                PageCount = reading.PageCount
            });
            await SaveSuppliesAsync(printer.Id, reading, now, snapshot.Sampling.SupplySampleMinChangePercent);
        }
        else
        {
            printer.ConsecutiveFailures++;
            var markedOffline = printer.ConsecutiveFailures >= Math.Max(1, snapshot.Thresholds.OfflineConsecutiveFailures);
            if (markedOffline) printer.LastOnline = false;
            await _printers.UpdateRuntimeFailureAsync(printer.Id, printer.ConsecutiveFailures, now,
                markedOffline ? false : printer.LastOnline);
            if (markedOffline)
            {
                await _metrics.InsertStatusSampleAsync(new StatusSample
                {
                    PrinterId = printer.Id,
                    CollectedAt = now,
                    Online = false
                });
            }
        }

        await EvaluateAlertsAsync(printer, reading, success, snapshot, ct);
        return new PollOutcome(success, error, reading);
    }

    private async Task SaveSuppliesAsync(int printerId, Core.Reading.DeviceReading reading, DateTime now,
        int sampleMinChangePercent)
    {
        foreach (var s in reading.Supplies)
        {
            var supply = new Supply
            {
                PrinterId = printerId,
                SupplyIndex = s.Index,
                Name = s.Description is { Length: > 60 } ? s.Description[..60] : s.Description,
                SupplyType = s.Type,
                Unit = s.Unit,
                MaxCapacity = s.MaxCapacity,
                Level = s.Level,
                Percent = s.Percent,
                UpdatedAt = now
            };
            await _metrics.UpsertSupplyAsync(supply);

            // 历史采样：百分比变化达到配置的百分点才写入，避免表无限膨胀
            if (s.Percent == null) continue;
            var minChange = Math.Max(1, sampleMinChangePercent);
            var last = await _metrics.GetLastSupplySampleAsync(printerId, s.Index);
            if (last == null || last.Percent == null ||
                Math.Abs(last.Percent.Value - s.Percent.Value) >= minChange)
            {
                await _metrics.InsertSupplySampleAsync(new SupplySample
                {
                    PrinterId = printerId,
                    SupplyIndex = s.Index,
                    Percent = s.Percent,
                    Level = s.Level,
                    CollectedAt = now
                });
            }
        }
        await _metrics.DeleteSuppliesNotInAsync(printerId, reading.Supplies.Select(x => x.Index));
    }

    private async Task EvaluateAlertsAsync(Printer printer, Core.Reading.DeviceReading? reading,
        bool success, SettingsSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            // 单台打印机的耗材低阈值覆盖（克隆全局阈值后套用）
            var thresholds = snapshot.Thresholds;
            if (printer.SupplyLowPercentOverride is { } ov && ov > 0)
            {
                thresholds = thresholds.Clone();
                thresholds.SupplyLowPercent = Math.Clamp(ov, 1, 99);
            }

            var existing = await _alerts.GetActiveByPrinterAsync(printer.Id);
            var actions = AlertEngine.Evaluate(printer, reading, success, snapshot.Thresholds,
                existing, DateTime.UtcNow);
            foreach (var action in actions)
            {
                switch (action.ActionType)
                {
                    case AlertActionType.Raise:
                    {
                        var alert = new Alert
                        {
                            PrinterId = printer.Id,
                            AlertKey = action.AlertKey,
                            Type = action.AlertType.ToString(),
                            Severity = action.Severity.ToString(),
                            Message = action.Message,
                            Status = "active",
                            FirstTriggeredAt = DateTime.UtcNow,
                            LastNotifiedAt = DateTime.UtcNow,
                            NotifyCount = 1
                        };
                        alert.Id = await _alerts.CreateAsync(alert);
                        await _dispatcher.DispatchAsync(alert, printer, AlertEventKind.Raised, ct);
                        _logger.LogInformation("打印机 {Printer} 触发新告警 [{Key}] {Message}",
                            printer.Name, action.AlertKey, action.Message);
                        break;
                    }
                    case AlertActionType.ReNotify:
                    {
                        if (action.Existing == null || !AlertEngine.ShouldNotify(action, snapshot.Thresholds, DateTime.UtcNow))
                            break;
                        await _alerts.MarkNotifiedAsync(action.Existing.Id, DateTime.UtcNow);
                        action.Existing.Message = action.Message;
                        await _dispatcher.DispatchAsync(action.Existing, printer, AlertEventKind.ReNotified, ct);
                        break;
                    }
                    case AlertActionType.Resolve:
                    {
                        if (action.Existing == null) break;
                        await _alerts.ResolveAsync(action.Existing.Id, DateTime.UtcNow);
                        action.Existing.Status = "resolved";
                        action.Existing.ResolvedAt = DateTime.UtcNow;
                        await _dispatcher.DispatchAsync(action.Existing, printer, AlertEventKind.Resolved, ct);
                        _logger.LogInformation("打印机 {Printer} 告警 [{Key}] 已恢复", printer.Name, action.AlertKey);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打印机 {Printer} 告警评估失败", printer.Name);
        }
    }
}
