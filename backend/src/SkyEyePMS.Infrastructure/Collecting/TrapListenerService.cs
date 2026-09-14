using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Infrastructure.Polling;

namespace SkyEyePMS.Infrastructure.Collecting;

/// <summary>Trap 接收配置（appsettings "Traps" 节；默认关闭，162 特权端口场景建议用 9162）</summary>
public class TrapOptions
{
    public bool Enabled { get; set; }
    /// <summary>UDP 监听端口</summary>
    public int Port { get; set; } = 9162;
    /// <summary>允许的 community 列表（与打印机注册的 community 无关，按发送方配置）</summary>
    public List<string> Communities { get; set; } = ["public"];
}

/// <summary>解析后的一次 Trap 事件</summary>
public record SnmpTrapPacket(
    string SenderIp,
    string Community,
    /// <summary>snmpTrapOID（v2c）或 enterprise OID（v1）</summary>
    string TrapOid,
    /// <summary>v1 泛用类型（0-6），v2c 为 null</summary>
    int? GenericCode,
    uint UpTimeTicks,
    DateTimeOffset ReceivedAt);

/// <summary>
/// 轻量 SNMP Trap 解析器：支持 v1 TRAP(0xA4) 与 v2c TRAP-V2(0xA7)，
/// 只提取路由所需字段（来源 / community / trapOID / sysUpTime），不做完整编解码。
/// </summary>
public static class SnmpTrapParser
{
    public static bool TryParse(byte[] datagram, out SnmpTrapPacket? packet)
    {
        packet = null;
        try
        {
            var pos = 0;
            if (!ReadTlv(datagram, ref pos, out var tag, out var content) || tag != 0x30) return false;

            var p = 0;
            if (!ReadTlv(content, ref p, out var vTag, out var vBody) || vTag != 0x02) return false;
            var version = DecodeInt(vBody); // 0=v1, 1=v2c

            if (!ReadTlv(content, ref p, out var cTag, out var communityBody) || cTag != 0x04) return false;
            var community = System.Text.Encoding.UTF8.GetString(communityBody);

            if (!ReadTlv(content, ref p, out var pduTag, out var pdu)) return false;

            uint upTime = 0;
            string trapOid;
            int? generic = null;

            if (pduTag == 0xA4 && version == 0)
            {
                // v1 TRAP: enterprise, agent-addr, generic, specific, timeTicks, varbinds（局部位置解析）
                var vp1 = 0;
                if (!ReadTlv(pdu, ref vp1, out var eTag, out var eBody) || eTag != 0x06) return false;
                trapOid = DecodeOid(eBody);
                if (!ReadTlv(pdu, ref vp1, out _, out _)) return false;            // agent-addr
                if (!ReadTlv(pdu, ref vp1, out var gTag, out var gBody) || gTag != 0x02) return false;
                generic = (int)DecodeUInt(gBody);
                if (!ReadTlv(pdu, ref vp1, out var sTag, out var sBody) || sTag != 0x02) return false;
                if (!ReadTlv(pdu, ref vp1, out var tTag, out var tBody) || tTag != 0x43) return false;
                upTime = DecodeUInt(tBody);
                _ = sBody;
            }
            else if (pduTag == 0xA7)
            {
                // v2c TRAP-V2: request-id, error-status, error-index, varbinds（p 已指向 PDU 之后，用局部位置解析）
                var pp = 0;
                for (var i = 0; i < 3; i++) pp += TlvLength(pdu, pp); // 跳过三个整数字段
                if (!ReadTlv(pdu, ref pp, out var vbTag, out var vbs) || vbTag != 0x30) return false;
                trapOid = "";
                var vp = 0;
                while (ReadTlv(vbs, ref vp, out var seqTag, out var seq) && seqTag == 0x30)
                {
                    var sp = 0;
                    if (!ReadTlv(seq, ref sp, out var oidTag, out var oidBody) || oidTag != 0x06) continue;
                    var oid = DecodeOid(oidBody);
                    if (!ReadTlv(seq, ref sp, out var valTag, out var valBody)) continue;
                    if (oid == "1.3.6.1.2.1.1.3.0" && valTag == 0x43)
                        upTime = DecodeUInt(valBody);
                    else if (oid == "1.3.6.1.6.3.1.1.4.1.0" && valTag == 0x06)
                        trapOid = DecodeOid(valBody);
                    else if (trapOid.Length == 0)
                        trapOid = oid;
                }
                if (trapOid.Length == 0) return false;
            }
            else
            {
                return false; // 不支持的 PDU 类型（Get/Set 等不该出现在监听口）
            }

            packet = new SnmpTrapPacket("", community, trapOid, generic, upTime, DateTimeOffset.UtcNow);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("TRAP-PARSE-EX: " + ex.GetType().Name + " " + ex.Message + "\n" + ex.StackTrace);
            return false; // 非法报文直接丢弃
        }
    }

