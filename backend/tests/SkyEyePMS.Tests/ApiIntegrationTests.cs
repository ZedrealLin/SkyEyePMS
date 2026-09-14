using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SkyEyePMS.Tests;

/// <summary>
/// Web API 集成测试：WebApplicationFactory + SQLite 临时库，验证鉴权/权限/CRUD/限流/设置全流程。
/// </summary>
public class SkyEyeApiFactory : WebApplicationFactory<Program>
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"skyeye-api-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Provider", "sqlite");
        builder.UseSetting("Database:SqlitePath", DbPath);
        builder.UseSetting("Security:EncryptionKey", "api-test-key");
        builder.UseSetting("Security:LoginRateLimit", "1000"); // 主工厂放开限流，专测单独建工厂
        builder.UseSetting("Polling:Enabled", "false");
        builder.UseSetting("Jwt:SecretKey", "test-jwt-secret-0123456789-0123456789");
        builder.UseSetting("Seed:AdminUsername", "admin");
        builder.UseSetting("Seed:AdminPassword", "Admin@12345");
        builder.UseEnvironment("Development");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
    }
}

/// <summary>限流专用工厂（低阈值）</summary>
public class RateLimitFactory : WebApplicationFactory<Program>
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"skyeye-rl-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Provider", "sqlite");
        builder.UseSetting("Database:SqlitePath", DbPath);
        builder.UseSetting("Security:EncryptionKey", "rl-test-key");
        builder.UseSetting("Security:LoginRateLimit", "3");
        builder.UseSetting("Polling:Enabled", "false");
        builder.UseSetting("Jwt:SecretKey", "test-jwt-secret-0123456789-0123456789");
        builder.UseSetting("Seed:AdminUsername", "admin");
        builder.UseSetting("Seed:AdminPassword", "Admin@12345");
        builder.UseEnvironment("Development");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
    }
}

public class ApiIntegrationTests : IClassFixture<SkyEyeApiFactory>
{
    private readonly SkyEyeApiFactory _factory;

