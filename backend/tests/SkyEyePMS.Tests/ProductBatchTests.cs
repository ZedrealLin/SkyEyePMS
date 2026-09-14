using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Notifying;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Infrastructure.Notifying;

namespace SkyEyePMS.Tests;

/// <summary>通知路由/免打扰（纯函数）+ 通用 Webhook 请求体。</summary>
public class NotificationRoutingTests
{
    private static readonly DateTime Now = new(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MinSeverityFiltersLowerLevels()
    {
        var routing = new ChannelRouting { MinSeverity = "Warning" };
        Assert.False(NotificationRouting.ShouldSend(routing, AlertSeverity.Info, Now));
        Assert.True(NotificationRouting.ShouldSend(routing, AlertSeverity.Warning, Now));
        Assert.True(NotificationRouting.ShouldSend(routing, AlertSeverity.Critical, Now));
    }

    [Fact]
    public void QuietHoursSuppressNonCritical_EvenAcrossMidnight()
    {
        // 22:00-08:00 跨零点
        var routing = new ChannelRouting { QuietHoursEnabled = true, QuietHoursStart = "22:00", QuietHoursEnd = "08:00" };
        var lateNight = Now.Date.AddHours(23).AddMinutes(30);
        var earlyMorning = Now.Date.AddHours(7);
        var noon = Now.Date.AddHours(12);

        Assert.True(NotificationRouting.InQuietHours(routing, lateNight));
        Assert.True(NotificationRouting.InQuietHours(routing, earlyMorning));
        Assert.False(NotificationRouting.InQuietHours(routing, noon));

        Assert.False(NotificationRouting.ShouldSend(routing, AlertSeverity.Warning, lateNight));
        Assert.True(NotificationRouting.ShouldSend(routing, AlertSeverity.Critical, lateNight));
        Assert.True(NotificationRouting.ShouldSend(routing, AlertSeverity.Warning, noon));
        Assert.True(NotificationRouting.ShouldSend(new ChannelRouting(), AlertSeverity.Info, lateNight));
    }

    [Fact]
    public void InvalidTimeWindowFailsOpen()
    {
        var routing = new ChannelRouting { QuietHoursEnabled = true, QuietHoursStart = "bad", QuietHoursEnd = "08:00" };
        Assert.False(NotificationRouting.InQuietHours(routing, Now));
        Assert.True(NotificationRouting.ShouldSend(routing, AlertSeverity.Info, Now));
    }
}

/// <summary>通用 Webhook 渠道请求体（子类暴露 protected 方法）。</summary>
public class WebhookBodyTests
{
    private class ExposedWebhookNotifier : GenericWebhookNotifier
    {
        public ExposedWebhookNotifier(WebhookOptions options)
            : base(options, new TestHttpClientFactory(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GenericWebhookNotifier>.Instance) { }

        public (string Url, System.Text.Json.Nodes.JsonObject Body) Expose(AlertNotification n) => BuildRequest(n);
        public (string Url, System.Text.Json.Nodes.JsonObject Body) ExposeDigest(string t, string m)
            => BuildDigestRequest(t, m)!.Value;
    }

    [Fact]
    public void BuildsStructuredJsonBody()
    {
        var notifier = new ExposedWebhookNotifier(new WebhookOptions { Enabled = true, Url = "https://hooks.example.com/x" });
        var alert = new Alert
        {
            Id = 7, PrinterId = 3, AlertKey = "offline", Type = "Offline", Severity = "Critical",
            Message = "设备离线", FirstTriggeredAt = new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc)
        };
        var printer = new Printer { Id = 3, Name = "PRN-X", Ip = "10.0.0.1", Port = 161 };
        var (url, body) = notifier.Expose(new AlertNotification(AlertEventKind.Raised, alert, printer, "SkyEyePMS"));

        Assert.Equal("https://hooks.example.com/x", url);
        Assert.Equal("raised", body["event"]!.GetValue<string>());
        Assert.Equal("Critical", body["severity"]!.GetValue<string>());
        Assert.Equal("PRN-X", body["printer"]!["name"]!.GetValue<string>());
        Assert.True(body.ContainsKey("timestamp"));

