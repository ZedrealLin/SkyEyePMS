using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SkyEyePMS.Tests;

/// <summary>收尾包：异步发现任务 + 审计保留校验。</summary>
public class FinishBatchTests : IClassFixture<SkyEyeApiFactory>
{
    private readonly SkyEyeApiFactory _factory;

    public FinishBatchTests(SkyEyeApiFactory factory) => _factory = factory;

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "Admin@12345" });
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", json.GetProperty("token").GetString()!);
        return client;
    }

    [Fact]
    public async Task DiscoveryScan_ReturnsJobAndCompletes()
    {
        var client = await LoginAsync();
        var start = await client.PostAsJsonAsync("/api/discovery/scan", new
        {
            startIp = "192.0.2.1", endIp = "192.0.2.3", port = 161,
            community = "public", snmpVersion = 2, timeoutMs = 200
        });
        Assert.True(start.IsSuccessStatusCode, await start.Content.ReadAsStringAsync());
        var job = await start.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = job.GetProperty("jobId").GetString()!;
        Assert.Equal(3, job.GetProperty("total").GetInt32());

        // 轮询至完成（3 个地址 × 200ms 超时，1 秒内应结束）
        var done = false;
        JsonElement status = default;
        for (var i = 0; i < 20; i++)
        {
            status = await client.GetFromJsonAsync<JsonElement>($"/api/discovery/scan/{jobId}");
            if (status.GetProperty("done").GetBoolean()) { done = true; break; }
            await Task.Delay(250);
        }
        Assert.True(done, "扫描应在数秒内完成");
        Assert.Null(status.GetProperty("error").GetString());
        Assert.Equal(3, status.GetProperty("scanned").GetInt32());
        var hits = status.GetProperty("hits").GetArrayLength();
        Assert.True(hits >= 0);
    }

    [Fact]
    public async Task DiscoveryScan_ValidatesRange()
    {
        var client = await LoginAsync();
        var resp = await client.PostAsJsonAsync("/api/discovery/scan", new
        {
            startIp = "10.0.0.1", endIp = "10.0.1.254", // 654 个 > … 合法；换超大区间
            port = 161, community = "public", snmpVersion = 2, timeoutMs = 200
        });
        // 654 个地址在 4096 内 → 允许
        Assert.True(resp.IsSuccessStatusCode);

        var tooBig = await client.PostAsJsonAsync("/api/discovery/scan", new
        {
            startIp = "10.0.0.1", endIp = "10.0.16.254", // 4300+ 个
            port = 161, community = "public", snmpVersion = 2, timeoutMs = 200
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);

        var notFound = await client.GetAsync("/api/discovery/scan/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task Retention_AuditDaysValidated()
    {
        var client = await LoginAsync();
        var ok = await client.PutAsJsonAsync("/api/settings/retention", new
        {
            statusHistoryDays = 30, supplyHistoryDays = 60,
            resolvedAlertDays = 90, notificationLogDays = 30, auditLogDays = 180
        });
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(180, body.GetProperty("auditLogDays").GetInt32());

        var bad = await client.PutAsJsonAsync("/api/settings/retention", new
        {
            statusHistoryDays = 30, supplyHistoryDays = 60,
            resolvedAlertDays = 90, notificationLogDays = 30, auditLogDays = 3
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
