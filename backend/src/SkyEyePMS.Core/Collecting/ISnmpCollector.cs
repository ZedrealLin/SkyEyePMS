using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Reading;

namespace SkyEyePMS.Core.Collecting;

/// <summary>SNMP 目标（Community 已解密）</summary>
public record PrinterEndpoint(string Ip, int Port, string Community, int SnmpVersion);

public record SnmpTestResult(bool Success, string? SysDescr, string? SysName, string? Error, int ElapsedMs);

/// <summary>SNMP 采集器：一次采集返回设备快照；失败抛 SnmpCollectException</summary>
public interface ISnmpCollector
{
    Task<DeviceReading> CollectAsync(PrinterEndpoint endpoint, CancellationToken ct = default);
    Task<SnmpTestResult> TestAsync(PrinterEndpoint endpoint, CancellationToken ct = default);
    Task<IReadOnlyList<DiscoveryHit>> DiscoverAsync(
        string startIp, string endIp, int port, string community, int snmpVersion,
        int timeoutMs, IProgress<(int Scanned, int Total)>? progress = null, CancellationToken ct = default);
}

public record DiscoveryHit(string Ip, string? SysName, string? SysDescr);

/// <summary>采集失败（超时/拒绝/协议错误）</summary>
public class SnmpCollectException : Exception
{
    public SnmpCollectException(string message, Exception? inner = null) : base(message, inner) { }
}
