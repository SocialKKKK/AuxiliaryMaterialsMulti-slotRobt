using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// app_operation_log 列迁移。这两个测试是「现场老库升级」的机器化保障：
/// schema.sqlite.sql 用 CREATE TABLE IF NOT EXISTS，对已存在的库不加列，
/// 必须靠 OperationLogSchema.Ensure 补齐——漏了现场就会 no such column。
/// </summary>
public class OperationLogSchemaTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    /// <summary>0.1 形态的既有列，升级后必须一个不少。</summary>
    private static readonly string[] LegacyColumns =
    {
        "operator_id", "flow_id", "node_id", "slot_no", "lot_no",
        "message", "mes_result", "lock_result", "error_info",
    };

    [Fact]
    public void FreshDatabase_SchemaScript_AlreadyHasEveryNewColumn()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);

        using var conn = db.Open();
        var columns = Columns(conn, "app_operation_log");

        // 建表脚本与迁移清单必须保持一致，否则新库与老库结构会分叉
        foreach (var (column, _) in OperationLogSchema.Columns)
            Assert.Contains(column, columns, StringComparer.OrdinalIgnoreCase);

        Assert.Contains("operation_log_pending", Tables(conn));
    }

    [Fact]
    public void LegacyDatabase_Ensure_AddsColumns_KeepsOldColumnsAndData_AndIsIdempotent()
    {
        // 造一个 0.1 形态的库：只有既有列，且已有一行历史数据
        using (var conn = Open(_dbPath))
        {
            Exec(conn, """
                CREATE TABLE app_operation_log (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts           TEXT NOT NULL DEFAULT (datetime('now')),
                    operator_id  TEXT,
                    flow_id      TEXT,
                    node_id      TEXT,
                    slot_no      TEXT,
                    lot_no       TEXT,
                    message      TEXT,
                    mes_result   TEXT,
                    lock_result  TEXT,
                    error_info   TEXT
                );
                """);
            Exec(conn, "INSERT INTO app_operation_log (ts, operator_id, slot_no) VALUES ('2026-01-01 00:00:00','OP001','F-A01');");
        }

        using (var conn = Open(_dbPath))
        {
            OperationLogSchema.Ensure(conn);
            OperationLogSchema.Ensure(conn); // 幂等：重复执行不报错

            var columns = Columns(conn, "app_operation_log");

            foreach (var (column, _) in OperationLogSchema.Columns)
                Assert.Contains(column, columns, StringComparer.OrdinalIgnoreCase);
            foreach (var legacy in LegacyColumns)
                Assert.Contains(legacy, columns, StringComparer.OrdinalIgnoreCase);

            Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM app_operation_log WHERE operator_id='OP001' AND slot_no='F-A01';"));
            Assert.Contains("operation_log_pending", Tables(conn));
        }
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        return conn;
    }

    private static List<string> Columns(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(1));
        return list;
    }

    private static List<string> Tables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar()!);
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
