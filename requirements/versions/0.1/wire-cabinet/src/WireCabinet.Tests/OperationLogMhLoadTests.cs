using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using WireCabinet.Flows;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 0.2 业务第一步：把存取记录挂到「MH 存入可用焊丝」一个动作上。
/// 走**真实流程引擎 + 真实 catalog + 真实 sink**，验证
///   1. 存料成功 → 恰好 1 条记录、关键字段完整；
///   2. 记录写入失败不改变流程 outcome（验收 §9 C1）；
///   3. 流程本身失败时不产生记录（本步只覆盖成功路径）。
/// </summary>
public class OperationLogMhLoadTests : IDisposable
{
    private const string MhLoadFlowId = "material_handler_load_available_wire";
    private const string Lot = "LOT-MH-0001";
    private const string Spec = "φ1.2-测试";

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-mhload-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private static FlowDefinition MhLoadFlow() =>
        FlowLoader.LoadAll().Single(f => f.FlowId == MhLoadFlowId);

    /// <summary>
    /// 建库并装配引擎。seedMesWire=true 时在 mock MES 里放一条该批号的「焊丝」记录，
    /// 否则流程会在校验批号时终止。
    ///
    /// failingMesSync=true 时接上 DISCOEQPNO 同步并让它的 SET 固定失败，
    /// 用来复现「MES 报错」这一失败分支（mock 里难以用数据造出：规格查询与 SET
    /// 命中同一张表的同一行，能查到就一定能更新到）。
    ///
    /// slot 用于注入特定格口控制器（如「门已被先关掉」的现场前提）；默认只记账的假控制器。
    /// </summary>
    private (SqliteDb Db, FlowEngine Engine) NewEngine(
        bool seedMesWire = true, bool failingMesSync = false, FakeSlotController? slot = null)
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        WireMesDiscoSchema.EnsureAll(db);
        OperationLogSchema.Ensure(db);

        if (seedMesWire)
        {
            var seed = db.Execute(
                """
                INSERT INTO v_fw_material_indetail (sublot, spec, type, code, shelflife, matlot, qty, state, DISCOEQPNO)
                VALUES (:lot, :spec, '焊丝', 'W-1.2', '2026-12-31 00:00:00', '14Z1888', 1, '可用', NULL);
                """,
                new Dictionary<string, object?> { ["lot"] = Lot, ["spec"] = Spec });
            Assert.False(seed.HasError, seed.Error);
        }

        var catalog = SqlCatalog.Load();
        var appDb = new SqliteAppDb(db);
        IMesGateway gateway = new SqliteMockMesGateway(db, new MesFunctionSettings());
        if (failingMesSync)
            gateway = new DiscoSetFailsGateway(gateway);

        var services = new EngineServices
        {
            Catalog = catalog,
            AppDb = appDb,
            Mes = gateway,
            Slot = slot ?? new FakeSlotController(),
            OperationLog = new SqliteOperationLogSink(catalog, appDb),
            DiscoSync = failingMesSync
                ? new WireDiscoEqpSyncService(
                    catalog, appDb, gateway, new WireMesDiscoSyncStore(db), "DISCO-01", new MesReconAuditLogStore(db))
                : null,
        };

