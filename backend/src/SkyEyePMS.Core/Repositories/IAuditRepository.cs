using SkyEyePMS.Core.Models;

namespace SkyEyePMS.Core.Repositories;

public interface IAuditRepository
{
    Task AddAsync(AuditLog entry);
    Task<IReadOnlyList<AuditLog>> QueryAsync(string? action = null, string? username = null,
        int limit = 100, int offset = 0);
    Task<int> DeleteOldAsync(DateTime olderThan);
}
