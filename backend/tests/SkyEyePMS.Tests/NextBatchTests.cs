using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Security;
using SkyEyePMS.Infrastructure.Collecting;
using SkyEyePMS.Infrastructure.Polling;

namespace SkyEyePMS.Tests;

/// <summary>SNMP Trap 解析与接收触发链路。</summary>
public class TrapTests : IClassFixture<SkyEyeApiFactory>
{
    private readonly SkyEyeApiFactory _factory;

    public TrapTests(SkyEyeApiFactory factory) => _factory = factory;

    /// <summary>构建 v2c Trap 报文（与模拟器实现一致的独立版本）</summary>
    internal static byte[] BuildV2cTrap(string community, uint upTimeTicks)
    {
        static byte[] Tlv(byte tag, byte[] content)
        {
            var len = content.Length;
            var head = len < 128 ? [tag, (byte)len] : new byte[] { tag, 0x82, (byte)(len >> 8), (byte)(len & 0xFF) };
            return head.Concat(content).ToArray();
        }
        static byte[] Seq(params byte[][] xs) => Tlv(0x30, xs.SelectMany(x => x).ToArray());
        static byte[] EncInt(long v)
        {
            if (v == 0) return Tlv(0x02, [0]);
            var bytes = new List<byte>();
            long val = v;
            while (val > 0) { bytes.Insert(0, (byte)(val & 0xFF)); val >>= 8; }
            return Tlv(0x02, bytes.ToArray());
        }
        static byte[] EncOctet(string s) => Tlv(0x04, Encoding.UTF8.GetBytes(s));
        static byte[] EncUInt(uint v)
        {
            var bytes = new List<byte>();
            while (v > 0) { bytes.Insert(0, (byte)(v & 0xFF)); v >>= 8; }
            if (bytes.Count == 0) bytes.Add(0);
            return bytes.ToArray();
        }
        static byte[] EncOid(int[] parts)
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

        var varbinds = Tlv(0x30, new byte[][]
        {
            Seq(EncOid(new[]{1,3,6,1,2,1,1,3,0}), Tlv(0x43, EncUInt(upTimeTicks))),
            Seq(EncOid(new[]{1,3,6,1,6,3,1,1,4,1,0}), EncOid(new[]{1,3,6,1,6,3,1,1,5,1}))
        }.SelectMany(x => x).ToArray());
        var pduContent = new byte[][] { EncInt(1), EncInt(0), EncInt(0), varbinds }
            .SelectMany(x => x).ToArray();
        var scoped = Tlv(0xA7, pduContent);
        return Tlv(0x30, new byte[][] { EncInt(1), EncOctet(community), scoped }
            .SelectMany(x => x).ToArray());
    }

    private static uint EncUIntValue(uint v) => v;

    [Fact]
    public void Parser_ExtractsV2cTrapFields()
    {
        var bytes = BuildV2cTrap("public", 123456);
        var dump = string.Join(" ", bytes.Select(b => b.ToString("X2")));
        Console.WriteLine("TRAP-BYTES: " + dump);
        var ok = SnmpTrapParser.TryParse(bytes, out var packet);
        Console.WriteLine("PARSE-OK: " + ok + " PKT: " + packet);
        Assert.True(ok);
        Assert.Equal("public", packet!.Community);
        Assert.Equal("1.3.6.1.6.3.1.1.5.1", packet.TrapOid); // coldStart
        Assert.Equal(123456u, packet.UpTimeTicks);
        Assert.Null(packet.GenericCode);
    }

    [Fact]
    public void Parser_RejectsGarbage()
    {
        Assert.False(SnmpTrapParser.TryParse(new byte[] { 0x30, 0x05, 1, 2, 3, 4, 5 }, out _));
        Assert.False(SnmpTrapParser.TryParse([], out _));
        // 错误 community 内容照常解析（过滤在服务层）
        Assert.True(SnmpTrapParser.TryParse(BuildV2cTrap("private", 1), out _));
    }

