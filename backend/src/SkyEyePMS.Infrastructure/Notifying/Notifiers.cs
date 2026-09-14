using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Notifying;

namespace SkyEyePMS.Infrastructure.Notifying;

/// <summary>通知消息渲染：各渠道共享的标题/正文</summary>
public static class AlertMessageRenderer
{
    public static string SeverityOf(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => "严重",
        AlertSeverity.Warning => "警告",
        _ => "提示"
    };

    public static string BuildTitle(AlertNotification n) => n.EventKind switch
    {
        AlertEventKind.Resolved => $"[已恢复] {n.Printer.Name}",
        _ => $"[{SeverityOf(n.Alert.SeverityEnum)}] {n.Printer.Name}：{ShortMessage(n.Alert.Message)}"
    };

    public static string BuildMarkdown(AlertNotification n)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {BuildTitle(n)}");
        sb.AppendLine($"- **打印机**：{n.Printer.Name}");
        sb.AppendLine($"- **地址**：{n.Printer.Ip}:{n.Printer.Port}");
        if (!string.IsNullOrWhiteSpace(n.Printer.Location)) sb.AppendLine($"- **位置**：{n.Printer.Location}");
        if (!string.IsNullOrWhiteSpace(n.Printer.Department)) sb.AppendLine($"- **部门**：{n.Printer.Department}");
        sb.AppendLine($"- **类型**：{SeverityOf(n.Alert.SeverityEnum)} / {n.EventText}");
        sb.AppendLine($"- **详情**：{n.Alert.Message}");
        sb.AppendLine($"- **首次触发**：{FormatTime(n.Alert.FirstTriggeredAt)}");
        if (n.EventKind != AlertEventKind.Raised)
            sb.AppendLine($"- **本次推送**：{FormatTime(DateTime.UtcNow)}");
        sb.AppendLine();
        sb.AppendLine($"> {n.SystemName}");
        return sb.ToString();
    }

    public static string BuildPlainText(AlertNotification n)
    {
        var lines = new List<string>
        {
            BuildTitle(n),
            $"打印机：{n.Printer.Name}（{n.Printer.Ip}:{n.Printer.Port}）"
        };
        if (!string.IsNullOrWhiteSpace(n.Printer.Location)) lines.Add($"位置：{n.Printer.Location}");
        if (!string.IsNullOrWhiteSpace(n.Printer.Department)) lines.Add($"部门：{n.Printer.Department}");
        lines.Add($"详情：{n.Alert.Message}");
        lines.Add($"首次触发：{FormatTime(n.Alert.FirstTriggeredAt)}");
        lines.Add($"-- {n.SystemName}");
        return string.Join('\n', lines);
    }

    /// <summary>飞书主题色：critical=red warning=orange info=blue resolved=green</summary>
    public static string FeishuTemplate(AlertNotification n) => n.EventKind switch
    {
        AlertEventKind.Resolved => "green",
        _ => n.Alert.SeverityEnum switch
        {
            AlertSeverity.Critical => "red",
            AlertSeverity.Warning => "orange",
            _ => "blue"
        }
    };

    public static string FormatTime(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private static string ShortMessage(string msg, int max = 60) =>
        msg.Length <= max ? msg : msg[..max] + "…";
}

/// <summary>HTTP Webhook 类渠道的公共基类</summary>
public abstract class WebhookNotifierBase : INotifier
{
    protected readonly IHttpClientFactory HttpClientFactory;
    protected readonly ILogger Logger;

    protected WebhookNotifierBase(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        HttpClientFactory = httpClientFactory;
        Logger = logger;
    }

    public abstract string Channel { get; }
    public abstract bool IsConfigured { get; }
    /// <summary>推送路由策略（来自渠道配置）</summary>
    public virtual ChannelRouting Routing { get; protected set; } = new();
    protected abstract (string Url, JsonObject Body) BuildRequest(AlertNotification n);
    /// <summary>摘要/自由文本推送的请求体（默认不支持）</summary>
    protected virtual (string Url, JsonObject Body)? BuildDigestRequest(string title, string markdown) => null;
    /// <summary>每次发送前对本次请求使用的 HttpClient 做额外配置（每次调用都是新实例）</summary>
    protected virtual void ConfigureClient(System.Net.Http.HttpClient client) { }

