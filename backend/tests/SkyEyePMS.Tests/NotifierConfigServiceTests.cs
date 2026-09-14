using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Security;
using SkyEyePMS.Infrastructure;
using SkyEyePMS.Infrastructure.Database;
using SkyEyePMS.Infrastructure.Notifying;

namespace SkyEyePMS.Tests;

/// <summary>通知渠道配置：保存 → 加密存储 → 读取合并解密（回归测试）</summary>
public class NotifierConfigServiceTests
{
    [Fact]
    public async Task SaveThenLoad_Feishu_PersistsEncryptsAndMerges()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"skyeye-diag-{Guid.NewGuid():N}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "sqlite",
            ["Database:SqlitePath"] = dbPath,
            ["Security:EncryptionKey"] = "diag-key"
        }).Build();
        var services = new ServiceCollection();
        services.AddSkyEyePmsInfrastructure(config);
        await services.BuildServiceProvider().DisposeAsync();
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<DbInitializer>().InitializeAsync();

        var settings = provider.GetRequiredService<IAppSettingsRepository>();
        var protector = provider.GetRequiredService<ISecretProtector>();
        var fileOpts = Microsoft.Extensions.Options.Options.Create(new NotifierOptions());
        var svc = new NotifierConfigService(fileOpts, settings, protector);

        var incoming = JsonDocument.Parse("""{"enabled":true,"webhookUrl":"https://open.feishu.cn/hook/abc","secret":"s3cret"}""").RootElement;
        await svc.SaveChannelAsync("feishu", incoming);

        var stored = await settings.GetAsync("notifiers");
        Console.WriteLine("STORED: " + stored);

        var effective = await svc.GetEffectiveAsync();
        Console.WriteLine($"ENABLED={effective.Feishu.Enabled}, URL={effective.Feishu.WebhookUrl}, SECRET={effective.Feishu.Secret}");

        Assert.True(effective.Feishu.Enabled);
        Assert.Equal("https://open.feishu.cn/hook/abc", effective.Feishu.WebhookUrl);
        Assert.Equal("s3cret", effective.Feishu.Secret);
    }
}
