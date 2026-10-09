using System.IO;
using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using WireCabinet.Hmi.Services;
using WireCabinet.Slots;
using WireCabinet.Slots.Hardware;
using Xunit;

namespace WireCabinet.Hmi.Tests;

/// <summary>
/// 同一次开门不能记出两条关门（2026-10-09 现场）。
///
/// 现场形态：OP 一次标准存废取新，每个格口除了流程自己那条关门（带 flow_id/节点/操作人/开门时刻）之外，
/// 巡检确认关库时又补了一条"无操作人、无开门时刻"的关门。这里钉住两条：
/// 未被本服务跟踪的开门，<see cref="MhDoorOnlyService.NotifySlotsClosedAsync"/> 不再补记录；
/// 而"无指令开门"那条配对关门仍必须写，否则会把合法的计划外开门配对也一起弄丢。
/// </summary>
public sealed class MhDoorCloseLogTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"wc-hmi-doorlog-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
        GC.SuppressFinalize(this);
    }

    private MhDoorOnlyService NewService(out SqliteDb db)
    {
        db = new SqliteDb(_path);
        db.Initialize(recreate: true);
        OperationLogSchema.Ensure(db);
        var slots = new SlotControlService(db, new NotConfiguredHardware(), new DoorOperationGate());
        var sink = new SqliteOperationLogSink(SqlCatalog.Load(), new SqliteAppDb(db));
        return new MhDoorOnlyService(slots, new WireOperationSession(), new SqliteAppDb(db),
            SqlCatalog.Load(), discoSync: null, operationLog: sink);
    }

    private static List<Dictionary<string, object?>> Rows(SqliteDb db) =>
        db.Query("SELECT * FROM app_operation_log ORDER BY id;", new Dictionary<string, object?>()).Rows;

    private static long SlotId(SqliteDb db, string slotNo) => Convert.ToInt64(
        db.Query($"SELECT slot_id FROM app_slot WHERE slot_no='{slotNo}';",
            new Dictionary<string, object?>()).First!["slot_id"]);

    [Fact]
    public async Task UntrackedClose_WritesNothing()
    {
        var service = NewService(out var db);
        var slotId = SlotId(db, "F-A01");

        await service.NotifySlotsClosedAsync([slotId]);

        // 该格口的开门不是本服务开的（流程开的）：流程自己会记那条关门，这里不能再补一条。
        Assert.Empty(Rows(db));
    }

    [Fact]
    public async Task UncommandedOpenAndClose_StillPairWithEachOther()
    {
        var service = NewService(out var db);

        Assert.True(service.RecordUncommandedOpen("F-A01"));
        Assert.True(service.RecordUncommandedClosed("F-A01"));

        var rows = Rows(db);
        Assert.Equal(2, rows.Count);
        // 两条都要留；关门必须与开门的时刻配对
        Assert.Equal(1L, Convert.ToInt64(rows[0]["is_uncommanded"]));
        Assert.Equal(1L, Convert.ToInt64(rows[1]["is_uncommanded"]));
        Assert.Equal(rows[0]["ts"], rows[1]["door_open_ts"]);
    }

    private sealed class NotConfiguredHardware : ISlotHardwareService
    {
        public bool IsConfigured => false;
        public IReadOnlyDictionary<string, bool> ModuleHealth => new Dictionary<string, bool>();
        public Task<bool> OpenLockAsync(string slotNo, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool?> ReadLockClosedAsync(string slotNo, CancellationToken ct = default) =>
            Task.FromResult<bool?>(null);
        public Task<IReadOnlyDictionary<string, SlotHardwareSnapshot>> ReadAllWiredSnapshotsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, SlotHardwareSnapshot>>(
                new Dictionary<string, SlotHardwareSnapshot>());
        public Task RefreshHealthAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