    public virtual async Task<(bool Success, string? Error)> SendAsync(AlertNotification n, CancellationToken ct = default)
    {
        if (!IsConfigured) return (false, "渠道未配置");
        try
        {
            var client = HttpClientFactory.CreateClient("notifier");
            ConfigureClient(client);
            var (url, body) = BuildRequest(n);
            using var resp = await client.PostAsync(url,
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}: {Truncate(text)}");
            // 各平台 HTTP 200 但 body.errcode/code != 0 的情况
            var err = CheckBusinessCode(text);
            return err == null ? (true, null) : (false, err);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "通知渠道 {Channel} 推送失败", Channel);
            return (false, Truncate(ex.Message));
        }
    }

    public virtual async Task<(bool Success, string? Error)> SendDigestAsync(string title, string markdown,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return (false, "渠道未配置");
        var request = BuildDigestRequest(title, markdown);
        if (request == null) return (false, "该渠道不支持摘要推送");
        try
        {
            var client = HttpClientFactory.CreateClient("notifier");
            ConfigureClient(client);
            var (url, body) = request.Value;
            using var resp = await client.PostAsync(url,
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}: {Truncate(text)}");
            var err = CheckBusinessCode(text);
            return err == null ? (true, null) : (false, err);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "通知渠道 {Channel} 摘要推送失败", Channel);
            return (false, Truncate(ex.Message));
        }
    }

    /// <summary>平台返回体校验，无问题返回 null</summary>
    protected virtual string? CheckBusinessCode(string responseBody)
    {
        try
        {
            var node = JsonNode.Parse(responseBody);
            var code = node?["code"]?.GetValue<int?>() ?? node?["errcode"]?.GetValue<int?>();
            if (code is 0 or null) return null;
            var msg = node?["msg"]?.GetValue<string>() ?? node?["errmsg"]?.GetValue<string>() ?? "";
            return $"平台返回错误 code={code}: {Truncate(msg)}";
        }
        catch (Exception)
        {
            return null; // 非标准返回体，交给 HTTP 状态码判断
        }
    }

    protected static string Truncate(string s, int max = 200) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>飞书自定义机器人：interactive 卡片消息（支持签名校验）</summary>
public class FeishuNotifier : WebhookNotifierBase
{
    private readonly FeishuOptions _options;

    public FeishuNotifier(FeishuOptions options, IHttpClientFactory httpFactory, ILogger<FeishuNotifier> logger)
        : base(httpFactory, logger)
    {
        _options = options;
        Routing = options.Routing;
    }

    public override string Channel => "feishu";
    public override bool IsConfigured => _options.Enabled && !string.IsNullOrEmpty(_options.WebhookUrl);

    protected override (string Url, JsonObject Body) BuildRequest(AlertNotification n)
    {
        var url = _options.WebhookUrl;
        if (!string.IsNullOrEmpty(_options.Secret))
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sign = FeishuSign(ts, _options.Secret);
            url += (url.Contains('?') ? "&" : "?") + $"timestamp={ts}&sign={Uri.EscapeDataString(sign)}";
        }

