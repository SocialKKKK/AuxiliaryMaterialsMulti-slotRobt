using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using WireCabinet.Flows;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 这组用例会调 SqliteConnection.ClearAllPools() 清临时库；与其它并行集合错开，
/// 避免清理池时把别的用例正在用的连接一起关掉（Release 下曾出现 Dispose 删库文件被占用）。
/// </summary>
[CollectionDefinition("SqliteFileSerial", DisableParallelization = true)]
public sealed class SqliteFileSerialCollection
{
}

/// <summary>
/// 中断领用的自动收尾（2026-10-22 现场 R-C01）。
///
/// 现场问题：MES 领用提交成功、程序在关门确认前中断，格口被标成「领用待取」；
/// 原方案要求物料员专门去开这一格核对，等于给 MH 增加了新规程。
///
/// 修复后的契约：
/// 1. 只对「领用待取」自动收尾；「领用中」、门未关、标记太新的一律不动；
/// 2. 自动收尾清空格口（complete_issue_pickup），并在原记录 detail 上留一句说明，
///    门时刻保持 NULL（事实未知，不伪造）；
/// 3. detail 只写一次，重复跑不会覆盖。
/// </summary>
[Collection("SqliteFileSerial")]
public class IssueAutoCompleteTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-autoclose-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void PendingState_ClosedDoor_OldEnough_AutoCompletes()
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(OpIssueAutoCompletePolicy.ShouldAutoComplete(
            "领用待取", now.AddMinutes(-11), doorOpen: false, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void InProgressState_IsNeverAutoCompleted()
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(OpIssueAutoCompletePolicy.ShouldAutoComplete(
            "领用中", now.AddHours(-2), doorOpen: false, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void OpenDoor_IsNeverAutoCompleted()
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(OpIssueAutoCompletePolicy.ShouldAutoComplete(
            "领用待取", now.AddHours(-2), doorOpen: true, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void RecentMarker_IsLeftForTheOperator()
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(OpIssueAutoCompletePolicy.ShouldAutoComplete(
            "领用待取", now.AddMinutes(-9), doorOpen: false, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void UnknownMarkerTime_IsLeftAlone()
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(OpIssueAutoCompletePolicy.ShouldAutoComplete(
            "领用待取", null, doorOpen: false, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void CompleteIssuePickup_ClearsQuarantinedSlot()
    {
        var (db, catalog) = NewDb();
        db.Execute(
            "UPDATE app_slot SET biz_state='available_wire', wire_state='领用待取', wire_lot_no='L1' WHERE slot_id=5;",
            new Dictionary<string, object?>());

        var appDb = new SqliteAppDb(db);
        var result = appDb.Run(catalog.Find("app.slot.complete_issue_pickup")!, new Dictionary<string, object?>
        {
            ["slot_id"] = 5L
        });

        Assert.False(result.HasError, result.Error);
        Assert.Equal(1, result.RowsAffected);
        var slot = ReadSlot(db, 5);
        Assert.Equal("idle", slot["biz_state"]?.ToString());
        Assert.Null(slot["wire_state"]);
        Assert.Null(slot["wire_lot_no"]);
    }

    [Fact]
    public void MarkAutoPickup_WritesLatestMatchingRecordOnlyOnce()
    {
        var (db, catalog) = NewDb();
        InsertIssue(db, catalog, "2026-10-22 06:00:00", "R-C01", "9022278626-7835");
        InsertIssue(db, catalog, "2026-10-22 06:22:13", "R-C01", "9022278626-7835");
        InsertIssue(db, catalog, "2026-10-22 06:30:00", "R-C02", "OTHER-LOT");

        var appDb = new SqliteAppDb(db);
        var mark = catalog.Find("app.operation_log.mark_auto_pickup")!;
        var args = new Dictionary<string, object?>
        {
            ["detail"] = "程序中断；启动巡检按门已关自动完成清库",
            ["slot_no"] = "R-C01",
            ["lot_no"] = "9022278626-7835"
        };

        var first = appDb.Run(mark, args);
        Assert.False(first.HasError, first.Error);
        Assert.Equal(1, first.RowsAffected);

        var rows = db.Query(
            "SELECT id, slot_no, detail, door_open_ts, door_close_ts FROM app_operation_log ORDER BY id;",
            new Dictionary<string, object?>()).Rows;
        Assert.Null(rows[0]["detail"]);
        Assert.NotNull(rows[1]["detail"]);
        Assert.Null(rows[2]["detail"]);
        Assert.Null(rows[1]["door_open_ts"]);
        Assert.Null(rows[1]["door_close_ts"]);

        var second = appDb.Run(mark, args);
        Assert.Equal(0, second.RowsAffected);
    }

    private (SqliteDb Db, SqlCatalog Catalog) NewDb()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        WireMesDiscoSchema.EnsureAll(db);
        WireMesMockSchema.Ensure(db);
        OperationLogSchema.Ensure(db);
        return (db, SqlCatalog.Load());
    }

    private static void InsertIssue(SqliteDb db, SqlCatalog catalog, string ts, string slotNo, string lotNo)
    {
        var result = new SqliteAppDb(db).Run(catalog.Find("app.operation_log.insert")!, new Dictionary<string, object?>
        {
            ["ts"] = ts,
            ["operator_id"] = "15103501",
            ["operator_name"] = "沈宇杰",
            ["operator_team"] = "甲班",
            ["operator_role"] = "OP",
            ["action_type"] = "op_issue_available_wire",
            ["slot_no"] = slotNo,
            ["lot_no"] = lotNo,
            ["wire_spec"] = "φ50(MAXSOFT)",
            ["eqp_no"] = "3QHS3076",
            ["product_lot_no"] = "Q26102336-4",
            ["qty"] = 500.0,
            ["remain_qty"] = null,
            ["station"] = null,
            ["result"] = "成功",
            ["message"] = "领用提交完成",
            ["mes_result"] = "ok",
            ["lock_result"] = null,
            ["error_info"] = null,
            ["door_open_ts"] = null,
            ["door_close_ts"] = null,
            ["is_cleared"] = 0,
            ["is_uncommanded"] = 0,
            ["flow_id"] = "operator_return_wire_and_issue_available_wire",
            ["node_id"] = "submitWireIssue",
            ["detail"] = null,
            ["corrects_id"] = null
        });
        Assert.False(result.HasError, result.Error);
        Assert.Equal(1, result.RowsAffected);
    }

    private static Dictionary<string, object?> ReadSlot(SqliteDb db, long slotId) =>
        db.Query("SELECT * FROM app_slot WHERE slot_id = :slot_id;",
            new Dictionary<string, object?> { ["slot_id"] = slotId }).Rows.Single();
}
