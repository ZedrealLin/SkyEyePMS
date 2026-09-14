using SkyEyePMS.Core.Collecting;
using SkyEyePMS.Core.Services;
using SkyEyePMS.Infrastructure.Collecting;

namespace SkyEyePMS.Api.Endpoints;

public static class DiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/discovery").WithTags("Discovery");

        // 发起网段扫描：立即返回 jobId，扫描后台执行，用 GET /scan/{jobId} 轮询进度与命中
        group.MapPost("/scan", async (ScanInput input, ISnmpCollector collector,
            DiscoveryJobStore store, IAuditService audit, System.Security.Claims.ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(input.StartIp) || string.IsNullOrWhiteSpace(input.EndIp))
                return Results.BadRequest(new ApiError("起始/结束 IP 不能为空"));
            if (!System.Net.IPAddress.TryParse(input.StartIp, out _) ||
                !System.Net.IPAddress.TryParse(input.EndIp, out _))
                return Results.BadRequest(new ApiError("IP 格式无效"));

            var timeout = Math.Clamp(input.TimeoutMs <= 0 ? 1200 : input.TimeoutMs, 200, 10000);
            var port = input.Port <= 0 ? 161 : input.Port;
            var community = string.IsNullOrWhiteSpace(input.Community) ? "public" : input.Community.Trim();

            // 计算区间大小（与采集器实现同规则）
            if (!System.Net.IPAddress.TryParse(input.StartIp.Trim(), out var s) ||
                !System.Net.IPAddress.TryParse(input.EndIp.Trim(), out var e))
                return Results.BadRequest(new ApiError("IP 格式无效"));
            var sb = s.GetAddressBytes();
            var eb = e.GetAddressBytes();
            uint a = (uint)(sb[0] << 24 | sb[1] << 16 | sb[2] << 8 | sb[3]);
            uint b = (uint)(eb[0] << 24 | eb[1] << 16 | eb[2] << 8 | eb[3]);
            if (a > b) (a, b) = (b, a);
            var total = b - a + 1;
            if (total > 4096) return Results.BadRequest(new ApiError("单次扫描最多 4096 个地址"));

            var job = store.Create((int)total);
            _ = Task.Run(async () =>
            {
                try
                {
                    var progress = new Progress<(int Scanned, int Total)>(p =>
                        System.Threading.Interlocked.Exchange(ref job.Scanned, p.Scanned));
                    var hits = await collector.DiscoverAsync(
                        input.StartIp.Trim(), input.EndIp.Trim(), port, community,
                        input.SnmpVersion, timeout, progress);
                    job.AddHits(hits);
                    System.Threading.Volatile.Write(ref job.Scanned, job.Total);
                    job.Done = true;
                }
                catch (Exception ex)
                {
                    job.Error = ex.Message;
                    job.Done = true;
                }
            });
            await audit.RecordAsync(user.Identity?.Name, "discovery.scan",
                $"{input.StartIp.Trim()}-{input.EndIp.Trim()}:{port}", $"{total} 个地址");
            return Results.Ok(new { jobId = job.Id, total });
        }).RequireAuthorization("AdminOnly");

        // 轮询扫描进度与命中结果
        group.MapGet("/scan/{jobId}", (string jobId, DiscoveryJobStore store) =>
        {
            var job = store.Get(jobId);
            if (job == null) return Results.NotFound(new ApiError("扫描任务不存在或已过期"));
            var (scanned, total, done, error, hits) = job.Snapshot();
            return Results.Ok(new
            {
                jobId,
                scanned,
                total,
                done,
                error,
                hits = hits.Select(h => new { h.Ip, h.SysName, h.SysDescr })
            });
        }).RequireAuthorization("AdminOnly");

        return app;
    }
}

public record ScanInput(string StartIp, string EndIp, int Port, string? Community, int SnmpVersion, int TimeoutMs);