    private static bool ReadTlv(byte[] data, ref int pos, out byte tag, out byte[] body)
    {
        tag = 0; body = [];
        if (pos >= data.Length) return false;
        tag = data[pos++];
        if (pos >= data.Length) return false;
        int len = data[pos++];
        if ((len & 0x80) != 0)
        {
            var n = len & 0x7F;
            if (n == 0 || pos + n > data.Length) return false;
            len = 0;
            for (var i = 0; i < n; i++) len = (len << 8) | data[pos++];
        }
        if (pos + len > data.Length) return false;
        body = data[pos..(pos + len)];
        pos += len;
        return true;
    }

    /// <summary>TLV 头部总长（tag+len 字节数），供跳过字段用</summary>
    private static int TlvLength(byte[] data, int pos)
    {
        var start = pos;
        pos++; // tag
        int len = data[pos++];
        if ((len & 0x80) != 0)
        {
            var n = len & 0x7F;
            len = 0;
            for (var i = 0; i < n; i++) len = (len << 8) | data[pos++];
        }
        return pos - start + len;
    }

    private static long DecodeInt(byte[] body)
    {
        long v = body[0] >= 0x80 ? -1 : 0; // 符号扩展
        foreach (var b in body) v = (v << 8) | b;
        return v;
    }

    private static uint DecodeUInt(byte[] body)
    {
        uint v = 0;
        foreach (var b in body) v = (v << 8) | b;
        return v;
    }

    private static string DecodeOid(byte[] body)
    {
        if (body.Length == 0) return "";
        var parts = new List<int> { body[0] / 40, body[0] % 40 };
        long cur = 0;
        var inProgress = false;
        for (var i = 1; i < body.Length; i++)
        {
            cur = (cur << 7) | (uint)(body[i] & 0x7F);
            inProgress = (body[i] & 0x80) != 0;
            if (!inProgress)
            {
                parts.Add((int)cur);
                cur = 0;
            }
        }
        if (inProgress) parts.Add((int)cur);
        return string.Join('.', parts);
    }
}

/// <summary>最近收到的 Trap 环形缓冲（诊断"为什么没触发采集"）</summary>
public class RecentTrapStore
{
    private readonly ConcurrentQueue<SnmpTrapPacket> _items = new();
    private const int Capacity = 50;

    public void Add(SnmpTrapPacket packet)
    {
        _items.Enqueue(packet);
        while (_items.Count > Capacity) _items.TryDequeue(out _);
    }

    public IReadOnlyList<SnmpTrapPacket> Latest() => _items.Reverse().Take(50).ToList();
}

/// <summary>
/// SNMP Trap 实时接收服务：收到 v1/v2c Trap 后按来源 IP 匹配打印机并立即触发一次采集，
/// 让卡纸 / 缺纸等事件在秒级进入告警引擎（而不是等下一轮轮询）。
/// </summary>
public class TrapListenerService : BackgroundService
{
    private readonly TrapOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RecentTrapStore _store;
    private readonly ILogger<TrapListenerService> _logger;

    public TrapListenerService(IOptions<TrapOptions> options, IServiceScopeFactory scopeFactory,
        RecentTrapStore store, ILogger<TrapListenerService> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Trap 接收未启用（Traps:Enabled=false），实时告警走轮询周期");
            return;
        }

        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, _options.Port));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trap 监听端口 {Port} 绑定失败，Trap 接收未启动", _options.Port);
            return;
        }

        _logger.LogInformation("SNMP Trap 监听已启动：UDP {Port}（community={Communities}）",
            _options.Port, string.Join(",", _options.Communities));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(stoppingToken);
                await HandleDatagramAsync(result.Buffer, result.RemoteEndPoint.Address);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                // 中断/关闭，继续循环
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trap 处理异常");
            }
        }
    }

    public async Task HandleDatagramAsync(byte[] datagram, IPAddress sender)
    {
        if (!SnmpTrapParser.TryParse(datagram, out var packet) || packet == null)
        {
            _logger.LogDebug("忽略无法解析的 Trap 报文（来源 {Ip}）", sender);
            return;
        }
        if (!_options.Communities.Contains(packet.Community, StringComparer.Ordinal))
        {
            _logger.LogDebug("Trap community 不匹配，丢弃（{Ip}）", sender);
            return;
        }

        var withIp = packet with { SenderIp = sender.ToString() };
        _store.Add(withIp);

        using var scope = _scopeFactory.CreateScope();
        var printers = scope.ServiceProvider.GetRequiredService<IPrinterRepository>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IPollingCoordinator>();
        var printer = (await printers.GetAllAsync(onlyEnabled: true))
            .FirstOrDefault(p => p.Ip == withIp.SenderIp);
        if (printer == null)
        {
            _logger.LogInformation("收到 Trap（{Ip} {Oid}）但无匹配的已启用打印机", withIp.SenderIp, withIp.TrapOid);
            return;
        }

        _logger.LogInformation("打印机 {Printer} 发来 Trap（{Oid}），立即触发采集", printer.Name, withIp.TrapOid);
        _ = await coordinator.PollPrinterNowAsync(printer.Id);
    }
}
