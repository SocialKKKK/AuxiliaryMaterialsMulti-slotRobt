using Microsoft.Data.Sqlite;

namespace WireCabinet.Data;

/// <summary>
/// app_operation_log 及其待补偿表的列级迁移。
///
/// 为什么需要它：<c>scripts/schema.sqlite.sql</c> 全部使用 CREATE TABLE IF NOT EXISTS，
/// 而 <see cref="SqliteDb.Initialize"/> 只顺序执行建表与种子脚本、没有列级迁移。
/// 因此**现场已存在的 app.db 不会因为改了建表语句就获得新列**，必须在这里幂等补齐。
///
/// 做法沿用工程内既有模式：WireCabinet.Hmi.Services.MhInterruptedLoadStore.EnsureColumn
/// （先 PRAGMA table_info 判断，再 ALTER TABLE ADD COLUMN）。
///
/// 改这张表时，请同时更新 <c>scripts/schema.sqlite.sql</c>；测试会校验两者一致。
/// </summary>
public static class OperationLogSchema
{
    /// <summary>相对 0.1 为 app_operation_log 新增的列。仅新增，不修改、不删除既有列。</summary>
    public static readonly (string Column, string Definition)[] Columns =
    {
        ("operator_name",  "TEXT"),
        ("operator_team",  "TEXT"),
        ("operator_role",  "TEXT"),
        ("action_type",    "TEXT"),
        ("wire_spec",      "TEXT"),
        ("eqp_no",         "TEXT"),
        ("product_lot_no", "TEXT"),
        ("qty",            "REAL"),
        ("remain_qty",     "REAL"),
        ("station",        "TEXT"),
        ("result",         "TEXT"),
        ("door_open_ts",   "TEXT"),
        ("door_close_ts",  "TEXT"),
        ("is_cleared",     "INTEGER NOT NULL DEFAULT 0"),
        ("is_uncommanded", "INTEGER NOT NULL DEFAULT 0"),
    };

    /// <summary>查询用索引：六项筛选条件 + 追溯。</summary>
    private static readonly (string Name, string Columns)[] Indexes =
    {
        ("ix_app_operation_log_ts", "ts"),
        ("ix_app_operation_log_lot_no", "lot_no"),
        ("ix_app_operation_log_slot_no", "slot_no"),
        ("ix_app_operation_log_operator_id", "operator_id"),
        ("ix_app_operation_log_eqp_no", "eqp_no"),
    };

    public static void Ensure(SqliteDb db)
    {
        using var conn = db.Open();
        Ensure(conn);
    }

    /// <summary>幂等：可重复执行，已存在的列与索引会被跳过。</summary>
    public static void Ensure(SqliteConnection conn)
    {
        foreach (var (column, definition) in Columns)
            EnsureColumn(conn, "app_operation_log", column, definition);

        foreach (var (name, columns) in Indexes)
            Exec(conn, $"CREATE INDEX IF NOT EXISTS {name} ON app_operation_log({columns});");

        Exec(conn, PendingTableSql);
    }

    /// <summary>待补偿表：记录写入失败时转存整条记录（JSON），供后台重试与人工重试。</summary>
    private const string PendingTableSql = """
        CREATE TABLE IF NOT EXISTS operation_log_pending (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            ts          TEXT NOT NULL DEFAULT (datetime('now')),
            entry_json  TEXT NOT NULL,
            retry_count INTEGER NOT NULL DEFAULT 0,
            last_error  TEXT,
            last_try_ts TEXT
        );
        """;

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table});";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        Exec(conn, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 幂等：并发或重复执行导致的失败忽略
        }
    }
}
