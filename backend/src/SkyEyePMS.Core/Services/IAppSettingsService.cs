using SkyEyePMS.Core.Models;

namespace SkyEyePMS.Core.Services;

/// <summary>审计记录（写入失败不影响业务，由实现内部吞异常）</summary>
public interface IAuditService
{
    Task RecordAsync(string? username, string action, string? target = null, string? detail = null);
}

/// <summary>运行期可调设置（存数据库 app_settings，管理员经 API 修改，轮询服务每轮读取）</summary>
public interface IAppSettingsService
{
    Task<SettingsSnapshot> GetSnapshotAsync();
    Task SaveThresholdsAsync(AlertThresholds thresholds);
    Task SaveRetentionAsync(RetentionOptions retention);
    Task SaveAnalysisAsync(AnalysisOptions analysis);
    Task SaveSamplingAsync(SamplingOptions sampling);
    Task SavePollingAsync(PollingSettings polling);
    Task SaveReportPushAsync(ReportPushOptions reportPush);
}