        var card = new JsonObject
        {
            ["config"] = new JsonObject { ["wide_screen_mode"] = true },
            ["header"] = new JsonObject
            {
                ["title"] = new JsonObject { ["tag"] = "plain_text", ["content"] = AlertMessageRenderer.BuildTitle(n) },
                ["template"] = AlertMessageRenderer.FeishuTemplate(n)
            },
            ["elements"] = new JsonArray(
                new JsonObject
                {
                    ["tag"] = "div",
                    ["fields"] = new JsonArray(
                        Field("打印机", n.Printer.Name),
                        Field("地址", $"{n.Printer.Ip}:{n.Printer.Port}"),
                        Field("位置", string.IsNullOrWhiteSpace(n.Printer.Location) ? "-" : n.Printer.Location!),
                        Field("部门", string.IsNullOrWhiteSpace(n.Printer.Department) ? "-" : n.Printer.Department!))
                },
                new JsonObject { ["tag"] = "hr" },
                new JsonObject
                {
                    ["tag"] = "div",
                    ["text"] = new JsonObject
                    {
                        ["tag"] = "lark_md",
                        ["content"] = $"**级别**：{AlertMessageRenderer.SeverityOf(n.Alert.SeverityEnum)}　**事件**：{n.EventText}\n" +
                                      $"**详情**：{n.Alert.Message}\n" +
                                      $"**首次触发**：{AlertMessageRenderer.FormatTime(n.Alert.FirstTriggeredAt)}"
                    }
                },
                new JsonObject
                {
                    ["tag"] = "note",
                    ["elements"] = new JsonArray(new JsonObject
                    {
                        ["tag"] = "plain_text",
                        ["content"] = $"{n.SystemName} · {n.EventText} · {AlertMessageRenderer.FormatTime(DateTime.UtcNow)}"
                    })
                })
        };
        return (url, new JsonObject { ["msg_type"] = "interactive", ["card"] = card });
    }

    protected override (string Url, JsonObject Body)? BuildDigestRequest(string title, string markdown)
    {
        var url = _options.WebhookUrl;
        if (!string.IsNullOrEmpty(_options.Secret))
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sign = FeishuSign(ts, _options.Secret);
            url += (url.Contains('?') ? "&" : "?") + $"timestamp={ts}&sign={Uri.EscapeDataString(sign)}";
        }
        var card = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["title"] = new JsonObject { ["tag"] = "plain_text", ["content"] = title },
                ["template"] = "blue"
            },
            ["elements"] = new JsonArray(new JsonObject
            {
                ["tag"] = "div",
                ["text"] = new JsonObject { ["tag"] = "lark_md", ["content"] = markdown }
            })
        };
        return (url, new JsonObject { ["msg_type"] = "interactive", ["card"] = card });
    }

    private static JsonObject Field(string label, string value) => new()
    {
        ["is_short"] = true,
        ["text"] = new JsonObject { ["tag"] = "lark_md", ["content"] = $"**{label}：**{value}" }
    };

    /// <summary>飞书签名算法：将 "timestamp\nsecret" 作为 HmacSHA256 的 KEY，对空串计算摘要</summary>
    public static string FeishuSign(long timestamp, string secret)
    {
        var key = Encoding.UTF8.GetBytes($"{timestamp}\n{secret}");
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(Array.Empty<byte>()));
    }
}

/// <summary>钉钉自定义机器人：Markdown 消息（支持加签）</summary>
public class DingTalkNotifier : WebhookNotifierBase
{
    private readonly DingTalkOptions _options;

    public DingTalkNotifier(DingTalkOptions options, IHttpClientFactory httpFactory, ILogger<DingTalkNotifier> logger)
        : base(httpFactory, logger)
    {
        _options = options;
        Routing = options.Routing;
    }

    public override string Channel => "dingtalk";
    public override bool IsConfigured => _options.Enabled && !string.IsNullOrEmpty(_options.WebhookUrl);

