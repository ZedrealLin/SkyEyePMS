using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SkyEyePMS.Tests.Helpers;

/// <summary>
/// 测试专用最小 SNMP v2c Agent：手写 BER 编解码，模拟一台可编程状态的打印机。
/// 支持 Get / GetNext / GetBulk，可在运行时切换"设备状态"或模拟离线（丢包）。
/// </summary>
public sealed class MiniSnmpAgent : IDisposable
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _localEp;
    private readonly Thread _thread;
    private volatile bool _running = true;

    public string Community { get; set; } = "public";
    /// <summary>true 时丢弃所有请求（模拟设备离线）</summary>
    public volatile bool Offline;
    /// <summary>GET sysDescr 延迟（毫秒），模拟慢响应</summary>
    public volatile int DelayMs;

    /// <summary>设备当前状态（测试直接改这些字段）</summary>
    public AgentDevice Device { get; } = new();

    public MiniSnmpAgent()
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _localEp = (IPEndPoint)_udp.Client.LocalEndPoint!;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    public int Port => _localEp.Port;

    public class AgentDevice
    {
        public string SysDescr { get; set; } = "MockPrinter TestModel 1.0 (SNMPv2c)";
        public string SysName { get; set; } = "MOCK-PRN";
        /// <summary>hrPrinterStatus：3=空闲 4=打印中</summary>
        public int HrPrinterStatus { get; set; } = 3;
        /// <summary>hrDeviceStatus：2=运行 3=警告 5=宕机</summary>
        public int HrDeviceStatus { get; set; } = 2;
        /// <summary>hrPrinterDetectedErrorState 位串字节（null=不支持）</summary>
        public byte[]? ErrorState { get; set; }
        public long PageCount { get; set; } = 12345;

        /// <summary>耗材：index → (type, description, maxCapacity, level)</summary>
        public Dictionary<int, (int Type, string Desc, long Max, long Level)> Supplies { get; } = new()
        {
            { 1, (3, "Black Cartridge", 10000, 8000) },
            { 2, (6, "Cyan Cartridge", 5000, 2500) }
        };

        /// <summary>纸盒：index → (name, maxCapacity, level)，level=0 表示缺纸</summary>
        public Dictionary<int, (string Name, long Max, long Level)> InputTrays { get; } = new()
        {
            { 1, ("Tray 1", 500, 250) }
        };

        /// <summary>告警表条目：(severity, group, code, description)</summary>
        public List<(int Severity, int Group, int Code, string Desc)> AlertTable { get; } = new();
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                var data = _udp.Receive(ref remote);
                if (Offline) continue; // 丢包 → 客户端超时
                if (DelayMs > 0) Thread.Sleep(DelayMs);
                var resp = Handle(data);
                if (resp != null)
                    _udp.Send(resp, resp.Length, remote);
            }
            catch (SocketException) when (!_running) { }
            catch (ObjectDisposedException) { break; }
            catch (Exception)
            {
                // 测试代理不中断
            }
        }
    }

    // ---------------- BER 编码助手 ----------------

    private static byte[] Tlv(byte tag, byte[] content)
    {
        var len = content.Length;
        byte[] lenBytes;
        if (len < 0x80) lenBytes = [(byte)len];
        else if (len <= 0xFF) lenBytes = [0x81, (byte)len];
        else { lenBytes = [0x82, (byte)(len >> 8), (byte)(len & 0xFF)]; }
        var buf = new byte[1 + lenBytes.Length + len];
        buf[0] = tag;
        Buffer.BlockCopy(lenBytes, 0, buf, 1, lenBytes.Length);
        Buffer.BlockCopy(content, 0, buf, 1 + lenBytes.Length, len);
        return buf;
    }

    private static byte[] EncSequence(params byte[][] items) => Tlv(0x30, items.SelectMany(x => x).ToArray());

    /// <summary>BER 有符号整数最小化编码（负数为补码；request-id 可能是负 int）</summary>
    private static byte[] EncInt(long v)
    {
        var bytes = new List<byte>();
        long val = v;
        while (true)
        {
            bytes.Insert(0, (byte)(val & 0xFF));
            val >>= 8; // 算术右移
            if ((val == 0 && (bytes[0] & 0x80) == 0) || (val == -1 && (bytes[0] & 0x80) != 0))
                break;
        }
        return Tlv(0x02, bytes.ToArray());
    }
    private static byte[] EncOctet(string s) => Tlv(0x04, System.Text.Encoding.UTF8.GetBytes(s));
    private static byte[] EncOctet(byte[] b) => Tlv(0x04, b);
    private static byte[] EncGauge(long v) => Tlv(0x42, EncInt(v).Skip(2).ToArray());
    private static byte[] EncCounter(long v) => Tlv(0x41, EncInt(v).Skip(2).ToArray());

    /// <summary>OID 编码：首字节 40*a+b，后续每段 base-128</summary>
    internal static byte[] EncOid(int[] oid)
    {
        var body = new List<byte> { (byte)(40 * oid[0] + oid[1]) };
        foreach (var part in oid.Skip(2))
        {
            if (part < 0x80) body.Add((byte)part);
            else
            {
                var stack = new Stack<byte>();
                var v = part;
                stack.Push((byte)(v & 0x7F));
                v >>= 7;
                while (v > 0) { stack.Push((byte)((v & 0x7F) | 0x80)); v >>= 7; }
                body.AddRange(stack);
            }
        }
        return Tlv(0x06, body.ToArray());
    }

    // ---------------- BER 解码（仅解析请求所需） ----------------

    private static (byte Tag, byte[] Content, int Consumed) ReadTlv(byte[] data, int offset)
    {
        var tag = data[offset];
        var pos = offset + 1;
        int len = data[pos++];
        if ((len & 0x80) != 0)
        {
            var numBytes = len & 0x7F;
            len = 0;
            for (var i = 0; i < numBytes; i++) len = (len << 8) | data[pos++];
        }
        var content = new byte[len];
        Buffer.BlockCopy(data, pos, content, 0, len);
        return (tag, content, pos + len - offset);
    }

    internal static int[] DecodeOid(byte[] content)
    {
        var parts = new List<int> { content[0] / 40, content[0] % 40 };
        int? acc = 0;
        for (var i = 1; i < content.Length; i++)
        {
            acc = (acc << 7) | (content[i] & 0x7F);
            if ((content[i] & 0x80) == 0) { parts.Add(acc.Value); acc = 0; }
        }
        return parts.ToArray();
    }

    // ---------------- 请求处理 ----------------

    /// <summary>MIB：OID(整数段) → TLV 值字节</summary>
    private SortedDictionary<int[], byte[]> BuildMib()
    {
        var mib = new SortedDictionary<int[], byte[]>(OidComparer.Instance);
        void Add(string oid, byte[] value)
        {
            var parts = oid.Split('.').Select(int.Parse).ToArray();
            mib[parts] = value;
        }

        Add("1.3.6.1.2.1.1.1.0", EncOctet(Device.SysDescr));
        Add("1.3.6.1.2.1.1.3.0", Tlv(0x43, EncInt(123456).Skip(2).ToArray())); // sysUpTime TimeTicks
        Add("1.3.6.1.2.1.1.5.0", EncOctet(Device.SysName));
        Add("1.3.6.1.2.1.25.3.2.1.5.1", EncInt(Device.HrDeviceStatus));
        Add("1.3.6.1.2.1.25.3.5.1.1.1", EncInt(Device.HrPrinterStatus));
        if (Device.ErrorState != null)
            Add("1.3.6.1.2.1.25.3.5.1.2.1", EncOctet(Device.ErrorState));

        foreach (var (idx, s) in Device.Supplies)
        {
            Add($"1.3.6.1.2.1.43.11.1.1.1.{idx}", EncInt(idx));
            Add($"1.3.6.1.2.1.43.11.1.1.2.{idx}", EncInt(s.Type));
            Add($"1.3.6.1.2.1.43.11.1.1.3.{idx}", EncOctet(s.Desc));
            Add($"1.3.6.1.2.1.43.11.1.1.4.{idx}", EncInt(7));
            Add($"1.3.6.1.2.1.43.11.1.1.5.{idx}", EncInt(s.Max));
            Add($"1.3.6.1.2.1.43.11.1.1.6.{idx}", EncInt(s.Level));
        }

        foreach (var (idx, t) in Device.InputTrays)
        {
            Add($"1.3.6.1.2.1.43.8.2.1.6.{idx}", EncInt(t.Max));
            Add($"1.3.6.1.2.1.43.8.2.1.7.{idx}", EncInt(t.Level));
            Add($"1.3.6.1.2.1.43.8.2.1.8.{idx}", EncOctet(t.Name));
            Add($"1.3.6.1.2.1.43.8.2.1.9.{idx}", EncOctet(t.Name));
        }

        var ai = 1;
        foreach (var a in Device.AlertTable)
        {
            Add($"1.3.6.1.2.1.43.18.1.1.1.{ai}", EncInt(ai));
            Add($"1.3.6.1.2.1.43.18.1.1.2.{ai}", EncInt(a.Severity));
            Add($"1.3.6.1.2.1.43.18.1.1.3.{ai}", EncInt(a.Group));
            Add($"1.3.6.1.2.1.43.18.1.1.6.{ai}", EncInt(a.Code));
            Add($"1.3.6.1.2.1.43.18.1.1.7.{ai}", EncOctet(a.Desc));
            ai++;
        }

        Add("1.3.6.1.2.1.43.10.2.1.4.1.1", EncCounter(Device.PageCount));
        return mib;
    }

    private byte[]? Handle(byte[] request)
    {
        try
        {
            var (_, msgBody, _) = ReadTlv(request, 0);
            var pos = 0;
            var (verTag, verBody, verLen) = ReadTlv(msgBody, pos); pos += verLen;
            var (comTag, comBody, comLen) = ReadTlv(msgBody, pos); pos += comLen;
            var pduTag = msgBody[pos];
            var (pduTag2, pduBody, _) = ReadTlv(msgBody, pos);
            _ = (verTag, verBody, comTag, comBody, pduTag2);

            if (System.Text.Encoding.ASCII.GetString(comBody) != Community) return null;

            var p = 0;
            var (reqIdBody, reqIdLen) = ReadTlvBody(pduBody, p); p += reqIdLen;
            var (_, errLen2) = ReadTlvBody(pduBody, p); p += errLen2;
            var (_, errIdxLen) = ReadTlvBody(pduBody, p); p += errIdxLen;

            var varbinds = new List<int[]>();
            var (_, vbSeq, _) = ReadTlv(pduBody, p);
            var vp = 0;
            while (vp < vbSeq.Length)
            {
                var (vbTag, vbContent, vbLen) = ReadTlv(vbSeq, vp);
                if (vbLen == 0) break;
                vp += vbLen;
                var (oidTag, oidContent, oidLen) = ReadTlv(vbContent, 0);
                if (oidTag != 0x06) continue;
                varbinds.Add(DecodeOid(oidContent));
            }

            var mib = BuildMib();
            var responseBinds = new List<byte[]>();
            switch (pduTag)
            {
                case 0xA0: // Get
                    foreach (var oid in varbinds)
                        responseBinds.Add(EncSequence(EncOid(oid), mib.TryGetValue(oid, out var v) ? v : Tlv(0x05, [])));
                    break;
                case 0xA1: // GetNext
                    foreach (var oid in varbinds)
                    {
                        var next = mib.Keys.FirstOrDefault(k => OidComparer.Instance.Compare(k, oid) > 0);
                        responseBinds.Add(next == null
                            ? EncSequence(EncOid(oid), Tlv(0x82, [])) // endOfMibView
                            : EncSequence(EncOid(next), mib[next]));
                    }
                    break;
                case 0xA5: // GetBulk
                {
                    var maxRepetitions = 10;
                    foreach (var oid in varbinds)
                    {
                        var cur = oid;
                        for (var i = 0; i < maxRepetitions; i++)
                        {
                            var next = mib.Keys.FirstOrDefault(k => OidComparer.Instance.Compare(k, cur) > 0);
                            if (next == null)
                            {
                                responseBinds.Add(EncSequence(EncOid(cur), Tlv(0x82, [])));
                                break;
                            }
                            responseBinds.Add(EncSequence(EncOid(next), mib[next]));
                            cur = next;
                        }
                    }
                    break;
                }
                default:
                    return null;
            }

            var requestId = DecodeInt(reqIdBody);
            var pdu = Tlv(0xA2, // GetResponse
                System.Linq.Enumerable.ToArray(
                    new byte[0].Concat(EncInt(requestId))
                        .Concat(EncInt(0))     // error-status
                        .Concat(EncInt(0))     // error-index
                        .Concat(Tlv(0x30, responseBinds.SelectMany(x => x).ToArray()))));
            return Tlv(0x30, System.Linq.Enumerable.ToArray(
                EncInt(1).Concat(EncOctet(Community)).Concat(pdu)));
        }
        catch
        {
            return null;
        }
    }

    private static (byte[] Body, int Consumed) ReadTlvBody(byte[] data, int offset)
    {
        var (_, content, consumed) = ReadTlv(data, offset);
        return (content, consumed);
    }

    /// <summary>按实际字节长度做补码解码（负 int 回显后须保持 ≤4 字节）</summary>
    private static long DecodeInt(byte[] body)
    {
        if (body.Length == 0) return 0;
        long v = (sbyte)body[0];
        for (var i = 1; i < body.Length; i++) v = (v << 8) | body[i];
        return v;
    }

    private class OidComparer : IComparer<int[]>
    {
        public static readonly OidComparer Instance = new();
        public int Compare(int[]? x, int[]? y)
        {
            if (x == null || y == null) return 0;
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
                if (x[i] != y[i]) return x[i] - y[i];
            return x.Length - y.Length;
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _udp.Close(); } catch { }
        try { _thread.Join(500); } catch { }
    }
}
