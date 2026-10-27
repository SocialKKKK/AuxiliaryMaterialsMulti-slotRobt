using System.IO;
using System.Reflection;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using WireCabinet.Data;
using WireCabinet.Flows;
using WireCabinet.Hmi;
using WireCabinet.Hmi.Services;
using WireCabinet.Rcs;
using WireCabinet.Slots;
using WireCabinet.Slots.Hardware;
using WireCabinet.Tests;

// Offline observation probe: only fake RCS and newly created temporary databases.
// No WPF Application is started, no hardware service is called, no site credentials are printed.
// Exit 0 means the probe completed, not that the observed product behavior is correct.
var root = Path.Combine(Path.GetTempPath(), "wirecabinet-audit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var findings = new List<object>();
var catalog = SqlCatalog.Load();

void Need(bool value, string message)
{
    if (!value) throw new InvalidOperationException("Probe precondition failed: " + message);
}
void Observe(string id, bool reproduced, string finding, object evidence) =>
    findings.Add(new { id, reproduced, finding, evidence });
(SqliteDb Db, SqliteOperationLogSink Sink) NewDb(string name)
{
    var db = new SqliteDb(Path.Combine(root, name + ".db"));
    db.Initialize();
    OperationLogSchema.Ensure(db);
    return (db, new SqliteOperationLogSink(catalog, new SqliteAppDb(db)));
}
void Exec(SqliteDb db, string sql)
{
    using var conn = db.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}
List<Dictionary<string, object?>> Rows(SqliteDb db) =>
    db.Query("SELECT * FROM app_operation_log ORDER BY id", new Dictionary<string, object?>()).Rows;
void RejectInserts(SqliteDb db) => Exec(db, """
    CREATE TRIGGER audit_reject_insert BEFORE INSERT ON app_operation_log
    BEGIN SELECT RAISE(ABORT, 'offline audit injected insert failure'); END;
    """);
AgvServices Service(FakeAgvClient client, StationConfiguration? stations = null, IOperationLogSink? sink = null)
{
    var slots = new FakeAgvSlots();
    return new AgvServices(client, FakeAgvClient.Options(), new() { LowBatteryPercent = 30, FullBatteryPercent = 90 },
        stations ?? FakeAgvClient.Stations(), slots, slots, sink);
}
void SetStatic(string property, object? value) =>
    typeof(App).GetProperty(property, BindingFlags.Public | BindingFlags.Static)!.SetValue(null, value);
AppBootstrap NewBootstrap(string name) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ApplicationDatabase:Path"] = Path.Combine(root, name + ".db"),
    ["Mes:ConnectionString"] = ""
}).Build());

