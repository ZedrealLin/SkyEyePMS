using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkyEyePMS.Core.Collecting;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Notifying;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Services;
using SkyEyePMS.Infrastructure;
using SkyEyePMS.Infrastructure.Database;
using SkyEyePMS.Infrastructure.Notifying;
using SkyEyePMS.Infrastructure.Polling;
using SkyEyePMS.Tests.Helpers;

namespace SkyEyePMS.Tests;

/// <summary>
/// 轮询全链路集成测试：MiniSnmpAgent（真实 UDP）→ 采集 → SQLite 落库 →
/// 告警引擎 → mock 通知渠道，覆盖"在线/耗材/缺纸 → 离线告警 → 恢复"完整状态机。
/// </summary>
public class PollingIntegrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"skyeye-test-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = default!;
    private readonly MiniSnmpAgent _agent = new();
    private readonly MockNotifier _feishu = new("feishu");
    private readonly MockNotifier _email = new("email");

    private IPrinterRepository Printers => _provider.GetRequiredService<IPrinterRepository>();
    private IAlertRepository Alerts => _provider.GetRequiredService<IAlertRepository>();
    private IMetricsRepository Metrics => _provider.GetRequiredService<IMetricsRepository>();
    private IAppSettingsService Settings => _provider.GetRequiredService<IAppSettingsService>();
    private IPollingCoordinator Coordinator => _provider.GetRequiredService<IPollingCoordinator>();

    private async Task<Printer> BuildAsync()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "sqlite",
                ["Database:SqlitePath"] = _dbPath,
                ["Security:EncryptionKey"] = "polling-test-key",
                ["Polling:Enabled"] = "false",
                ["Snmp:TimeoutMs"] = "1500",
                ["Snmp:Retries"] = "0"
            })
            .Build();
        services.AddSkyEyePmsInfrastructure(config);
        // 后注册的 mock 注册表覆盖默认实现，AlertDispatcher 会随容器解析到它
        services.AddSingleton<INotifierRegistry>(new MockNotifierRegistry(_feishu, _email));

        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<DbInitializer>().InitializeAsync();

        var printers = _provider.GetRequiredService<IPrinterRepository>();
        var protector = _provider.GetRequiredService<Core.Security.ISecretProtector>();
        var id = await printers.CreateAsync(new Printer
        {
            Name = "MOCK-PRN-1",
            Ip = "127.0.0.1",
            Port = _agent.Port,
            CommunityCipher = protector.Protect("public"),
            SnmpVersion = 2,
            Location = "3F",
            Department = "Finance",
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        return (await printers.GetByIdAsync(id))!;
    }

    public void Dispose()
    {
        _agent.Dispose();
        (_provider as IDisposable)?.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task FullCycle_OnlineAlertOffline_Recovery()
    {
        var printer = await BuildAsync();

        // ---------- 第 1 轮：设备正常，耗材 80% ----------
        var snapshot = await Settings.GetSnapshotAsync();
        snapshot.Thresholds.OfflineConsecutiveFailures = 2; // 加速离线判定
        snapshot.Thresholds.AlertSilenceMinutes = 60;
        await Settings.SaveThresholdsAsync(snapshot.Thresholds);

        var outcome = await Coordinator.PollPrinterNowAsync(printer.Id);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Success, outcome.Error);

        var after = await Printers.GetByIdAsync(printer.Id);
        Assert.True(after!.LastOnline);
        Assert.Equal(12345, after.PageCount);
        Assert.Equal(0, after.ConsecutiveFailures);

        var supplies = await Metrics.GetSuppliesAsync(printer.Id);
        Assert.Equal(2, supplies.Count);
        Assert.Equal(80, supplies.First(s => s.SupplyIndex == 1).Percent);
        Assert.Equal(50, supplies.First(s => s.SupplyIndex == 2).Percent); // 2500/5000

        var samples = await Metrics.GetStatusSamplesAsync(printer.Id, DateTime.UtcNow.AddMinutes(-5));
        Assert.Single(samples);
        Assert.True(samples[0].Online);

        Assert.Empty(await Alerts.QueryAsync("active"));

        // ---------- 第 2 轮：设备缺纸 ----------
        _agent.Device.ErrorState = new byte[] { 0b0100_0000 };
        _agent.Device.Supplies[1] = (3, "Black Toner", 10000, 1500); // 15% → 低于 20%
        await Coordinator.PollPrinterNowAsync(printer.Id);
        var active = await Alerts.QueryAsync("active");
        Assert.Contains(active, a => a.AlertKey == "paper_out");
        Assert.Contains(active, a => a.AlertKey == "supply_low:1");
        Assert.All(await Alerts.QueryAsync("active"), a => Assert.Equal(1, a.NotifyCount));
        Assert.Equal(2, _feishu.Sent.Count); // paper_out + supply_low
        Assert.Equal(2, _email.Sent.Count);

        // ---------- 第 3-4 轮：设备掉线（阈值 2 → 第 2 次失败触发离线告警） ----------
        _agent.Offline = true;
        await Coordinator.PollPrinterNowAsync(printer.Id); // 第 1 次失败（未达阈值）
        var mid = await Printers.GetByIdAsync(printer.Id);
        Assert.Equal(1, mid!.ConsecutiveFailures);
        Assert.True(mid.LastOnline); // 未达阈值仍视为在线

        await Coordinator.PollPrinterNowAsync(printer.Id); // 第 2 次失败 → 离线
        var offline = await Printers.GetByIdAsync(printer.Id);
        Assert.False(offline!.LastOnline);
        Assert.Equal(2, offline.ConsecutiveFailures);
        var activeAfterOffline = await Alerts.QueryAsync("active");
        Assert.Contains(activeAfterOffline, a => a.AlertKey == "offline" && a.StatusEnum == AlertStatus.Active);

        // 采样表里出现 offline=false 的记录
        var offlineSamples = await Metrics.GetStatusSamplesAsync(printer.Id, DateTime.UtcNow.AddMinutes(-5));
        Assert.Contains(offlineSamples, s => !s.Online);

        // ---------- 第 5 轮：设备恢复 → 离线/缺纸/低耗材全部自动恢复 ----------
        _agent.Offline = false;
        _agent.Device.ErrorState = null;
        _agent.Device.Supplies[1] = (3, "Black Toner", 10000, 9000); // 90%
        await Coordinator.PollPrinterNowAsync(printer.Id);

        Assert.Empty(await Alerts.QueryAsync("active"));
        var resolved = await Alerts.QueryAsync("resolved");
        Assert.Equal(3, resolved.Count); // paper_out, supply_low:1, offline

        // 恢复事件也推送（3 类告警的 resolved 通知）
        var resolvedNotifies = _feishu.Sent.Count(n => n.EventKind == AlertEventKind.Resolved);
        Assert.Equal(3, resolvedNotifies);
    }

    [Fact]
    public async Task SupplySampleHistory_OnlyWrittenOnChange()
    {
        var printer = await BuildAsync();

        await Coordinator.PollPrinterNowAsync(printer.Id);
        var s1 = await Metrics.GetSupplySamplesAsync(printer.Id, 1, DateTime.UtcNow.AddMinutes(-5));
        Assert.Single(s1); // 首次采集写一条

        await Coordinator.PollPrinterNowAsync(printer.Id); // 相同值不再写
        var s2 = await Metrics.GetSupplySamplesAsync(printer.Id, 1, DateTime.UtcNow.AddMinutes(-5));
        Assert.Single(s2);

        _agent.Device.Supplies[1] = (3, "Black Toner", 10000, 7800); // 78%，变化 ≥1 点
        await Coordinator.PollPrinterNowAsync(printer.Id);
        var s3 = await Metrics.GetSupplySamplesAsync(printer.Id, 1, DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(2, s3.Count);
    }

    [Fact]
    public async Task ManualPoll_FailedDevice_ReportsError()
    {
        var printer = await BuildAsync();
        _agent.Offline = true;
        var outcome = await Coordinator.PollPrinterNowAsync(printer.Id);
        Assert.NotNull(outcome);
        Assert.False(outcome!.Success);
        Assert.NotNull(outcome.Error);
    }
}
