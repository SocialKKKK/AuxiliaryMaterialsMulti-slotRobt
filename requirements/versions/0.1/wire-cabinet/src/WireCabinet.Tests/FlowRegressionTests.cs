using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using WireCabinet.Flows;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 0.1 流程主路径回归：用**产品自己的引擎**（WireCabinet.Flow）+ 真实 catalog
/// 把流程从 start 跑到终止，断言正常路径落在 success 终止节点上。
///
/// 为什么不交给 wire-flow-lab：lab 里有一份 fork 的 FlowEngine（540 行 vs 产品 710 行，
/// 且 WireFlowLab.csproj 不引用任何产品工程），它的 selftest 测的是它自己那份引擎，
/// 保护不了这里的改动。
///
/// MH 存料见 <see cref="OperationLogMhLoadTests"/>；操作员「存废取新」因流程定义缺
/// success 终止节点（结尾路由到未定义的 End）未纳入，见下方 <c>RoutingTargets...</c>。
/// </summary>
public class FlowRegressionTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-flowreg-{Guid.NewGuid():N}.db");

    private const int MaxSteps = 5000;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private static List<FlowDefinition> AllFlows() => FlowLoader.LoadAll();

    private (SqliteDb Db, FlowEngine Engine) NewEngine()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        WireMesDiscoSchema.EnsureAll(db);
        OperationLogSchema.Ensure(db);

        var services = new EngineServices
        {
            Catalog = SqlCatalog.Load(),
            AppDb = new SqliteAppDb(db),
            Mes = new SqliteMockMesGateway(db, new MesFunctionSettings()),
            Slot = new FakeSlotController(),
        };

        return (db, new FlowEngine(services));
    }

    /// <summary>跑到底；guard 用尽仍没结束则断言失败（而不是死循环）。</summary>
    private static List<TraceEntry> RunToEnd(FlowEngine engine)
    {
        var steps = 0;
        while (!engine.Finished && steps++ < MaxSteps)
            engine.StepOnce();

        Assert.True(engine.Finished, $"流程 {MaxSteps} 步内未结束（疑似成环）。");
        return engine.Trace;
    }

    [Theory]
    [InlineData("material_handler_open_single_slot")]
    [InlineData("material_handler_open_all_slots")]
    [InlineData("material_handler_open_all_available_wire_slots")]
    [InlineData("material_handler_open_all_returned_wire_slots")]
    public void HappyPath_ReachesSuccessTerminal(string flowId)
    {
        var (db, engine) = NewEngine();
        var flow = AllFlows().Single(f => f.FlowId == flowId);

        engine.Begin(flow);
        engine.Context!.UserInputs["inputSelectedSlot"] =
            new Dictionary<string, string> { ["selected_slot_id"] = "1" }; // 仅 open_single_slot 使用

        var trace = RunToEnd(engine);
        var last = trace[^1];

        Assert.Equal("success", last.Outcome);
        Assert.Equal("endSuccess", last.NodeId);

        // 主路径上不应出现 error
        Assert.DoesNotContain(trace, t => t.Outcome == "error");
        // 每次关门都成功
        Assert.DoesNotContain(trace, t => t.NodeId == "closeSingleSlotDoor" && t.Outcome != "success");
        // 至少真的开了门，否则这条断言是空转
        Assert.Contains(trace, t => t.Outcome == "success" && t.NodeId.StartsWith("open", StringComparison.Ordinal));
        // 也必须真的走过关门循环：空集合时引擎会直接判定「全关」而成功，
        // 不检查这一步的话，seed 里没有可用格口时用例会假通过。
        Assert.Contains(trace, t => t.NodeId == "closeSingleSlotDoor");

        // 端到端不变量：流程说「所有门已关闭」，库里的 door_state 必须真的没有 open
        var stillOpen = db.Query(
            "SELECT COUNT(*) AS c FROM app_slot WHERE door_state = 'open';",
            new Dictionary<string, object?>()).First;
        Assert.Equal(0L, Convert.ToInt64(stillOpen!["c"]));
    }

    /// <summary>
    /// 流程定义结构校验：路由目标必须指向已定义节点（终止节点的空目标除外）。
    ///
    /// 此前这里固定着一个缺陷：操作员流程结尾写的是 `success: End`，而 End 不是已定义节点，
    /// 且该流程没有任何 success 终止节点。结果是操作员**成功**走完存废取新后，最后一条 trace
    /// 变成「路由指向不存在的节点 'End'」，FlowCoordinator 会把这句内部串当作结束消息显示出来。
    /// 已按 lab README 的约定回写 flow-sql-map：补 endSuccess 终止节点并把 success 改指向它。
    /// 现在该断言是严格的——任何悬空路由都会失败。
    /// </summary>
    [Fact]
    public void RoutingTargets_AllPointToDefinedNodes()
    {
        var dangling = AllFlows()
            .SelectMany(f => f.Nodes.Values
                .SelectMany(n => n.Routing.Values
                    .Where(target => !f.Nodes.ContainsKey(target))
                    .Select(target => $"{f.FlowId}.{n.Id} -> {target}")))
            .OrderBy(x => x)
            .ToList();

        Assert.Empty(dangling);
    }

    /// <summary>流程引用的每个 sql_id 都必须在 catalog 中可解析（防止 catalog 改名后流程静默断链）。</summary>
    [Fact]
    public void EveryFlowSqlId_ResolvesInCatalog()
    {
        var catalog = SqlCatalog.Load();

        var missing = AllFlows()
            .SelectMany(f => f.Nodes.Values.SelectMany(n =>
                new[] { n.SqlId, n.OpenSqlId }
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => (f.FlowId, Node: n.Id, Id: id!))
                    .Where(x => catalog.Find(x.Id) is null)
                    .Select(x => $"{x.FlowId}.{x.Node} -> {x.Id}")))
            .OrderBy(x => x)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>每条流程都必须有 success 终止节点（缺失会让正常结束变成「节点不存在」错误）。</summary>
    [Fact]
    public void EveryFlow_HasSuccessTerminal()
    {
        var withoutSuccess = AllFlows()
            .Where(f => !f.Nodes.Values.Any(n => n.TerminalKind == "success"))
            .Select(f => f.FlowId)
            .OrderBy(x => x)
            .ToList();

        Assert.Empty(withoutSuccess);
    }

    /// <summary>
    /// 操作员「存废取新」主路径。这条流程此前在本机根本跑不起来——它要查
    /// mv_fw_username / fw_eqpres_eqpinformation / fw_wip_trans / v_fw_wip_sublot，
    /// 而产品脚本从不创建这些 mock MES 表（只有 v_fw_material_indetail 被建出来且无数据）。
    /// 补上 scripts/mes-mock.sqlite.sql 后这里才可能通过。
    /// </summary>
    [Fact]
    public void OperatorFlow_HappyPath_ReachesSuccessTerminal()
    {
        var (_, engine) = NewEngine();
        var flow = AllFlows().Single(f => f.FlowId == "operator_return_wire_and_issue_available_wire");

        engine.Begin(flow);
        foreach (var (nodeId, fields) in OperatorInputs())
            engine.Context!.UserInputs[nodeId] = fields;

        var trace = RunToEnd(engine);
        var last = trace[^1];

        var tail = string.Join("\n", trace.TakeLast(8).Select(t => $"{t.NodeId} [{t.Outcome}] {t.Result}"));
        Assert.True(
            last.NodeId == "endSuccess",
            $"未走到成功终止，实际停在 {last.NodeId} [{last.Outcome}] {last.Result}\n末尾轨迹：\n{tail}");
        Assert.Equal("success", last.Outcome);
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
}