        var digest = notifier.ExposeDigest("周报", "- 项目一");
        Assert.Equal("digest", digest.Body["event"]!.GetValue<string>());
        Assert.Equal("周报", digest.Body["title"]!.GetValue<string>());
        Assert.True(notifier.IsConfigured);
    }
}

/// <summary>周报摘要构建（独立库避免数据污染）。</summary>
public class DigestBuilderTests : IClassFixture<SkyEyeApiFactory>
{
    private readonly SkyEyeApiFactory _factory;

    public DigestBuilderTests(SkyEyeApiFactory factory) => _factory = factory;

    [Fact]
    public async Task IncludesKeySections()
    {
        using var scope = _factory.Services.CreateScope();
        var printers = scope.ServiceProvider.GetRequiredService<IPrinterRepository>();
        var metrics = scope.ServiceProvider.GetRequiredService<IMetricsRepository>();
        var alerts = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
        var settings = scope.ServiceProvider.GetRequiredService<SkyEyePMS.Core.Services.IAppSettingsService>();

        var now = DateTime.UtcNow;
        var pid = await printers.CreateAsync(new Printer { Name = "摘要机", Ip = "192.0.2.71", Port = 161, CommunityCipher = "public" });
        await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = pid, CollectedAt = now.AddHours(-2), Online = true, PageCount = 500 });
        await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = pid, CollectedAt = now, Online = true, PageCount = 1500 });
        await alerts.CreateAsync(new Alert
        {
            PrinterId = pid, AlertKey = "supply_low:1", Type = "SupplyLow", Severity = "Warning",
            Message = "余量不足", FirstTriggeredAt = now.AddHours(-1), Status = "active"
        });

        var digest = await SkyEyePMS.Api.Services.DigestBuilder.BuildAsync(printers, metrics, alerts, settings, days: 7);
        Assert.Contains("周报", digest.Title);
        Assert.Contains("使用概况", digest.Markdown);
        Assert.Contains("耗材预警", digest.Markdown);
        Assert.Contains("故障排行", digest.Markdown);
        Assert.Contains("告警中 1 条", digest.Markdown);
        Assert.Contains("1000", digest.Markdown); // 正增量 1500-500
    }
}

/// <summary>运维质量指标（独立库）。</summary>
public class QualityIntegrationTests : IClassFixture<SkyEyeApiFactory>
{
    private readonly SkyEyeApiFactory _factory;

    public QualityIntegrationTests(SkyEyeApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ComputesMttaAckRateAndNotifySuccess()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "Admin@12345" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        using var scope = _factory.Services.CreateScope();
        var printers = scope.ServiceProvider.GetRequiredService<IPrinterRepository>();
        var alerts = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
        var pid = await printers.CreateAsync(new Printer { Name = "质量机", Ip = "192.0.2.72", Port = 161, CommunityCipher = "public" });

        var now = DateTime.UtcNow;
        var a = await alerts.CreateAsync(new Alert
        {
            PrinterId = pid, AlertKey = "k1", Type = "Offline", Severity = "Critical",
            Message = "离线A", FirstTriggeredAt = now.AddHours(-3), Status = "active"
        });
        await alerts.AcknowledgeAsync(a, "admin", now.AddHours(-2));
        await alerts.CreateAsync(new Alert
        {
            PrinterId = pid, AlertKey = "k2", Type = "PaperJam", Severity = "Warning",
            Message = "卡纸B", FirstTriggeredAt = now.AddHours(-2), Status = "active"
        });
        await alerts.InsertNotificationLogAsync(new NotificationLogEntry { AlertId = a, Channel = "feishu", Success = true, SentAt = now.AddHours(-3) });
        await alerts.InsertNotificationLogAsync(new NotificationLogEntry { AlertId = a, Channel = "email", Success = false, Error = "smtp", SentAt = now.AddHours(-3) });

        var q = await client.GetFromJsonAsync<JsonElement>("/api/reports/quality?days=7");
        Assert.Equal(60, q.GetProperty("mttaMinutes").GetDouble(), 1);
        Assert.Equal(2, q.GetProperty("triggeredCount").GetInt32());
        Assert.Equal(1, q.GetProperty("acknowledgedCount").GetInt32());
        Assert.Equal(50, q.GetProperty("ackRatePercent").GetDouble(), 1);
        Assert.Equal(2, q.GetProperty("notifyAttempts").GetInt64());
        Assert.Equal(1, q.GetProperty("notifySuccesses").GetInt64());
        Assert.Equal(50, q.GetProperty("notifySuccessPercent").GetDouble(), 1);
        Assert.Equal(1, q.GetProperty("criticalCount").GetInt32());
    }
}

