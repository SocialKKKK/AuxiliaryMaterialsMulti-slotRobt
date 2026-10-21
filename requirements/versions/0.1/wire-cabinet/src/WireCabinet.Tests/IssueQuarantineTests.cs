using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using WireCabinet.Flows;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// OP 领用格口隔离（2026-10-21 现场 F-A03 幽灵料回归）。
///
/// 现场问题：MES 领用提交成功、操作员取走料但没关门，流程没走到 complete_issue_pickup，
/// 格口仍 available_wire，被下一个人反复匹配；MES 则报 ORA-20007 找不到库存记录。
///
/// 修复后的契约：
/// 1. 提交 MES 前标「领用中」，成功后标「领用待取」，两者都被 find_matched_available 排除；
/// 2. 关门取料后 complete_issue_pickup 清成 idle，wire_state 置空；
/// 3. MES 明确报「找不到物料…库存记录」时标「MES无库存」，不再发给操作员；
/// 4. 可重试的 MES 业务失败恢复原状态（通常是「可用」），不误隔离。
/// </summary>
public class IssueQuarantineTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-issueq-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void IssueSubmitted_WithoutDoorClose_IsExcludedFromMatching()
    {
        var (db, engine) = NewEngine();

        RunUntil(engine, "closeIssueSlotDoor");

        var slotId = Convert.ToInt64(engine.Context!.NodeResults["queryMatchedAvailableWire"]["slot_id"]);
        var slot = ReadSlot(db, slotId);
        Assert.Equal("领用待取", slot["wire_state"]?.ToString());
        Assert.Equal("available_wire", slot["biz_state"]?.ToString());

        // 同规格只剩这一盘（seed 中 φ42 可用只有 slot 5）；被隔离后不能再匹配。
        Assert.Empty(QueryMatched(db, "φ42(MAXSOFT)"));
    }

    [Fact]
    public void IssueSubmitted_WithDoorClose_ClearsSlotAndFillsDoorTimes()
    {
        var (db, engine) = NewEngine();

        RunUntil(engine, "closeIssueSlotDoor");
        RunToEnd(engine);

        var slot = ReadSlot(db, 5);
        Assert.Equal("idle", slot["biz_state"]?.ToString());
        Assert.Null(slot["wire_state"]);

        var record = db.Query(
            "SELECT door_open_ts, door_close_ts FROM app_operation_log WHERE action_type='op_issue_available_wire';",
            new Dictionary<string, object?>()).Rows.Single();
        Assert.False(string.IsNullOrWhiteSpace(record["door_open_ts"]?.ToString()));
        Assert.False(string.IsNullOrWhiteSpace(record["door_close_ts"]?.ToString()));
    }

    [Fact]
    public void IssueSubmit_NoStockRecord_BlocksSlot()
    {
        var (db, engine) = NewEngine(
            "ORA-20007: 找不到物料[121010800045PHA0001][14Z1888-0888]的库存记录！");

        RunToEnd(engine);

        var slot = ReadSlot(db, 5);
        Assert.Equal("MES无库存", slot["wire_state"]?.ToString());
        Assert.Empty(QueryMatched(db, "φ42(MAXSOFT)"));
    }

    [Fact]
    public void IssueSubmit_TransientMesReject_RestoresAvailableState()
    {
        var (db, engine) = NewEngine(
            "ORA-20007: 员工1001在Q04条线五分钟内有领用记录，请稍后再试！");

        RunToEnd(engine);

        var slot = ReadSlot(db, 5);
        Assert.Equal("可用", slot["wire_state"]?.ToString());
        Assert.Single(QueryMatched(db, "φ42(MAXSOFT)"));
    }

    private (SqliteDb Db, FlowEngine Engine) NewEngine(string? issueError = null)
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        WireMesDiscoSchema.EnsureAll(db);
        WireMesMockSchema.Ensure(db);
        OperationLogSchema.Ensure(db);

        var catalog = SqlCatalog.Load();
        var appDb = new SqliteAppDb(db);
        IMesGateway mes = issueError is null
            ? new SqliteMockMesGateway(db, new MesFunctionSettings())
            : new IssueRejectingMesGateway(db, issueError);
        var engine = new FlowEngine(new EngineServices
        {
            Catalog = catalog,
            AppDb = appDb,
            Mes = mes,
            Slot = new FakeSlotController(),
            OperationLog = new SqliteOperationLogSink(catalog, appDb),
        });

        var flow = FlowLoader.LoadAll()
            .Single(f => f.FlowId == "operator_return_wire_and_issue_available_wire");

        engine.Begin(flow);
        foreach (var (nodeId, fields) in OperatorInputs())
            engine.Context!.UserInputs[nodeId] = fields;
        return (db, engine);
    }

    private static void RunUntil(FlowEngine engine, string nodeId)
    {
        var steps = 0;
        while (!engine.Finished
               && !string.Equals(engine.CurrentNodeId, nodeId, StringComparison.OrdinalIgnoreCase)
               && steps++ < 5000)
            engine.StepOnce();

        Assert.False(engine.Finished, $"流程提前结束，未到 {nodeId}。最后：{engine.Trace.LastOrDefault()?.NodeId}");
        Assert.Equal(nodeId, engine.CurrentNodeId);
    }

    private static void RunToEnd(FlowEngine engine)
    {
        var steps = 0;
        while (!engine.Finished && steps++ < 5000)
            engine.StepOnce();
        Assert.True(engine.Finished, "流程未结束。");
    }

    private static Dictionary<string, object?> ReadSlot(SqliteDb db, long slotId) =>
        db.Query(
            "SELECT * FROM app_slot WHERE slot_id = :slot_id;",
            new Dictionary<string, object?> { ["slot_id"] = slotId }).Rows.Single();

    private static List<Dictionary<string, object?>> QueryMatched(SqliteDb db, string spec)
    {
        var item = SqlCatalog.Load().Find("app.wire_inventory.find_matched_available")!;
        return new SqliteAppDb(db)
            .Run(item, new Dictionary<string, object?> { ["returned_wire_spec"] = spec })
            .Rows;
    }

    private static Dictionary<string, Dictionary<string, string>> OperatorInputs() => new()
    {
        ["inputOperatorWorkInfo"] = new()
        {
            ["operator_id"] = "1001",
            ["work_group"] = "A班",
            ["shift"] = "白班",
        },
        ["inputReturnedWireLotNo"] = new() { ["returned_wire_lot_no"] = "14Z1888-0160" },
        ["inputEqpNo"] = new() { ["eqp_no"] = "1QH029" },
        ["inputRemainingQty"] = new() { ["remaining_qty"] = "1000" },
        ["inputIssueProductLotNo"] = new() { ["issue_product_lot_no"] = "P20260602-01" },
        ["closeReturnSlotDoor"] = new() { ["closed"] = "已关闭" },
        ["loadReturnedWire"] = new() { ["loaded"] = "已放入" },
        ["closeIssueSlotDoor"] = new() { ["closed"] = "已关闭" },
        ["unloadWire"] = new() { ["unloaded"] = "已取出" },
    };

    /// <summary>让归还成功、领用按指定错误失败，用于验证 no-stock 与其他业务失败的分流。</summary>
    private sealed class IssueRejectingMesGateway : IMesGateway
    {
        private readonly IMesGateway _inner;
        private readonly string _error;

        public IssueRejectingMesGateway(SqliteDb db, string error)
        {
            _error = error;
            _inner = new SqliteMockMesGateway(db, new MesFunctionSettings());
        }

        public MesMode Mode => _inner.Mode;

        public DbResult Run(SqlCatalogItem item, IDictionary<string, object?> parameters)
        {
            if (item.Id.EndsWith("mat_trans.submit_return", StringComparison.OrdinalIgnoreCase)
                && parameters.TryGetValue("v_Task", out var task)
                && string.Equals(task?.ToString(), "领用", StringComparison.Ordinal))
                return DbResult.Failure(item.Sql, _error);

            return _inner.Run(item, parameters);
        }
    }
}
