namespace WireCabinet.Data;

/// <summary>
/// MES mock（SQLite）表与演示数据。本机/离线模式下 MES 只读查询直接打在这些表上。
///
/// **为什么是代码里的迁移，而不是 scripts/*.sql**：<c>SqliteDb.Initialize</c> 只在**首次建库**
/// 时执行（AppBootstrap 用 HasAppSlotTable 判断），而开发机上更常见的是**已存在**的 app.db——
/// 挂在建库脚本里，老库永远拿不到这些表，操作员流程在本机依旧跑不起来。
/// 故沿用 WireMesDiscoSchema.EnsureMockMesColumn 的既有做法，做成每次启动都执行的幂等迁移。
///
/// **演示数据只在表为空时补，不做 DELETE**：本迁移每次启动都跑，若照建库脚本那样先清后插，
/// 每次启动都会把手工录入的演示数据抹掉。需要重置时删掉 data/app.db 重建即可。
///
/// 现场用 Oracle 时这些表不会被读：FlowEngine 对 mes_mock.* 条目会改走正式 catalog。
/// </summary>
public static class WireMesMockSchema
{
    /// <summary>批号必须与 seed-app_slot.sqlite.sql 的格口数据一致，否则柜内焊丝在 MES 里查不到。</summary>
    private static readonly (string Table, string Insert)[] Seed =
    [
        ("mv_fw_username",
            "INSERT INTO mv_fw_username (usercode, username) VALUES ('1001', '张三'), ('1002', '李四');"),

        ("v_fw_material_indetail",
            """
            INSERT INTO v_fw_material_indetail (sublot, spec, type, code, shelflife, matlot, qty, state, DISCOEQPNO) VALUES
             ('14Z1888-0888', 'φ42(MAXSOFT)', '焊丝', 'WIRE-φ42', '2026-12-31 00:00:00', '14Z1888', 1.0, '可用', NULL),
             ('14Z1888-0777', 'φ42(MAXSOFT)', '焊丝', 'WIRE-φ42', '2026-10-31 00:00:00', '14Z1888', 1.0, '归还', NULL),
             ('14Z1999-0001', 'φ36(SOFT)',    '焊丝', 'WIRE-φ36', '2026-11-30 00:00:00', '14Z1999', 1.0, '可用', NULL),
             ('14Z1888-0160', 'φ42(MAXSOFT)', '焊丝', 'WIRE-φ42', '2026-12-31 00:00:00', '14Z1888', 1.0, '归还', NULL);
            """),

        ("fw_eqpres_eqpinformation",
            "INSERT INTO fw_eqpres_eqpinformation (eqpno, step) VALUES ('1QH029', '焊线'), ('1QH030', '焊线');"),

        ("fw_wip_trans",
            """
            INSERT INTO fw_wip_trans (lot, eqp, step, task, remark, dates) VALUES
             ('P20260601-01', '1QH029', '焊线01', '完工', NULL,       '2026-06-01 08:00:00'),
             ('P20260602-01', '1QH029', '键合01', '完工', NULL,       '2026-06-02 09:30:00'),
             ('P20260602-09', '1QH029', '焊线01', '完工', '取消-误报', '2026-06-02 23:59:00');
            """),

        ("v_fw_wip_sublot",
            """
            INSERT INTO v_fw_wip_sublot (lot, qty, step, state) VALUES
             ('P20260602-01', 3000, '焊线', '在制'),
             ('P20260602-01', 2000, '焊线', '在制'),
             ('P20260601-01', 1500, '键合', '关闭');
            """),
    ];

    /// <summary>mock MES 表结构。v_fw_material_indetail 含 DISCOEQPNO（与工位同步列一致）。</summary>
    private const string TablesSql = """
        CREATE TABLE IF NOT EXISTS mv_fw_username (
            usercode TEXT PRIMARY KEY,
            username TEXT
        );

        CREATE TABLE IF NOT EXISTS v_fw_material_indetail (
            sublot     TEXT,
            spec       TEXT,
            type       TEXT,
            code       TEXT,
            shelflife  TEXT,
            matlot     TEXT,
            qty        REAL,
            state      TEXT,
            DISCOEQPNO TEXT
        );

        CREATE TABLE IF NOT EXISTS fw_eqpres_eqpinformation (
            eqpno TEXT,
            step  TEXT
        );

        CREATE TABLE IF NOT EXISTS fw_wip_trans (
            lot    TEXT,
            eqp    TEXT,
            step   TEXT,
            task   TEXT,
            remark TEXT,
            dates  TEXT
        );

        CREATE TABLE IF NOT EXISTS v_fw_wip_sublot (
            lot   TEXT,
            qty   REAL,
            step  TEXT,
            state TEXT
        );
        """;

    /// <summary>幂等：可重复执行；表已存在则跳过建表，表内已有数据则不补演示数据。</summary>
    public static void Ensure(SqliteDb db)
    {
        using var conn = db.Open();

        using (var create = conn.CreateCommand())
        {
            create.CommandText = TablesSql;
            create.ExecuteNonQuery();
        }

        foreach (var (table, insert) in Seed)
        {
            if (HasRows(conn, table)) continue;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = insert;
            cmd.ExecuteNonQuery();
        }
    }

    private static bool HasRows(Microsoft.Data.Sqlite.SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }
}
