using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 失败补偿（0.2 需求范围 §3.8：不阻断 + 告警 + 自动重试 + 待补偿可人工重试）。
///
/// 故障注入手段与验收演示脚本 5 一致：把 app_operation_log 临时改名，
/// 写入即失败；恢复改名后再重试。不用删表，便于在同一用例里往返。
/// </summary>
public class OperationLogCompensationTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-comp-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private (SqliteDb Db, SqliteOperationLogSink Sink) NewSink()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        WireMesDiscoSchema.EnsureAll(db);
        OperationLogSchema.Ensure(db);
        return (db, new SqliteOperationLogSink(SqlCatalog.Load(), new SqliteAppDb(db)));
    }

    private static void BreakRecordTable(SqliteDb db) =>
        db.Execute(
            "ALTER TABLE app_operation_log RENAME TO app_operation_log_broken;",
            new Dictionary<string, object?>());

    private static void RestoreRecordTable(SqliteDb db) =>
        db.Execute(
            "ALTER TABLE app_operation_log_broken RENAME TO app_operation_log;",
            new Dictionary<string, object?>());

    private static OperationLogEntry Entry(string lot) => new()
    {
        ActionType = "mh_load_available_wire",
        LotNo = lot,
        SlotNo = "F-A01",
        WireSpec = "φ1.2-测试",
        Qty = 1,
        OperatorRole = "MH",
        Result = "成功",
    };

    private static long PendingCount(SqliteDb db) =>
        Convert.ToInt64(db.Query(
            "SELECT COUNT(*) AS c FROM operation_log_pending;",
            new Dictionary<string, object?>()).First!["c"]);

    [Fact]
    public void AfterRecovery_BatchRetry_WritesRecordAndClearsPending()
    {
        var (db, sink) = NewSink();
        BreakRecordTable(db);

        Assert.False(sink.TryWrite(Entry("LOT-RETRY-1")).Success);
        var queued = Assert.Single(sink.ListPending());
        Assert.Equal(0, queued.RetryCount);
        // 待补偿列表要显示首次入队时间
        Assert.False(string.IsNullOrWhiteSpace(queued.Ts), "待补偿行缺少入队时间。");

        RestoreRecordTable(db);
        Assert.Equal(1, sink.RetryPendingBatch());

        // 记录补写成功，且字段经 JSON 往返后没有丢
        var row = Assert.Single(db.Query("SELECT * FROM app_operation_log;", new Dictionary<string, object?>()).Rows);
        Assert.Equal("LOT-RETRY-1", row["lot_no"]);
        Assert.Equal("mh_load_available_wire", row["action_type"]);
        Assert.Equal("F-A01", row["slot_no"]);
        Assert.Equal("φ1.2-测试", row["wire_spec"]);
        Assert.Equal("成功", row["result"]);

        // 补写成功后不再出现在待补偿列表里
        Assert.Empty(sink.ListPending());
    }

    [Fact]
    public void Retry_KeepsOriginalActionTime_InsteadOfRetryTime()
    {
        var (db, sink) = NewSink();
        BreakRecordTable(db);

        const string occurredAt = "2026-09-22 09:19:58";
        var entry = Entry("LOT-TIME") with { Ts = occurredAt };
        Assert.True(sink.TryWrite(entry).Queued);

        RestoreRecordTable(db);
        Assert.Equal(1, sink.RetryPendingBatch());

        var row = Assert.Single(db.Query("SELECT ts FROM app_operation_log;", new Dictionary<string, object?>()).Rows);
        Assert.Equal(occurredAt, row["ts"]);
    }

    [Fact]
    public void Retry_WhenCompletionMarkerFails_DoesNotInsertDuplicate()
    {
        var (db, sink) = NewSink();
        BreakRecordTable(db);
        Assert.True(sink.TryWrite(Entry("LOT-ATOMIC")).Queued);
        var id = sink.ListPending().Single().Id;
        RestoreRecordTable(db);

        // 故障发生在主表 INSERT 之后的状态更新；事务应把 INSERT 一并回滚。
        db.Execute("""
            CREATE TRIGGER fail_pending_complete BEFORE UPDATE OF status ON operation_log_pending
            WHEN NEW.status = 'success' BEGIN SELECT RAISE(ABORT, 'completion fault'); END;
            """, new Dictionary<string, object?>());
        Assert.False(sink.RetryPendingRow(id).Success);
        Assert.Empty(db.Query("SELECT * FROM app_operation_log;", new Dictionary<string, object?>()).Rows);

        db.Execute("DROP TRIGGER fail_pending_complete;", new Dictionary<string, object?>());
        Assert.True(sink.RetryPendingRow(id, manual: true).Success);
        Assert.Single(db.Query("SELECT * FROM app_operation_log;", new Dictionary<string, object?>()).Rows);
        Assert.Empty(sink.ListPending());
    }

    /// <summary>
    /// 关键回归：重试失败**不得**再入队。
    /// 若重试路径复用业务路径的 TryWrite，每重试一次队列就长一条，越滚越大。
    /// </summary>
    [Fact]
    public void Retry_WhenStillFailing_DoesNotEnqueueAgain()
    {
        var (db, sink) = NewSink();
        BreakRecordTable(db);
        sink.TryWrite(Entry("LOT-RETRY-2"));

        var id = sink.ListPending().Single().Id;
        Assert.False(sink.RetryPendingRow(id).Success);

        Assert.Equal(1L, PendingCount(db));
        Assert.Equal(1, sink.ListPending().Single().RetryCount);
    }

    [Fact]
    public void ManualRetry_RetriesFailedRow_WhileBackgroundIgnoresIt()
    {
        var (db, sink) = NewSink();
        BreakRecordTable(db);
        sink.TryWrite(Entry("LOT-RETRY-3"));

        var id = sink.ListPending().Single().Id;
        db.Execute(
            "UPDATE operation_log_pending SET status = 'failed' WHERE id = :id;",
            new Dictionary<string, object?> { ["id"] = id });
        RestoreRecordTable(db);

        // 后台只处理 pending：failed 必须留给人工，否则会变成无限自动重试
        Assert.Equal(0, sink.RetryPendingBatch());
        Assert.False(sink.RetryPendingRow(id).Success);

        // 人工重试放行
        Assert.True(sink.RetryPendingRow(id, manual: true).Success);
        Assert.Empty(sink.ListPending());
    }

    [Fact]
    public void RetryBatch_AfterMaxRetries_MarksFailedAndStops()
    {
        var (db, sink) = NewSink();
        BreakRecordTable(db);
        sink.TryWrite(Entry("LOT-RETRY-4"));

        var id = sink.ListPending().Single().Id;
        for (var i = 0; i < 5; i++)
            sink.RetryPendingRow(id);

        var state = db.Query(
            "SELECT status, retry_count, next_retry_at FROM operation_log_pending WHERE id = :id;",
            new Dictionary<string, object?> { ["id"] = id }).First;

        Assert.Equal("failed", state!["status"]);
        Assert.Equal(5L, Convert.ToInt64(state["retry_count"]));
        Assert.Null(state["next_retry_at"]);

        // 已达上限后后台不再捡它（仍留在列表里等人工处理）
        Assert.Equal(0, sink.RetryPendingBatch());
        Assert.Single(sink.ListPending());
    }

    [Fact]
    public void RetryBatch_PicksUpRowsLeftByPreviousProcess()
    {
        var (db, sink) = NewSink();
        BreakRecordTable(db);
        sink.TryWrite(Entry("LOT-RETRY-5"));
        RestoreRecordTable(db);

        // 换一个 sink 实例：模拟程序重启后由启动时的补偿把上次遗留的补上
        var fresh = new SqliteOperationLogSink(SqlCatalog.Load(), new SqliteAppDb(db));

        Assert.Equal(1, fresh.RetryPendingBatch());
        Assert.Empty(fresh.ListPending());
    }

    [Fact]
    public void RetryBatch_SkipsCorruptRowWithoutThrowing()
    {
        var (db, sink) = NewSink();
        db.Execute(
            "INSERT INTO operation_log_pending (entry_json, status) VALUES ('{ 不是合法 JSON', 'pending');",
            new Dictionary<string, object?>());

        // 坏行不能让整条补偿链路崩掉，也不能阻塞其它行
        Assert.Equal(0, sink.RetryPendingBatch());
        Assert.Empty(sink.ListPending());
    }
}
