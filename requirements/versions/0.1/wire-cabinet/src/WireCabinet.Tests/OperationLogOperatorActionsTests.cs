using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using WireCabinet.Flows;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 操作员「存废取新」的记录（0.2 需求范围 §3.1 第 2/3/4 项）。
///
/// 口径优化（2026-10-09）后：一次操作产生**2 条**——归还一条、「领用并取出」一条（领用与取出合并）；
/// 门时刻是这两条上的字段（关门确认后回填），不再单独写门事件。
/// </summary>
public class OperationLogOperatorActionsTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-opact-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    /// <summary>跑一遍完整的操作员存废取新，返回落库的记录按写入顺序排列。</summary>
    private List<Dictionary<string, object?>> RunOperatorFlowAndReadRecords()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        WireMesDiscoSchema.EnsureAll(db);
        WireMesMockSchema.Ensure(db);
        OperationLogSchema.Ensure(db);

        var catalog = SqlCatalog.Load();
        var appDb = new SqliteAppDb(db);
        var engine = new FlowEngine(new EngineServices
        {
            Catalog = catalog,
            AppDb = appDb,
            Mes = new SqliteMockMesGateway(db, new MesFunctionSettings()),
            Slot = new FakeSlotController(),
            OperationLog = new SqliteOperationLogSink(catalog, appDb),
        });

        var flow = FlowLoader.LoadAll()
            .Single(f => f.FlowId == "operator_return_wire_and_issue_available_wire");

        engine.Begin(flow);
        foreach (var (nodeId, fields) in OperatorInputs())
            engine.Context!.UserInputs[nodeId] = fields;

        var steps = 0;
        while (!engine.Finished && steps++ < 5000)
            engine.StepOnce();

        Assert.True(engine.Finished, "流程未结束。");
        Assert.Equal("endSuccess", engine.Trace[^1].NodeId);

        return db.Query(
            "SELECT * FROM app_operation_log ORDER BY id;",
            new Dictionary<string, object?>()).Rows;
    }

    /// <summary>演示数据取值：操作员 1001=张三、归还批号 14Z1888-0160、机台 1QH029。</summary>
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

    [Fact]
    public void OneReturnAndIssueRun_ProducesTwoBusinessRecordsWithDoorTimestamps()
    {
        var rows = RunOperatorFlowAndReadRecords();

        // 口径优化（2026-10-09）：归还一条 +「领用并取出」一条（领用与取出合并）；
        // 门事件降级为字段，不再单独成条。原先是 3 条业务 + 4 条门事件 = 7 条。
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            new[] { "op_return_wire", "op_issue_available_wire" },
            rows.Select(r => r["action_type"]?.ToString()).ToArray());

        // 两条的门时刻都靠「关门确认后回填」拿到（归还/领用是先提交、后开关门的）
        foreach (var row in rows)
        {
            var open = row["door_open_ts"]?.ToString();
            var close = row["door_close_ts"]?.ToString();
            Assert.False(string.IsNullOrWhiteSpace(open), $"{row["action_type"]} 缺 door_open_ts。");
            Assert.False(string.IsNullOrWhiteSpace(close), $"{row["action_type"]} 缺 door_close_ts。");
            Assert.True(DateTime.Parse(close!) >= DateTime.Parse(open!), $"{row["action_type"]} 关门早于开门。");
        }
    }

    [Fact]
    public void Records_CarryOperatorAndBusinessFields()
    {
        var rows = RunOperatorFlowAndReadRecords();
        var byAction = rows.Where(r => r["action_type"]?.ToString()?.StartsWith("op_") == true)
            .ToDictionary(r => r["action_type"]!.ToString()!, r => r);

        foreach (var row in byAction.Values)
        {
            Assert.Equal("OP", row["operator_role"]);
            Assert.Equal("1001", row["operator_id"]);
            Assert.Equal("张三", row["operator_name"]);   // 来自 MES 操作员查询
            Assert.Equal("A班", row["operator_team"]);
            Assert.Equal("1QH029", row["eqp_no"]);
            Assert.Equal("成功", row["result"]);
        }

        var ret = byAction["op_return_wire"];
        Assert.Equal("14Z1888-0160", ret["lot_no"]);          // 归还的是操作员录入的旧焊丝
        Assert.Equal("φ42(MAXSOFT)", ret["wire_spec"]);
        Assert.Equal("P20260602-01", ret["product_lot_no"]);  // 该机台最近完工产品批号

        // 领用这条同时就是「取出」那条（合并）：批号/格口取自领用匹配到的柜内可用焊丝
        var issue = byAction["op_issue_available_wire"];
        Assert.Equal("14Z1888-0888", issue["lot_no"]);
        Assert.Equal("P20260602-01", issue["product_lot_no"]); // 领用段手工输入的工单批次
        Assert.False(string.IsNullOrWhiteSpace(issue["slot_no"]?.ToString()));
    }

    /// <summary>§3.1 第 4 项：取出与领用合并后，那条记录必须含该格的开门与关门确认时刻。</summary>
    [Fact]
    public void MergedIssueTakeRecord_HasDoorOpenAndCloseTimestamps()
    {
        var rows = RunOperatorFlowAndReadRecords();
        var issue = rows.Single(r => r["action_type"]?.ToString() == "op_issue_available_wire");

        Assert.False(string.IsNullOrWhiteSpace(issue["door_open_ts"]?.ToString()), "领用并取出记录缺 door_open_ts。");
        Assert.False(string.IsNullOrWhiteSpace(issue["door_close_ts"]?.ToString()), "领用并取出记录缺 door_close_ts。");

        // 库里的时间必须与 SQLite ts 同为 UTC 文本；否则页面的本地时区换算会原样透传。
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", issue["door_open_ts"]!.ToString()!);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", issue["door_close_ts"]!.ToString()!);

        // 关门确认发生在开门之后
        var open = DateTime.Parse(issue["door_open_ts"]!.ToString()!);
        var close = DateTime.Parse(issue["door_close_ts"]!.ToString()!);
        Assert.True(close >= open, $"door_close_ts({close:o}) 早于 door_open_ts({open:o})。");

        // 取出不再单独成条
        Assert.DoesNotContain(rows, r => r["action_type"]?.ToString() == "op_take_wire");
    }
}