// A01: unknown physical door is rendered from the database and accepted by the cleanup policy.
{
    var slot = new SlotDoorState { SlotId = 1, SlotNo = "F-A01", IsOpen = false };
    var hw = new SlotHardwareSnapshot { SlotCode = "F-A01", Wired = true, ReadOk = false, LockClosed = null };
    var displayedOpen = SlotDoorDisplay.IsDisplayOpen(slot, hw, hardwareConfigured: true);
    var now = DateTime.UtcNow;
    var decision = OpIssueAutoCompletePolicy.ShouldAutoComplete("领用待取", now.AddMinutes(-20), displayedOpen, now,
        OpIssueAutoCompletePolicy.DefaultGrace);
    using var bootstrap = NewBootstrap("unknown-di-cleanup");
    Exec(bootstrap.AppDb, """
        UPDATE app_slot SET biz_state='available_wire', wire_lot_no='AUDIT-ONLY', wire_state='领用待取',
            door_state='closed', lock_state='locked', updated_at='2000-01-01 00:00:00' WHERE slot_id=1;
        """);
    bootstrap.FlowSlots.Reload();
    var hardware = new OfflineConfiguredHardware();
    typeof(AppBootstrap).GetField("<Hardware>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bootstrap, hardware);
    var priorBootstrap = App.Bootstrap;
    var priorFlows = App.Flows;
    var priorDoors = App.DoorOps;
    var priorPoll = App.SlotHardwarePoll;
    using var poll = new SlotHardwarePollService(hardware);
    SetStatic("Bootstrap", bootstrap);
    SetStatic("Flows", new FlowCoordinator(bootstrap));
    SetStatic("DoorOps", bootstrap.DoorOps);
    SetStatic("SlotHardwarePoll", poll); // no snapshots, no timer, all hardware methods throw if called
    try
    {
        var completed = OpInterruptedIssueAutoComplete.TryRun();
        var after = bootstrap.AppDb.Query("SELECT biz_state, wire_lot_no FROM app_slot WHERE slot_id=1", new Dictionary<string, object?>()).First!;
        Observe("A01", decision && completed.Count == 1 && after["wire_lot_no"] is null,
            "DI unknown permits interrupted-issue auto cleanup",
            new { hardwareConfigured = true, hw.ReadOk, hw.LockClosed, databaseDoor = "closed", displayedOpen,
                shouldAutoComplete = decision, completed = completed.Count, afterBizState = after["biz_state"], afterLot = after["wire_lot_no"] });
    }
    finally
    {
        SetStatic("Bootstrap", priorBootstrap); SetStatic("Flows", priorFlows);
        SetStatic("DoorOps", priorDoors); SetStatic("SlotHardwarePoll", priorPoll);
    }
}

// A02: two queued rows share the actual second-resolution timestamp; close B before A.
{
    var (db, sink) = NewDb("same-second");
    RejectInserts(db);
    const string ts = "2026-10-27 00:00:00";
    const string action = "mh_take_wire";
    var a = sink.TryWrite(new() { Ts = ts, ActionType = action, SlotNo = "F-A01", DoorOpenTs = ts });
    var b = sink.TryWrite(new() { Ts = ts, ActionType = action, SlotNo = "F-A02", DoorOpenTs = ts });
    Need(a.Queued && b.Queued, "both records must be queued");
    var closeB = sink.BackfillDoorTs(null, action, ts, ts, "2026-10-27 00:00:05");
    var closeA = sink.BackfillDoorTs(null, action, ts, ts, "2026-10-27 00:00:10");
    Exec(db, "DROP TRIGGER audit_reject_insert;");
    Need(sink.RetryPendingBatch() == 2, "both records must be compensated");
    var rows = Rows(db);
    Observe("A02", rows[0]["door_close_ts"]?.ToString() == "2026-10-27 00:00:05",
        "Same-second pending rows receive each other's close timestamps",
        new { closeB.Success, closeASuccess = closeA.Success, expectedA = "00:00:10", expectedB = "00:00:05",
            actualA = rows[0]["door_close_ts"], actualB = rows[1]["door_close_ts"] });
}

// A03: no race is needed: retry succeeds BEFORE the user closes the door.
{
    var (db, sink) = NewDb("retry-before-close");
    RejectInserts(db);
    const string ts = "2026-10-27 01:00:00";
    var write = sink.TryWrite(new() { Ts = ts, ActionType = "op_issue_available_wire", SlotNo = "F-A01" });
    Need(write.Queued && write.RowId is null, "record must be queued without row ID");
    Exec(db, "DROP TRIGGER audit_reject_insert;");
    Need(sink.RetryPendingBatch() == 1, "retry must finish before close");
    var backfill = sink.BackfillDoorTs(write.RowId, "op_issue_available_wire", ts, "2026-10-27 01:00:01", "2026-10-27 01:03:00");
    var row = Rows(db).Single();
    Observe("A03", !backfill.Success && row["door_close_ts"] is null,
        "Retry-before-close loses the association with the inserted record",
        new { backfill.Success, backfill.Error, pending = sink.ListPending().Count, doorClose = row["door_close_ts"] });
}

// A04: start a real coordinator session at its input node, then run product auto-charge with fake RCS.
{
    using var bootstrap = NewBootstrap("active-flow");
    var flows = new FlowCoordinator(bootstrap);
    Need(flows.TryBeginFlow("operator_return_wire_and_issue_available_wire", out var message), message);
    var prior = App.Flows;
    SetStatic("Flows", flows);
    try
    {
        var client = new FakeAgvClient { Snapshot = FakeAgvClient.Ready("IDLE", 20, 22) };
        using var service = Service(client);
        await service.PollCycleAsync(null, null);
        Observe("A04", flows.IsFlowActive && client.Charges == 1,
            "Automatic charging dispatches during an active material workflow",
            new { flows.IsFlowActive, bootstrap.WireSession.HasActiveSession, charges = client.Charges, node = flows.CurrentNodeId });
    }
    finally { flows.EndFlow(); SetStatic("Flows", prior); }
}

// A05: an actual failed status refresh leaves station/open-door UI gates on the last idle state.
{
    var client = new FakeAgvClient { Snapshot = FakeAgvClient.Ready("IDLE", 100, 22) };
    using var service = Service(client);
    Need(await service.ConnectAsync(), "fake connection");
    var priorAgv = App.Agv;
    var priorGate = App.StationGate;
    var priorUi = App.UiGate;
    service.Stations.WorkStations.Single(s => s.Code == "OP").AllowedWireFlows.Add(OpStationAccess.FlowId);
    SetStatic("Agv", service);
    SetStatic("StationGate", new StationArrivalGate(service.Stations));
    SetStatic("UiGate", new HmiUiGateService());
    try
    {
        client.SnapshotError = new IOException("offline audit simulated RCS disconnect");
        var mhAccess = await MhStationAccess.CanRunLoadWireFlowAsync();
        client.SnapshotError = null;
        Need(await service.ConnectAsync(), "reconnect before testing OP refresh failure");
        client.SnapshotError = new IOException("offline audit simulated RCS disconnect");
        var opAccess = await OpStationAccess.CanRunOpFlowAsync();
        var gate = new HmiUiGateService();
        Observe("A05", !service.IsConnected && mhAccess.Allowed && opAccess.Allowed && !gate.BlocksSlotDoorControls,
            "RCS disconnect retains permission to use OP/MH and open doors from stale stopped state",
            new { service.IsConnected, service.LastSysState, service.LastMoveState, gate.CanUseMhPage,
                gate.CanUseOpPage, mhFlowAllowed = mhAccess.Allowed, opFlowAllowed = opAccess.Allowed,
                gate.BlocksSlotDoorControls, gate.BlocksMhSlotDoorControls });
    }
    finally { SetStatic("Agv", priorAgv); SetStatic("StationGate", priorGate); SetStatic("UiGate", priorUi); }
}

// A06: names cannot be resolved; manual charging blocks, while the automatic path still places an order.
{
    var client = new FakeAgvClient { Snapshot = FakeAgvClient.Ready("IDLE", 20, 22) };
    var stations = FakeAgvClient.Stations();
    stations.ChargeStation = new() { RcsDestination = 99, RcsStationName = "not-present-in-map" };
    using var service = Service(client, stations);
    Need(await service.ConnectAsync(), "RCS transport remains connected despite name error");
    var manual = await service.GoChargeAsync();
    await service.PollCycleAsync(null, null);
    Observe("A06", !manual.Ok && client.Charges == 1,
        "Automatic dispatch bypasses the station-name error that blocks manual commands",
        new { manual.Ok, manual.Message, automaticCharges = client.Charges, cachedDestination = stations.ChargeStation.RcsDestination });
}

// A07: a successful low-battery automatic charge has no operation-log entry.
{
    var (db, sink) = NewDb("charge-log");
    var client = new FakeAgvClient { Snapshot = FakeAgvClient.Ready("IDLE", 20, 22) };
    using var service = Service(client, sink: sink);
    await service.PollCycleAsync(null, null);
    var count = Rows(db).Count;
    Observe("A07", client.Charges == 1 && count == 0, "Low-battery automatic charging omits its operation-log record",
        new { charges = client.Charges, records = count, pending = sink.ListPending().Count });
}

// A08: the live list exposed by FlowSlotController can be invalidated by the background Reload caller.
{
    var (db, _) = NewDb("reload-list");
    var controller = new FlowSlotController(new FakeAgvSlots(), db);
    using var enumerator = controller.Slots.GetEnumerator();
    Need(enumerator.MoveNext(), "seed slots exist");
    controller.Reload();
    string? error = null;
    try { enumerator.MoveNext(); }
    catch (InvalidOperationException ex) { error = ex.Message; }
    Observe("A08", error is not null, "Reload mutates the same slot list consumed by concurrent UI enumeration",
        new { error, slots = controller.Slots.Count });
}

// A09: recovery detail exists in the read model but is absent from the exported explanation.
{
    const string detail = "程序中断；启动巡检按门已关自动完成清库（原门时刻未记录）";
    var path = Path.Combine(root, "recovery-detail.xlsx");
    var export = new OperationLogExcelExporter().Export([
        new OperationLogRecord { ActionType = "op_issue_available_wire", Message = "领用提交完成", Result = "成功", Detail = detail }
    ], path);
    Need(export.Success, export.Error ?? "Excel export");
    using var workbook = new XLWorkbook(path);
    var explanation = workbook.Worksheet(1).Cell(2, 16).GetString();
    Observe("A09", explanation == "领用提交完成" && !explanation.Contains("程序中断"),
        "Recovery detail is invisible in the exported explanation", new { storedDetail = detail, exportedExplanation = explanation });
}

// B01 is a DB protection boundary, not an application DELETE call: update is protected, delete is not.
{
    var (db, sink) = NewDb("delete-boundary");
    Need(sink.TryWrite(new() { ActionType = "maint_trial_open", SlotNo = "F-A01" }).Success, "sample inserted");
    var deletion = db.Execute("DELETE FROM app_operation_log", new Dictionary<string, object?>());
    Observe("B01", !deletion.HasError && deletion.RowsAffected == 1,
        "Database trigger protects UPDATE only; direct DELETE is allowed",
        new { deletion.HasError, deletion.RowsAffected, remaining = Rows(db).Count });
}

var definitions = FlowLoader.LoadAll();
var missingSql = definitions.SelectMany(flow => flow.Nodes.Values.SelectMany(node =>
    new[] { node.SqlId, node.OpenSqlId }.Where(id => !string.IsNullOrWhiteSpace(id)).SelectMany(id =>
        new[] { MesMode.SqliteMock, MesMode.Oracle }.Where(mode => catalog.FindForMode(id!, mode) is null)
            .Select(mode => new { flow = flow.FlowId, node = node.Id, sqlId = id, mode = mode.ToString() })))).ToList();
var invalidRoutes = definitions.SelectMany(flow => flow.Nodes.Values.SelectMany(node =>
    node.Routing.Where(route => !string.IsNullOrWhiteSpace(route.Value) && flow.Node(route.Value) is null)
        .Select(route => new { flow = flow.FlowId, node = node.Id, outcome = route.Key, target = route.Value }))).ToList();
var (structureDb, _) = NewDb("structure");
var structure = new { flows = definitions.Count, nodes = definitions.Sum(f => f.Nodes.Count), sqlItems = catalog.Items.Count,
    operationLogColumns = structureDb.Query("PRAGMA table_info(app_operation_log)", new Dictionary<string, object?>()).Rows.Count,
    operationLogAddedColumns = OperationLogSchema.Columns.Length,
    operationLogSqlItems = catalog.Items.Keys.Count(id => id.StartsWith("app.operation_log.", StringComparison.Ordinal)),
    missingSql, invalidRoutes, unknownNodes = definitions.SelectMany(f => f.Nodes.Values).Count(n => n.Type == NodeType.Unknown) };
var json = JsonSerializer.Serialize(new { productBaseline = "2e1086c", scope = "offline fake RCS and isolated temporary databases", structure, findings },
    new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
if (args.Length > 0) File.WriteAllText(args[0], json + Environment.NewLine);
Console.WriteLine("Temporary evidence: " + root);

sealed class OfflineConfiguredHardware : ISlotHardwareService
{
    public bool IsConfigured => true;
    public IReadOnlyDictionary<string, bool> ModuleHealth => new Dictionary<string, bool>();
    public Task<bool> OpenLockAsync(string slotCode, CancellationToken ct = default) => throw new InvalidOperationException("Hardware access forbidden in audit probe");
    public Task<bool?> ReadLockClosedAsync(string slotCode, CancellationToken ct = default) => throw new InvalidOperationException("Hardware access forbidden in audit probe");
    public Task<IReadOnlyDictionary<string, SlotHardwareSnapshot>> ReadAllWiredSnapshotsAsync(CancellationToken ct = default) => throw new InvalidOperationException("Hardware access forbidden in audit probe");
    public Task RefreshHealthAsync(CancellationToken ct = default) => throw new InvalidOperationException("Hardware access forbidden in audit probe");
}
