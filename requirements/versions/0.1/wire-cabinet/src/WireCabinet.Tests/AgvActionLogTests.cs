using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using WireCabinet.Rcs;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 控车与回充的记录（0.2 §3.1 第 8 项）。
///
/// 最要紧的是验收 C5：**不得按轮询 tick 记录，静置 5 分钟记录条数为 0**。
/// 轮询每 5 秒一轮，一天下来是 1.7 万轮——记录只能在状态迁移时产生。
/// 所以这里用「连续多轮静置 → 0 条」把它钉住，而不是只测单个动作的字段。
/// </summary>
public class AgvActionLogTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-agv-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private (SqliteDb Db, AgvActionLog Log) NewLog()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        OperationLogSchema.Ensure(db);
        return (db, new AgvActionLog(new SqliteOperationLogSink(SqlCatalog.Load(), new SqliteAppDb(db))));
    }

    private static List<Dictionary<string, object?>> Rows(SqliteDb db) =>
        db.Query("SELECT * FROM app_operation_log ORDER BY id;", new Dictionary<string, object?>()).Rows;

    // ---------- C5：静置不产生记录 ----------

    /// <summary>静置即 ExecutedAction 为 null；连跑一小时的轮询轮数也不该落一条。</summary>
    [Fact]
    public void IdleTicks_ProduceNoRecords()
    {
        var (db, log) = NewLog();

        // 5 秒一轮 × 720 轮 = 1 小时静置
        for (var i = 0; i < 720; i++)
            Assert.False(log.RecordTickAction(null));

        Assert.Empty(Rows(db));
    }

    /// <summary>未登记的动作也不能记——新增动作时若忘了登记，宁可漏记也不要写成错误的类型。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("SomethingNew")]
    [InlineData("NoOp")]
    public void UnknownActions_ProduceNoRecords(string action)
    {
        var (db, log) = NewLog();

        Assert.False(log.RecordTickAction(action));
        Assert.Empty(Rows(db));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UnknownAction")]
    public void Describe_ReturnsNull_ForNothingAndForUnknown(string? action)
    {
        Assert.Null(AgvActionLog.Describe(action));
    }

    // ---------- 状态迁移各记一条 ----------

    [Theory]
    [InlineData("CreateMoveOrder", "下达移动单")]
    [InlineData("PauseMovement", "检测到格口打开，任务已暂停")]
    [InlineData("ContinueMovement", "格口已关闭，任务已继续")]
    [InlineData("CancelOrder", "订单被取消")]
    public void LoopActions_WriteOneMoveRecord(string action, string expectedMessage)
    {
        var (db, log) = NewLog();

        Assert.True(log.RecordTickAction(action));

        var row = Assert.Single(Rows(db));
        Assert.Equal("agv_move", row["action_type"]);
        Assert.Equal(expectedMessage, row["message"]);
        Assert.Equal("成功", row["result"]);
    }

    /// <summary>暂停后继续：两次迁移各一条，静置期间一条不落。</summary>
    [Fact]
    public void PauseThenContinue_RecordsExactlyTwo()
    {
        var (db, log) = NewLog();

        log.RecordTickAction(null);                 // 静置
        log.RecordTickAction("PauseMovement");      // 格口打开 → 暂停
        for (var i = 0; i < 100; i++) log.RecordTickAction(null);   // 一直暂停中
        log.RecordTickAction("ContinueMovement");   // 格口关闭 → 继续
        for (var i = 0; i < 100; i++) log.RecordTickAction(null);   // 继续后静置

        var rows = Rows(db);
        Assert.Equal(2, rows.Count);
        Assert.Contains("已暂停", rows[0]["message"]?.ToString());
        Assert.Contains("已继续", rows[1]["message"]?.ToString());
    }

    [Fact]
    public void TickAction_AppendsDetail()
    {
        var (db, log) = NewLog();

        log.RecordTickAction("CreateMoveOrder", "→ OP站（站点 12）");

        Assert.Equal("下达移动单（→ OP站（站点 12））", Rows(db)[0]["message"]);
    }

    // ---------- 用户指令 ----------

    [Fact]
    public void ChargeCommand_WritesChargeRecord()
    {
        var (db, log) = NewLog();

        Assert.True(log.RecordCommand(AgvActionLog.AgvCharge, "下充电单（自动回充）", ok: true, detail: "→ 充电点（O-1）"));

        var row = Assert.Single(Rows(db));
        Assert.Equal("agv_charge", row["action_type"]);
        Assert.Equal("成功", row["result"]);
        Assert.Contains("充电点", row["message"]?.ToString());
    }

    [Fact]
    public void FailedCommand_RecordsFailureWithReason()
    {
        var (db, log) = NewLog();

        Assert.True(log.RecordCommand(AgvActionLog.AgvMove, "取消当前订单", ok: false, error: "RCS 超时", detail: "T-9"));

        var row = Assert.Single(Rows(db));
        Assert.Equal("失败", row["result"]);
        Assert.Equal("RCS 超时", row["error_info"]);
    }

    [Fact]
    public void WithoutSink_IsNoOp()
    {
        var log = new AgvActionLog(null);

        Assert.False(log.RecordTickAction("PauseMovement"));
        Assert.False(log.RecordCommand(AgvActionLog.AgvMove, "取消当前订单", ok: true));
    }

    /// <summary>记录写入失败不得把异常抛回控车链路。</summary>
    [Fact]
    public void WhenWriteFails_DoesNotThrow()
    {
        var (db, log) = NewLog();
        db.Execute("ALTER TABLE app_operation_log RENAME TO app_operation_log_broken;", new Dictionary<string, object?>());

        Assert.False(log.RecordTickAction("PauseMovement"));
        Assert.False(log.RecordCommand(AgvActionLog.AgvCharge, "下充电单（自动回充）", ok: true));
    }
}
