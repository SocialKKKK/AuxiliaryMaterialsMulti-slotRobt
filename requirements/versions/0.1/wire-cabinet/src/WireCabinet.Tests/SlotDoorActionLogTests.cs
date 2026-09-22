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

        var written = log.RecordOpened(
            SlotDoorActionLog.MhOpenSlot, [(5, "F-B02")], DoorOperator.Mh("1001", "张三", "A班"));

        Assert.Equal(1, written);
        var row = Assert.Single(Rows(db));
        Assert.Equal("mh_open_slot", row["action_type"]);
        Assert.Equal("F-B02", row["slot_no"]);
        Assert.Equal("成功", row["result"]);
        Assert.Equal("MH", row["operator_role"]);
        Assert.Equal("1001", row["operator_id"]);
        Assert.Equal("张三", row["operator_name"]);
        Assert.Equal("A班", row["operator_team"]);
    }

    /// <summary>§ 矩阵第 6 项：批量开门**每个格口各一条**，条数与被打开的格口数一致。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(54)]
    public void BatchOpen_WritesOneRecordPerOpenedSlot(int count)
    {
        var (db, _, log) = NewLog();
        var slots = Enumerable.Range(1, count).Select(i => ((long)i, $"F-{i:00}")).ToList();

        var written = log.RecordOpened(SlotDoorActionLog.MhOpenSlotsBatch, slots, DoorOperator.Mh());

        Assert.Equal(count, written);
        Assert.Equal(count, Rows(db).Count);
        // 每个格口恰好一条，且格口号与输入一一对应
        Assert.Equal(
            slots.Select(s => s.Item2).ToList(),
            Rows(db).Select(r => r["slot_no"]?.ToString() ?? "").ToList());
    }

    /// <summary>§ 矩阵第 9 项：开锁失败也留一条，且带原因。</summary>
    [Fact]
    public void FailedOpen_WritesFailureRecordWithReason()
    {
        var (db, _, log) = NewLog();

        Assert.True(log.RecordFailed(
            SlotDoorActionLog.MhOpenSlot, "F-B02", "格口开锁失败（已禁用、硬件无响应或格口不存在）。", DoorOperator.Mh()));

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

        log.RecordOpened(SlotDoorActionLog.MaintTrialOpen, [(3, "F-A03")], DoorOperator.Maint());

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
            log.RecordOpened(SlotDoorActionLog.MhOpenSlot, [(5, "F-B02")], DoorOperator.Mh("1001", "张三", "A班"));

        Assert.Equal(3, Rows(db).Count);
        Assert.All(Rows(db), r => Assert.Equal("F-B02", r["slot_no"]));
    }

    /// <summary>没有 sink（记录功能被停用）时不得抛异常，也不该影响开门（§1.4 第 6 条：关闭即可复原）。</summary>
    [Fact]
    public void WithoutSink_IsNoOp()
    {
        var log = new SlotDoorActionLog(null);

        Assert.Equal(0, log.RecordOpened(SlotDoorActionLog.MhOpenSlot, [(5, "F-B02")], DoorOperator.Mh()));
        Assert.False(log.RecordFailed(SlotDoorActionLog.MhOpenSlot, "F-B02", "x", DoorOperator.Mh()));
    }

    /// <summary>记录写入失败（库坏了）不得把异常抛回开门链路。</summary>
    [Fact]
    public void WhenWriteFails_DoesNotThrow_AndReportsZero()
    {
        var (db, _, log) = NewLog();
        db.Execute("ALTER TABLE app_operation_log RENAME TO app_operation_log_broken;", new Dictionary<string, object?>());

        Assert.Equal(0, log.RecordOpened(SlotDoorActionLog.MhOpenSlot, [(5, "F-B02")], DoorOperator.Mh()));
        Assert.False(log.RecordFailed(SlotDoorActionLog.MhOpenSlot, "F-B02", "x", DoorOperator.Mh()));
    }
}
