using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 记录口径优化（2026-10-09）的 A 档：业务事实不可改写，唯一放行「只把 NULL 的门时刻补齐」。
///
/// 归还 / 领用是先提交、后开关门的，写记录时关门时刻还不存在；这条回填就是它的落点。
/// 两道防线都要验：源码层的白名单正则（OperationLogRetentionTests）与库层的
/// trg_app_operation_log_append_only 触发器——这里验触发器与回填本身。
/// </summary>
public class OperationLogBackfillTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-backfill-{Guid.NewGuid():N}.db");

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
        OperationLogSchema.Ensure(db);

        return (db, new SqliteOperationLogSink(SqlCatalog.Load(), new SqliteAppDb(db)));
    }

    private static List<Dictionary<string, object?>> Rows(SqliteDb db) =>
        db.Query("SELECT * FROM app_operation_log ORDER BY id;", new Dictionary<string, object?>()).Rows;

    private static void Exec(SqliteDb db, string sql)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void BackfillDoorTs_FillsNullDoorTimestamps()
    {
        var (db, sink) = NewSink();
        const string submitted = "2026-10-09 08:51:19.000";
        Assert.True(sink.TryWrite(new OperationLogEntry
        {
            Ts = submitted,
            ActionType = OperationLogActionTypes.OpReturnWire,
            SlotNo = "R-I03",
            LotNo = "MC268142-02-0111",
            Result = "成功",
        }).Success);

        var result = sink.BackfillDoorTs(
            OperationLogActionTypes.OpReturnWire, submitted, "2026-10-09 08:51:12.000", "2026-10-09 08:51:22.000");

        Assert.True(result.Success, result.Error);
        var row = Assert.Single(Rows(db));
        Assert.Equal("2026-10-09 08:51:12.000", row["door_open_ts"]);
        Assert.Equal("2026-10-09 08:51:22.000", row["door_close_ts"]);
    }

    /// <summary>定位键必须恰好命中 1 行；已补过（door_close_ts 非 NULL）按失败返回，不重复补。</summary>
    [Fact]
    public void BackfillDoorTs_WhenAlreadyBackfilled_ReportsFailure()
    {
        var (_, sink) = NewSink();
        const string submitted = "2026-10-09 08:51:19.000";
        sink.TryWrite(new OperationLogEntry
        {
            Ts = submitted,
            ActionType = OperationLogActionTypes.OpReturnWire,
            Result = "成功",
        });
        Assert.True(sink.BackfillDoorTs(OperationLogActionTypes.OpReturnWire, submitted, "08:51:12", "08:51:22").Success);

        var second = sink.BackfillDoorTs(OperationLogActionTypes.OpReturnWire, submitted, "08:51:12", "08:51:23");

        Assert.False(second.Success);
    }

    [Fact]
    public void BackfillDoorTs_UnknownKey_ReportsFailure()
    {
        var (_, sink) = NewSink();

        var result = sink.BackfillDoorTs(OperationLogActionTypes.OpReturnWire, "2026-10-09 09:00:00.000", "a", "b");

        Assert.False(result.Success);
    }

    /// <summary>库层触发器：改业务事实列必须 ABORT，否则「不可改写」只是一句注释。</summary>
    [Theory]
    [InlineData("UPDATE app_operation_log SET result = '失败' WHERE id = 1;")]
    [InlineData("UPDATE app_operation_log SET message = '改个说法' WHERE id = 1;")]
    [InlineData("UPDATE app_operation_log SET door_close_ts = '2099-01-01 00:00:00.000' WHERE id = 1;")]
    public void Trigger_AbortsFactChanges(string sql)
    {
        var (db, sink) = NewSink();
        sink.TryWrite(new OperationLogEntry
        {
            Ts = "2026-10-09 08:51:19.000",
            ActionType = OperationLogActionTypes.OpReturnWire,
            Result = "成功",
            DoorOpenTs = "2026-10-09 08:51:12.000",
            DoorCloseTs = "2026-10-09 08:51:22.000",
        });

        var ex = Assert.Throws<SqliteException>(() => Exec(db, sql));

        Assert.Contains("不可改写", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>触发器放行「只补空的门时刻」，否则回填这条路会被自己堵死。</summary>
    [Fact]
    public void Trigger_AllowsFillingNullDoorTimestamps()
    {
        var (db, sink) = NewSink();
        sink.TryWrite(new OperationLogEntry
        {
            Ts = "2026-10-09 08:51:19.000",
            ActionType = OperationLogActionTypes.OpReturnWire,
            Result = "成功",
        });

        Exec(db, "UPDATE app_operation_log SET door_close_ts = '2026-10-09 08:51:22.000' WHERE id = 1;");

        Assert.Equal("2026-10-09 08:51:22.000", Assert.Single(Rows(db))["door_close_ts"]);
    }

    [Fact]
    public void Insert_WritesDetailAndCorrectsId()
    {
        var (db, sink) = NewSink();
        sink.TryWrite(new OperationLogEntry
        {
            ActionType = OperationLogActionTypes.MhOpenSlotsBatch,
            Result = "成功",
            Detail = """{"count":9,"first_ts":"2026-10-09 15:07:57","last_ts":"2026-10-09 15:08:59"}""",
        });

        var row = Assert.Single(Rows(db));
        Assert.Contains("\"count\":9", row["detail"]?.ToString(), StringComparison.Ordinal);
        Assert.Null(row["corrects_id"]);
    }
}