    [Fact]
    public async Task TrapService_TriggersPollForMatchingPrinter()
    {
        using var scope = _factory.Services.CreateScope();
        var printers = scope.ServiceProvider.GetRequiredService<IPrinterRepository>();
        var printer = await printers.GetByIdAsync(1);
        if (printer == null || printer.Ip != "127.0.0.1")
        {
            await printers.CreateAsync(new Printer
            {
                Name = "Trap机", Ip = "127.0.0.1", Port = 59999, CommunityCipher = "public"
            });
        }
        var before = (await printers.GetAllAsync()).First(p => p.Ip == "127.0.0.1").LastPollAt;

        var service = _factory.Services.GetServices<IHostedService>()
            .OfType<TrapListenerService>().First();
        var bytes = BuildV2cTrap("public", 777);
        await service.HandleDatagramAsync(bytes, IPAddress.Loopback);

        var after = (await printers.GetAllAsync()).First(p => p.Ip == "127.0.0.1").LastPollAt;
        Assert.NotNull(after);
        if (before.HasValue) Assert.True(after > before, "Trap 应触发立即采集并更新 lastPollAt");

        var store = _factory.Services.GetRequiredService<RecentTrapStore>();
        var recent = store.Latest();
        Assert.Contains(recent, t => t.TrapOid == "1.3.6.1.6.3.1.1.5.1" && t.SenderIp == "127.0.0.1");
    }
}

/// <summary>RFC 6238 TOTP 实现（对照官方测试向量）。</summary>
public class TotpTests
{
    // RFC 6238 附录 B：SHA1 密钥为 ASCII "12345678901234567890"
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    private static DateTimeOffset T(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

    [Theory]
    [InlineData(59, 287082)]        // T=59 → 94287082（8位）→ 6位取低 6 位
    [InlineData(1111111109, 081804)]
    [InlineData(1111111111, 050471)]
    [InlineData(1234567890, 005924)]
    [InlineData(2000000000, 279037)]
    public void MatchesRfc6238Vectors(long unixTime, int expected)
    {
        var code = Totp.ComputeCode(Secret, T(unixTime));
        Assert.Equal(expected.ToString("D6"), code);
    }

    [Fact]
    public void Verify_AllowsOneStepDrift()
    {
        var now = T(59);
        Assert.True(Totp.Verify(Secret, "287082", now));
        Assert.True(Totp.Verify(Secret, Totp.ComputeCode(Secret, T(59 + 30)), now)); // 下一步
        Assert.False(Totp.Verify(Secret, "000000", now));
        Assert.False(Totp.Verify(Secret, "28708", now));       // 长度不对
        Assert.False(Totp.Verify(Secret, "28708a", now));      // 非数字
    }

    [Fact]
    public void Base32Roundtrip()
    {
        var secret = Totp.GenerateSecret();
        Assert.Equal(32, secret.Length); // 20 字节 → 32 字符
        var decoded = Totp.Base32Decode(secret);
        Assert.Equal(secret, Totp.Base32Encode(decoded));
    }

    [Fact]
    public void OtpAuthUriContainsIssuerAndSecret()
    {
        var uri = Totp.BuildOtpAuthUri("ABC234DEF", "admin", "SkyEyePMS");
        Assert.StartsWith("otpauth://totp/SkyEyePMS:admin", uri);
        Assert.Contains("secret=ABC234DEF", uri);
    }
}

/// <summary>TOTP 登录两步流程（集成）。</summary>
public class TotpFlowTests : IClassFixture<SkyEyeApiFactory>
{
    private readonly SkyEyeApiFactory _factory;

    public TotpFlowTests(SkyEyeApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Setup_Enable_TwoStepLogin_Works()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "Admin@12345" });
        login.EnsureSuccessStatusCode();
        var first = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(first.TryGetProperty("totpRequired", out _)); // 未启用前正常返回 token
        client.DefaultRequestHeaders.Authorization = new("Bearer", first.GetProperty("token").GetString()!);

        // setup → 明文密钥 + otpauth URI
        var setup = await client.PostAsJsonAsync("/api/auth/totp/setup", new { });
        setup.EnsureSuccessStatusCode();
        var setupData = await setup.Content.ReadFromJsonAsync<JsonElement>();
        var secret = setupData.GetProperty("secret").GetString()!;
        Assert.Contains("otpauth://totp/", setupData.GetProperty("otpauthUri").GetString());

