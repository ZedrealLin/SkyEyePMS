using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyEyePMS.Core.Collecting;
using SkyEyePMS.Core.Reading;

namespace SkyEyePMS.Infrastructure.Collecting;

/// <summary>SNMP 采集参数（appsettings "Snmp" 节）</summary>
public class SnmpOptions
{
    /// <summary>单次请求超时（毫秒）</summary>
    public int TimeoutMs { get; set; } = 2500;
    /// <summary>发现扫描的单 IP 超时（毫秒）</summary>
    public int DiscoverTimeoutMs { get; set; } = 1200;
    /// <summary>发现扫描并发数</summary>
    public int DiscoverConcurrency { get; set; } = 50;
    /// <summary>采集失败重试次数（同轮内）</summary>
    public int Retries { get; set; } = 1;
}

public class SnmpCollector : ISnmpCollector
{
    private readonly ILogger<SnmpCollector> _logger;
    private readonly SnmpOptions _options;

    public SnmpCollector(IOptions<SnmpOptions> options, ILogger<SnmpCollector> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DeviceReading> CollectAsync(PrinterEndpoint endpoint, CancellationToken ct = default)
    {
        var attempts = Math.Max(0, _options.Retries) + 1;
        SnmpCollectException? lastError = null;
        for (var i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await CollectOnceAsync(endpoint, ct);
            }
            catch (SnmpCollectException ex)
            {
                lastError = ex;
                if (i < attempts - 1)
                {
                    _logger.LogDebug("SNMP collect retry {Attempt}/{Total} for {Ip}: {Error}",
                        i + 1, attempts, endpoint.Ip, ex.Message);
                    await Task.Delay(300, ct);
                }
            }
        }
        throw lastError!;
    }

