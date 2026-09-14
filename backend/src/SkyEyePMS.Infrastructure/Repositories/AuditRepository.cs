using Dapper;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Infrastructure.Database;

namespace SkyEyePMS.Infrastructure.Repositories;

public class AuditRepository : IAuditRepository
{
    private readonly IDbConnectionFactory _dbf;

    public AuditRepository(IDbConnectionFactory dbf) => _dbf = dbf;

    public async Task AddAsync(AuditLog e)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO audit_logs (created_at, username, action, target, detail)
            VALUES (@CreatedAt, @Username, @Action, @Target, @Detail)
            """, e);
    }

    public async Task<IReadOnlyList<AuditLog>> QueryAsync(string? action = null, string? username = null,
        int limit = 100, int offset = 0)
    {
        var where = new List<string>();
        var p = new DynamicParameters();
        if (!string.IsNullOrEmpty(action)) { where.Add("action = @action"); p.Add("action", action); }
        if (!string.IsNullOrEmpty(username)) { where.Add("username = @username"); p.Add("username", username); }
        var dialect = DbDialect.For(_dbf.Provider);
        var sql = "SELECT * FROM audit_logs" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                  " ORDER BY id DESC" + dialect.LimitOffset(Math.Clamp(limit, 1, 500), Math.Max(0, offset));
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<AuditLog>(sql, p)).ToList();
    }

    public async Task<int> DeleteOldAsync(DateTime olderThan)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.ExecuteAsync("DELETE FROM audit_logs WHERE created_at < @olderThan", new { olderThan });
    }
}