        return (db, new FlowEngine(services));
    }

    /// <summary>让 DISCOEQPNO 的 SET 固定失败，其余调用透传。</summary>
    private sealed class DiscoSetFailsGateway : IMesGateway
    {
        private readonly IMesGateway _inner;

        public DiscoSetFailsGateway(IMesGateway inner) => _inner = inner;

        public MesMode Mode => _inner.Mode;

        public DbResult Run(SqlCatalogItem item, IDictionary<string, object?> parameters) =>
            item.Id.Contains("set_disco_eqp_no", StringComparison.OrdinalIgnoreCase)
                ? DbResult.Failure(item.Sql, "ORA-04068: 包状态已丢弃")
                : _inner.Run(item, parameters);
    }

    /// <summary>跑完整条流程，返回结束时的 trace。runtime 模拟界面在开跑前写入的运行期值（如操作人）。</summary>
    private static TraceEntry RunToEnd(
        FlowEngine engine, string lot, IReadOnlyDictionary<string, string>? runtime = null)
    {
        engine.Begin(MhLoadFlow());
        foreach (var (key, value) in runtime ?? new Dictionary<string, string>())
            engine.Context!.Runtime[key] = value;

        engine.Context!.UserInputs["inputAvailableWireLotNo"] =
            new Dictionary<string, string> { ["available_wire_lot_no"] = lot };

        var guard = 0;
        while (!engine.Finished && guard++ < 50)
            engine.StepOnce();

        Assert.True(engine.Finished, "流程未在 50 步内结束。");
        return engine.Trace[^1];
    }

    [Fact]
    public void MhLoad_Success_WritesOneRecordCarryingDoorTimestamps()
    {
        var (db, engine) = NewEngine();

        var last = RunToEnd(engine, Lot);
        Assert.Equal("success", last.Outcome);
        Assert.Equal("endSuccess", last.NodeId);

        // 口径优化（2026-10-09）：一次存料 = 1 条；开门/关门降级为这条上的两个时刻，
        // 不再各写一条 door_open / door_close（原先是 3 条）。
        var rows = db.Query("SELECT * FROM app_operation_log;", new Dictionary<string, object?>()).Rows;
        var row = Assert.Single(rows);

        // 格位号应与流程实际选中的格口一致（不硬编码，避免依赖 seed 顺序）
        var chosenSlotNo = engine.Context!.NodeResults["checkAvailableSlot"]["slot_no"]?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(chosenSlotNo));

        Assert.Equal("mh_load_available_wire", row["action_type"]);
        Assert.Equal(Lot, row["lot_no"]);              // 焊丝批号
        Assert.Equal(Spec, row["wire_spec"]);          // 焊丝型号
        Assert.Equal(chosenSlotNo, row["slot_no"]);    // 存储格位
        Assert.Equal(1d, Convert.ToDouble(row["qty"]));
        Assert.Equal("成功", row["result"]);
        Assert.Equal("MH", row["operator_role"]);
        Assert.Equal(MhLoadFlowId, row["flow_id"]);
        Assert.Equal("updateDB", row["node_id"]);
        Assert.False(string.IsNullOrWhiteSpace(row["ts"]?.ToString()), "时间 ts 不应为空。");

        // 门时刻必须挂在这条业务记录上——否则停写门事件就等于把信息丢了
        var open = row["door_open_ts"]?.ToString();
        var close = row["door_close_ts"]?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(open), "存料记录缺 door_open_ts。");
        Assert.False(string.IsNullOrWhiteSpace(close), "存料记录缺 door_close_ts。");
        Assert.True(DateTime.Parse(close!) >= DateTime.Parse(open!), "door_close_ts 早于 door_open_ts。");
    }

    /// <summary>
    /// 现场前提：门在流程推进到「关门」节点之前就被人工/硬件关掉了。
    ///
    /// 真实实现里 <c>IsOpen</c> 取自 <c>app_slot.door_state</c>，此时已是 closed，
    /// 于是关门节点里的 <c>CloseNextOpenDoor()</c> 返回 null。修复前这种情形下
    /// <c>door_close_ts</c> 会留空——2026-10-10 现场实测 MH 存料 10/10 条都只有
    /// 开门时刻（假控制器默认把门一直保持「开着」，所以这条缺口此前没被拦住）。
    /// </summary>
    [Fact]
    public void MhLoad_WhenDoorAlreadyClosedBeforeCloseNode_StillStampsDoorCloseTs()
    {
        var (db, engine) = NewEngine(slot: new FakeSlotController { DoorClosesItselfAfterOpen = true });

        var last = RunToEnd(engine, Lot);
        Assert.Equal("success", last.Outcome);
        Assert.Equal("endSuccess", last.NodeId);

        var row = db.Query("SELECT * FROM app_operation_log;", new Dictionary<string, object?>()).Rows.Single();

        var open = row["door_open_ts"]?.ToString();
        var close = row["door_close_ts"]?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(open), "存料记录缺 door_open_ts。");
        Assert.False(string.IsNullOrWhiteSpace(close), "门已被先关掉时仍应记录 door_close_ts。");
        Assert.True(DateTime.Parse(close!) >= DateTime.Parse(open!), "door_close_ts 早于 door_open_ts。");
    }

    [Fact]
    public void MhLoad_WhenRecordWriteFails_FlowOutcomeIsUnchanged()
    {
        var (db, engine) = NewEngine();
        // 用真实 sink，但干掉记录表让写入真的失败（模拟库损坏 / 迁移未执行）
        db.Execute("DROP TABLE app_operation_log;", new Dictionary<string, object?>());

        var last = RunToEnd(engine, Lot);

        // C1：记录写不进去，业务 outcome 必须与正常时一致
        Assert.Equal("success", last.Outcome);
        Assert.Equal("endSuccess", last.NodeId);

        // 但必须被发现：有告警，且已转待补偿
        var updateDb = engine.Trace.Single(t => t.NodeId == "updateDB");
        Assert.Contains(updateDb.Findings, f => f.Category == "存取记录" && f.Severity == Severity.Warning);
        Assert.DoesNotContain(updateDb.Findings, f => f.Category == "存取记录" && f.Severity == Severity.Error);

        var pending = db.Query("SELECT COUNT(*) AS c FROM operation_log_pending;", new Dictionary<string, object?>()).First;
        // 口径优化后一次存料只有 1 条记录，所以待补偿也是 1 条（原先业务+两条门事件 = 3 条）
        Assert.Equal(1L, Convert.ToInt64(pending!["c"]));

        // 业务本身仍然完成：格口确实绑上了焊丝
        var slot = db.Query(
            "SELECT COUNT(*) AS c FROM app_slot WHERE wire_lot_no = :lot;",
            new Dictionary<string, object?> { ["lot"] = Lot }).First;
        Assert.Equal(1L, Convert.ToInt64(slot!["c"]));
    }

    /// <summary>
    /// 操作人由界面经运行期上下文传入（MH 流程没有操作人输入节点，§1.3 不允许改流程定义）。
    /// </summary>
    [Fact]
    public void MhLoad_CarriesOperatorFromRuntime()
    {
        var (db, engine) = NewEngine();

        RunToEnd(engine, Lot, new Dictionary<string, string>
        {
            [FlowEngine.OperatorIdKey] = "1001",
            [FlowEngine.OperatorNameKey] = "张三",
            [FlowEngine.OperatorTeamKey] = "A班",
        });

        var row = db.Query("SELECT * FROM app_operation_log WHERE action_type='mh_load_available_wire';",
            new Dictionary<string, object?>()).Rows.Single();
        Assert.Equal("1001", row["operator_id"]);
        Assert.Equal("张三", row["operator_name"]);
        Assert.Equal("A班", row["operator_team"]);
        Assert.Equal("MH", row["operator_role"]);
    }

    /// <summary>
    /// 界面没传操作人时**仍要落记录**（只是没有姓名工号）。
    /// 记录字段缺失绝不能变成丢记录——那正是 §1.4「不得影响既有功能」要防的。
    /// </summary>
    [Fact]
    public void MhLoad_WithoutOperator_StillRecordsWithRoleOnly()
    {
        var (db, engine) = NewEngine();

        RunToEnd(engine, Lot);

        var row = db.Query("SELECT * FROM app_operation_log WHERE action_type='mh_load_available_wire';",
            new Dictionary<string, object?>()).Rows.Single();
        Assert.Null(row["operator_id"]);
        Assert.Equal("MH", row["operator_role"]);
        Assert.Equal(Lot, row["lot_no"]);
    }

    [Fact]
    public void MhLoad_WhenFlowFails_DoesNotWriteRecord()
    {
        var (db, engine) = NewEngine(seedMesWire: false);

        var last = RunToEnd(engine, "LOT-NOT-IN-MES");

        Assert.Equal("fail", last.Outcome);  // 批号无效 → 终止于失败节点
        var count = db.Query("SELECT COUNT(*) AS c FROM app_operation_log;", new Dictionary<string, object?>()).First;
        Assert.Equal(0L, Convert.ToInt64(count!["c"]));
    }

    /// <summary>
    /// §3 覆盖矩阵第 9 项：操作失败要记录 result=失败 且 error_info 有原因，
    /// 且失败**不单独成动作类型**，仍是同一动作的一条记录。
    /// </summary>
    [Fact]
    public void MhLoad_WhenMesSyncFails_RecordsFailureWithReason()
    {
        var (db, engine) = NewEngine(failingMesSync: true);

        var last = RunToEnd(engine, Lot);

        // MES 同步失败会把流程 outcome 改成 error（既有行为，不由记录功能决定）
        Assert.Equal("fail", last.Outcome);
        Assert.Equal("endFail", last.NodeId);

        var row = db.Query("SELECT * FROM app_operation_log WHERE action_type='mh_load_available_wire';",
            new Dictionary<string, object?>()).Rows.Single();
        Assert.Equal("mh_load_available_wire", row["action_type"]); // 与成功记录同一动作类型
        Assert.Equal("失败", row["result"]);
        Assert.Equal(Lot, row["lot_no"]);
        Assert.False(string.IsNullOrWhiteSpace(row["error_info"]?.ToString()), "失败必须写明原因。");
    }
}
