using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace SkyEyePMS.Infrastructure.Database;

public enum DatabaseProvider
{
    MySql,
    Sqlite
}

/// <summary>数据库配置（appsettings "Database" 节）</summary>
public class DbOptions
{
    /// <summary>mysql（默认）或 sqlite（本地试用/演示）</summary>
    public string Provider { get; set; } = "mysql";
    /// <summary>MySQL 连接串；为空时使用 SqlitePath</summary>
    public string ConnectionString { get; set; } = string.Empty;
    /// <summary>SQLite 文件路径（Provider=sqlite 时使用）</summary>
    public string SqlitePath { get; set; } = "data/skyeyepms.db";

    public DatabaseProvider ResolvedProvider =>
        string.Equals(Provider, "sqlite", StringComparison.OrdinalIgnoreCase) ? DatabaseProvider.Sqlite : DatabaseProvider.MySql;
}

public interface IDbConnectionFactory
{
    DatabaseProvider Provider { get; }
    Task<DbConnection> OpenAsync(CancellationToken ct = default);
}

public class DbConnectionFactory : IDbConnectionFactory
{
    private readonly DbOptions _options;

    public DbConnectionFactory(Microsoft.Extensions.Options.IOptions<DbOptions> options) => _options = options.Value;

    public DatabaseProvider Provider => _options.ResolvedProvider;

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        DbConnection conn;
        if (Provider == DatabaseProvider.Sqlite)
        {
            var path = _options.SqlitePath;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(path),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true
            }.ToString());
        }
        else
        {
            conn = new MySqlConnection(_options.ConnectionString);
        }
        await conn.OpenAsync(ct);
        return conn;
    }
}

/// <summary>
/// 统一时间存储约定：所有 DateTime 参数以 "yyyy-MM-dd HH:mm:ss"（UTC）字符串写入，
/// 避免 MySqlConnector 对 Kind=Utc 参数做隐式时区转换；DATETIME/TEXT 两种列型下比较语义一致。
/// </summary>
public class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
{
    public static readonly UtcDateTimeHandler Instance = new();

    public override void SetValue(IDbDataParameter parameter, DateTime value)
    {
        parameter.DbType = System.Data.DbType.String;
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        parameter.Value = utc.ToString("yyyy-MM-dd HH:mm:ss");
    }

    public override DateTime Parse(object value) => value switch
    {
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        string s when DateTime.TryParse(s, out var parsed) => DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
        _ => default
    };
}

/// <summary>不同数据库的 SQL 方言差异集中在这里</summary>
public class DbDialect
{
    public static readonly DbDialect MySql = new(DatabaseProvider.MySql);
    public static readonly DbDialect Sqlite = new(DatabaseProvider.Sqlite);

    public static DbDialect For(DatabaseProvider provider) =>
        provider == DatabaseProvider.Sqlite ? Sqlite : MySql;

    public DatabaseProvider Provider { get; }
    private DbDialect(DatabaseProvider provider) => Provider = provider;

    /// <summary>取最近一次 INSERT 的自增 ID（须在同一连接上执行）</summary>
    public string LastInsertIdSql => Provider == DatabaseProvider.MySql
        ? "SELECT LAST_INSERT_ID();"
        : "SELECT last_insert_rowid();";

    /// <summary>耗材当前值 upsert（printer_id+supply_index 唯一键）</summary>
    public string UpsertSupplySql => Provider == DatabaseProvider.MySql
        ? """
          INSERT INTO supplies (printer_id, supply_index, name, supply_type, unit, max_capacity, level, percent, low_percent_threshold, updated_at)
          VALUES (@PrinterId, @SupplyIndex, @Name, @SupplyType, @Unit, @MaxCapacity, @Level, @Percent, @LowPercentThreshold, @UpdatedAt)
          ON DUPLICATE KEY UPDATE name=VALUES(name), supply_type=VALUES(supply_type), unit=VALUES(unit),
            max_capacity=VALUES(max_capacity), level=VALUES(level), percent=VALUES(percent),
            low_percent_threshold=VALUES(low_percent_threshold), updated_at=VALUES(updated_at)
          """
        : """
          INSERT INTO supplies (printer_id, supply_index, name, supply_type, unit, max_capacity, level, percent, low_percent_threshold, updated_at)
          VALUES (@PrinterId, @SupplyIndex, @Name, @SupplyType, @Unit, @MaxCapacity, @Level, @Percent, @LowPercentThreshold, @UpdatedAt)
          ON CONFLICT(printer_id, supply_index) DO UPDATE SET
            name=excluded.name, supply_type=excluded.supply_type, unit=excluded.unit,
            max_capacity=excluded.max_capacity, level=excluded.level, percent=excluded.percent,
            low_percent_threshold=excluded.low_percent_threshold, updated_at=excluded.updated_at
          """;

    /// <summary>日粒度统计 upsert（printer_id+stat_date 唯一键）</summary>
    public string UpsertDailyStatSql => Provider == DatabaseProvider.MySql
        ? """
          INSERT INTO daily_stats (printer_id, stat_date, pages_printed, online_samples, total_samples)
          VALUES (@PrinterId, @StatDate, @PagesPrinted, @OnlineSamples, @TotalSamples)
          ON DUPLICATE KEY UPDATE pages_printed=VALUES(pages_printed), online_samples=VALUES(online_samples),
            total_samples=VALUES(total_samples)
          """
        : """
          INSERT INTO daily_stats (printer_id, stat_date, pages_printed, online_samples, total_samples)
          VALUES (@PrinterId, @StatDate, @PagesPrinted, @OnlineSamples, @TotalSamples)
          ON CONFLICT(printer_id, stat_date) DO UPDATE SET
            pages_printed=excluded.pages_printed, online_samples=excluded.online_samples,
            total_samples=excluded.total_samples
          """;

    public string LimitOffset(int limit, int offset) =>
        $" LIMIT {limit} OFFSET {offset}";
}
