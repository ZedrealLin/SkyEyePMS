using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkyEyePMS.Core.Models;
using SkyEyePMS.Core.Notifying;
using SkyEyePMS.Infrastructure.Notifying;

namespace SkyEyePMS.Tests;

public class NotifierMessageTests
{
    private static AlertNotification Sample(AlertEventKind kind = AlertEventKind.Raised,
        AlertSeverity severity = AlertSeverity.Warning)
    {
        var alert = new Alert
        {
            Id = 9, PrinterId = 1, AlertKey = "supply_low:1", Type = AlertType.SupplyLow.ToString(),
            Severity = severity.ToString(), Message = "黑色硒鼓 余量仅 15%（低于阈值 20%）",
            Status = "active", FirstTriggeredAt = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc)
        };
        var printer = new Printer
        {
            Id = 1, Name = "3F-财务-HP401", Ip = "10.1.3.20", Port = 161,
            Location = "3楼东侧", Department = "财务部"
        };
        return new AlertNotification(kind, alert, printer, "SkyEyePMS 测试实例");
    }

    [Fact]
    public void DingTalkSign_MatchesKnownAlgorithm()
    {
        var ts = 1725974400000L;
        var secret = "SEC7d9x";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}\n{secret}")));
        Assert.Equal(expected, DingTalkNotifier.DingTalkSign(ts, secret));
    }

    [Fact]
    public void FeishuSign_UsesTimestampPlusSecretAsKey()
    {
        var ts = 1725974400L;
        var secret = "my-secret";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes($"{ts}\n{secret}"));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Array.Empty<byte>()));
        Assert.Equal(expected, FeishuNotifier.FeishuSign(ts, secret));
    }

    [Fact]
    public void FeishuRequest_BuildsInteractiveCard()
    {
        var options = new FeishuOptions { Enabled = true, WebhookUrl = "https://open.feishu.cn/hook/x" };
        var notifier = new FeishuNotifier(options, HttpClientFactory(), NullLogger<FeishuNotifier>.Instance);
        var method = notifier.GetType().GetMethod("BuildRequest",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var (url, body) = ((string Url, JsonObject Body))method.Invoke(notifier, new object[]
        {
            Sample(severity: AlertSeverity.Critical)
        })!;

        Assert.Equal(options.WebhookUrl, url);
        Assert.Equal("interactive", body["msg_type"]!.GetValue<string>());
        var card = body["card"]!.AsObject();
        Assert.Equal("red", card["header"]!["template"]!.GetValue<string>());
        Assert.Contains("黑色硒鼓", card["elements"]!.AsArray()[2]!["text"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void FeishuTemplate_ResolvesBySeverity()
    {
        Assert.Equal("red", AlertMessageRenderer.FeishuTemplate(Sample(severity: AlertSeverity.Critical)));
        Assert.Equal("orange", AlertMessageRenderer.FeishuTemplate(Sample(severity: AlertSeverity.Warning)));
        Assert.Equal("blue", AlertMessageRenderer.FeishuTemplate(Sample(severity: AlertSeverity.Info)));
        Assert.Equal("green", AlertMessageRenderer.FeishuTemplate(Sample(AlertEventKind.Resolved)));
    }

    [Fact]
    public void EmailHtml_EscapesContent()
    {
        var n = Sample();
        n.Alert.Message = "<script>alert(1)</script>";
        var html = EmailNotifier.BuildHtml(n);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    private static IHttpClientFactory HttpClientFactory()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddHttpClient();
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }
}

/// <summary>Webhook 渠道端到端：真实 HTTP POST → 本地监听器，校验请求体与结果判定</summary>
public class WebhookNotifierHttpTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly TaskCompletionSource<(string? Body, string Response)> _lastRequest = new();

    public WebhookNotifierHttpTests()
    {
        var port = GetFreePort();
        _prefix = $"http://127.0.0.1:{port}/hook/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        Task.Run(async () =>
        {
            try
            {
                while (_listener.IsListening)
                {
                    var ctx = await _listener.GetContextAsync();
                    string body = "";
                    if (ctx.Request.HasEntityBody)
                    {
                        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                        body = await reader.ReadToEndAsync();
                    }
                    var resp = "{\"errcode\":0,\"errmsg\":\"ok\"}";
                    var buf = Encoding.UTF8.GetBytes(resp);
                    await ctx.Response.OutputStream.WriteAsync(buf);
                    ctx.Response.Close();
                    _lastRequest.TrySetResult((body, resp));
                }
            }
            catch (Exception) { /* 监听器关闭 */ }
        });
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose() => _listener.Stop();

    private static AlertNotification Sample() => new(
        AlertEventKind.Raised,
        new Alert
        {
            Id = 1, PrinterId = 1, AlertKey = "offline", Type = "Offline", Severity = "Critical",
            Message = "设备连续 3 次采集无响应", FirstTriggeredAt = DateTime.UtcNow
        },
        new Printer { Id = 1, Name = "PRN-X", Ip = "10.0.0.5", Port = 161 },
        "SkyEyePMS");

    [Fact]
    public async Task DingTalkNotifier_PostsMarkdownWithSign()
    {
        var options = new DingTalkOptions
        {
            Enabled = true,
            WebhookUrl = _prefix + "dingtalk",
            Secret = "SECtest"
        };
        var notifier = new DingTalkNotifier(options, HttpClientFactory(), NullLogger<DingTalkNotifier>.Instance);

        var (success, error) = await notifier.SendAsync(Sample());

        Assert.True(success, error);
        var (body, _) = await _lastRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var json = JsonNode.Parse(body!)!.AsObject();
        Assert.Equal("markdown", json["msgtype"]!.GetValue<string>());
        Assert.Contains("PRN-X", json["markdown"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task WeComNotifier_PostsMarkdown()
    {
        var options = new WeComOptions { Enabled = true, WebhookUrl = _prefix + "wecom" };
        var notifier = new WeComNotifier(options, HttpClientFactory(), NullLogger<WeComNotifier>.Instance);

        var (success, error) = await notifier.SendAsync(Sample());

        Assert.True(success, error);
        var (body, _) = await _lastRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var json = JsonNode.Parse(body!)!.AsObject();
        Assert.Equal("markdown", json["msgtype"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notifier_BusinessError_ReturnsFailure()
    {
        var port = GetFreePort();
        using var bad = new HttpListener();
        bad.Prefixes.Add($"http://127.0.0.1:{port}/hook/");
        bad.Start();
        var serverTask = Task.Run(async () =>
        {
            var ctx = await bad.GetContextAsync();
            var buf = Encoding.UTF8.GetBytes("{\"errcode\":310000,\"errmsg\":\"sign not match\"}");
            await ctx.Response.OutputStream.WriteAsync(buf);
            ctx.Response.Close();
        });

        var notifier = new DingTalkNotifier(
            new DingTalkOptions { Enabled = true, WebhookUrl = $"http://127.0.0.1:{port}/hook/d" },
            HttpClientFactory(), NullLogger<DingTalkNotifier>.Instance);
        var (success, error) = await notifier.SendAsync(Sample());

        Assert.False(success);
        Assert.Contains("310000", error);
        await serverTask;
    }

    private static IHttpClientFactory HttpClientFactory()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddHttpClient("notifier", c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddHttpClient();
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }
}
