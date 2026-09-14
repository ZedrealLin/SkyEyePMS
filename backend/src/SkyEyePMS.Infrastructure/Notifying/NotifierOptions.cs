using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SkyEyePMS.Core.Notifying;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Security;

namespace SkyEyePMS.Infrastructure.Notifying;

// ---------- 各渠道的配置模型（appsettings "Notifiers" 节 + 数据库覆盖） ----------
// 每个渠道都可独立配置推送路由：MinSeverity（最低级别）与免打扰时段（时段内仅推 Critical）

public class FeishuOptions
{
    public bool Enabled { get; set; }
    public string WebhookUrl { get; set; } = string.Empty;
    /// <summary>签名校验密钥（未开启签名校验则留空）</summary>
    public string Secret { get; set; } = string.Empty;
    /// <summary>最低推送级别：Info / Warning / Critical</summary>
    public string MinSeverity { get; set; } = "Info";
    public bool QuietHoursEnabled { get; set; }
    public string QuietHoursStart { get; set; } = "22:00";
    public string QuietHoursEnd { get; set; } = "08:00";

    public ChannelRouting Routing => new()
    {
        MinSeverity = MinSeverity,
        QuietHoursEnabled = QuietHoursEnabled,
        QuietHoursStart = QuietHoursStart,
        QuietHoursEnd = QuietHoursEnd
    };
}

public class DingTalkOptions
{
    public bool Enabled { get; set; }
    public string WebhookUrl { get; set; } = string.Empty;
    /// <summary>加签密钥（SEC 开头）；用关键词校验则留空</summary>
    public string Secret { get; set; } = string.Empty;
    public string MinSeverity { get; set; } = "Info";
    public bool QuietHoursEnabled { get; set; }
    public string QuietHoursStart { get; set; } = "22:00";
    public string QuietHoursEnd { get; set; } = "08:00";

    public ChannelRouting Routing => new()
    {
        MinSeverity = MinSeverity,
        QuietHoursEnabled = QuietHoursEnabled,
        QuietHoursStart = QuietHoursStart,
        QuietHoursEnd = QuietHoursEnd
    };
}

public class WeComOptions
{
    public bool Enabled { get; set; }
    public string WebhookUrl { get; set; } = string.Empty;
    public string MinSeverity { get; set; } = "Info";
    public bool QuietHoursEnabled { get; set; }
    public string QuietHoursStart { get; set; } = "22:00";
    public string QuietHoursEnd { get; set; } = "08:00";

    public ChannelRouting Routing => new()
    {
        MinSeverity = MinSeverity,
        QuietHoursEnabled = QuietHoursEnabled,
        QuietHoursStart = QuietHoursStart,
        QuietHoursEnd = QuietHoursEnd
    };
}

/// <summary>通用 Webhook 渠道：POST JSON 到任意地址，用于对接 ITSM / n8n / 自建系统</summary>
public class WebhookOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = string.Empty;
    /// <summary>可选鉴权令牌，通过 X-SkyEye-Token 请求头携带（存储时加密）</summary>
    public string Secret { get; set; } = string.Empty;
}

public class EmailOptions
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 465;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    /// <summary>SMTP 密码/授权码（存储时加密）</summary>
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "SkyEyePMS";
    public List<string> ToAddresses { get; set; } = new();
    public string MinSeverity { get; set; } = "Info";
    public bool QuietHoursEnabled { get; set; }
    public string QuietHoursStart { get; set; } = "22:00";
    public string QuietHoursEnd { get; set; } = "08:00";

    public ChannelRouting Routing => new()
    {
        MinSeverity = MinSeverity,
        QuietHoursEnabled = QuietHoursEnabled,
        QuietHoursStart = QuietHoursStart,
        QuietHoursEnd = QuietHoursEnd
    };
}

public class NotifierOptions
{
    public FeishuOptions Feishu { get; set; } = new();
    public DingTalkOptions DingTalk { get; set; } = new();
    public WeComOptions WeCom { get; set; } = new();
    public EmailOptions Email { get; set; } = new();
    public WebhookOptions Webhook { get; set; } = new();
}

