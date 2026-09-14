using Dapper;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Infrastructure.Database;

namespace SkyEyePMS.Infrastructure.Repositories;

public class AppSettingsRepository : IAppSettingsRepository
{
    private readonly IDbConnectionFactory _dbf;

    public AppSettingsRepository(IDbConnectionFactory dbf) => _dbf = dbf;

    public async Task<string?> GetAsync(string key)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT svalue FROM app_settings WHERE skey = @key", new { key });
    }

    public async Task SetAsync(string key, string value)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync(_dbf.Provider == DatabaseProvider.MySql
            ? """
              INSERT INTO app_settings (skey, svalue, updated_at) VALUES (@key, @value, @now)
              ON DUPLICATE KEY UPDATE svalue = VALUES(svalue), updated_at = VALUES(updated_at)
              """
            : """
              INSERT INTO app_settings (skey, svalue, updated_at) VALUES (@key, @value, @now)
              ON CONFLICT(skey) DO UPDATE SET svalue = excluded.svalue, updated_at = excluded.updated_at
              """,
            new { key, value, now = DateTime.UtcNow });
    }
}
