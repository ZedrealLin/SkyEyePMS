using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;

namespace SkyEyePMS.Tests;

/// <summary>
/// 新增功能集成测试：告警确认、告警/报表 CSV 导出、使用统计、耗材耗尽预测。
/// 复用 SkyEyeApiFactory（SQLite 临时库，轮询关闭）。
/// </summary>
public class FeaturesIntegrationTests : IClassFixture<SkyEyeApiFactory>
{
    private readonly SkyEyeApiFactory _factory;

    public FeaturesIntegrationTests(SkyEyeApiFactory factory) => _factory = factory;

    private async Task<HttpClient> LoginAsync(string username = "admin", string password = "Admin@12345")
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", json.GetProperty("token").GetString()!);
        return client;
    }

    private static int _ipSeq;

    private async Task<int> CreatePrinterAsync(string name = "测试打印机")
    {
        using var scope = _factory.Services.CreateScope();
        var printers = scope.ServiceProvider.GetRequiredService<IPrinterRepository>();
        var seq = Interlocked.Increment(ref _ipSeq); // ip+port 唯一，逐用例递增
        return await printers.CreateAsync(new Printer
        {
            Name = name,
            Ip = $"192.0.2.{seq}",
            Port = 161,
            Location = "测试位置",
            Department = "测试部门",
            CommunityCipher = "public"
        });
    }

    // ---------- 告警确认 ----------

    [Fact]
    public async Task Ack_SetsFieldsAndVisibleInQuery()
    {
        var printerId = await CreatePrinterAsync();
        int alertId;
        using (var scope = _factory.Services.CreateScope())
        {
            var alerts = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
            alertId = await alerts.CreateAsync(new Alert
            {
                PrinterId = printerId,
                AlertKey = "offline",
                Type = "Offline",
                Severity = "Critical",
                Message = "设备离线",
                FirstTriggeredAt = DateTime.UtcNow
            });
        }

        var client = await LoginAsync();
        var resp = await client.PostAsync($"/api/alerts/{alertId}/ack", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var query = await client.GetFromJsonAsync<JsonElement>("/api/alerts/?status=active&limit=100");
        var item = query.GetProperty("items").EnumerateArray().First(a => a.GetProperty("id").GetInt32() == alertId);
        Assert.Equal("admin", item.GetProperty("acknowledgedBy").GetString());
        Assert.True(item.TryGetProperty("acknowledgedAt", out var ackAt) && ackAt.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task Ack_ForbiddenForViewer()
    {
        var printerId = await CreatePrinterAsync();
        int alertId;
        using (var scope = _factory.Services.CreateScope())
        {
            var alerts = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
            alertId = await alerts.CreateAsync(new Alert
            {
                PrinterId = printerId,
                AlertKey = "paper_jam",
                Type = "PaperJam",
                Severity = "Warning",
                Message = "卡纸",
                FirstTriggeredAt = DateTime.UtcNow
            });
        }

        var admin = await LoginAsync();
        var create = await admin.PostAsJsonAsync("/api/users", new { username = "ackviewer", password = "Viewer@12345", role = "viewer" });
        Assert.True(HttpStatusCode.Created == create.StatusCode || HttpStatusCode.OK == create.StatusCode,
            $"创建只读用户失败：{create.StatusCode}");

        var viewer = await LoginAsync("ackviewer", "Viewer@12345");
        var resp = await viewer.PostAsync($"/api/alerts/{alertId}/ack", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---------- CSV 导出 ----------

    [Fact]
    public async Task AlertsExport_ReturnsCsvWithBomAndHeaders()
    {
        var client = await LoginAsync();
        var resp = await client.GetAsync("/api/alerts/export.csv");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("text/csv", resp.Content.Headers.ContentType!.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.StartsWith("\uFEFF", body);
        Assert.Contains("打印机", body);
        Assert.Contains("确认人", body);
    }

    [Fact]
    public async Task UsageExport_ReturnsCsv()
    {
        var client = await LoginAsync();
        var resp = await client.GetAsync("/api/reports/usage/export.csv?days=7");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("text/csv", resp.Content.Headers.ContentType!.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("打印页数", body);
        Assert.Contains("可用率", body);
    }

    [Fact]
    public async Task ForecastExport_ReturnsCsv()
    {
        var client = await LoginAsync();
        var resp = await client.GetAsync("/api/reports/forecast/export.csv");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("预计剩余天数", body);
    }

    // ---------- 使用统计 ----------

    [Fact]
    public async Task UsageReport_ComputesPagesAndAvailability()
    {
        var printerId = await CreatePrinterAsync("使用统计机");
        using (var scope = _factory.Services.CreateScope())
        {
            var metrics = scope.ServiceProvider.GetRequiredService<IMetricsRepository>();
            var now = DateTime.UtcNow;
            // 100 → 250 → 250 → 30（清零）→ 130，打印量应为 250
            await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = printerId, CollectedAt = now.AddDays(-3), Online = true, PageCount = 100 });
            await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = printerId, CollectedAt = now.AddDays(-2), Online = true, PageCount = 250 });
            await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = printerId, CollectedAt = now.AddDays(-1), Online = true, PageCount = 250 });
            await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = printerId, CollectedAt = now.AddHours(-12), Online = true, PageCount = 30 });
            await metrics.InsertStatusSampleAsync(new StatusSample { PrinterId = printerId, CollectedAt = now, Online = false, PageCount = 130 });
        }

        var client = await LoginAsync();
        var report = await client.GetFromJsonAsync<JsonElement>("/api/reports/usage?days=7");
        var item = report.GetProperty("items").EnumerateArray().First(i => i.GetProperty("printerId").GetInt32() == printerId);

        Assert.Equal(250, item.GetProperty("pagesPrinted").GetInt64());
        Assert.Equal(80.0, item.GetProperty("availabilityPercent").GetDouble(), 1);
        Assert.Equal(5, item.GetProperty("sampleCount").GetInt32());
    }

    // ---------- 耗材耗尽预测 ----------

    [Fact]
    public async Task ForecastReport_PredictsConsumingSupply()
    {
        var printerId = await CreatePrinterAsync("预测机");
        using (var scope = _factory.Services.CreateScope())
        {
            var metrics = scope.ServiceProvider.GetRequiredService<IMetricsRepository>();
            var now = DateTime.UtcNow;
            await metrics.UpsertSupplyAsync(new Supply
            {
                PrinterId = printerId, SupplyIndex = 1, Name = "Black Toner", Percent = 60, UpdatedAt = now
            });
            // 每天约 -15%
            await metrics.InsertSupplySampleAsync(new SupplySample { PrinterId = printerId, SupplyIndex = 1, Percent = 90, CollectedAt = now.AddDays(-2) });
            await metrics.InsertSupplySampleAsync(new SupplySample { PrinterId = printerId, SupplyIndex = 1, Percent = 75, CollectedAt = now.AddDays(-1) });
            await metrics.InsertSupplySampleAsync(new SupplySample { PrinterId = printerId, SupplyIndex = 1, Percent = 60, CollectedAt = now });
        }

        var client = await LoginAsync();
        var report = await client.GetFromJsonAsync<JsonElement>("/api/reports/forecast?windowDays=14");
        var item = report.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("printerId").GetInt32() == printerId);

        Assert.Equal(60, item.GetProperty("percent").GetDouble(), 1);
        Assert.Equal(15, item.GetProperty("dailyConsumptionPercent").GetDouble(), 1);
        Assert.Equal(4, item.GetProperty("daysLeft").GetDouble(), 1);

        // 单机详情预测接口返回一致
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/printers/{printerId}/forecast");
        var dItem = detail.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(4, dItem.GetProperty("daysLeft").GetDouble(), 1);
    }

    [Fact]
    public async Task ForecastReport_StableSupplyHasNullDaysLeft()
    {
        var printerId = await CreatePrinterAsync("平稳机");
        using (var scope = _factory.Services.CreateScope())
        {
            var metrics = scope.ServiceProvider.GetRequiredService<IMetricsRepository>();
            var now = DateTime.UtcNow;
            await metrics.UpsertSupplyAsync(new Supply
            {
                PrinterId = printerId, SupplyIndex = 1, Name = "Cyan", Percent = 50, UpdatedAt = now
            });
            await metrics.InsertSupplySampleAsync(new SupplySample { PrinterId = printerId, SupplyIndex = 1, Percent = 50, CollectedAt = now.AddDays(-2) });
            await metrics.InsertSupplySampleAsync(new SupplySample { PrinterId = printerId, SupplyIndex = 1, Percent = 50, CollectedAt = now });
        }

        var client = await LoginAsync();
        var report = await client.GetFromJsonAsync<JsonElement>("/api/reports/forecast");
        var item = report.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("printerId").GetInt32() == printerId);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("daysLeft").ValueKind);
    }
}