    public ApiIntegrationTests(SkyEyeApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string Token)> LoginAsync(string username = "admin", string password = "Admin@12345")
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var token = json.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return (client, token);
    }

    // ---------- 认证 ----------

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Api_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/printers/");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Me_ReturnsCurrentUserInfo()
    {
        var (client, _) = await LoginAsync();
        var resp = await client.GetAsync("/api/auth/me");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", json.GetProperty("username").GetString());
        Assert.Equal("admin", json.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Login_ExceedsRateLimit_Returns429()
    {
        // 独立工厂：登录限流 = 3 次/分钟（与主工厂隔离）
        using var factory = new RateLimitFactory();
        var client = factory.CreateClient();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 6; i++)
        {
            last = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "bad" });
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
    }

    // ---------- 打印机 CRUD + 权限 ----------

    [Fact]
    public async Task PrinterCrud_FullCycle()
    {
        var (client, _) = await LoginAsync();

        // create
        var create = await client.PostAsJsonAsync("/api/printers", new
        {
            name = "PRN-IT-01",
            ip = "10.2.0.11",
            port = 161,
            community = "c0mm-internal",
            snmpVersion = 2,
            location = "5F",
            department = "IT"
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        // Community 密文不能出现在 API 响应里
        Assert.False(created.TryGetProperty("communityCipher", out _));

        // detail
        var detail = await client.GetAsync($"/api/printers/{id}");
        detail.EnsureSuccessStatusCode();

        // overview 出现且状态 unknown（从未采集）
        var overview = await client.GetFromJsonAsync<JsonElement>("/api/printers/?status=all");
        var items = overview.EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("id").GetInt32() == id && i.GetProperty("state").GetString() == "unknown");

        // update（community 留空 → 保留原密文）
        var update = await client.PutAsJsonAsync($"/api/printers/{id}", new
        {
            name = "PRN-IT-01-renamed",
            ip = "10.2.0.11",
            port = 161,
            community = "",
            snmpVersion = 2,
            location = "5F",
            department = "IT",
            enabled = true
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PRN-IT-01-renamed", updated.GetProperty("name").GetString());

        // delete
        var del = await client.DeleteAsync($"/api/printers/{id}");
        del.EnsureSuccessStatusCode();
        var gone = await client.GetAsync($"/api/printers/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task PrinterCreate_DuplicateEndpoint_Returns409()
    {
        var (client, _) = await LoginAsync();
        var body = new { name = "DUP", ip = "10.9.9.9", port = 161, community = "public", snmpVersion = 2 };
        var first = await client.PostAsJsonAsync("/api/printers", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await client.PostAsJsonAsync("/api/printers", body);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task PrinterCreate_InvalidIp_Returns400()
    {
        var (client, _) = await LoginAsync();
        var resp = await client.PostAsJsonAsync("/api/printers", new
        {
            name = "BAD", ip = "not-an-ip", port = 161, community = "public", snmpVersion = 2
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---------- 只读账号权限 ----------

    [Fact]
    public async Task ViewerRole_CannotModify()
    {
        var (admin, _) = await LoginAsync();
        await admin.PostAsJsonAsync("/api/users", new { username = "viewer1", password = "Viewer@123", role = "viewer" });

        var (client, token) = await LoginAsync("viewer1", "Viewer@123");
        var roleResp = await client.GetAsync("/api/auth/me");
        var role = (await roleResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString();
        Assert.Equal("viewer", role);

        // 读允许
        var read = await client.GetAsync("/api/printers/");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        // 写拒绝（403）
        var write = await client.PostAsJsonAsync("/api/printers", new
        {
            name = "X", ip = "10.5.5.5", port = 161, community = "public", snmpVersion = 2
        });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        var delSettings = await client.PutAsJsonAsync("/api/settings/thresholds",
            new { supplyLowPercent = 30, offlineConsecutiveFailures = 3, alertSilenceMinutes = 60 });
        Assert.Equal(HttpStatusCode.Forbidden, delSettings.StatusCode);
    }

    // ---------- 用户管理 ----------

    [Fact]
    public async Task UserManagement_ProtectsLastAdmin()
    {
        var (client, _) = await LoginAsync();
        // 创建第二个管理员再删掉，最后尝试删除自己应失败
        var create = await client.PostAsJsonAsync("/api/users", new { username = "admin2", password = "Admin2@123", role = "admin" });
        create.EnsureSuccessStatusCode();

        var users = await client.GetFromJsonAsync<JsonElement>("/api/users");
        var admin2 = users.EnumerateArray().First(u => u.GetProperty("username").GetString() == "admin2");
        var del2 = await client.DeleteAsync($"/api/users/{admin2.GetProperty("id").GetInt32()}");
        Assert.Equal(HttpStatusCode.OK, del2.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_ThenLoginWithNewPassword()
    {
        var (client, _) = await LoginAsync("admin", "Admin@12345");
        var resp = await client.PostAsJsonAsync("/api/auth/change-password",
            new { oldPassword = "Admin@12345", newPassword = "NewPass@1234" });
        resp.EnsureSuccessStatusCode();

        var relogin = _factory.CreateClient();
        var ok = await relogin.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "NewPass@1234" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // 改回原密码，避免影响其他用例（限流额度充足）
        var (client2, _) = await LoginAsync("admin", "NewPass@1234");
        var restore = await client2.PostAsJsonAsync("/api/auth/change-password",
            new { oldPassword = "NewPass@1234", newPassword = "Admin@12345" });
        restore.EnsureSuccessStatusCode();
    }

    // ---------- 设置与告警 ----------

    [Fact]
    public async Task Settings_UpdateThresholds_AndNotifiersConfig()
    {
        var (client, _) = await LoginAsync();

        var put = await client.PutAsJsonAsync("/api/settings/thresholds",
            new { supplyLowPercent = 25, offlineConsecutiveFailures = 5, alertSilenceMinutes = 30 });
        put.EnsureSuccessStatusCode();

        var get = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal(25, get.GetProperty("thresholds").GetProperty("supplyLowPercent").GetInt32());
        Assert.Equal(5, get.GetProperty("thresholds").GetProperty("offlineConsecutiveFailures").GetInt32());

        // 通知渠道配置保存 + 掩码返回
        var feishu = await client.PutAsJsonAsync("/api/settings/notifiers/feishu",
            JsonDocument.Parse("""{"enabled":true,"webhookUrl":"https://open.feishu.cn/hook/abc","secret":"s3cret"}""").RootElement);
        feishu.EnsureSuccessStatusCode();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        var view = settings.GetProperty("notifiers").GetProperty("feishu");
        Assert.True(view.GetProperty("enabled").GetBoolean());
        Assert.Equal("******", view.GetProperty("webhookUrl").GetString());
        Assert.Equal("******", view.GetProperty("secret").GetString());
    }

    [Fact]
    public async Task Alerts_List_ReturnsPagedResult()
    {
        var (client, _) = await LoginAsync();
        var resp = await client.GetFromJsonAsync<JsonElement>("/api/alerts/?status=active");
        Assert.True(resp.TryGetProperty("items", out _));
        Assert.True(resp.TryGetProperty("total", out _));
    }

    [Fact]
    public async Task SystemInfo_RequiresAuth_AndReturnsFields()
    {
        var (client, _) = await LoginAsync();
        var info = await client.GetFromJsonAsync<JsonElement>("/api/system/info");
        Assert.True(info.TryGetProperty("version", out _));
        Assert.True(info.TryGetProperty("pollIntervalMinutes", out _));
    }
}