    private async Task<DeviceReading> CollectOnceAsync(PrinterEndpoint endpoint, CancellationToken ct)
    {
        var (version, ep, community) = Resolve(endpoint);
        var reading = new DeviceReading { Online = true };

        // 1) 存活探测 + 系统信息：任一失败即整轮采集失败
        var scalars = new Dictionary<ObjectIdentifier, ISnmpData>();
        try
        {
            var vars = await GetAsync(version, ep, community,
                new List<Variable> { new(SnmpOids.SysDescr), new(SnmpOids.SysName) },
                _options.TimeoutMs, ct);
            foreach (var v in vars) scalars[v.Id] = v.Data;
        }
        catch (SnmpCollectException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SnmpCollectException($"设备无响应（{endpoint.Ip}:{endpoint.Port}）：{ex.Message}", ex);
        }

        reading.SysDescr = Truncate(AsText(scalars, SnmpOids.SysDescr), 255);
        reading.SysName = Truncate(AsText(scalars, SnmpOids.SysName), 100);

        // 2) 打印机状态位（可选字段，失败不致命）
        try
        {
            var rows = await WalkAsync(version, ep, community, SnmpOids.HrPrinterStatus, _options.TimeoutMs, ct);
            var first = rows.FirstOrDefault();
            if (first?.Data is Integer32 hs) reading.HrPrinterStatus = hs.ToInt32();

            rows = await WalkAsync(version, ep, community, SnmpOids.HrPrinterDetectedErrorState, _options.TimeoutMs, ct);
            first = rows.FirstOrDefault();
            if (first?.Data is OctetString err) reading.DetectedErrorState = err.GetRaw();

            rows = await WalkAsync(version, ep, community, SnmpOids.HrDeviceStatus, _options.TimeoutMs, ct);
            first = rows.FirstOrDefault();
            if (first?.Data is Integer32 ds) reading.HrDeviceStatus = ds.ToInt32();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("hrPrinter 状态采集失败（忽略）{Ip}: {Error}", endpoint.Ip, ex.Message);
        }

        // 3.5) 设备序列号（可选，失败不致命；资产台账自动回填用）
        try
        {
            var vars = await GetAsync(version, ep, community,
                new List<Variable> { new(SnmpOids.PrtGeneralSerialNumber) }, _options.TimeoutMs, ct);
            var serial = vars.FirstOrDefault()?.Data switch
            {
                OctetString os => CleanDescription(os.ToString()),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(serial)) reading.SerialNumber = serial[..Math.Min(serial.Length, 120)];
        }
        catch (Exception ex)
        {
            _logger.LogDebug("序列号采集失败（忽略）{Ip}: {Error}", endpoint.Ip, ex.Message);
        }

        // 3) 累计页数
        try
        {
            var rows = await WalkAsync(version, ep, community, SnmpOids.PrtMarkerLifeCount, _options.TimeoutMs, ct);
            long max = 0;
            foreach (var row in rows)
                if (row.Data is Integer32 i32 && i32.ToInt32() >= 0 && i32.ToInt32() > max) max = i32.ToInt32();
                else if (row.Data is Counter32 c && c.ToUInt32() > (ulong)max) max = c.ToUInt32();
                else if (row.Data is Gauge32 g && g.ToUInt32() > (ulong)max) max = g.ToUInt32();
            if (max > 0) reading.PageCount = max;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("页数采集失败（忽略）{Ip}: {Error}", endpoint.Ip, ex.Message);
        }

        // 4) 耗材表
        try
        {
            var types = await WalkToMapAsync(version, ep, community, SnmpOids.SuppliesType, ct);
            var descs = await WalkToTextMapAsync(version, ep, community, SnmpOids.SuppliesDescription, ct);
            var units = await WalkToMapAsync(version, ep, community, SnmpOids.SuppliesUnit, ct);
            var maxCaps = await WalkToMapAsync(version, ep, community, SnmpOids.SuppliesMaxCapacity, ct);
            var levels = await WalkToMapAsync(version, ep, community, SnmpOids.SuppliesLevel, ct);

            foreach (var idx in levels.Keys.Union(maxCaps.Keys).Distinct())
            {
                reading.Supplies.Add(new SupplyReading(
                    Index: idx,
                    Type: types.TryGetValue(idx, out var t) ? (int?)t : null,
                    Description: CleanDescription(descs.TryGetValue(idx, out var d) ? d : null),
                    Unit: units.TryGetValue(idx, out var u) ? (int?)u : null,
                    MaxCapacity: maxCaps.TryGetValue(idx, out var mc) ? (long?)mc : null,
                    Level: levels.TryGetValue(idx, out var lv) ? (long?)lv : null));
            }
            reading.Supplies.Sort((a, b) => a.Index.CompareTo(b.Index));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("耗材表采集失败（忽略）{Ip}: {Error}", endpoint.Ip, ex.Message);
        }

        // 5) 纸盒表
        try
        {
            var mediaLevels = await WalkToMapAsync(version, ep, community, SnmpOids.InputMediaLevel, ct);
            var mediaMax = await WalkToMapAsync(version, ep, community, SnmpOids.InputMediaMaxCapacity, ct);
            var names = await WalkToTextMapAsync(version, ep, community, SnmpOids.InputMediaName, ct);
            var inputNames = await WalkToTextMapAsync(version, ep, community, SnmpOids.InputName, ct);

            foreach (var idx in mediaLevels.Keys.Union(mediaMax.Keys).Distinct())
            {
                var name = names.TryGetValue(idx, out var n) && !string.IsNullOrWhiteSpace(n) ? n
                    : inputNames.TryGetValue(idx, out var n2) ? n2 : null;
                reading.InputTrays.Add(new InputTrayReading(
                    Index: idx,
                    Name: CleanDescription(name),
                    MaxCapacity: mediaMax.TryGetValue(idx, out var mc) ? (long?)mc : null,
                    Level: mediaLevels.TryGetValue(idx, out var lv) ? (long?)lv : null));
            }
            reading.InputTrays.Sort((a, b) => a.Index.CompareTo(b.Index));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("纸盒表采集失败（忽略）{Ip}: {Error}", endpoint.Ip, ex.Message);
        }

        // 6) 告警表
        try
        {
            var sev = await WalkToMapAsync(version, ep, community, SnmpOids.AlertSeverity, ct);
            var grp = await WalkToMapAsync(version, ep, community, SnmpOids.AlertGroup, ct);
            var code = await WalkToMapAsync(version, ep, community, SnmpOids.AlertCode, ct);
            var desc = await WalkToTextMapAsync(version, ep, community, SnmpOids.AlertDescription, ct);

            foreach (var idx in sev.Keys.Union(code.Keys).Distinct())
            {
                reading.AlertTableEntries.Add(new DeviceAlertReading(
                    Index: idx,
                    Severity: sev.TryGetValue(idx, out var s) ? (int)s : 0,
                    Group: grp.TryGetValue(idx, out var g) ? (int)g : 0,
                    Code: code.TryGetValue(idx, out var c) ? (int)c : 0,
                    Description: CleanDescription(desc.TryGetValue(idx, out var d) ? d : null)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("告警表采集失败（忽略）{Ip}: {Error}", endpoint.Ip, ex.Message);
        }

        return reading;
    }

    public async Task<SnmpTestResult> TestAsync(PrinterEndpoint endpoint, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (version, ep, community) = Resolve(endpoint);
            var vars = await GetAsync(version, ep, community,
                new List<Variable> { new(SnmpOids.SysDescr), new(SnmpOids.SysName) },
                _options.TimeoutMs, ct);
            string? descr = null, name = null;
            foreach (var v in vars)
            {
                if (v.Id.Equals(SnmpOids.SysDescr)) descr = Truncate(v.Data.ToString(), 255);
                if (v.Id.Equals(SnmpOids.SysName)) name = Truncate(v.Data.ToString(), 100);
            }
            return new SnmpTestResult(true, descr, name, null, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new SnmpTestResult(false, null, null, ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<DiscoveryHit>> DiscoverAsync(
        string startIp, string endIp, int port, string community, int snmpVersion,
        int timeoutMs, IProgress<(int Scanned, int Total)>? progress = null, CancellationToken ct = default)
    {
        var ips = ExpandIpRange(startIp, endIp);
        var hits = new ConcurrentBag<DiscoveryHit>();
        using var gate = new SemaphoreSlim(Math.Max(1, _options.DiscoverConcurrency));
        var total = ips.Count;
        var scanned = 0;
        progress?.Report((0, total));

        var version = snmpVersion == 1 ? VersionCode.V1 : VersionCode.V2;
        var tasks = ips.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!IPAddress.TryParse(ip, out var addr)) return;
                var vars = await GetAsync(version, new IPEndPoint(addr, port), new OctetString(community),
                    new List<Variable> { new(SnmpOids.SysDescr), new(SnmpOids.SysName) }, timeoutMs, ct);
                string? descr = null, name = null;
                foreach (var v in vars)
                {
                    if (v.Id.Equals(SnmpOids.SysDescr)) descr = Truncate(v.Data.ToString(), 255);
                    if (v.Id.Equals(SnmpOids.SysName)) name = Truncate(v.Data.ToString(), 100);
                }
                hits.Add(new DiscoveryHit(ip, name, descr));
            }
            catch { /* 无响应即跳过 */ }
            finally
            {
                gate.Release();
                var done = Interlocked.Increment(ref scanned);
                progress?.Report((done, total));
            }
        });
        await Task.WhenAll(tasks);
        return hits.OrderBy(h => IPAddress.Parse(h.Ip).GetAddressBytes(), BytesComparer.Instance).ToList();
    }

    private class BytesComparer : IComparer<byte[]>
    {
        public static readonly BytesComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            if (x == null || y == null) return 0;
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
                if (x[i] != y[i]) return x[i] - y[i];
            return x.Length - y.Length;
        }
    }

    private static (VersionCode Version, IPEndPoint Ep, OctetString Community) Resolve(PrinterEndpoint endpoint)
    {
        if (!IPAddress.TryParse(endpoint.Ip, out var addr))
            throw new SnmpCollectException($"无效的 IP 地址：{endpoint.Ip}");
        var version = endpoint.SnmpVersion == 1 ? VersionCode.V1 : VersionCode.V2;
        return (version, new IPEndPoint(addr, endpoint.Port), new OctetString(endpoint.Community));
    }

    /// <summary>GET + 自管超时（SharpSnmpLib 异步接口不带超时参数，用 WaitAsync 兜底）</summary>
    private static async Task<IList<Variable>> GetAsync(VersionCode version, IPEndPoint ep, OctetString community,
        IList<Variable> variables, int timeoutMs, CancellationToken ct)
    {
        var task = Messenger.GetAsync(version, ep, community, variables, ct);
        try
        {
            return await task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
        }
        catch (Lextm.SharpSnmpLib.Messaging.TimeoutException)
        {
            throw new SnmpCollectException("SNMP 请求超时");
        }
        catch (System.TimeoutException)
        {
            throw new SnmpCollectException("SNMP 请求超时");
        }
        catch (ErrorException ex)
        {
            throw new SnmpCollectException($"SNMP 协议错误：{ex.Message}", ex);
        }
        catch (SocketException ex)
        {
            throw new SnmpCollectException($"网络错误：{ex.Message}", ex);
        }
    }

    private static async Task<IList<Variable>> WalkAsync(VersionCode version, IPEndPoint ep, OctetString community,
        ObjectIdentifier root, int timeoutMs, CancellationToken ct)
    {
        var list = new List<Variable>();
        Task walkTask = version == VersionCode.V1
            ? Messenger.WalkAsync(VersionCode.V1, ep, community, root, list, WalkMode.WithinSubtree, ct)
            : Messenger.BulkWalkAsync(version, ep, community, OctetString.Empty, root, list, 10,
                WalkMode.WithinSubtree, null, null, ct);
        try
        {
            await walkTask.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 3000)), ct);
        }
        catch (Lextm.SharpSnmpLib.Messaging.TimeoutException)
        {
            throw new SnmpCollectException("SNMP 遍历超时");
        }
        catch (System.TimeoutException)
        {
            throw new SnmpCollectException("SNMP 遍历超时");
        }
        return list;
    }

    /// <summary>遍历一列，按行尾索引建 map</summary>
    private static async Task<Dictionary<int, long>> WalkToMapAsync(VersionCode version, IPEndPoint ep,
        OctetString community, ObjectIdentifier column, CancellationToken ct)
    {
        var rows = await WalkAsync(version, ep, community, column, 2500, ct);
        var map = new Dictionary<int, long>();
        foreach (var row in rows)
        {
            if (row.Data is Null or NoSuchObject or NoSuchInstance) continue;
            var idxStr = row.Id.ToString();
            if (idxStr.Length <= column.ToString().Length + 1) continue;
            if (!int.TryParse(idxStr[(column.ToString().Length + 1)..], out var idx)) continue;
            map[idx] = row.Data switch
            {
                Integer32 i32 => i32.ToInt32(),
                Counter32 c => c.ToUInt32(),
                Gauge32 g => g.ToUInt32(),
                TimeTicks t => t.ToUInt32(),
                OctetString o when o.ToBytes().Length <= 8 => BitConverter.ToInt64(o.GetRaw()),
                _ => 0
            };
        }
        return map;
    }

    /// <summary>文本列遍历（OctetString 或 Integer32）</summary>
    private async Task<Dictionary<int, string>> WalkToTextMapAsync(VersionCode version, IPEndPoint ep,
        OctetString community, ObjectIdentifier column, CancellationToken ct)
    {
        var rows = await WalkAsync(version, ep, community, column, 2500, ct);
        var map = new Dictionary<int, string>();
        foreach (var row in rows)
        {
            if (row.Data is Null or NoSuchObject or NoSuchInstance) continue;
            var idxStr = row.Id.ToString();
            if (idxStr.Length <= column.ToString().Length + 1) continue;
            if (!int.TryParse(idxStr[(column.ToString().Length + 1)..], out var idx)) continue;
            var text = row.Data switch
            {
                OctetString o => o.ToString(),
                Integer32 i32 => i32.ToInt32().ToString(),
                _ => null
            };
            if (!string.IsNullOrEmpty(text)) map[idx] = text;
        }
        return map;
    }

    private static long AsLong(Dictionary<ObjectIdentifier, ISnmpData> data, ObjectIdentifier oid)
    {
        if (!data.TryGetValue(oid, out var d)) return 0;
        return d switch
        {
            Integer32 i32 => i32.ToInt32(),
            Counter32 c => c.ToUInt32(),
            Gauge32 g => g.ToUInt32(),
            TimeTicks t => t.ToUInt32(),
            _ => 0
        };
    }

    private static string AsText(Dictionary<ObjectIdentifier, ISnmpData> data, ObjectIdentifier oid) =>
        data.TryGetValue(oid, out var d) ? d switch
        {
            OctetString o => o.ToString(),
            Integer32 i32 => i32.ToInt32().ToString(),
            _ => string.Empty
        } : string.Empty;

    /// <summary>
    /// Printer MIB 的描述字段可能是 locale 前缀二进制串（如 "\x01\xffHP..."），剥离不可见前缀
    /// </summary>
    internal static string? CleanDescription(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var start = 0;
        while (start < raw.Length && (char.IsControl(raw[start]) || raw[start] > 0xFF - 0x08 && raw[start] < 0x100 && !char.IsLetterOrDigit(raw[start])))
        {
            start++;
        }
        var cleaned = start > 0 ? raw[start..] : raw;
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned.Trim();
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s[..max];

    private static IReadOnlyList<string> ExpandIpRange(string startIp, string endIp)
    {
        if (!System.Net.IPAddress.TryParse(startIp, out var s) || !System.Net.IPAddress.TryParse(endIp, out var e))
            throw new SnmpCollectException("IP 范围格式无效");
        var sb = s.GetAddressBytes();
        var eb = e.GetAddressBytes();
        if (sb.Length != 4 || eb.Length != 4) throw new SnmpCollectException("仅支持 IPv4");
        var start = BitConverter.ToUInt32(sb.Reverse().ToArray(), 0);
        var end = BitConverter.ToUInt32(eb.Reverse().ToArray(), 0);
        if (end < start) (start, end) = (end, start);
        if (end - start + 1 > 4096) throw new SnmpCollectException("扫描范围过大（最多 4096 个地址）");
        var result = new List<string>();
        for (var v = start; v <= end; v++)
        {
            var bytes = BitConverter.GetBytes(v).Reverse().ToArray();
            result.Add(new System.Net.IPAddress(bytes).ToString());
            if (v == end) break; // uint 回绕保护
        }
        return result;
    }
}
