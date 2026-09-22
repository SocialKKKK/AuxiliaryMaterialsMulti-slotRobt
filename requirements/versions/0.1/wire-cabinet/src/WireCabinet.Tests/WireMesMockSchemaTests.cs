using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// MES mock 表与演示数据的迁移。
///
/// 背景（真跑应用才发现）：<c>SqliteDb.Initialize</c> 只在**首次建库**时执行
/// （AppBootstrap 用 HasAppSlotTable 判断），所以把 mock MES 表挂在建库脚本里，
/// 已存在的 app.db 永远拿不到它们 —— 本机跑不起操作员流程。改成每次启动都跑的
/// 幂等迁移后，这里同时钉住两件事：老库能被补上，且已有数据不会被覆盖。
/// </summary>
public class WireMesMockSchemaTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-mesmock-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    /// <summary>只建应用库（模拟开发机上已有的 app.db），不含任何 mock MES 表。</summary>
    private SqliteDb ExistingAppDbWithoutMockMes()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        Assert.False(TableExists(db, "mv_fw_username"));
        return db;
    }

    private static bool TableExists(SqliteDb db, string table) =>
        db.Query(
            "SELECT COUNT(*) AS c FROM sqlite_master WHERE type = 'table' AND name = :name;",
            new Dictionary<string, object?> { ["name"] = table }).First!["c"] is { } v
        && Convert.ToInt64(v) > 0;

    private static long RowCount(SqliteDb db, string table) =>
        Convert.ToInt64(db.Query($"SELECT COUNT(*) AS c FROM {table};", new Dictionary<string, object?>()).First!["c"]);

    [Fact]
    public void Ensure_OnExistingDb_CreatesMockMesTablesAndDemoData()
    {
        var db = ExistingAppDbWithoutMockMes();

        WireMesMockSchema.Ensure(db);

        foreach (var table in new[]
                 {
                     "mv_fw_username", "v_fw_material_indetail",
                     "fw_eqpres_eqpinformation", "fw_wip_trans", "v_fw_wip_sublot",
                 })
        {
            Assert.True(TableExists(db, table), $"未建出 {table}");
            Assert.True(RowCount(db, table) > 0, $"{table} 没有演示数据");
        }

        // 演示批号必须与 app_slot 种子对得上，否则柜内焊丝在 MES 里查不到
        Assert.Equal(1L, RowCount(db, "v_fw_material_indetail WHERE sublot = '14Z1888-0888'"));
        Assert.Equal(1L, RowCount(db, "mv_fw_username WHERE usercode = '1001'"));
    }

    [Fact]
    public void Ensure_IsIdempotent()
    {
        var db = ExistingAppDbWithoutMockMes();

        WireMesMockSchema.Ensure(db);
        var first = RowCount(db, "v_fw_material_indetail");

        WireMesMockSchema.Ensure(db);
        WireMesMockSchema.Ensure(db);

        Assert.Equal(first, RowCount(db, "v_fw_material_indetail"));
    }

    /// <summary>
    /// 这个迁移每次启动都跑，因此**不能**照建库脚本那样先清后插——
    /// 否则每次启动都会把手工录入的演示数据抹掉。
    /// </summary>
    [Fact]
    public void Ensure_DoesNotOverwriteExistingData()
    {
        var db = ExistingAppDbWithoutMockMes();
        WireMesMockSchema.Ensure(db);

        var insert = db.Execute(
            "INSERT INTO v_fw_material_indetail (sublot, spec, type, state) VALUES ('LOT-MANUAL', 'φ1.0-手工', '焊丝', '可用');",
            new Dictionary<string, object?>());
        Assert.False(insert.HasError, insert.Error);
        var before = RowCount(db, "v_fw_material_indetail");

        WireMesMockSchema.Ensure(db);

        Assert.Equal(before, RowCount(db, "v_fw_material_indetail"));
        Assert.Equal(1L, RowCount(db, "v_fw_material_indetail WHERE sublot = 'LOT-MANUAL'"));
    }

    /// <summary>补上 mock MES 后，操作员流程需要的四张表都可查（此前每张都报「无此表」）。</summary>
    [Fact]
    public void Ensure_MakesOperatorFlowQueriesResolvable()
    {
        var db = ExistingAppDbWithoutMockMes();
        WireMesMockSchema.Ensure(db);

        Assert.Equal(1L, RowCount(db, "mv_fw_username WHERE usercode = '1001'"));
        Assert.Equal(1L, RowCount(db, "fw_eqpres_eqpinformation WHERE eqpno = '1QH029'"));

        var latest = db.Query(
            """
            SELECT COALESCE(MAX(lot), 'N/A') AS lot
            FROM (SELECT lot FROM fw_wip_trans
                  WHERE eqp = '1QH029' AND task = '完工'
                    AND COALESCE(remark, 'NA') NOT LIKE '取消%'
                  ORDER BY dates DESC LIMIT 1);
            """,
            new Dictionary<string, object?>()).First;

        Assert.Equal("P20260602-01", latest!["lot"]);
    }
}
