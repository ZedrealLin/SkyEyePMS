// SkyEyePMS SNMP 打印机模拟器
// 用途：没有支持 SNMP 的真实打印机时，用于本地试用/联调/演示 SkyEyePMS。
//
//   dotnet run --project tools/SkyEyePMS.SnmpSim -- --ports 16100,16101 --control 16900 --community public
//
// 每个端口模拟一台标准 Printer MIB (RFC 3805) 打印机；
// --control 开启一个简单的 HTTP 控制接口，可在运行中改变第一台打印机的模拟状态：
//   GET  http://127.0.0.1:16900/state
//   POST http://127.0.0.1:16900/state  body: {"offline":true,"noPaper":true,"jam":false,"toner1Level":15}
//   POST http://127.0.0.1:16900/send-trap?target=127.0.0.1:9162  向目标发送 v2c 冷启动 Trap（测试实时告警）

using System.Net;
using System.Net.Sockets;
using System.Text;

var portsArg = "16100";
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--ports") portsArg = args[i + 1];
    if (args[i] == "--community") { /* 见下 */ }
}
var community = "public";
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--community") community = args[i + 1];
var controlPort = 16900;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--control") controlPort = int.Parse(args[i + 1]);

var devices = new List<SimDevice>();
foreach (var portStr in portsArg.Split(','))
    devices.Add(new SimDevice(int.Parse(portStr.Trim()), community));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

foreach (var d in devices) d.Start();
Console.WriteLine($"SNMP 模拟打印机已启动：{string.Join(", ", devices.Select(d => $"127.0.0.1:{d.Port}"))}（community={community}）");

var first = devices[0];
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{controlPort}/state/");
listener.Prefixes.Add($"http://127.0.0.1:{controlPort}/send-trap/");
listener.Start();
Console.WriteLine($"状态控制接口：http://127.0.0.1:{controlPort}/state （POST JSON: offline/noPaper/jam/toner1Level）");

_ = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            var ctx = await listener.GetContextAsync();
            if (ctx.Request.HttpMethod == "POST" &&
                (ctx.Request.Url?.AbsolutePath.EndsWith("send-trap", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                var target = ctx.Request.QueryString.Get("target") ?? "127.0.0.1:9162";
                var parts = target.Split(':');
                using var udp = new UdpClient();
                var packet = SimDevice.BuildV2cTrap(community,
                    enterpriseOid: "1.3.6.1.4.1.99999.1",
                    upTimeTicks: (uint)Environment.TickCount64);
                await udp.SendAsync(packet, packet.Length,
                    new System.Net.IPEndPoint(System.Net.IPAddress.Parse(parts[0]), int.Parse(parts[1])));
                var ok = Encoding.UTF8.GetBytes($"trap sent to {target} ({packet.Length} bytes)");
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.OutputStream.WriteAsync(ok);
                ctx.Response.Close();
                continue;
            }
            if (ctx.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var json = await reader.ReadToEndAsync();
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("offline", out var off)) first.Offline = off.GetBoolean();
                if (doc.RootElement.TryGetProperty("noPaper", out var np)) first.NoPaper = np.GetBoolean();
                if (doc.RootElement.TryGetProperty("jam", out var jam)) first.Jam = jam.GetBoolean();
                if (doc.RootElement.TryGetProperty("toner1Level", out var t1)) first.Toner1Level = t1.GetInt32();
            }
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                offline = first.Offline,
                noPaper = first.NoPaper,
                jam = first.Jam,
                toner1Level = first.Toner1Level
            });
            var buf = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(buf);
            ctx.Response.Close();
        }
        catch (Exception) when (cts.IsCancellationRequested) { break; }
        catch (Exception) { }
    }
});

try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
listener.Stop();
foreach (var d in devices) d.Stop();
Console.WriteLine("模拟器已退出");

internal class SimDevice
{
    private readonly UdpClient _udp;
    private readonly string _community;
    private Thread? _thread;
    private volatile bool _running;

    public int Port { get; }
    public volatile bool Offline;
    public volatile bool NoPaper;
    public volatile bool Jam;
    public volatile int Toner1Level = 80;

