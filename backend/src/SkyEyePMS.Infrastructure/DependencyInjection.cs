using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SkyEyePMS.Core.Collecting;
using SkyEyePMS.Core.Notifying;
using SkyEyePMS.Core.Repositories;
using SkyEyePMS.Core.Security;
using SkyEyePMS.Core.Services;
using SkyEyePMS.Infrastructure.Collecting;
using SkyEyePMS.Infrastructure.Database;
using SkyEyePMS.Infrastructure.Notifying;
using SkyEyePMS.Infrastructure.Polling;
using SkyEyePMS.Infrastructure.Repositories;
using SkyEyePMS.Infrastructure.Security;
using SkyEyePMS.Infrastructure.Services;

namespace SkyEyePMS.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSkyEyePmsInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        // snake_case 列名 ↔ PascalCase 属性
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        // DateTime 参数统一以 UTC 字符串写入（规避 MySQL 连接器隐式时区转换）
        Dapper.SqlMapper.AddTypeHandler(Database.UtcDateTimeHandler.Instance);
        Dapper.SqlMapper.AddTypeHandler(typeof(DateTime?), Database.UtcDateTimeHandler.Instance);

        services.Configure<DbOptions>(configuration.GetSection("Database"));
        services.Configure<SnmpOptions>(configuration.GetSection("Snmp"));
        services.Configure<PollingOptions>(configuration.GetSection("Polling"));
        services.Configure<SecurityOptions>(configuration.GetSection("Security"));
        services.Configure<NotifierOptions>(configuration.GetSection("Notifiers"));
        services.Configure<TrapOptions>(configuration.GetSection("Traps"));

        services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
        services.AddSingleton<DbInitializer>();
        services.AddSingleton<ISecretProtector, AesSecretProtector>();
        services.AddSingleton<ISnmpCollector, SnmpCollector>();
        services.AddSingleton<DiscoveryJobStore>();

        // 仓储无状态（连接每次操作独立创建），注册为单例以便被单例后台服务直接依赖
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IPrinterRepository, PrinterRepository>();
        services.AddSingleton<IAlertRepository, AlertRepository>();
        services.AddSingleton<IMetricsRepository, MetricsRepository>();
        services.AddSingleton<IAppSettingsRepository, AppSettingsRepository>();

        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        services.AddSingleton<IAuditRepository, AuditRepository>();
        services.AddSingleton<IAuditService, AuditService>();
        services.AddSingleton<NotifierConfigService>();
        services.AddSingleton<INotifierRegistry, NotifierRegistry>();
        services.AddSingleton<AlertDispatcher>();
        services.AddSingleton<IPollingCoordinator, PollingCoordinator>();
        services.AddSingleton<RecentTrapStore>();
        services.AddHostedService<TrapListenerService>();

        services.AddHostedService<PollingService>();
        services.AddHostedService<RetentionService>();

        services.AddHttpClient("notifier", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SkyEyePMS/1.0");
        });

        return services;
    }
}
