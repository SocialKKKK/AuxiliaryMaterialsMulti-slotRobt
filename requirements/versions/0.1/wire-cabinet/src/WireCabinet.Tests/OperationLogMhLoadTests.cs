using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using WireCabinet.Flows;
using WireCabinet.Slots;
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
    /// </summary>
    private (SqliteDb Db, FlowEngine Engine) NewEngine(bool seedMesWire = true)
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

        var appDb = new SqliteAppDb(db);
        var services = new EngineServices
        {
            Catalog = SqlCatalog.Load(),
            AppDb = appDb,
            Mes = new SqliteMockMesGateway(db, new MesFunctionSettings()),
            Slot = new FakeSlotController(),
            OperationLog = new SqliteOperationLogSink(SqlCatalog.Load(), appDb),
        };

        return (db, new FlowEngine(services));
    }

    /// <summary>跑完整条流程，返回结束时的 trace。</summary>
    private static TraceEntry RunToEnd(FlowEngine engine, string lot)
    {
        engine.Begin(MhLoadFlow());
        engine.Context!.UserInputs["inputAvailableWireLotNo"] =
            new Dictionary<string, string> { ["available_wire_lot_no"] = lot };

        var guard = 0;
        while (!engine.Finished && guard++ < 50)
            engine.StepOnce();

        Assert.True(engine.Finished, "流程未在 50 步内结束。");
        return engine.Trace[^1];
    }

    [Fact]
    public void MhLoad_Success_WritesExactlyOneRecordWithExpectedFields()
    {
        var (db, engine) = NewEngine();

        var last = RunToEnd(engine, Lot);
        Assert.Equal("success", last.Outcome);
        Assert.Equal("endSuccess", last.NodeId);

        var rows = db.Query("SELECT * FROM app_operation_log;", new Dictionary<string, object?>()).Rows;
        Assert.Single(rows); // 一次业务操作一条，不多不少

        // 格位号应与流程实际选中的格口一致（不硬编码，避免依赖 seed 顺序）
        var chosenSlotNo = engine.Context!.NodeResults["checkAvailableSlot"]["slot_no"]?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(chosenSlotNo));

        var row = rows[0];
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
        Assert.Equal(1L, Convert.ToInt64(pending!["c"]));

        // 业务本身仍然完成：格口确实绑上了焊丝
        var slot = db.Query(
            "SELECT COUNT(*) AS c FROM app_slot WHERE wire_lot_no = :lot;",
            new Dictionary<string, object?> { ["lot"] = Lot }).First;
        Assert.Equal(1L, Convert.ToInt64(slot!["c"]));
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

    /// <summary>只做开门记账、不做真实硬件动作。</summary>
    private sealed class FakeSlotController : ISlotController
    {
        private readonly HashSet<long> _open = new();

        // 显式空 add/remove：测试不关心 Changed，也避免未使用事件的编译告警。
        public event EventHandler? Changed { add { } remove { } }

        public IReadOnlyList<SlotDoorState> Slots => Array.Empty<SlotDoorState>();

        public void Reload() { }

        public Task<bool> OpenDoorAsync(long slotId, CancellationToken ct = default)
        {
            _open.Add(slotId);
            return Task.FromResult(true);
        }

        public Task CloseDoorAsync(long slotId, CancellationToken ct = default)
        {
            _open.Remove(slotId);
            return Task.CompletedTask;
        }

        public void OpenDoor(long slotId) => _open.Add(slotId);

        public void CloseDoor(long slotId) => _open.Remove(slotId);

        public long? CloseNextOpenDoor() => _open.Count > 0 ? _open.Min() : null;

        public bool AreAllDoorsClosed() => _open.Count == 0;

        public IReadOnlyList<long> OpenDoorIds() => _open.ToList();
    }
}
