using Microsoft.Extensions.Caching.Memory;
using SkyEyePMS.Core.Analysis;
using SkyEyePMS.Core.Reading;
using SkyEyePMS.Core.Collecting;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Security;
using SkyEyePMS.Core.Services;
using SkyEyePMS.Infrastructure.Polling;

namespace SkyEyePMS.Api.Endpoints;

public static class PrinterEndpoints
{
    private const string AdminOnly = "AdminOnly";
    private const string CacheKey = "printers:overview";

    public static IEndpointRouteBuilder MapPrinterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/printers").WithTags("Printers");

        // 看板列表（含最新耗材 + 活跃告警汇总；15 秒缓存，多客户端常开刷新不放大 DB 压力）
        group.MapGet("/", async (IPrinterRepository printers, IMetricsRepository metrics,
            IAlertRepository alerts, IAppSettingsService settings, IMemoryCache cache,
            string? status, string? location, string? department, string? keyword) =>
        {
            // 全量数据整体缓存（15s），过滤条件在缓存结果上执行
            var all = await cache.GetOrCreateAsync(CacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15);
                var snapshot = await settings.GetSnapshotAsync();
                var printerList = await printers.GetAllAsync();
                var allSupplies = await metrics.GetAllSuppliesAsync();
                var activeAlerts = await alerts.QueryAsync(status: "active", limit: 10000);

                var alertByPrinter = activeAlerts.GroupBy(a => a.PrinterId).ToDictionary(g => g.Key, g => g.ToList());
                var supplyByPrinter = allSupplies.GroupBy(s => s.PrinterId).ToDictionary(g => g.Key, g => g.ToList());

                return printerList.Select(p =>
                {
                    var supplies = supplyByPrinter.TryGetValue(p.Id, out var s) ? s : [];
                    var printerAlerts = alertByPrinter.TryGetValue(p.Id, out var a) ? a : [];
                    return ToOverviewItem(p, supplies, printerAlerts, snapshot.Thresholds);
                }).ToList();
            }) ?? [];

