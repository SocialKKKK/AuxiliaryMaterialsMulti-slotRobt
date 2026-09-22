using System.IO;
using Microsoft.Data.Sqlite;
using WireCabinet.Core;

namespace WireCabinet.Data;

/// <summary>
/// SQLite 连接与执行封装。应用库与 MES mock 共用同一个数据库文件。
/// 首次创建时执行 schema + seed。
/// </summary>
public sealed class SqliteDb
{
    private readonly string _connectionString;

    public string DbPath { get; }

    /// <summary>
    /// 忙等待上限（秒）。库被其他写入者占用时，SQLite 会重试到这个上限再报 "database is locked"。
    /// 不显式设置会用默认值，现场多个写入者（硬件轮询 2s、AGV 轮询 5s、MES 对账 2min）叠加时
    /// 容易等到很晚才失败，表现为界面卡顿。
    /// </summary>
    public const int BusyTimeoutSeconds = 15;

    public SqliteDb(string dbPath)
    {
        DbPath = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = BusyTimeoutSeconds,
        }.ToString();
    }

    /// <summary>
    /// 开启 WAL（写前日志）。WAL 是**库文件的持久属性**，设一次即可，但对**已存在的库**必须显式设置——
    /// 所以放在每次启动的迁移链里，而不是建库脚本里（建库脚本只在首次建库时执行）。
    ///
    /// 为什么需要：默认的 rollback journal 模式下读会阻塞写、写会阻塞读，
    /// 而本库同时被硬件轮询、AGV 轮询与记录写入共用，争用会表现为界面卡顿而非报错。
    /// WAL 下读写不互斥，显著降低争用。返回设置后的实际模式，便于断言。
    /// </summary>
    public string EnsureWal()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    /// <summary>当前日志模式，供自检与测试使用。</summary>
    public string JournalMode()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    /// <summary>初始化数据库：建库 + 灌种子。recreate=true 时删除旧文件重建。</summary>
    public void Initialize(bool recreate = false)
    {
        if (recreate && File.Exists(DbPath))
        {
            SqliteConnection.ClearAllPools();
            File.Delete(DbPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

        using var conn = Open();
        ExecScript(conn, File.ReadAllText(CabinetPaths.SchemaScript));
        if (File.Exists(CabinetPaths.CreateMaterialsScript))
            ExecScript(conn, File.ReadAllText(CabinetPaths.CreateMaterialsScript));
        if (File.Exists(CabinetPaths.SeedMaterialsScript))
            ExecScript(conn, File.ReadAllText(CabinetPaths.SeedMaterialsScript));
        if (File.Exists(CabinetPaths.SeedSlotScript))
            ExecScript(conn, File.ReadAllText(CabinetPaths.SeedSlotScript));
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void ExecScript(SqliteConnection conn, string script)
    {
        foreach (var statement in SplitStatements(script))
        {
            if (string.IsNullOrWhiteSpace(statement)) continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            cmd.ExecuteNonQuery();
        }
    }

    private static IEnumerable<string> SplitStatements(string script)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in script.Split('\n', StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("--", StringComparison.Ordinal)) continue;
            sb.AppendLine(line);
            if (trimmed.EndsWith(";", StringComparison.Ordinal))
            {
                yield return sb.ToString().Trim();
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            yield return sb.ToString().Trim();
    }

    public static bool HasAppSlotTable(string dbPath)
    {
        if (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0)
            return false;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='app_slot' LIMIT 1";
            return cmd.ExecuteScalar() is not null;
        }
        catch
        {
            return false;
        }
    }

    public DbResult Query(string sql, IDictionary<string, object?> parameters)
    {
        var result = new DbResult { RenderedSql = RenderForDisplay(sql, parameters) };
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            Bind(cmd, sql, parameters);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[reader.GetName(i)] = val;
                }
                result.Rows.Add(row);
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }

    public DbResult Execute(string sql, IDictionary<string, object?> parameters)
    {
        var result = new DbResult { RenderedSql = RenderForDisplay(sql, parameters) };
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            Bind(cmd, sql, parameters);
            result.RowsAffected = cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }

    private static void Bind(SqliteCommand cmd, string sql, IDictionary<string, object?> parameters)
    {
        foreach (var (key, value) in parameters)
        {
            // 仅绑定 SQL 中实际出现的参数，避免多余参数报错
            if (!sql.Contains(":" + key, StringComparison.Ordinal)) continue;
            cmd.Parameters.AddWithValue(":" + key, value ?? DBNull.Value);
        }
    }

    private static string RenderForDisplay(string sql, IDictionary<string, object?> parameters)
    {
        var rendered = sql;
        foreach (var (key, value) in parameters.OrderByDescending(p => p.Key.Length))
        {
            var display = value is null ? "NULL" : $"'{value}'";
            rendered = rendered.Replace(":" + key, display, StringComparison.Ordinal);
        }
        return rendered.Trim();
    }
}