/// <summary>部门抄表（独立库）。</summary>
public class MeterIntegrationTests : IClassFixture<SkyEyeApiFactory>
{
    private readonly SkyEyeApiFactory _factory;

    public MeterIntegrationTests(SkyEyeApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GroupsByDepartmentAndExportsCsv()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "Admin@12345" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        using var scope = _factory.Services.CreateScope();
        var printers = scope.ServiceProvider.GetRequiredService<IPrinterRepository>();
        var metrics = scope.ServiceProvider.GetRequiredService<IMetricsRepository>();
        var p1 = await printers.CreateAsync(new Printer { Name = "抄表A", Ip = "192.0.2.73", Port = 161, CommunityCipher = "public", Department = "研发部" });
        var p2 = await printers.CreateAsync(new Printer { Name = "抄表B", Ip = "192.0.2.74", Port = 161, CommunityCipher = "public", Department = "研发部" });

        var now = DateTime.UtcNow;
        await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = p1, CollectedAt = now.AddDays(-2), Online = true, PageCount = 100 });
        await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = p1, CollectedAt = now, Online = true, PageCount = 600 });
        await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = p2, CollectedAt = now.AddDays(-2), Online = false, PageCount = 50 });
        await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = p2, CollectedAt = now, Online = false, PageCount = 150 });

        var meter = await client.GetFromJsonAsync<JsonElement>("/api/reports/meter?days=7");
        var dept = meter.GetProperty("departments").EnumerateArray()
            .First(d => d.GetProperty("department").GetString() == "研发部");
        Assert.Equal(2, dept.GetProperty("printerCount").GetInt32());
        Assert.Equal(600, dept.GetProperty("totalPages").GetInt64());
        Assert.Equal(50, dept.GetProperty("avgAvailabilityPercent").GetDouble(), 1);

        var csv = await client.GetAsync("/api/reports/meter/export.csv?days=7");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var body = await csv.Content.ReadAsStringAsync();
        Assert.Contains("部门：研发部", body);
        Assert.Contains("抄表A", body);
    }
}

/// <summary>审计日志（独立库）。</summary>
public class AuditIntegrationTests : IClassFixture<SkyEyeApiFactory>
{
    private readonly SkyEyeApiFactory _factory;

    public AuditIntegrationTests(SkyEyeApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RecordsPrinterCreateAndAck()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "Admin@12345" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var create = await client.PostAsJsonAsync("/api/printers", new
        {
            name = "审计机", ip = "192.0.2.75", port = 161, community = "public", snmpVersion = 2, enabled = true
        });
        create.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var alerts = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
        var aid = await alerts.CreateAsync(new Alert
        {
            PrinterId = 1, AlertKey = "audit-k", Type = "Offline", Severity = "Critical",
            Message = "审计测试", FirstTriggeredAt = DateTime.UtcNow, Status = "active"
        });
        await client.PostAsync($"/api/alerts/{aid}/ack", null);

        var logs = await client.GetFromJsonAsync<JsonElement>("/api/audit/?limit=50");
        var actions = logs.EnumerateArray().Select(x => x.GetProperty("action").GetString()).ToList();
        Assert.Contains("printer.create", actions);
        Assert.Contains("alert.ack", actions);
        var ackEntry = logs.EnumerateArray().First(x => x.GetProperty("action").GetString() == "alert.ack");
        Assert.Equal("admin", ackEntry.GetProperty("username").GetString());
    }
}

/// <summary>测试用 IHttpClientFactory（不真正发请求，仅构造）。</summary>
public class TestHttpClientFactory : System.Net.Http.IHttpClientFactory
{
    public System.Net.Http.HttpClient CreateClient(string name) => new();
}
