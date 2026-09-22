using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 记录写入通道。第 2 个测试是 C1「写入失败不阻断业务」的底座：
/// sink 必须把失败变成返回值（并转入待补偿），而不是抛异常。
/// 调用方（流程引擎等）据此只做告警，不改变业务 outcome。
/// </summary>
public class OperationLogSinkTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-{Guid.NewGuid():N}.db");

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
        OperationLogSchema.Ensure(db); // 幂等，顺带覆盖迁移路径
        return (db, new SqliteOperationLogSink(SqlCatalog.Load(), new SqliteAppDb(db)));
    }

    [Fact]
    public void Catalog_ContainsInsertItem_AsWriteOperation()
    {
        var item = SqlCatalog.Load().Find(SqliteOperationLogSink.InsertSqlId);

        Assert.NotNull(item);
        Assert.Equal(SqlOperation.Write, item!.Operation);
    }

    [Fact]
    public void TryWrite_Succeeds_PersistsEveryField()
    {
        var (db, sink) = NewSink();

        var result = sink.TryWrite(new OperationLogEntry
        {
            ActionType = "mh_load_available_wire",
            SlotNo = "F-A03",
            LotNo = "LOT-2026-001",
            WireSpec = "1.2mil",
            Station = "MH站",
            OperatorId = "MH001",
            OperatorName = "王光",
            OperatorTeam = "A班",
            OperatorRole = "MH",
            EqpNo = "EQP-01",
            ProductLotNo = "PRD-001",
            Qty = 1,
            RemainQty = 0.8,
            Result = "成功",
            Message = "存料完成",
            MesResult = "ok",
            LockResult = "ok",
            ErrorInfo = null,
            DoorOpenTs = "2026-09-22 10:00:00",
            DoorCloseTs = "2026-09-22 10:00:30",
            IsCleared = true,
            IsUncommanded = false,
            FlowId = "material_handler_load_available_wire",
            NodeId = "updateDB",
        });

        Assert.True(result.Success, result.Error);

        var row = db.Query(
            "SELECT * FROM app_operation_log WHERE lot_no = :lot;",
            new Dictionary<string, object?> { ["lot"] = "LOT-2026-001" }).First;

        Assert.NotNull(row);
        // 逐字段核对：catalog 条目里少写一个参数就会让该列静默变 NULL，这里会直接暴露
        Assert.Equal("mh_load_available_wire", row!["action_type"]);
        Assert.Equal("F-A03", row["slot_no"]);
        Assert.Equal("1.2mil", row["wire_spec"]);
        Assert.Equal("MH站", row["station"]);
        Assert.Equal("MH001", row["operator_id"]);
        Assert.Equal("王光", row["operator_name"]);
        Assert.Equal("A班", row["operator_team"]);
        Assert.Equal("MH", row["operator_role"]);
        Assert.Equal("EQP-01", row["eqp_no"]);
        Assert.Equal("PRD-001", row["product_lot_no"]);
        Assert.Equal(1d, Convert.ToDouble(row["qty"]));
        Assert.Equal(0.8d, Convert.ToDouble(row["remain_qty"]));
        Assert.Equal("成功", row["result"]);
        Assert.Equal("存料完成", row["message"]);
        Assert.Equal("ok", row["mes_result"]);
        Assert.Equal("ok", row["lock_result"]);
        Assert.Null(row["error_info"]);
        Assert.Equal("2026-09-22 10:00:00", row["door_open_ts"]);
        Assert.Equal("2026-09-22 10:00:30", row["door_close_ts"]);
        Assert.Equal(1L, Convert.ToInt64(row["is_cleared"]));
        Assert.Equal(0L, Convert.ToInt64(row["is_uncommanded"]));
        Assert.Equal("material_handler_load_available_wire", row["flow_id"]);
        Assert.Equal("updateDB", row["node_id"]);
    }

    [Fact]
    public void TryWrite_WhenDatabaseFails_ReturnsFailureWithoutThrowing_AndQueuesPending()
    {
        var (db, sink) = NewSink();
        // 制造写入失败：干掉记录表（模拟库损坏 / 迁移未执行 / 表被改名）
        db.Execute("DROP TABLE app_operation_log;", new Dictionary<string, object?>());

        var entry = new OperationLogEntry { ActionType = "op_issue", LotNo = "LOT-2026-002" };

        // 关键：不得抛异常
        var result = sink.TryWrite(entry);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));

        // 失败记录转入待补偿
        var pending = db.Query(
            "SELECT COUNT(*) AS c FROM operation_log_pending;",
            new Dictionary<string, object?>()).First;
        Assert.Equal(1L, Convert.ToInt64(pending!["c"]));
    }

    [Fact]
    public void TryWrite_WhenDatabaseFails_RaisesWriteFailedEvent()
    {
        var (db, sink) = NewSink();
        db.Execute("DROP TABLE app_operation_log;", new Dictionary<string, object?>());

        OperationLogEntry? failed = null;
        var error = "";
        sink.WriteFailed += (e, msg) => { failed = e; error = msg; };

        sink.TryWrite(new OperationLogEntry { ActionType = "op_return", LotNo = "LOT-2026-003" });

        Assert.NotNull(failed);
        Assert.Equal("LOT-2026-003", failed!.LotNo);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void NullSink_IsNoOp()
    {
        IOperationLogSink sink = new NullOperationLogSink();
        Assert.True(sink.TryWrite(new OperationLogEntry { ActionType = "x" }).Success);
    }
}