            IEnumerable<PrinterOverviewItem> query = all;
            if (!string.IsNullOrWhiteSpace(location))
                query = query.Where(p => string.Equals(p.Location, location, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(department))
                query = query.Where(p => string.Equals(p.Department, department, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var kw = keyword.Trim();
                query = query.Where(p =>
                    p.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    p.Ip.Contains(kw, StringComparison.Ordinal) ||
                    (p.SerialNumber?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (p.AssetTag?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (!string.IsNullOrWhiteSpace(status) && status != "all")
                query = query.Where(i => i.State == status);

            return Results.Ok(query.ToList());
        }).RequireAuthorization();

        group.MapGet("/{id:int}", async (int id, IPrinterRepository printers) =>
        {
            var printer = await printers.GetByIdAsync(id);
            return printer == null ? Results.NotFound(new ApiError("打印机不存在")) : Results.Ok(PrinterView.From(printer));
        }).RequireAuthorization();

        group.MapGet("/{id:int}/detail", async (int id, IPrinterRepository printers, IMetricsRepository metrics,
            IAlertRepository alerts, ISecretProtector protector, int hours = 24) =>
        {
            var printer = await printers.GetByIdAsync(id);
            if (printer == null) return Results.NotFound(new ApiError("打印机不存在"));
            var supplies = await metrics.GetSuppliesAsync(id);
            var since = DateTime.UtcNow.AddHours(-Math.Clamp(hours <= 0 ? 24 : hours, 1, 24 * 30));
            var samples = await metrics.GetStatusSamplesAsync(id, since);
            var active = await alerts.GetActiveByPrinterAsync(id);
            var recent = await alerts.QueryAsync(printerId: id, limit: 50);
            var masked = protector.IsEncrypted(printer.CommunityCipher)
                ? "******（已加密存储）" : "******";
            return Results.Ok(new PrinterDetail(
                Printer: PrinterView.From(printer),
                CommunityMasked: masked,
                Supplies: supplies.Select(ToSupplyDto).ToList(),
                RecentSamples: samples,
                ActiveAlerts: active,
                RecentAlerts: recent));
        }).RequireAuthorization();

        group.MapGet("/{id:int}/forecast", async (int id, IPrinterRepository printers,
            IMetricsRepository metrics, IAppSettingsService settings, int? windowDays) =>
        {
            var printer = await printers.GetByIdAsync(id);
            if (printer == null) return Results.NotFound(new ApiError("打印机不存在"));
            var snapshot = await settings.GetSnapshotAsync();
            var w = Math.Clamp(windowDays ?? snapshot.Analysis.ForecastWindowDays, 2, 90);
            var now = DateTime.UtcNow;
            var supplies = await metrics.GetSuppliesAsync(id);
            var samples = await metrics.GetSupplySamplesAsync(id, null, now.AddDays(-w));
            var byIndex = samples.GroupBy(s => s.SupplyIndex)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.CollectedAt).ToList());
            var items = supplies.Select(s =>
            {
                byIndex.TryGetValue(s.SupplyIndex, out var ss);
                var f = SupplyForecaster.Forecast(s.Percent, ss ?? [], now, w);
                return new ForecastItem(id, printer.Name, printer.Location, s.SupplyIndex,
                    string.IsNullOrWhiteSpace(s.Name) ? $"耗材 #{s.SupplyIndex}" : s.Name!,
                    s.Percent, f.DailyConsumptionPercent, f.DaysLeft, f.EstimatedEmptyAt);
            })
            .OrderBy(x => x.DaysLeft ?? double.MaxValue)
            .ThenBy(x => x.Percent ?? 100)
            .ToList();
            return Results.Ok(new ForecastReport(w, items));
        }).RequireAuthorization();

        group.MapGet("/{id:int}/history", async (int id, IMetricsRepository metrics, IPrinterRepository printers,
            string metric = "status", int hours = 24, int? supplyIndex = null) =>
        {
            if (await printers.GetByIdAsync(id) == null) return Results.NotFound(new ApiError("打印机不存在"));
            var since = DateTime.UtcNow.AddHours(-Math.Clamp(hours <= 0 ? 24 : hours, 1, 24 * 30));
            if (string.Equals(metric, "supply", StringComparison.OrdinalIgnoreCase))
            {
                var samples = await metrics.GetSupplySamplesAsync(id, supplyIndex, since);
                return Results.Ok(samples.Select(s => new
                {
                    t = s.CollectedAt,
                    supplyIndex = s.SupplyIndex,
                    percent = s.Percent,
                    level = s.Level
                }));
            }
            var status = await metrics.GetStatusSamplesAsync(id, since);
            return Results.Ok(status.Select(s => new
            {
                t = s.CollectedAt,
                online = s.Online,
                hrStatus = s.HrStatus,
                pageCount = s.PageCount
            }));
        }).RequireAuthorization();

        group.MapPost("/", async (PrinterInput input, IPrinterRepository printers, ISecretProtector protector,
            IAuditService audit, System.Security.Claims.ClaimsPrincipal user, IMemoryCache cache) =>
        {
            var error = Validate(input);
            if (error != null) return Results.BadRequest(new ApiError(error));

            var community = input.Community?.Trim() ?? "";
            if (await printers.GetByEndpointAsync(input.Ip.Trim(), input.Port) != null)
                return Results.Conflict(new ApiError("该 IP:端口 已注册"));

            var printer = new Printer
            {
                Name = input.Name.Trim(),
                Ip = input.Ip.Trim(),
                Port = input.Port,
                CommunityCipher = protector.Protect(community),
                SnmpVersion = input.SnmpVersion,
                Location = input.Location?.Trim(),
                Department = input.Department?.Trim(),
                Enabled = input.Enabled,
                SupplyLowPercentOverride = input.LowSupplyPercentOverride,
                Model = input.Model?.Trim(),
                SerialNumber = input.SerialNumber?.Trim(),
                AssetTag = input.AssetTag?.Trim(),
                InstalledAt = input.InstalledAt,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            printer.Id = await printers.CreateAsync(printer);
            cache.Remove(CacheKey);
            await audit.RecordAsync(user.Identity?.Name, "printer.create", printer.Name, $"{printer.Ip}:{printer.Port}");
            return Results.Created($"/api/printers/{printer.Id}", PrinterView.From(printer));
        }).RequireAuthorization(AdminOnly);

        group.MapPut("/{id:int}", async (int id, PrinterInput input, IPrinterRepository printers,
            ISecretProtector protector, IAuditService audit, System.Security.Claims.ClaimsPrincipal user,
            IMemoryCache cache) =>
        {
            var printer = await printers.GetByIdAsync(id);
            if (printer == null) return Results.NotFound(new ApiError("打印机不存在"));
            var error = Validate(input);
            if (error != null) return Results.BadRequest(new ApiError(error));

            var existing = await printers.GetByEndpointAsync(input.Ip.Trim(), input.Port);
            if (existing != null && existing.Id != id)
                return Results.Conflict(new ApiError("该 IP:端口 已被其他打印机占用"));

            printer.Name = input.Name.Trim();
            printer.Ip = input.Ip.Trim();
            printer.Port = input.Port;
            if (!string.IsNullOrWhiteSpace(input.Community))
                printer.CommunityCipher = protector.Protect(input.Community.Trim()); // 留空保持原值
            printer.SnmpVersion = input.SnmpVersion;
            printer.Location = input.Location?.Trim();
            printer.Department = input.Department?.Trim();
            printer.Enabled = input.Enabled;
            printer.SupplyLowPercentOverride = input.LowSupplyPercentOverride;
            // 资产字段：序列号留空则交由采集自动回填（不覆盖已有值）
            printer.Model = input.Model?.Trim();
            if (!string.IsNullOrWhiteSpace(input.SerialNumber) || printer.SerialNumber == null)
                printer.SerialNumber = input.SerialNumber?.Trim();
            printer.AssetTag = input.AssetTag?.Trim();
            printer.InstalledAt = input.InstalledAt;
            printer.UpdatedAt = DateTime.UtcNow;
            await printers.UpdateAsync(printer);
            cache.Remove(CacheKey);
            await audit.RecordAsync(user.Identity?.Name, "printer.update", printer.Name, $"{printer.Ip}:{printer.Port}");
            return Results.Ok(PrinterView.From(printer));
        }).RequireAuthorization(AdminOnly);

        group.MapDelete("/{id:int}", async (int id, IPrinterRepository printers, IAuditService audit,
            System.Security.Claims.ClaimsPrincipal user, IMemoryCache cache) =>
        {
            var printer = await printers.GetByIdAsync(id);
            if (printer == null) return Results.NotFound(new ApiError("打印机不存在"));
            await printers.DeleteAsync(id);
            cache.Remove(CacheKey);
            await audit.RecordAsync(user.Identity?.Name, "printer.delete", printer.Name, $"{printer.Ip}:{printer.Port}");
            return Results.Ok(new { message = "已删除" });
        }).RequireAuthorization(AdminOnly);

        // SNMP 连通性测试（对未注册的打印机也可用）
        group.MapPost("/test", async (TestPrinterInput input, ISnmpCollector collector, ISecretProtector protector) =>
        {
            if (string.IsNullOrWhiteSpace(input.Ip)) return Results.BadRequest(new ApiError("IP 不能为空"));
            var result = await collector.TestAsync(new PrinterEndpoint(
                input.Ip.Trim(), input.Port <= 0 ? 161 : input.Port,
                input.Community?.Trim() ?? "public", input.SnmpVersion));
            return Results.Ok(result);
        }).RequireAuthorization(AdminOnly);

        // 立即采集一次
        group.MapPost("/{id:int}/poll", async (int id, IPollingCoordinator coordinator) =>
        {
            var outcome = await coordinator.PollPrinterNowAsync(id);
            if (outcome == null) return Results.NotFound(new ApiError("打印机不存在"));
            return Results.Ok(new { success = outcome.Success, error = outcome.Error });
        }).RequireAuthorization(AdminOnly);

        // 分组元数据（筛选用）
        group.MapGet("/meta/groups", async (IPrinterRepository printers) =>
        {
            var all = await printers.GetAllAsync();
            return Results.Ok(new
            {
                locations = all.Select(p => p.Location).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct().OrderBy(l => l).ToList(),
                departments = all.Select(p => p.Department).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().OrderBy(d => d).ToList()
            });
        }).RequireAuthorization();

        return app;
    }

    private static PrinterOverviewItem ToOverviewItem(Printer p, List<Supply> supplies, List<Alert> alerts,
        AlertThresholds thresholds)
    {
        var critical = alerts.Count(a => a.SeverityEnum == AlertSeverity.Critical);
        var offline = p.LastOnline == false || p.ConsecutiveFailures >= Math.Max(1, thresholds.OfflineConsecutiveFailures);
        var state = offline ? "offline"
            : critical > 0 ? "error"
            : alerts.Count > 0 ? "warn"
            : p.LastPollAt == null ? "unknown"
            : "ok";
        return new PrinterOverviewItem(
            Id: p.Id,
            Name: p.Name,
            Ip: p.Ip,
            Port: p.Port,
            Location: p.Location,
            Department: p.Department,
            Enabled: p.Enabled,
            SupplyLowPercentOverride: p.SupplyLowPercentOverride,
            State: state,
            HrStatusText: HrPrinterStatusValues.Describe(p.HrStatus),
            HrStatus: p.HrStatus,
            PageCount: p.PageCount,
            LastPollAt: p.LastPollAt,
            LastSuccessAt: p.LastSuccessAt,
            FailureCount: p.ConsecutiveFailures,
            Online: p.LastOnline != false,
            Supplies: supplies.Select(ToSupplyDto).ToList(),
            ActiveAlertCount: alerts.Count,
            CriticalAlertCount: critical,
            Model: p.Model,
            SerialNumber: p.SerialNumber,
            AssetTag: p.AssetTag,
            InstalledAt: p.InstalledAt);
    }

    private static SupplyDto ToSupplyDto(Supply s) => new(
        s.SupplyIndex, s.Name, s.SupplyType, s.Percent, s.Level, s.MaxCapacity, s.LowPercentThreshold);

    private static string? Validate(PrinterInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "名称不能为空";
        if (string.IsNullOrWhiteSpace(input.Ip)) return "IP 不能为空";
        if (!System.Net.IPAddress.TryParse(input.Ip.Trim(), out _)) return "IP 格式无效";
        if (input.Port is < 1 or > 65535) return "端口无效";
        if (input.SnmpVersion is not (1 or 2)) return "SNMP 版本仅支持 1 或 2c";
        if (input.LowSupplyPercentOverride is < 1 or > 99) return "耗材低阈值覆盖应在 1-99 之间";
        return null;
    }
}

public record TestPrinterInput(string Ip, int Port, string? Community, int SnmpVersion);
