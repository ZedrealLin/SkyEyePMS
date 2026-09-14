using Microsoft.Extensions.Logging;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Notifying;

namespace SkyEyePMS.Infrastructure.Notifying;

/// <summary>根据当前生效配置构建通知渠道实例（每次调用都读取最新配置，管理页改动即时生效）</summary>
public class NotifierRegistry : INotifierRegistry
{
    private readonly NotifierConfigService _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;

    public NotifierRegistry(NotifierConfigService config, IHttpClientFactory httpFactory, ILoggerFactory loggerFactory)
    {
        _config = config;
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
    }

    public async Task<IReadOnlyList<INotifier>> GetEnabledAsync()
    {
        var options = await _config.GetEffectiveAsync();
        var list = new List<INotifier>();
        AddIfEnabled(list, new FeishuNotifier(options.Feishu, _httpFactory, _loggerFactory.CreateLogger<FeishuNotifier>()));
        AddIfEnabled(list, new DingTalkNotifier(options.DingTalk, _httpFactory, _loggerFactory.CreateLogger<DingTalkNotifier>()));
        AddIfEnabled(list, new WeComNotifier(options.WeCom, _httpFactory, _loggerFactory.CreateLogger<WeComNotifier>()));
        AddIfEnabled(list, new EmailNotifier(options.Email, _loggerFactory.CreateLogger<EmailNotifier>()));
        AddIfEnabled(list, new GenericWebhookNotifier(options.Webhook, _httpFactory, _loggerFactory.CreateLogger<GenericWebhookNotifier>()));
        return list;
    }

    public async Task<INotifier?> GetAsync(string channel)
    {
        var options = await _config.GetEffectiveAsync();
        return channel.ToLowerInvariant() switch
        {
            "feishu" => new FeishuNotifier(options.Feishu, _httpFactory, _loggerFactory.CreateLogger<FeishuNotifier>()),
            "dingtalk" => new DingTalkNotifier(options.DingTalk, _httpFactory, _loggerFactory.CreateLogger<DingTalkNotifier>()),
            "wecom" => new WeComNotifier(options.WeCom, _httpFactory, _loggerFactory.CreateLogger<WeComNotifier>()),
            "email" => new EmailNotifier(options.Email, _loggerFactory.CreateLogger<EmailNotifier>()),
            "webhook" => new GenericWebhookNotifier(options.Webhook, _httpFactory, _loggerFactory.CreateLogger<GenericWebhookNotifier>()),
            _ => null
        };
    }

    private static void AddIfEnabled(List<INotifier> list, INotifier notifier)
    {
        if (notifier.IsConfigured) list.Add(notifier);
    }
}

/// <summary>
/// 告警推送分发：对每条通知并行调用所有启用渠道，单渠道失败不影响其它渠道，
/// 每个渠道的推送结果写入 notification_log。
/// </summary>
public class AlertDispatcher
{
    private readonly INotifierRegistry _registry;
    private readonly Core.Repositories.IAlertRepository _alerts;
    private readonly ILogger<AlertDispatcher> _logger;
    private readonly string _systemName;

    public AlertDispatcher(INotifierRegistry registry, Core.Repositories.IAlertRepository alerts,
        ILogger<AlertDispatcher> logger, string? systemName = null)
    {
        _registry = registry;
        _alerts = alerts;
        _logger = logger;
        _systemName = string.IsNullOrWhiteSpace(systemName) ? "SkyEyePMS" : systemName!;
    }

    public async Task<int> DispatchAsync(Alert alert, Printer printer, AlertEventKind kind, CancellationToken ct = default)
    {
        var notifiers = await _registry.GetEnabledAsync();
        if (notifiers.Count == 0) return 0;

        var notification = new AlertNotification(kind, alert, printer, _systemName);
        var nowLocal = DateTime.Now;
        var tasks = notifiers
            .Where(n => NotificationRouting.ShouldSend(n.Routing, alert.SeverityEnum, nowLocal))
            .Select(async n =>
            {
                (bool success, string? error) = await n.SendAsync(notification, ct);
                _logger.LogInformation("告警 #{AlertId} 推送渠道 {Channel}: {Result}", alert.Id, n.Channel,
                    success ? "成功" : $"失败（{error}）");
                await _alerts.InsertNotificationLogAsync(new NotificationLogEntry
                {
                    AlertId = alert.Id,
                    Channel = n.Channel,
                    Success = success,
                    Error = error,
                    SentAt = DateTime.UtcNow
                });
                return success;
            });
        var results = await Task.WhenAll(tasks);
        return results.Count(r => r);
    }
}