        // 用当前验证码启用
        var code = Totp.ComputeCode(secret, DateTimeOffset.UtcNow);
        var enable = await client.PostAsJsonAsync("/api/auth/totp/enable", new { code });
        Assert.True(enable.IsSuccessStatusCode, await enable.Content.ReadAsStringAsync());

        // 密码登录 → 要求验证码
        var step1 = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "Admin@12345" });
        step1.EnsureSuccessStatusCode();
        var s1 = await step1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(s1.GetProperty("totpRequired").GetBoolean());
        var loginToken = s1.GetProperty("loginToken").GetString()!;

        // 错误验证码被拒绝
        var bad = await client.PostAsJsonAsync("/api/auth/login/totp", new { loginToken, code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        // 正确验证码 → 拿到正式 token 并可访问业务接口
        var good = await client.PostAsJsonAsync("/api/auth/login/totp",
            new { loginToken, code = Totp.ComputeCode(secret, DateTimeOffset.UtcNow) });
        var goodBody = await good.Content.ReadAsStringAsync();
        Assert.True(good.IsSuccessStatusCode, "good: " + good.StatusCode + " " + goodBody);
        var final = JsonDocument.Parse(goodBody).RootElement;
        var token = final.GetProperty("token").GetString()!;
        Assert.False(final.TryGetProperty("totpRequired", out _)); // 正式登录结果不应再要求验证码

        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var me = await client.GetAsync("/api/auth/me");
        Assert.True(me.IsSuccessStatusCode);

        // 清理：停用两步验证，避免影响同库其他测试
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var disable = await client.PostAsJsonAsync("/api/auth/totp/disable",
            new { code = Totp.ComputeCode(secret, DateTimeOffset.UtcNow) });
        Assert.True(disable.IsSuccessStatusCode, await disable.Content.ReadAsStringAsync());
    }
}

/// <summary>日粒度统计汇总（集成）。</summary>
public class DailyStatsTests : IClassFixture<SkyEyeApiFactory>
{
    private readonly SkyEyeApiFactory _factory;

    public DailyStatsTests(SkyEyeApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RollupAndDailyReport_Work()
    {
        using var scope = _factory.Services.CreateScope();
        var metrics = scope.ServiceProvider.GetRequiredService<IMetricsRepository>();
        var printers = scope.ServiceProvider.GetRequiredService<IPrinterRepository>();
        var pid = await printers.CreateAsync(new Printer { Name = "趋势机", Ip = "192.0.2.91", Port = 161, CommunityCipher = "public" });

        var today = DateTime.UtcNow.Date;
        // 昨天：100 → 400（+300）；今天：400 → 700（+300）
        await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = pid, CollectedAt = today.AddDays(-1).AddHours(2), Online = true, PageCount = 100 });
        await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = pid, CollectedAt = today.AddDays(-1).AddHours(20), Online = true, PageCount = 400 });
        await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = pid, CollectedAt = today.AddHours(1), Online = true, PageCount = 700 });

        var retention = _factory.Services.GetServices<IHostedService>()
            .OfType<RetentionService>().First();
        await retention.RollupDailyStatsAsync();

        var stats = await metrics.GetDailyStatsAsync(today.AddDays(-7));
        var yesterday = stats.First(d => d.StatDate.Date == today.AddDays(-1));
        var todayStat = stats.First(d => d.StatDate.Date == today);
        Assert.Equal(300, yesterday.PagesPrinted);
        Assert.Equal(2, yesterday.TotalSamples);
        Assert.Equal(300, todayStat.PagesPrinted);

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "Admin@12345" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var daily = await client.GetFromJsonAsync<JsonElement>("/api/reports/daily?days=30");
        var dates = daily.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("date").GetString()).ToList();
        Assert.Contains(today.AddDays(-1).ToString("yyyy-MM-dd"), dates);
        Assert.Contains(today.ToString("yyyy-MM-dd"), dates);
        var todayRow = daily.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("date").GetString() == today.ToString("yyyy-MM-dd"));
        Assert.Equal(300, todayRow.GetProperty("totalPages").GetInt64());
    }
}
