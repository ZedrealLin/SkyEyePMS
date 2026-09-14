using Microsoft.Extensions.Logging;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Services;

namespace SkyEyePMS.Infrastructure.Services;

/// <summary>审计写入：失败只记日志，绝不影响业务主流程</summary>
public class AuditService : IAuditService
{
    private readonly IAuditRepository _repo;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IAuditRepository repo, ILogger<AuditService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task RecordAsync(string? username, string action, string? target = null, string? detail = null)
    {
        try
        {
            await _repo.AddAsync(new AuditLog
            {
                CreatedAt = DateTime.UtcNow,
                Username = string.IsNullOrWhiteSpace(username) ? "system" : username!.Trim(),
                Action = action,
                Target = Truncate(target, 200),
                Detail = Truncate(detail, 500)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "审计写入失败（action={Action} target={Target}）", action, target);
        }
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s[..max];
}
