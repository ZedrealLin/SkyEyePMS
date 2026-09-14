using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;

namespace SkyEyePMS.Infrastructure.Database;

/// <summary>
/// 启动时执行建表/迁移脚本（Infrastructure/Database/Scripts/{provider}/V*.sql，按版本号顺序）。
/// 已应用版本记录在 schema_version 表。开发期脚本不可变更，只能新增 V(n+1)。
/// </summary>
public class DbInitializer
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(IDbConnectionFactory factory, ILogger<DbInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var providerDir = _factory.Provider == DatabaseProvider.Sqlite ? "sqlite" : "mysql";

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS schema_version (
                version INT NOT NULL PRIMARY KEY,
                applied_at TEXT NOT NULL
            )
            """);

        var applied = (await conn.QueryAsync<int>("SELECT version FROM schema_version")).ToHashSet();
        var scripts = Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(n => n.Contains($".Scripts.{providerDir}.") && n.EndsWith(".sql"))
            .Select(n =>
            {
                var file = n.Substring(n.IndexOf(".Scripts.") + 9); // e.g. mysql.V1__init.sql
                var versionPart = file.Split("__")[0];               // mysql.V1
                var version = int.Parse(versionPart.Split('V')[1]);
                return (Version: version, Resource: n);
            })
            .OrderBy(x => x.Version)
            .ToList();

        foreach (var (version, resource) in scripts)
        {
            if (applied.Contains(version)) continue;
            var sql = ReadResource(resource);
            _logger.LogInformation("Applying database migration V{Version} ({Provider})...", version, providerDir);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(sql, transaction: tx);
            await conn.ExecuteAsync("INSERT INTO schema_version (version, applied_at) VALUES (@v, @at)",
                new { v = version, at = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") }, tx);
            await tx.CommitAsync(ct);
            _logger.LogInformation("Migration V{Version} applied.", version);
        }
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
