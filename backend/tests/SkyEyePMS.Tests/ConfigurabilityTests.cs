using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SkyEyePMS.Core.Alerting;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Reading;
using SkyEyePMS.Core.Repositories;

namespace SkyEyePMS.Tests;

/// <summary>
/// 细粒度可配置参数：告警严重阈值、单台打印机阈值覆盖、分析/采样/轮询设置的存取与校验。
/// </summary>
public class ConfigurabilityTests : IClassFixture<SkyEyeApiFactory>
{
    private readonly SkyEyeApiFactory _factory;

    public ConfigurabilityTests(SkyEyeApiFactory factory) => _factory = factory;

    private static readonly DateTime Now = new(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);

    private static Printer Printer(int? supplyLowOverride = null) => new()
    {
        Id = 1, Name = "PRN-CFG", Ip = "10.0.0.9", Port = 161,
        ConsecutiveFailures = 0,
        SupplyLowPercentOverride = supplyLowOverride
    };

    private static DeviceReading Reading(int supplyPercent) => new()
    {
        Supplies = { new SupplyReading(1, 3, "Black Toner", 7, 10000, supplyPercent * 100) }
    };

    // ---------- 单台打印机阈值覆盖（引擎层） ----------

    [Fact]
    public void PrinterOverride_HigherThreshold_SuppressesAlert()
    {
        // 全局 20%，该机覆盖为 10%：余量 15% 全局会告警，覆盖后不告警
        var thresholds = new AlertThresholds { SupplyLowPercent = 20 };
        thresholds.SupplyLowPercent = 10; // 模拟轮询服务套用单机覆盖

        var actions = AlertEngine.Evaluate(Printer(), Reading(15), true, thresholds, Array.Empty<Alert>(), Now);
        Assert.Empty(actions);
    }

    [Fact]
    public void PrinterOverride_LowerThreshold_RaisesWithEffectiveValue()
    {
        var thresholds = new AlertThresholds { SupplyLowPercent = 20 };
        thresholds.SupplyLowPercent = 10;

        var actions = AlertEngine.Evaluate(Printer(), Reading(8), true, thresholds, Array.Empty<Alert>(), Now);
        var raise = actions.Single(a => a.AlertKey == "supply_low:1");
        Assert.Equal(AlertActionType.Raise, raise.ActionType);
        Assert.Contains("低于阈值 10%", raise.Message);
    }

    [Fact]
    public void Clone_KeepsAllValues_AndAllowsOverride()
    {
        var t = new AlertThresholds { SupplyLowPercent = 25, SupplyCriticalPercent = 8, OfflineConsecutiveFailures = 4, AlertSilenceMinutes = 30 };
        var c = t.Clone();
        Assert.Equal(25, c.SupplyLowPercent);
        Assert.Equal(8, c.SupplyCriticalPercent);
        Assert.Equal(4, c.OfflineConsecutiveFailures);
        Assert.Equal(30, c.AlertSilenceMinutes);
        c.SupplyLowPercent = 5;
        Assert.Equal(25, t.SupplyLowPercent); // 克隆互不影响
    }

