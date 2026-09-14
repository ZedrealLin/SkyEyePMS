using SkyEyePMS.Core.Models;

namespace SkyEyePMS.Core.Repositories;

public interface IUserRepository
{
    Task<User?> GetByUsernameAsync(string username);
    Task<User?> GetByIdAsync(int id);
    Task<IReadOnlyList<User>> GetAllAsync();
    Task<int> CreateAsync(User user);
    Task UpdatePasswordAsync(int id, string passwordHash);
    Task UpdateLastLoginAsync(int id, DateTime at);
    Task UpdateTotpAsync(int id, string? secretCipher, bool enabled);
    Task DeleteAsync(int id);
    Task<int> CountAsync();
}

public interface IPrinterRepository
{
    Task<IReadOnlyList<Printer>> GetAllAsync(bool onlyEnabled = false);
    Task<Printer?> GetByIdAsync(int id);
    Task<Printer?> GetByEndpointAsync(string ip, int port);
    Task<int> CreateAsync(Printer printer);
    Task UpdateAsync(Printer printer);
    Task UpdateRuntimeAsync(Printer printer);
    Task DeleteAsync(int id);
    Task UpdateRuntimeFailureAsync(int id, int consecutiveFailures, DateTime at, bool? online);
}

public interface IAlertRepository
{
    Task<IReadOnlyList<Alert>> GetActiveByPrinterAsync(int printerId);
    Task<IReadOnlyList<Alert>> QueryAsync(string? status = null, int? printerId = null, int limit = 200, int offset = 0);
    Task<int> CountAsync(string? status = null);
    Task<int> CreateAsync(Alert alert);
    Task MarkNotifiedAsync(int id, DateTime at);
    Task ResolveAsync(int id, DateTime at);
    Task AcknowledgeAsync(int id, string by, DateTime at);
    Task<int> CountFaultsAsync(int printerId, DateTime since);
    Task<int> DeleteOldResolvedAsync(DateTime olderThan);
    Task<Alert?> GetByIdAsync(int id);
    Task InsertNotificationLogAsync(NotificationLogEntry entry);
    Task<int> DeleteOldNotificationLogsAsync(DateTime olderThan);
    Task<IReadOnlyList<NotificationLogEntry>> GetNotificationLogsAsync(int alertId);
    Task<IReadOnlyList<NotificationLogEntry>> GetNotificationLogsSinceAsync(DateTime since);
}

public interface IMetricsRepository
{
    // 状态采样
    Task InsertStatusSampleAsync(StatusSample sample);
    Task<IReadOnlyList<StatusSample>> GetStatusSamplesAsync(int printerId, DateTime since);
    Task<IReadOnlyList<StatusSample>> GetAllStatusSamplesAsync(DateTime since);
    Task<int> DeleteOldStatusSamplesAsync(DateTime olderThan);

    // 耗材当前值
    Task UpsertSupplyAsync(Supply supply);
    Task<IReadOnlyList<Supply>> GetSuppliesAsync(int printerId);
    Task<IReadOnlyList<Supply>> GetAllSuppliesAsync();
    Task DeleteSuppliesNotInAsync(int printerId, IEnumerable<int> keepIndexes);

    // 耗材历史
    Task InsertSupplySampleAsync(SupplySample sample);
    Task<SupplySample?> GetLastSupplySampleAsync(int printerId, int supplyIndex);
    Task<IReadOnlyList<SupplySample>> GetSupplySamplesAsync(int printerId, int? supplyIndex, DateTime since);
    Task<IReadOnlyList<SupplySample>> GetAllSupplySamplesAsync(DateTime since);
    Task<int> DeleteOldSupplySamplesAsync(DateTime olderThan);

    // 日粒度统计（年度趋势）
    Task UpsertDailyStatAsync(DailyStat stat);
    Task<IReadOnlyList<DailyStat>> GetDailyStatsAsync(DateTime since);
}

public interface IAppSettingsRepository
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
}