    protected override (string Url, JsonObject Body) BuildRequest(AlertNotification n)
    {
        var url = _options.WebhookUrl;
        if (!string.IsNullOrEmpty(_options.Secret))
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sign = DingTalkSign(ts, _options.Secret);
            url += (url.Contains('?') ? "&" : "?") + $"timestamp={ts}&sign={Uri.EscapeDataString(sign)}";
        }
        var body = new JsonObject
        {
            ["msgtype"] = "markdown",
            ["markdown"] = new JsonObject
            {
                ["title"] = AlertMessageRenderer.BuildTitle(n),
                ["text"] = AlertMessageRenderer.BuildMarkdown(n)
            }
        };
        return (url, body);
    }

    protected override (string Url, JsonObject Body)? BuildDigestRequest(string title, string markdown)
    {
        var url = _options.WebhookUrl;
        if (!string.IsNullOrEmpty(_options.Secret))
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sign = DingTalkSign(ts, _options.Secret);
            url += (url.Contains('?') ? "&" : "?") + $"timestamp={ts}&sign={Uri.EscapeDataString(sign)}";
        }
        return (url, new JsonObject
        {
            ["msgtype"] = "markdown",
            ["markdown"] = new JsonObject { ["title"] = title, ["text"] = markdown }
        });
    }

    /// <summary>钉钉加签：HmacSHA256(secret, "timestamp\nsecret") → base64 → urlencode</summary>
    public static string DingTalkSign(long timestampMs, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestampMs}\n{secret}"));
        return Convert.ToBase64String(hash);
    }

    protected override string? CheckBusinessCode(string responseBody)
    {
        try
        {
            var node = JsonNode.Parse(responseBody);
            var code = node?["errcode"]?.GetValue<int?>();
            if (code is 0 or null) return null;
            return $"钉钉返回错误 errcode={code}: {node?["errmsg"]?.GetValue<string>()}";
        }
        catch (Exception) { return null; }
    }
}

/// <summary>企业微信群机器人：Markdown 消息</summary>
public class WeComNotifier : WebhookNotifierBase
{
    private readonly WeComOptions _options;

    public WeComNotifier(WeComOptions options, IHttpClientFactory httpFactory, ILogger<WeComNotifier> logger)
        : base(httpFactory, logger)
    {
        _options = options;
        Routing = options.Routing;
    }

    public override string Channel => "wecom";
    public override bool IsConfigured => _options.Enabled && !string.IsNullOrEmpty(_options.WebhookUrl);

    protected override (string Url, JsonObject Body) BuildRequest(AlertNotification n)
    {
        var content = AlertMessageRenderer.BuildMarkdown(n);
        // 企业微信 markdown 内容上限 4096 字节
        while (Encoding.UTF8.GetByteCount(content) > 4000)
            content = content[..^100];
        var body = new JsonObject
        {
            ["msgtype"] = "markdown",
            ["markdown"] = new JsonObject { ["content"] = content }
        };
        return (_options.WebhookUrl, body);
    }

    protected override (string Url, JsonObject Body)? BuildDigestRequest(string title, string markdown)
    {
        var content = $"## {title}\n{markdown}";
        // 企业微信 markdown 内容上限 4096 字节
        while (Encoding.UTF8.GetByteCount(content) > 4000)
            content = content[..^100];
        return (_options.WebhookUrl, new JsonObject
        {
            ["msgtype"] = "markdown",
            ["markdown"] = new JsonObject { ["content"] = content }
        });
    }

    protected override string? CheckBusinessCode(string responseBody)
    {
        try
        {
            var node = JsonNode.Parse(responseBody);
            var code = node?["errcode"]?.GetValue<int?>();
            if (code is 0 or null) return null;
            return $"企业微信返回错误 errcode={code}: {node?["errmsg"]?.GetValue<string>()}";
        }
        catch (Exception) { return null; }
    }
}

/// <summary>邮件通知：SMTP + HTML 模板（MailKit）</summary>
public class EmailNotifier : INotifier
{
    private readonly EmailOptions _options;
    private readonly ILogger<EmailNotifier> _logger;

    public EmailNotifier(EmailOptions options, ILogger<EmailNotifier> logger)
    {
        _options = options;
        _logger = logger;
        Routing = options.Routing;
    }

    public string Channel => "email";
    public ChannelRouting Routing { get; }

    public bool IsConfigured =>
        _options.Enabled && !string.IsNullOrEmpty(_options.SmtpHost)
        && !string.IsNullOrEmpty(_options.FromAddress) && _options.ToAddresses.Count > 0;