    // ---------- 设置存取与校验（集成） ----------

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
    public async Task AnalysisSettings_PutThenGetRoundtrip()
    {
        var client = await LoginAsync();
        var put = await client.PutAsJsonAsync("/api/settings/analysis",
            new AnalysisOptions { ForecastWindowDays = 21, SupplyReplacementRisePercent = 25 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await client.GetFromJsonAsync<JsonElement>("/api/settings/");
        Assert.Equal(21, get.GetProperty("analysis").GetProperty("forecastWindowDays").GetInt32());
        Assert.Equal(25, get.GetProperty("analysis").GetProperty("supplyReplacementRisePercent").GetInt32());
    }

    [Fact]
    public async Task PollingSettings_PutThenGetRoundtrip_AndValidation()
    {
        var client = await LoginAsync();
        var put = await client.PutAsJsonAsync("/api/settings/polling",
            new PollingSettings { IntervalMinutes = 5, Concurrency = 30 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await client.GetFromJsonAsync<JsonElement>("/api/settings/");
        Assert.Equal(5, get.GetProperty("polling").GetProperty("intervalMinutes").GetInt32());
        Assert.Equal(30, get.GetProperty("polling").GetProperty("concurrency").GetInt32());

        var bad = await client.PutAsJsonAsync("/api/settings/polling",
            new PollingSettings { IntervalMinutes = 0, Concurrency = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task SamplingSettings_Roundtrip_AndThresholdValidation()
    {
        var client = await LoginAsync();
        var put = await client.PutAsJsonAsync("/api/settings/sampling",
            new SamplingOptions { SupplySampleMinChangePercent = 3 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var get = await client.GetFromJsonAsync<JsonElement>("/api/settings/");
        Assert.Equal(3, get.GetProperty("sampling").GetProperty("supplySampleMinChangePercent").GetInt32());

        var bad = await client.PutAsJsonAsync("/api/settings/sampling",
            new SamplingOptions { SupplySampleMinChangePercent = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Thresholds_CriticalMustNotExceedLow()
    {
        var client = await LoginAsync();
        var bad = await client.PutAsJsonAsync("/api/settings/thresholds",
            new AlertThresholds { SupplyLowPercent = 10, SupplyCriticalPercent = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task PrinterSupplyOverride_PersistThroughEditApi()
    {
        using var scope = _factory.Services.CreateScope();
        var printers = scope.ServiceProvider.GetRequiredService<IPrinterRepository>();
        var id = await printers.CreateAsync(new Printer
        {
            Name = "覆盖机", Ip = "192.0.2.88", Port = 161, CommunityCipher = "public"
        });

        var client = await LoginAsync();
        var put = await client.PutAsJsonAsync($"/api/printers/{id}", new
        {
            name = "覆盖机", ip = "192.0.2.88", port = 161, community = "",
            snmpVersion = 2, enabled = true, lowSupplyPercentOverride = 12
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var view = await client.GetFromJsonAsync<JsonElement>($"/api/printers/{id}");
        Assert.Equal(12, view.GetProperty("supplyLowPercentOverride").GetInt32());

        // 置空 = 回到全局阈值
        var clear = await client.PutAsJsonAsync($"/api/printers/{id}", new
        {
            name = "覆盖机", ip = "192.0.2.88", port = 161, community = "",
            snmpVersion = 2, enabled = true, lowSupplyPercentOverride = (int?)null
        });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var view2 = await client.GetFromJsonAsync<JsonElement>($"/api/printers/{id}");
        Assert.Equal(JsonValueKind.Null, view2.GetProperty("supplyLowPercentOverride").ValueKind);
    }

    [Fact]
    public async Task Forecast_UsesConfiguredWindow()
    {
        var client = await LoginAsync();
        // 默认窗口 14 天；把窗口设为 2 天后，13 天前的旧采样不再参与 → 不可预测
        await client.PutAsJsonAsync("/api/settings/analysis",
            new AnalysisOptions { ForecastWindowDays = 2, SupplyReplacementRisePercent = 15 });
        try
        {
            using var scope = _factory.Services.CreateScope();
            var metrics = scope.ServiceProvider.GetRequiredService<IMetricsRepository>();
            var now = DateTime.UtcNow;
            await metrics.UpsertSupplyAsync(new Supply
            {
                PrinterId = 9991, SupplyIndex = 1, Name = "Toner", Percent = 40, UpdatedAt = now
            });
            await metrics.InsertSupplySampleAsync(new SupplySample { PrinterId = 9991, SupplyIndex = 1, Percent = 90, CollectedAt = now.AddDays(-13) });
            await metrics.InsertSupplySampleAsync(new SupplySample { PrinterId = 9991, SupplyIndex = 1, Percent = 60, CollectedAt = now.AddDays(-7) });
            await metrics.InsertSupplySampleAsync(new SupplySample { PrinterId = 9991, SupplyIndex = 1, Percent = 40, CollectedAt = now.AddHours(-1) });

            var report = await client.GetFromJsonAsync<JsonElement>("/api/reports/forecast");
            Assert.Equal(2, report.GetProperty("windowDays").GetInt32());
            var item = report.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("printerId").GetInt32() == 9991);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("daysLeft").ValueKind);
        }
        finally
        {
            await client.PutAsJsonAsync("/api/settings/analysis",
                new AnalysisOptions { ForecastWindowDays = 14, SupplyReplacementRisePercent = 15 });
        }
    }
}
