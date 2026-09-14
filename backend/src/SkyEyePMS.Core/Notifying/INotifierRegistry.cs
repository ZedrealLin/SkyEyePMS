namespace SkyEyePMS.Core.Notifying;

/// <summary>按当前配置构建启用的通知渠道实例（每次告警轮询时获取，配置改动即时生效）</summary>
public interface INotifierRegistry
{
    Task<IReadOnlyList<INotifier>> GetEnabledAsync();

    /// <summary>构建指定渠道实例（用于发送测试消息），渠道不存在返回 null</summary>
    Task<INotifier?> GetAsync(string channel);
}
