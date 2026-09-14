using Dapper;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Infrastructure.Database;

namespace SkyEyePMS.Infrastructure.Repositories;

public class MetricsRepository : IMetricsRepository
{
    private readonly IDbConnectionFactory _dbf;
    private readonly DbDialect _dialect;

    public MetricsRepository(IDbConnectionFactory dbf)
    {
        _dbf = dbf;
        _dialect = DbDialect.For(dbf.Provider);
    }

    public async Task InsertStatusSampleAsync(StatusSample s)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO status_samples (printer_id, collected_at, online, hr_status, page_count)
            VALUES (@PrinterId, @CollectedAt, @Online, @HrStatus, @PageCount)
            """, s);
    }

    public async Task<IReadOnlyList<StatusSample>> GetStatusSamplesAsync(int printerId, DateTime since)
    {
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<StatusSample>("""
            SELECT * FROM status_samples
            WHERE printer_id = @printerId AND collected_at >= @since
            ORDER BY collected_at
            """, new { printerId, since })).ToList();
    }

    public async Task<IReadOnlyList<StatusSample>> GetAllStatusSamplesAsync(DateTime since)
    {
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<StatusSample>(
            "SELECT * FROM status_samples WHERE collected_at >= @since ORDER BY printer_id, collected_at",
            new { since })).ToList();
    }

    public async Task<int> DeleteOldStatusSamplesAsync(DateTime olderThan)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.ExecuteAsync("DELETE FROM status_samples WHERE collected_at < @olderThan",
            new { olderThan });
    }

    public async Task UpsertSupplyAsync(Supply s)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync(_dialect.UpsertSupplySql, s);
    }

    public async Task<IReadOnlyList<Supply>> GetSuppliesAsync(int printerId)
    {
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<Supply>(
            "SELECT * FROM supplies WHERE printer_id = @printerId ORDER BY supply_index",
            new { printerId })).ToList();
    }

    public async Task<IReadOnlyList<Supply>> GetAllSuppliesAsync()
    {
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<Supply>("SELECT * FROM supplies ORDER BY printer_id, supply_index")).ToList();
    }

    public async Task DeleteSuppliesNotInAsync(int printerId, IEnumerable<int> keepIndexes)
    {
        var keep = keepIndexes.ToList();
        await using var conn = await _dbf.OpenAsync();
        if (keep.Count == 0)
        {
            await conn.ExecuteAsync("DELETE FROM supplies WHERE printer_id = @printerId", new { printerId });
            return;
        }
        // 少量索引（一般 < 20），直接拼接参数
        var names = keep.Select((_, i) => $"@k{i}").ToList();
        var p = new DynamicParameters();
        p.Add("printerId", printerId);
        for (var i = 0; i < keep.Count; i++) p.Add($"k{i}", keep[i]);
        await conn.ExecuteAsync(
            $"DELETE FROM supplies WHERE printer_id = @printerId AND supply_index NOT IN ({string.Join(",", names)})", p);
    }

    public async Task InsertSupplySampleAsync(SupplySample s)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO supply_samples (printer_id, supply_index, percent, level, collected_at)
            VALUES (@PrinterId, @SupplyIndex, @Percent, @Level, @CollectedAt)
            """, s);
    }

    public async Task<SupplySample?> GetLastSupplySampleAsync(int printerId, int supplyIndex)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<SupplySample>("""
            SELECT * FROM supply_samples
            WHERE printer_id = @printerId AND supply_index = @supplyIndex
            ORDER BY collected_at DESC
            """ + _dialect.LimitOffset(1, 0), new { printerId, supplyIndex });
    }

    public async Task<IReadOnlyList<SupplySample>> GetSupplySamplesAsync(int printerId, int? supplyIndex, DateTime since)
    {
        var where = "printer_id = @printerId AND collected_at >= @since";
        var p = new DynamicParameters();
        p.Add("printerId", printerId);
        p.Add("since", since);
        if (supplyIndex.HasValue) { where += " AND supply_index = @supplyIndex"; p.Add("supplyIndex", supplyIndex.Value); }
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<SupplySample>(
            $"SELECT * FROM supply_samples WHERE {where} ORDER BY collected_at", p)).ToList();
    }

    public async Task<IReadOnlyList<SupplySample>> GetAllSupplySamplesAsync(DateTime since)
    {
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<SupplySample>(
            "SELECT * FROM supply_samples WHERE collected_at >= @since ORDER BY printer_id, supply_index, collected_at",
            new { since })).ToList();
    }

    public async Task<int> DeleteOldSupplySamplesAsync(DateTime olderThan)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.ExecuteAsync("DELETE FROM supply_samples WHERE collected_at < @olderThan",
            new { olderThan });
    }

    public async Task UpsertDailyStatAsync(DailyStat stat)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync(_dialect.UpsertDailyStatSql, new
        {
            stat.PrinterId,
            StatDate = stat.StatDate.ToString("yyyy-MM-dd"),
            stat.PagesPrinted,
            stat.OnlineSamples,
            stat.TotalSamples
        });
    }

    public async Task<IReadOnlyList<DailyStat>> GetDailyStatsAsync(DateTime since)
    {
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<DailyStat>(
            "SELECT printer_id AS PrinterId, stat_date AS StatDate, pages_printed AS PagesPrinted, " +
            "online_samples AS OnlineSamples, total_samples AS TotalSamples " +
            "FROM daily_stats WHERE stat_date >= @since ORDER BY stat_date",
            new { since = since.ToString("yyyy-MM-dd") })).ToList();
    }
}