    public SimDevice(int port, string community)
    {
        Port = port;
        _community = community;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _udp.Close(); } catch { }
    }

    // ----- 最小 SNMP v2c Agent（BER 编解码） -----

    private static byte[] Tlv(byte tag, byte[] content)
    {
        var len = content.Length;
        byte[] lenBytes;
        if (len < 0x80) lenBytes = [(byte)len];
        else if (len <= 0xFF) lenBytes = [0x81, (byte)len];
        else lenBytes = [0x82, (byte)(len >> 8), (byte)(len & 0xFF)];
        var buf = new byte[1 + lenBytes.Length + len];
        buf[0] = tag;
        Buffer.BlockCopy(lenBytes, 0, buf, 1, lenBytes.Length);
        Buffer.BlockCopy(content, 0, buf, 1 + lenBytes.Length, len);
        return buf;
    }

    private static byte[] EncInt(long v)
    {
        var bytes = new List<byte>();
        long val = v;
        while (true)
        {
            bytes.Insert(0, (byte)(val & 0xFF));
            val >>= 8;
            if ((val == 0 && (bytes[0] & 0x80) == 0) || (val == -1 && (bytes[0] & 0x80) != 0)) break;
        }
        return Tlv(0x02, bytes.ToArray());
    }

    private static byte[] EncOctet(string s) => Tlv(0x04, Encoding.UTF8.GetBytes(s));
    private static byte[] EncCounter(long v) => Tlv(0x41, EncInt(v).Skip(2).ToArray());
    private static byte[] Seq(params byte[][] xs) => Tlv(0x30, xs.SelectMany(x => x).ToArray());

    private static byte[] EncOid(int[] parts)
    {
        var body = new List<byte> { (byte)(40 * parts[0] + parts[1]) };
        foreach (var part in parts.Skip(2))
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

    private (string Oid, byte[] Value)[] Mib()
    {
        var errByte = (byte)((NoPaper ? 0x40 : 0) | (Jam ? 0x04 : 0));
        var list = new List<(string, byte[])>
        {
            ("1.3.6.1.2.1.1.1.0", EncOctet("SkyEyePMS SimPrinter 1.0 (demo device)")),
            ("1.3.6.1.2.1.1.5.0", EncOctet($"SIM-PRN-{Port}")),
            ("1.3.6.1.2.1.25.3.2.1.5.1", EncInt(NoPaper || Jam ? 3 : 2)),
            ("1.3.6.1.2.1.25.3.5.1.1.1", EncInt(NoPaper || Jam ? 4 : 3)),
            ("1.3.6.1.2.1.25.3.5.1.2.1", Tlv(0x04, new[] { errByte })),
            ("1.3.6.1.2.1.43.10.2.1.3.1", EncCounter(987654 + Port)),
            ("1.3.6.1.2.1.43.5.1.1.17.1", EncOctet($"SIMSERIAL-{Port}"))
        };

        var supplies = new Dictionary<string, byte[]>
        {
            ["1.3.6.1.2.1.43.11.1.1.1.1"] = EncInt(1),
            ["1.3.6.1.2.1.43.11.1.1.2.1"] = EncInt(3),
            ["1.3.6.1.2.1.43.11.1.1.3.1"] = EncOctet("Black Toner Cartridge"),
            ["1.3.6.1.2.1.43.11.1.1.4.1"] = EncInt(7),
            ["1.3.6.1.2.1.43.11.1.1.5.1"] = EncInt(10000),
            ["1.3.6.1.2.1.43.11.1.1.6.1"] = EncInt(Toner1Level * 100)
        };
        foreach (var kv in supplies) list.Add((kv.Key, kv.Value));

        list.Add(("1.3.6.1.2.1.43.8.2.1.6.1", EncInt(500)));
        list.Add(("1.3.6.1.2.1.43.8.2.1.7.1", EncInt(NoPaper ? 0 : 250)));
        list.Add(("1.3.6.1.2.1.43.8.2.1.8.1", EncOctet("Tray 1")));
        list.Add(("1.3.6.1.2.1.43.8.2.1.9.1", EncOctet("Tray 1")));

        return list.ToArray();
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                var data = _udp.Receive(ref remote);
                if (Offline) continue;
                var resp = Handle(data);
                if (resp != null) _udp.Send(resp, resp.Length, remote);
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception) { }
        }
    }

    private byte[]? Handle(byte[] request)
    {
        try
        {
            var (_, msgBody, _) = ReadTlv(request, 0);
            var pos = 0;
            var (_, _, verLen) = ReadTlv(msgBody, pos); pos += verLen;
            var (_, comBody, comLen) = ReadTlv(msgBody, pos); pos += comLen;
            if (Encoding.ASCII.GetString(comBody) != _community) return null;
            var pduTag = msgBody[pos];
            var (_, pduBody, _) = ReadTlv(msgBody, pos);

            var p = 0;
            var (_, reqIdBody, reqIdLen) = ReadTlv(pduBody, p); p += reqIdLen;
            p += TlvLen(pduBody, p); // error-status
            p += TlvLen(pduBody, p); // error-index
            var (_, vbSeq, _) = ReadTlv(pduBody, p);

            // 请求的 OID 列表
            var requested = new List<int[]>();
            var vp = 0;
            while (vp < vbSeq.Length)
            {
                var (tag, content, len) = ReadTlv(vbSeq, vp);
                if (len == 0) break;
                vp += len;
                var (oidTag, oidBody, _) = ReadTlv(content, 0);
                if (oidTag != 0x06) continue;
                requested.Add(Oid(OidString(oidBody)));
            }

            var mib = Mib()
                .Select(k => (Oid: Oid(k.Oid), k.Value))
                .OrderBy(k => k.Oid, new OidComparer())
                .ToList();
            var keys = mib.Select(k => k.Oid).ToArray();
            var valueOf = mib.ToDictionary(k => string.Join('.', k.Oid), k => k.Value);

            byte[]? ValueFor(int[] oid)
            {
                return valueOf.TryGetValue(string.Join('.', oid), out var v) ? v : null;
            }

            int[]? NextOf(int[] oid)
            {
                return keys.FirstOrDefault(k => new OidComparer().Compare(k, oid) > 0);
            }

            var binds = new List<byte[]>();
            switch (pduTag)
            {
                case 0xA0: // Get
                    foreach (var oid in requested)
                    {
                        var v = ValueFor(oid) ?? Tlv(0x05, []);
                        binds.Add(Seq(EncOid(oid), v));
                    }
                    break;
                case 0xA1: // GetNext
                    foreach (var oid in requested)
                    {
                        var next = NextOf(oid);
                        binds.Add(next == null
                            ? Seq(EncOid(oid), Tlv(0x82, [])) // endOfMibView
                            : Seq(EncOid(next), ValueFor(next)!));
                    }
                    break;
                case 0xA5: // GetBulk（表遍历用）
                {
                    foreach (var oid in requested)
                    {
                        var cur = oid;
                        for (var i = 0; i < 10; i++)
                        {
                            var next = NextOf(cur);
                            if (next == null)
                            {
                                binds.Add(Seq(EncOid(cur), Tlv(0x82, [])));
                                break;
                            }
                            binds.Add(Seq(EncOid(next), ValueFor(next)!));
                            cur = next;
                        }
                    }
                    break;
                }
                default:
                    return null;
            }

            var pdu = Tlv(0xA2, EncInt(DecodeInt(reqIdBody))
                .Concat(EncInt(0)).Concat(EncInt(0))
                .Concat(Tlv(0x30, binds.SelectMany(x => x).ToArray())).ToArray());
            return Tlv(0x30, EncInt(1).Concat(EncOctet(_community)).Concat(pdu).ToArray());
        }
        catch { return null; }
    }

    private class OidComparer : IComparer<int[]>
    {
        public int Compare(int[]? x, int[]? y)
        {
            if (x == null || y == null) return 0;
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
                if (x[i] != y[i]) return x[i] - y[i];
            return x.Length - y.Length;
        }
    }

    private static int TlvLen(byte[] data, int offset)
    {
        var pos = offset + 1;
        int len = data[pos++];
        if ((len & 0x80) != 0)
        {
            var n = len & 0x7F;
            len = 0;
            for (var i = 0; i < n; i++) len = (len << 8) | data[pos++];
        }
        return pos - offset + len;
    }

    private static (byte Tag, byte[] Content, int Consumed) ReadTlv(byte[] data, int offset)
    {
        var tag = data[offset];
        var pos = offset + 1;
        int len = data[pos++];
        if ((len & 0x80) != 0)
        {
            var n = len & 0x7F;
            len = 0;
            for (var i = 0; i < n; i++) len = (len << 8) | data[pos++];
        }
        var content = new byte[len];
        Buffer.BlockCopy(data, pos, content, 0, len);
        return (tag, content, pos + len - offset);
    }

    private static string OidString(byte[] content)
    {
        var parts = new List<long> { content[0] / 40, content[0] % 40 };
        long acc = 0;
        for (var i = 1; i < content.Length; i++)
        {
            acc = (acc << 7) | (content[i] & 0x7F);
            if ((content[i] & 0x80) == 0) { parts.Add(acc); acc = 0; }
        }
        return string.Join('.', parts);
    }

    private static int[] Oid(string s) => s.Split('.').Select(int.Parse).ToArray();

    /// <summary>构建 v2c Trap 报文（coldStart + sysUpTime.0 + snmpTrapOID.0），用于实时告警链路测试</summary>
    internal static byte[] BuildV2cTrap(string community, string enterpriseOid, uint upTimeTicks)
    {
        var varbinds = Tlv(0x30, new byte[][]
        {
            Seq(EncOid(Oid("1.3.6.1.2.1.1.3.0")), Tlv(0x43, EncUInt(upTimeTicks))),
            Seq(EncOid(Oid("1.3.6.1.6.3.1.1.4.1.0")), EncOid(Oid("1.3.6.1.6.3.1.1.5.1")))
        }.SelectMany(x => x).ToArray());
        var pduContent = new byte[][] { EncInt(1), EncInt(0), EncInt(0), varbinds }
            .SelectMany(x => x).ToArray();
        var scoped = Tlv(0xA7, pduContent); // TRAP-V2 PDU
        var msg = Tlv(0x30, new byte[][] { EncInt(1), EncOctet(community), scoped }
            .SelectMany(x => x).ToArray());
        return msg;

        static byte[] EncUInt(uint v)
        {
            var bytes = new List<byte>();
            while (v > 0) { bytes.Insert(0, (byte)(v & 0xFF)); v >>= 8; }
            if (bytes.Count == 0) bytes.Add(0);
            return bytes.ToArray();
        }
    }

    private static long DecodeInt(byte[] body)
    {
        if (body.Length == 0) return 0;
        long v = (sbyte)body[0];
        for (var i = 1; i < body.Length; i++) v = (v << 8) | body[i];
        return v;
    }
}
