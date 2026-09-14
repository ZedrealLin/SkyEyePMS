using Dapper;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Infrastructure.Database;

namespace SkyEyePMS.Infrastructure.Repositories;

public class AlertRepository : IAlertRepository
{
    private readonly IDbConnectionFactory _dbf;
    private readonly DbDialect _dialect;

    public AlertRepository(IDbConnectionFactory dbf)
    {
        _dbf = dbf;
        _dialect = DbDialect.For(dbf.Provider);
    }

    public async Task<IReadOnlyList<Alert>> GetActiveByPrinterAsync(int printerId)
    {
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<Alert>(
            "SELECT * FROM alerts WHERE printer_id = @printerId AND status = 'active'",
            new { printerId })).ToList();
    }

    public async Task<IReadOnlyList<Alert>> QueryAsync(string? status = null, int? printerId = null,
        int limit = 200, int offset = 0)
    {
        var where = new List<string>();
        var p = new DynamicParameters();
        if (!string.IsNullOrEmpty(status)) { where.Add("status = @status"); p.Add("status", status); }
        if (printerId.HasValue) { where.Add("printer_id = @printerId"); p.Add("printerId", printerId.Value); }
        var sql = "SELECT * FROM alerts" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                  " ORDER BY first_triggered_at DESC" + _dialect.LimitOffset(limit, offset);
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<Alert>(sql, p)).ToList();
    }

    public async Task<int> CountAsync(string? status = null)
    {
        var sql = "SELECT COUNT(*) FROM alerts" + (string.IsNullOrEmpty(status) ? "" : " WHERE status = @status");
        await using var conn = await _dbf.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(sql, new { status });
    }

    public async Task<int> CreateAsync(Alert a)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO alerts (printer_id, alert_key, type, severity, message, status,
                                first_triggered_at, last_notified_at, notify_count, resolved_at)
            VALUES (@PrinterId, @AlertKey, @Type, @Severity, @Message, @Status,
                    @FirstTriggeredAt, @LastNotifiedAt, @NotifyCount, @ResolvedAt)
            """, a);
        return await conn.ExecuteScalarAsync<int>(_dialect.LastInsertIdSql);
    }

    public async Task MarkNotifiedAsync(int id, DateTime at)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("""
            UPDATE alerts SET last_notified_at = @at,
                   notify_count = notify_count + 1
            WHERE id = @id
            """, new { id, at });
    }

    public async Task ResolveAsync(int id, DateTime at)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("""
            UPDATE alerts SET status = 'resolved', resolved_at = @at
            WHERE id = @id AND status = 'active'
            """, new { id, at });
    }

    public async Task AcknowledgeAsync(int id, string by, DateTime at)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE alerts SET acknowledged_at = @at, acknowledged_by = @by WHERE id = @id",
            new { id, by, at });
    }

    public async Task<int> CountFaultsAsync(int printerId, DateTime since)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM alerts
            WHERE printer_id = @printerId AND first_triggered_at >= @since
            """, new { printerId, since });
    }

    public async Task<int> DeleteOldResolvedAsync(DateTime olderThan)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.ExecuteAsync("""
            DELETE FROM alerts WHERE status = 'resolved' AND resolved_at < @olderThan
            """, new { olderThan });
    }

    public async Task<Alert?> GetByIdAsync(int id)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<Alert>("SELECT * FROM alerts WHERE id = @id", new { id });
    }

    public async Task InsertNotificationLogAsync(NotificationLogEntry e)
    {
        await using var conn = await _dbf.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO notification_log (alert_id, channel, success, error, sent_at)
            VALUES (@AlertId, @Channel, @Success, @Error, @SentAt)
            """, e);
    }

    public async Task<int> DeleteOldNotificationLogsAsync(DateTime olderThan)
    {
        await using var conn = await _dbf.OpenAsync();
        return await conn.ExecuteAsync("DELETE FROM notification_log WHERE sent_at < @olderThan", new { olderThan });
    }

    public async Task<IReadOnlyList<NotificationLogEntry>> GetNotificationLogsAsync(int alertId)
    {
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<NotificationLogEntry>(
            "SELECT * FROM notification_log WHERE alert_id = @alertId ORDER BY sent_at DESC",
            new { alertId })).ToList();
    }

    public async Task<IReadOnlyList<NotificationLogEntry>> GetNotificationLogsSinceAsync(DateTime since)
    {
        await using var conn = await _dbf.OpenAsync();
        return (await conn.QueryAsync<NotificationLogEntry>(
            "SELECT * FROM notification_log WHERE sent_at >= @since", new { since })).ToList();
    }
}
