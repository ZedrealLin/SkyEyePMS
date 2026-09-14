using Dapper;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Infrastructure.Database;

namespace SkyEyePMS.Infrastructure.Repositories;

public class PrinterRepository : IPrinterRepository
{
    private readonly IDbConnectionFactory _dbf;
    private readonly DbDialect _dialect;

    public PrinterRepository(IDbConnectionFactory dbf)
    {
        _dbf = dbf;
        _dialect = DbDialect.For(dbf.Provider);
    }

    public async Task<IReadOnlyList<Printer>> GetAllAsync(bool onlyEnabled = false)
    {
        await using var conn = await _dbf.OpenAsync();
        var sql = onlyEnabled
            ? "SELECT * FROM printers WHERE enabled = 1 ORDER BY location, department, name"
            : "SELECT * FROM printers ORDER BY location, department, name";
        return (await conn.QueryAsync<Printer>(sql)).ToList();
    }

    public async Task<Printer?> GetByIdAsync(int id)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<Printer>("SELECT * FROM printers WHERE id = @id", new { id });
    }

    public async Task<Printer?> GetByEndpointAsync(string ip, int port)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<Printer>(
            "SELECT * FROM printers WHERE ip = @ip AND port = @port", new { ip, port });
    }

    public async Task<int> CreateAsync(Printer p)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO printers (name, ip, port, community_cipher, snmp_version, location, department, enabled,
                                  supply_low_percent_override, model, serial_number, asset_tag, installed_at,
                                  consecutive_failures, created_at, updated_at)
            VALUES (@Name, @Ip, @Port, @CommunityCipher, @SnmpVersion, @Location, @Department, @Enabled,
                    @SupplyLowPercentOverride, @Model, @SerialNumber, @AssetTag, @InstalledAt,
                    0, @CreatedAt, @UpdatedAt)
            """, p);
        return await conn.ExecuteScalarAsync<int>(_dialect.LastInsertIdSql);
    }

    public async Task UpdateAsync(Printer p)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("""
            UPDATE printers SET name = @Name, ip = @Ip, port = @Port, community_cipher = @CommunityCipher,
                   snmp_version = @SnmpVersion, location = @Location, department = @Department,
                   enabled = @Enabled, supply_low_percent_override = @SupplyLowPercentOverride,
                   model = @Model, serial_number = @SerialNumber, asset_tag = @AssetTag, installed_at = @InstalledAt,
                   updated_at = @UpdatedAt
            WHERE id = @Id
            """, p);
    }

    public async Task UpdateRuntimeAsync(Printer p)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("""
            UPDATE printers SET consecutive_failures = @ConsecutiveFailures, last_poll_at = @LastPollAt,
                   last_success_at = @LastSuccessAt, last_online = @LastOnline, hr_status = @HrStatus,
                   status_changed_at = @StatusChangedAt, page_count = @PageCount,
                   sys_name = @SysName, sys_descr = @SysDescr, serial_number = @SerialNumber
            WHERE id = @Id
            """, p);
    }

    public async Task UpdateRuntimeFailureAsync(int id, int consecutiveFailures, DateTime at, bool? online)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("""
            UPDATE printers SET consecutive_failures = @consecutiveFailures, last_poll_at = @at,
                   last_online = @online WHERE id = @id
            """, new { id, consecutiveFailures, at, online });
    }

    public async Task DeleteAsync(int id)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("DELETE FROM printers WHERE id = @id", new { id });
    }
}
