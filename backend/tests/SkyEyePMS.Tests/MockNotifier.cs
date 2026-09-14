using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Notifying;

namespace SkyEyePMS.Tests;

/// <summary>测试用通知渠道：记录推送，可模拟失败</summary>
public class MockNotifier(string channel, bool success = true) : INotifier
{
    public List<AlertNotification> Sent { get; } = new();
    public string Channel { get; } = channel;
    public bool IsConfigured => true;

    public Task<(bool Success, string? Error)> SendAsync(AlertNotification notification, CancellationToken ct = default)
    {
        lock (Sent) Sent.Add(notification);
        return Task.FromResult((Success, (string?)Error));
    }

    public bool Success { get; set; } = success;
    public string? Error { get; set; } = success ? null : "模拟发送失败";
}

public class MockNotifierRegistry(params MockNotifier[] notifiers) : INotifierRegistry
{
    public MockNotifier[] Notifiers { get; } = notifiers;

    public Task<IReadOnlyList<INotifier>> GetEnabledAsync() =>
        Task.FromResult<IReadOnlyList<INotifier>>(Notifiers);

    public Task<INotifier?> GetAsync(string channel) =>
        Task.FromResult<INotifier?>(Notifiers.FirstOrDefault(n => n.Channel == channel));
}
