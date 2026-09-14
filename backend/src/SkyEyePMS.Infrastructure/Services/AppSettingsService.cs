using System.Text.Json;
using System.Text.Json.Nodes;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Services;

namespace SkyEyePMS.Infrastructure.Services;

/// <summary>阈值/保留策略/分析采样/轮询参数存 app_settings（JSON），按属性合并到默认值上</summary>
public class AppSettingsService : IAppSettingsService
{
    public const string ThresholdsKey = "thresholds";
    public const string RetentionKey = "retention";
    public const string AnalysisKey = "analysis";
    public const string SamplingKey = "sampling";
    public const string PollingKey = "polling";
    public const string ReportPushKey = "reportPush";

    private readonly IAppSettingsRepository _repo;

    public AppSettingsService(IAppSettingsRepository repo) => _repo = repo;

    public async Task<SettingsSnapshot> GetSnapshotAsync()
    {
        var snapshot = new SettingsSnapshot();
        await MergeInto(snapshot.Thresholds, ThresholdsKey);
        await MergeInto(snapshot.Retention, RetentionKey);
        await MergeInto(snapshot.Analysis, AnalysisKey);
        await MergeInto(snapshot.Sampling, SamplingKey);
        await MergeInto(snapshot.Polling, PollingKey);
        await MergeInto(snapshot.ReportPush, ReportPushKey);
        return snapshot;
    }

    public async Task SaveThresholdsAsync(AlertThresholds thresholds)
    {
        await _repo.SetAsync(ThresholdsKey, JsonSerializer.Serialize(thresholds));
    }

    public async Task SaveRetentionAsync(RetentionOptions retention)
    {
        await _repo.SetAsync(RetentionKey, JsonSerializer.Serialize(retention));
    }

    public async Task SaveAnalysisAsync(AnalysisOptions analysis)
    {
        await _repo.SetAsync(AnalysisKey, JsonSerializer.Serialize(analysis));
    }

    public async Task SaveSamplingAsync(SamplingOptions sampling)
    {
        await _repo.SetAsync(SamplingKey, JsonSerializer.Serialize(sampling));
    }

    public async Task SavePollingAsync(PollingSettings polling)
    {
        await _repo.SetAsync(PollingKey, JsonSerializer.Serialize(polling));
    }

    public async Task SaveReportPushAsync(ReportPushOptions reportPush)
    {
        await _repo.SetAsync(ReportPushKey, JsonSerializer.Serialize(reportPush));
    }

    private async Task MergeInto<T>(T target, string key) where T : class
    {
        var json = await _repo.GetAsync(key);
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var defaults = JsonSerializer.SerializeToNode(target) as JsonObject;
            var stored = JsonNode.Parse(json) as JsonObject;
            if (defaults == null || stored == null) return;
            foreach (var (name, node) in stored)
                defaults[name] = node?.DeepClone();
            var merged = defaults.Deserialize<T>();
            if (merged == null) return;
            foreach (var prop in typeof(T).GetProperties())
                prop.SetValue(target, prop.GetValue(merged));
        }
        catch (JsonException)
        {
            // 配置损坏时使用默认值
        }
    }
}