    public async Task<(bool Success, string? Error)> SendAsync(AlertNotification n, CancellationToken ct = default)
    {
        if (!IsConfigured) return (false, "邮件渠道未配置完整");
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(
                string.IsNullOrWhiteSpace(_options.FromName) ? "SkyEyePMS" : _options.FromName,
                _options.FromAddress));
            foreach (var to in _options.ToAddresses.Where(a => !string.IsNullOrWhiteSpace(a)))
                message.To.Add(MailboxAddress.Parse(to.Trim()));
            message.Subject = AlertMessageRenderer.BuildTitle(n);
            message.Body = new BodyBuilder
            {
                HtmlBody = BuildHtml(n),
                TextBody = AlertMessageRenderer.BuildPlainText(n)
            }.ToMessageBody();

            using var client = new SmtpClient();
            var secure = !_options.UseSsl ? SecureSocketOptions.None
                : _options.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
            await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, secure, ct);
            if (!string.IsNullOrEmpty(_options.Username))
                await client.AuthenticateAsync(_options.Username, _options.Password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "邮件通知发送失败");
            return (false, Truncate(ex.Message));
        }
    }

    public static string BuildHtml(AlertNotification n)
    {
        var color = n.EventKind == AlertEventKind.Resolved ? "#0a8f3c"
            : n.Alert.SeverityEnum == AlertSeverity.Critical ? "#d93026"
            : n.Alert.SeverityEnum == AlertSeverity.Warning ? "#e67700" : "#1a6fd4";
        static string Row(string k, string v) =>
            $"<tr><td style='padding:6px 12px;color:#666;white-space:nowrap'>{k}</td><td style='padding:6px 12px'>{v}</td></tr>";
        var sb = new StringBuilder();
        sb.Append("<div style='font-family:Segoe UI,Arial,sans-serif;max-width:640px;border:1px solid #eee;border-radius:8px;overflow:hidden'>");
        sb.Append($"<div style='background:{color};color:#fff;padding:14px 18px;font-size:16px;font-weight:600'>{Enc(AlertMessageRenderer.BuildTitle(n))}</div>");
        sb.Append("<table style='border-collapse:collapse;width:100%;font-size:14px'>");
        sb.Append(Row("打印机", Enc(n.Printer.Name)));
        sb.Append(Row("地址", $"{Enc(n.Printer.Ip)}:{n.Printer.Port}"));
        if (!string.IsNullOrWhiteSpace(n.Printer.Location)) sb.Append(Row("位置", Enc(n.Printer.Location!)));
        if (!string.IsNullOrWhiteSpace(n.Printer.Department)) sb.Append(Row("部门", Enc(n.Printer.Department!)));
        sb.Append(Row("事件", n.EventText));
        sb.Append(Row("详情", Enc(n.Alert.Message)));
        sb.Append(Row("首次触发", AlertMessageRenderer.FormatTime(n.Alert.FirstTriggeredAt)));
        sb.Append("</table>");
        sb.Append($"<div style='padding:10px 18px;color:#999;font-size:12px'>{Enc(n.SystemName)}</div>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Truncate(string s, int max = 200) => s.Length <= max ? s : s[..max] + "…";

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    /// <summary>摘要邮件：markdown 按行转 HTML（支持 #/## 标题、- 列表、加粗、普通段落）</summary>
    public async Task<(bool Success, string? Error)> SendDigestAsync(string title, string markdown,
        CancellationToken ct = default)
    {
        if (!IsConfigured) return (false, "邮件渠道未配置完整");
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(
                string.IsNullOrWhiteSpace(_options.FromName) ? "SkyEyePMS" : _options.FromName,
                _options.FromAddress));
            foreach (var to in _options.ToAddresses.Where(a => !string.IsNullOrWhiteSpace(a)))
                message.To.Add(MailboxAddress.Parse(to.Trim()));
            message.Subject = title;
            message.Body = new BodyBuilder
            {
                HtmlBody = MarkdownToHtml(title, markdown),
                TextBody = markdown
            }.ToMessageBody();

            using var client = new SmtpClient();
            var secure = !_options.UseSsl ? SecureSocketOptions.None
                : _options.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
            await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, secure, ct);
            if (!string.IsNullOrEmpty(_options.Username))
                await client.AuthenticateAsync(_options.Username, _options.Password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "摘要邮件发送失败");
            return (false, Truncate(ex.Message));
        }
    }

    public static string MarkdownToHtml(string title, string markdown)
    {
        var sb = new StringBuilder();
        sb.Append("<div style='font-family:Segoe UI,Arial,sans-serif;max-width:720px;border:1px solid #eee;border-radius:8px;overflow:hidden'>");
        sb.Append($"<div style='background:#2b62d9;color:#fff;padding:14px 18px;font-size:16px;font-weight:600'>{Enc(title)}</div>");
        sb.Append("<div style='padding:6px 18px 14px;font-size:14px;line-height:1.7'>");
        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("### ")) sb.Append($"<h3 style='margin:12px 0 4px'>{Enc(line[4..])}</h3>");
            else if (line.StartsWith("## ")) sb.Append($"<h2 style='margin:14px 0 6px'>{Enc(line[3..])}</h2>");
            else if (line.StartsWith("# ")) sb.Append($"<h2 style='margin:14px 0 6px'>{Enc(line[2..])}</h2>");
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                sb.Append($"<div style='padding-left:14px'>• {Bold(Enc(line[2..]))}</div>");
            else if (line.StartsWith("> ")) sb.Append($"<div style='color:#888;font-size:12px'>{Bold(Enc(line[2..]))}</div>");
            else if (line.Length == 0) sb.Append("<div style='height:6px'></div>");
            else sb.Append($"<div>{Bold(Enc(line))}</div>");
        }
        sb.Append("</div></div>");
        return sb.ToString();

        static string Bold(string encoded)
        {
            // 编码后 ** 成对替换为 <b>
            var parts = encoded.Split("\u0001B\u0001");
            var sb2 = new StringBuilder();
            for (var i = 0; i < parts.Length; i++)
                sb2.Append(i % 2 == 1 ? $"<b>{parts[i]}</b>" : parts[i]);
            return sb2.ToString();
        }
    }
}