/// <summary>
/// 通知渠道配置合并：appsettings.json 为默认，数据库 app_settings["notifiers"] 覆盖（管理页保存）。
/// Secret/Password 等敏感字段在写库前加密、读库后解密；API 读取时以掩码返回。
/// </summary>
public class NotifierConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web); // camelCase
    public const string SettingsKey = "notifiers";
    /// <summary>API 返回配置时敏感字段的掩码；保存时收到该值或空串则保留原值</summary>
    public const string Mask = "******";
    private static readonly string[] SensitiveFields = ["Secret", "Password"];

    private readonly IOptions<NotifierOptions> _fileOptions;
    private readonly IAppSettingsRepository _settings;
    private readonly ISecretProtector _protector;

    public NotifierConfigService(IOptions<NotifierOptions> fileOptions,
        IAppSettingsRepository settings, ISecretProtector protector)
    {
        _fileOptions = fileOptions;
        _settings = settings;
        _protector = protector;
    }

    /// <summary>合并文件配置与数据库配置，返回已解密的实际生效配置</summary>
    public async Task<NotifierOptions> GetEffectiveAsync()
    {
        var merged = JsonSerializer.SerializeToNode(_fileOptions.Value, JsonOpts) as JsonObject ?? new JsonObject();
        var stored = await _settings.GetAsync(SettingsKey);
        if (!string.IsNullOrEmpty(stored))
        {
            try
            {
                if (JsonNode.Parse(stored) is JsonObject dbNode)
                    MergeInto(merged, dbNode);
            }
            catch (JsonException)
            {
                // 数据库配置损坏时退回文件配置
            }
        }
        var result = merged.Deserialize<NotifierOptions>(JsonOpts) ?? new NotifierOptions();
        DecryptSecrets(result);
        return result;
    }

    /// <summary>保存单个渠道配置；敏感字段为空/掩码时保留已存密文</summary>
    public async Task SaveChannelAsync(string channel, JsonElement channelConfig)
    {
        var incoming = channelConfig.Deserialize<JsonObject>()
            ?? throw new ArgumentException("无效的渠道配置");

        var stored = await _settings.GetAsync(SettingsKey);
        JsonObject current;
        if (!string.IsNullOrEmpty(stored))
        {
            current = JsonNode.Parse(stored) as JsonObject ?? new JsonObject();
        }
        else
        {
            current = JsonSerializer.SerializeToNode(_fileOptions.Value, JsonOpts) as JsonObject ?? new JsonObject();
            EncryptPlaintextSecrets(current); // 文件配置中的明文密钥入库前统一加密
        }

        var target = current[channel] as JsonObject ?? new JsonObject();
        foreach (var (name, value) in incoming)
        {
            if (value == null) continue;
            if (SensitiveFields.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                var raw = value.ToJsonString().Trim('"');
                if (string.IsNullOrEmpty(raw) || raw == Mask) continue; // 保留已存密文
                target[name] = _protector.Protect(raw);
            }
            else
            {
                target[name] = JsonNode.Parse(value.ToJsonString()); // 保留原始 JSON 类型
            }
        }
        current[channel] = target;
        await _settings.SetAsync(SettingsKey, current.ToJsonString());
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (name, node) in source)
        {
            if (node is JsonObject srcObj && target[name] is JsonObject dstObj)
                MergeInto(dstObj, srcObj);
            else
                target[name] = node?.DeepClone();
        }
    }

    private void EncryptPlaintextSecrets(JsonObject channels)
    {
        foreach (var (_, channelNode) in channels)
        {
            if (channelNode is not JsonObject ch) continue;
            foreach (var field in SensitiveFields)
            {
                if (ch[field] is JsonValue v && v.TryGetValue<string>(out var raw)
                    && !string.IsNullOrEmpty(raw) && !_protector.IsEncrypted(raw))
                {
                    ch[field] = _protector.Protect(raw);
                }
            }
        }
    }

    private void DecryptSecrets(NotifierOptions options)
    {
        TryDecrypt(s => options.Feishu.Secret = s, options.Feishu.Secret);
        TryDecrypt(s => options.DingTalk.Secret = s, options.DingTalk.Secret);
        TryDecrypt(s => options.Email.Password = s, options.Email.Password);
    }

    private void TryDecrypt(Action<string> set, string value)
    {
        if (!string.IsNullOrEmpty(value) && _protector.IsEncrypted(value))
            set(_protector.Unprotect(value));
    }
}
