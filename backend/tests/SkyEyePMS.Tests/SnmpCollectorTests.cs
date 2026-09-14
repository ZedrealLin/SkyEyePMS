using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkyEyePMS.Core.Collecting;
using SkyEyePMS.Infrastructure.Collecting;
using SkyEyePMS.Tests.Helpers;

namespace SkyEyePMS.Tests;

/// <summary>SNMP 采集器 ↔ MiniSnmpAgent（真实 UDP 协议）集成测试</summary>
public class SnmpCollectorTests : IDisposable
{
    private readonly MiniSnmpAgent _agent = new();
    private readonly SnmpCollector _collector = new(
        Options.Create(new SnmpOptions { TimeoutMs = 1500, Retries = 0 }),
        NullLogger<SnmpCollector>.Instance);

    private PrinterEndpoint Endpoint() => new("127.0.0.1", _agent.Port, "public", 2);

    public void Dispose() => _agent.Dispose();

    [Fact]
    public async Task Collect_ReturnsFullDeviceReading()
    {
        var agent = _agent.Device;
        agent.Supplies[1] = (3, "Black Toner", 10000, 8000);  // 80%
        agent.Supplies[2] = (6, "Cyan Cartridge", 5000, 500);  // 10%
        agent.InputTrays[1] = ("Tray 1", 500, 250);
        agent.PageCount = 4242;

        var reading = await _collector.CollectAsync(Endpoint());

        Assert.True(reading.Online);
        Assert.Equal(agent.SysDescr, reading.SysDescr);
        Assert.Equal(agent.SysName, reading.SysName);
        Assert.Equal(3, reading.HrPrinterStatus);
        Assert.Equal(4242, reading.PageCount);

        var black = reading.Supplies.Single(s => s.Index == 1);
        Assert.Equal(3, black.Type);
        Assert.Equal(80, black.Percent);
        var cyan = reading.Supplies.Single(s => s.Index == 2);
        Assert.Equal(10, cyan.Percent);

        var tray = reading.InputTrays.Single(t => t.Index == 1);
        Assert.Equal("Tray 1", tray.Name);
        Assert.False(tray.IsEmpty);
    }

    [Fact]
    public async Task Collect_ParsesErrorStateFlags()
    {
        _agent.Device.ErrorState = new byte[] { 0b0100_0100 }; // noPaper + jammed（MSB-first）
        var reading = await _collector.CollectAsync(Endpoint());
        Assert.True(reading.ErrorFlags.HasFlag(Core.Reading.PrinterErrorFlag.NoPaper));
        Assert.True(reading.ErrorFlags.HasFlag(Core.Reading.PrinterErrorFlag.Jammed));
    }

    [Fact]
    public async Task Collect_ReadsAlertTable()
    {
        _agent.Device.AlertTable.Add((3, 8, 1712, "Fuser failure"));
        var reading = await _collector.CollectAsync(Endpoint());
        var entry = reading.AlertTableEntries.Single();
        Assert.Equal(3, entry.Severity);
        Assert.True(entry.IsCritical);
        Assert.Equal("Fuser failure", entry.Description);
    }

    [Fact]
    public async Task Collect_WrongCommunity_Throws()
    {
        _agent.Community = "private";
        await Assert.ThrowsAsync<SnmpCollectException>(() => _collector.CollectAsync(Endpoint()));
    }

    [Fact]
    public async Task Collect_OfflineDevice_ThrowsQuickly()
    {
        _agent.Offline = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<SnmpCollectException>(() => _collector.CollectAsync(Endpoint()));
        // TimeoutMs=1500，重试 0 → 应在数秒内返回，不会无限挂起
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"elapsed={sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Collect_UnreachableHost_ThrowsQuickly()
    {
        // 203.0.113.x 为 TEST-NET-3 文档地址，正常环境不可达；若直连无效会快速超时
        var endpoint = new PrinterEndpoint("203.0.113.1", 161, "public", 2);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<SnmpCollectException>(() => _collector.CollectAsync(endpoint));
        Assert.True(sw.ElapsedMilliseconds < 15_000, $"elapsed={sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task TestAsync_ReturnsSuccessAndInfo()
    {
        var result = await _collector.TestAsync(Endpoint());
        Assert.True(result.Success);
        Assert.NotNull(result.SysDescr);
        Assert.True(result.ElapsedMs >= 0);
    }

    [Fact]
    public async Task Discover_FindsAgent()
    {
        var hits = await _collector.DiscoverAsync("127.0.0.1", "127.0.0.1", _agent.Port, "public", 2, 1000);
        var hit = Assert.Single(hits);
        Assert.Equal("127.0.0.1", hit.Ip);
        Assert.NotNull(hit.SysDescr);
    }

    [Fact]
    public async Task Discover_WrongCommunity_NoHits()
    {
        _agent.Community = "other";
        var hits = await _collector.DiscoverAsync("127.0.0.1", "127.0.0.1", _agent.Port, "public", 2, 800);
        Assert.Empty(hits);
    }
}