/// <summary>通用 Webhook 渠道：POST 结构化 JSON 到任意地址（对接 ITSM / n8n / 自建系统）</summary>
public class GenericWebhookNotifier : WebhookNotifierBase
{
    private readonly WebhookOptions _options;

    public GenericWebhookNotifier(WebhookOptions options, IHttpClientFactory httpFactory,
        ILogger<GenericWebhookNotifier> logger)
        : base(httpFactory, logger)
    {
        _options = options;
        Routing = new ChannelRouting(); // 通用渠道默认全量推送（消费方自行过滤）
    }

    public override string Channel => "webhook";
    public override bool IsConfigured => _options.Enabled && !string.IsNullOrEmpty(_options.Url);

    protected override (string Url, JsonObject Body) BuildRequest(AlertNotification n)
    {
        var body = new JsonObject
        {
            ["event"] = n.EventKind.ToString().ToLowerInvariant(),
            ["title"] = AlertMessageRenderer.BuildTitle(n),
            ["severity"] = n.Alert.Severity,
            ["type"] = n.Alert.Type,
            ["alertKey"] = n.Alert.AlertKey,
            ["message"] = n.Alert.Message,
            ["printer"] = new JsonObject
            {
                ["id"] = n.Printer.Id,
                ["name"] = n.Printer.Name,
                ["ip"] = n.Printer.Ip,
                ["port"] = n.Printer.Port,
                ["location"] = n.Printer.Location,
                ["department"] = n.Printer.Department
            },
            ["firstTriggeredAt"] = n.Alert.FirstTriggeredAt,
            ["timestamp"] = DateTime.UtcNow,
            ["systemName"] = n.SystemName
        };
        return (_options.Url, body);
    }

    protected override (string Url, JsonObject Body)? BuildDigestRequest(string title, string markdown) =>
        (_options.Url, new JsonObject
        {
            ["event"] = "digest",
            ["title"] = title,
            ["markdown"] = markdown,
            ["timestamp"] = DateTime.UtcNow
        });

    protected override void ConfigureClient(System.Net.Http.HttpClient client)
    {
        if (!string.IsNullOrEmpty(_options.Secret))
            client.DefaultRequestHeaders.Add("X-SkyEye-Token", _options.Secret);
    }
}
