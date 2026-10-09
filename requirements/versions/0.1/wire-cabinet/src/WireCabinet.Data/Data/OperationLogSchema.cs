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
        ("detail",         "TEXT"),
        ("slot_count",     "INTEGER"),
        ("corrects_id",    "INTEGER"),
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

        // 触发器引用了上面刚补的列，必须在补列之后创建。
        Exec(conn, AppendOnlyTriggerSql);

        Exec(conn, PendingTableSql);

        // 待补偿表同样要补列：老库里该表已存在，CREATE TABLE IF NOT EXISTS 不会加列。
        foreach (var (column, definition) in PendingColumns)
            EnsureColumn(conn, "operation_log_pending", column, definition);
    }

    /// <summary>
    /// 库层的「业务事实不可改写」触发器（口径优化 A 档）。与 <c>scripts/schema.sqlite.sql</c>
    /// 中的定义必须一致——老库靠这里幂等补建，新库走建表脚本。改动请同步两处。
    ///
    /// 放行两件事：补齐 NULL 的门时刻；更新 <c>detail</c>（过程派生字段，承载「连续失败次数 /
    /// 首末时刻」这类随后续事件增长的信息）。业务事实列一律 ABORT。
    /// </summary>
    private const string AppendOnlyTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS trg_app_operation_log_append_only
        BEFORE UPDATE ON app_operation_log
        FOR EACH ROW
        WHEN
               NEW.id             IS NOT OLD.id
            OR NEW.ts             IS NOT OLD.ts
            OR NEW.operator_id    IS NOT OLD.operator_id
            OR NEW.operator_name  IS NOT OLD.operator_name
            OR NEW.operator_team  IS NOT OLD.operator_team
            OR NEW.operator_role  IS NOT OLD.operator_role
            OR NEW.action_type    IS NOT OLD.action_type
            OR NEW.slot_no        IS NOT OLD.slot_no
            OR NEW.lot_no         IS NOT OLD.lot_no
            OR NEW.wire_spec      IS NOT OLD.wire_spec
            OR NEW.eqp_no         IS NOT OLD.eqp_no
            OR NEW.product_lot_no IS NOT OLD.product_lot_no
            OR NEW.station        IS NOT OLD.station
            OR NEW.qty            IS NOT OLD.qty
            OR NEW.remain_qty     IS NOT OLD.remain_qty
            OR NEW.result         IS NOT OLD.result
            OR NEW.message        IS NOT OLD.message
            OR NEW.mes_result     IS NOT OLD.mes_result
            OR NEW.lock_result    IS NOT OLD.lock_result
            OR NEW.error_info     IS NOT OLD.error_info
            OR NEW.is_cleared     IS NOT OLD.is_cleared
            OR NEW.is_uncommanded IS NOT OLD.is_uncommanded
            OR NEW.flow_id        IS NOT OLD.flow_id
            OR NEW.node_id        IS NOT OLD.node_id
            OR NEW.slot_count     IS NOT OLD.slot_count
            OR NEW.corrects_id    IS NOT OLD.corrects_id
            OR (OLD.door_open_ts  IS NOT NULL AND NEW.door_open_ts  IS NOT OLD.door_open_ts)
            OR (OLD.door_close_ts IS NOT NULL AND NEW.door_close_ts IS NOT OLD.door_close_ts)
        BEGIN
            SELECT RAISE(ABORT, 'app_operation_log 业务事实不可改写：仅允许补齐 NULL 的门时刻与更新 detail');
        END;
        """;

    /// <summary>待补偿表：记录写入失败时转存整条记录（JSON），供后台重试与人工重试。</summary>
    private const string PendingTableSql = """
        CREATE TABLE IF NOT EXISTS operation_log_pending (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            ts            TEXT NOT NULL DEFAULT (datetime('now')),
            entry_json    TEXT NOT NULL,
            status        TEXT NOT NULL DEFAULT 'pending',
            retry_count   INTEGER NOT NULL DEFAULT 0,
            next_retry_at TEXT,
            last_error    TEXT,
            last_try_ts   TEXT
        );
        """;

    /// <summary>
    /// 待补偿表在 0.2 期间新增的列。status 用于区分 pending / failed / success，
    /// next_retry_at 供后台按退避重试。
    /// </summary>
    private static readonly (string Column, string Definition)[] PendingColumns =
    {
        ("status", "TEXT NOT NULL DEFAULT 'pending'"),
        ("next_retry_at", "TEXT"),
    };

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
