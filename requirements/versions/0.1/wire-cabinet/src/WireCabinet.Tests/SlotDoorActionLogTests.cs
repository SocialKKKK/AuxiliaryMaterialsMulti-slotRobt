using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using WireCabinet.Slots;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 格口开/关门动作的记录（0.2 §3 覆盖矩阵第 5/6/7/9 项）。
///
/// 重点在第 6 项：**批量开门条数必须与实际开门数一致**——这正是放在 Slots 层而不是
/// 界面层的理由，界面层（net8.0-windows）测试引用不了，条数错了也没人拦。
/// </summary>
public class SlotDoorActionLogTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-door-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private (SqliteDb Db, SqliteOperationLogSink Sink, SlotDoorActionLog Log) NewLog()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        OperationLogSchema.Ensure(db);

        var sink = new SqliteOperationLogSink(SqlCatalog.Load(), new SqliteAppDb(db));
        return (db, sink, new SlotDoorActionLog(sink));
    }

    private static List<Dictionary<string, object?>> Rows(SqliteDb db) =>
        db.Query("SELECT * FROM app_operation_log ORDER BY id;", new Dictionary<string, object?>()).Rows;

    [Fact]
    public void SingleOpen_WritesOneRecord()
    {
        var (db, _, log) = NewLog();

        Assert.NotNull(log.RecordOpened(
            SlotDoorActionLog.MhTakeWire, "F-B02", DoorOperator.Mh("1001", "张三", "A班")));

        var row = Assert.Single(Rows(db));
        Assert.Equal("mh_take_wire", row["action_type"]);
        Assert.Equal("F-B02", row["slot_no"]);
        Assert.Equal("成功", row["result"]);
        Assert.Equal("MH", row["operator_role"]);
        Assert.Equal("1001", row["operator_id"]);
        Assert.Equal("张三", row["operator_name"]);
        Assert.Equal("A班", row["operator_team"]);
        Assert.False(string.IsNullOrWhiteSpace(row["door_open_ts"]?.ToString()));
        Assert.Null(row["door_close_ts"]);
    }

    /// <summary>
    /// 口径优化（2026-10-09）：关门不再另写一条，而是把 door_close_ts 补进开门那条——
    /// 一次开门始终只有一条记录。
    /// </summary>
    [Fact]
    public void Close_BackfillsDoorCloseTsOnTheSameRecord()
    {
        var (db, _, log) = NewLog();
        const string openedAt = "2026-09-22 09:19:58";
        const string closedAt = "2026-09-22 09:20:31";
        var op = DoorOperator.Mh("1001", "张三", "A班");
        var rowId = log.RecordOpened(SlotDoorActionLog.MhTakeWire, "F-B02", op, openedAt);
        Assert.NotNull(rowId);
        Assert.True(log.RecordClosed(rowId, SlotDoorActionLog.MhTakeWire, openedAt, closedAt));

        var row = Assert.Single(Rows(db));
        Assert.Equal("mh_take_wire", row["action_type"]);
        Assert.Equal(openedAt, row["door_open_ts"]);
        Assert.Equal(closedAt, row["door_close_ts"]);
        Assert.Equal("1001", row["operator_id"]);
    }

    /// <summary>
    /// § 矩阵第 6 项（口径收敛 2026-10-09）：批量开门**逐格一条**，打开 N 格就是 N 条
    /// 「MH 取出旧焊丝」——一次批量在语义上就是"逐格取走"，而"取出一个一定记一条"。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(54)]
    public void BatchOpen_WritesOneRecordPerSlot(int count)
    {
        var (db, _, log) = NewLog();

        for (var i = 1; i <= count; i++)
            Assert.NotNull(log.RecordOpened(SlotDoorActionLog.MhTakeWire, $"F-{i:00}", DoorOperator.Mh()));

        var rows = Rows(db);
        Assert.Equal(count, rows.Count);
        Assert.All(rows, r => Assert.Equal("mh_take_wire", r["action_type"]));
        // 每格一条，格口号与开门口一一对应
        Assert.Equal(
            Enumerable.Range(1, count).Select(i => $"F-{i:00}").ToList(),
            rows.Select(r => r["slot_no"]?.ToString() ?? "").ToList());
    }

    /// <summary>空格口也记一条（系统分不清"取走了"还是"本来就空"），只是批号为空、不置 is_cleared。</summary>
    [Fact]
    public void OpenWithoutClearedLot_RecordsTheSlotWithEmptyLot()
    {
        var (db, _, log) = NewLog();

        Assert.NotNull(log.RecordOpened(SlotDoorActionLog.MhTakeWire, "F-A01", DoorOperator.Mh()));

        var row = Assert.Single(Rows(db));
        Assert.Null(row["lot_no"]);
        Assert.Equal(0L, Convert.ToInt64(row["is_cleared"]));
        Assert.Contains("取出旧焊丝", row["message"]?.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// **同一秒打开的两格，关门必须各回填各的**（2026-10-09 复查发现）。
    ///
    /// 批量开门是每格 400ms，同一种动作在同一秒出现两条是常态。回填曾经按
    /// (action_type, ts) 定位，而 UPDATE 是先执行、后检查影响行数 ——
    /// 先关的那一格会把两条记录的 door_close_ts 都写成它的关门时刻，检查拦不住。
    /// 现在按 rowid 定位。
    /// </summary>
    [Fact]
    public void CloseBackfill_TwoSlotsOpenedInTheSameSecond_EachKeepsItsOwnCloseTime()
    {
        var (db, _, log) = NewLog();
        const string sameSecond = "2026-10-09 02:42:41";
        var op = DoorOperator.Mh();

        var first = log.RecordOpened(SlotDoorActionLog.MhTakeWire, "F-A01", op, sameSecond);
        var second = log.RecordOpened(SlotDoorActionLog.MhTakeWire, "F-A02", op, sameSecond);
        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.True(log.RecordClosed(first, SlotDoorActionLog.MhTakeWire, sameSecond, "2026-10-09 02:45:05"));
        Assert.True(log.RecordClosed(second, SlotDoorActionLog.MhTakeWire, sameSecond, "2026-10-09 02:47:59"));

        var rows = Rows(db).OrderBy(r => r["slot_no"]?.ToString(), StringComparer.Ordinal).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("2026-10-09 02:45:05", rows[0]["door_close_ts"]);   // F-A01
        Assert.Equal("2026-10-09 02:47:59", rows[1]["door_close_ts"]);   // F-A02（修复前会被写成 02:45:05）
    }

    /// <summary>§ 矩阵第 9 项：开锁失败也留一条，且带原因。</summary>
    [Fact]
    public void FailedOpen_WritesFailureRecordWithReason()
    {
        var (db, _, log) = NewLog();

        Assert.True(log.RecordFailed(
            SlotDoorActionLog.MhTakeWire, "F-B02", "格口开锁失败（已禁用、硬件无响应或格口不存在）。", DoorOperator.Mh()));

        var row = Assert.Single(Rows(db));
        Assert.Equal("失败", row["result"]);
        Assert.Equal("F-B02", row["slot_no"]);
        Assert.False(string.IsNullOrWhiteSpace(row["error_info"]?.ToString()));
    }

    /// <summary>§ 矩阵第 7 项：维护试开也留痕，且维护界面没有工号，只记角色。</summary>
    [Fact]
    public void MaintTrial_WritesRecordWithRoleOnly()
    {
        var (db, _, log) = NewLog();

        log.RecordOpened(SlotDoorActionLog.MaintTrialOpen, "F-A03", DoorOperator.Maint());

        var row = Assert.Single(Rows(db));
        Assert.Equal("maint_trial_open", row["action_type"]);
        Assert.Equal("MAINT", row["operator_role"]);
        Assert.Null(row["operator_id"]);
    }

    /// <summary>§ 矩阵第 12 项：同一格口连续多次操作，每次各一条。</summary>
    [Fact]
    public void RepeatedOpensOnSameSlot_EachWritesItsOwnRecord()
    {
        var (db, _, log) = NewLog();

        for (var i = 0; i < 3; i++)
            log.RecordOpened(SlotDoorActionLog.MhTakeWire, "F-B02", DoorOperator.Mh("1001", "张三", "A班"));

        Assert.Equal(3, Rows(db).Count);
        Assert.All(Rows(db), r => Assert.Equal("F-B02", r["slot_no"]));
    }

    /// <summary>没有 sink（记录功能被停用）时不得抛异常，也不该影响开门（§1.4 第 6 条：关闭即可复原）。</summary>
    [Fact]
    public void WithoutSink_IsNoOp()
    {
        var log = new SlotDoorActionLog(null);

        Assert.Null(log.RecordOpened(SlotDoorActionLog.MhTakeWire, "F-B02", DoorOperator.Mh()));
        Assert.False(log.RecordFailed(SlotDoorActionLog.MhTakeWire, "F-B02", "x", DoorOperator.Mh()));
    }

    /// <summary>记录写入失败（库坏了）不得把异常抛回开门链路。</summary>
    [Fact]
    public void WhenWriteFails_DoesNotThrow_AndReportsFailure()
    {
        var (db, _, log) = NewLog();
        db.Execute("ALTER TABLE app_operation_log RENAME TO app_operation_log_broken;", new Dictionary<string, object?>());

        Assert.Null(log.RecordOpened(SlotDoorActionLog.MhTakeWire, "F-B02", DoorOperator.Mh()));
        Assert.False(log.RecordFailed(SlotDoorActionLog.MhTakeWire, "F-B02", "x", DoorOperator.Mh()));
    }
}
