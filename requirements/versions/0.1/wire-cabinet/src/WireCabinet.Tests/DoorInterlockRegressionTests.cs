using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using WireCabinet.Slots;
using WireCabinet.Slots.Hardware;
using WireCabinet.Rcs;
using Xunit;

namespace WireCabinet.Tests;

public sealed class DoorInterlockRegressionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wc-door-fault-{Guid.NewGuid():N}.db");

    [Theory]
    [InlineData("ready", true)]
    [InlineData("missing", false)]
    [InlineData("failed", false)]
    [InlineData("null", false)]
    [InlineData("open", false)]
    [InlineData("disabled-failed", false)]
    [InlineData("unwired", true)]
    [InlineData("no-wired-slots", false)]
    public async Task ActualDoorProviderBlocksUnsafeDeparture(string fault, bool allowed)
    {
        var db = new SqliteDb(_path); db.Initialize(true);
        var hardware = new Hardware();
        var slots = new SlotControlService(db, hardware, new DoorOperationGate());
        foreach (var slot in await slots.ListSlotsAsync(new() { EnabledOnly = false }))
            if (slot.IoWired)
                hardware.Data[slot.SlotNo] = Snapshot(slot.SlotNo, true, true);
        const string no = "F-A01";
        switch (fault)
        {
            case "missing": hardware.Data.Remove(no); break;
            case "failed": hardware.Data[no] = Snapshot(no, false, null); break;
            case "null": hardware.Data[no] = Snapshot(no, true, null); break;
            case "open": hardware.Data[no] = Snapshot(no, true, false); break;
            case "disabled-failed":
                Assert.False(db.Execute("UPDATE app_slot SET is_enabled=0 WHERE slot_no='F-A01';", new Dictionary<string, object?>()).HasError);
                hardware.Data[no] = Snapshot(no, false, null); break;
            case "unwired":
                Assert.False(db.Execute("UPDATE app_slot SET io_wired=0 WHERE slot_no='F-A01';", new Dictionary<string, object?>()).HasError);
                hardware.Data.Remove(no); break;
            case "no-wired-slots":
                Assert.False(db.Execute("UPDATE app_slot SET io_wired=0;", new Dictionary<string, object?>()).HasError);
                hardware.Data.Clear(); break;
        }
        foreach (var sys in new[] { "IDLE", "CHARGING" })
        {
            var client = new FakeAgvClient { Snapshot = FakeAgvClient.Ready(sys) };
            var result = await new AgvControlLoop(client, FakeAgvClient.Options(), new CabinetDoorStateProvider(slots)).TickAsync(22);
            Assert.Equal(allowed, result.Doors.AllDoorsClosed);
            Assert.Equal(allowed ? 1 : 0, client.Moves.Count);
            Assert.Equal(fault == "open", result.Doors.AnyDoorOpen);
        }
    }

    [Fact]
    public async Task MockModeStillUsesDatabaseDoorState()
    {
        var db = new SqliteDb(_path); db.Initialize(true);
        var slots = new SlotControlService(db, new Hardware { IsConfigured = false }, new DoorOperationGate());
        Assert.True((await slots.GetDoorStateForAgvAsync()).AllDoorsClosed);
        db.Execute("UPDATE app_slot SET door_state='open' WHERE slot_no='F-A01';", new Dictionary<string, object?>());
        Assert.False((await slots.GetDoorStateForAgvAsync()).AllDoorsClosed);
    }

    private static SlotHardwareSnapshot Snapshot(string no, bool readOk, bool? closed) =>
        new() { SlotCode = no, Wired = true, ReadOk = readOk, LockClosed = closed };

    private sealed class Hardware : ISlotHardwareService
    {
        public bool IsConfigured { get; init; } = true;
        public Dictionary<string, SlotHardwareSnapshot> Data { get; } = new();
        public IReadOnlyDictionary<string, bool> ModuleHealth => new Dictionary<string, bool>();
        public Task<bool> OpenLockAsync(string no, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool?> ReadLockClosedAsync(string no, CancellationToken ct = default) => Task.FromResult(Data.GetValueOrDefault(no)?.LockClosed);
        public Task<IReadOnlyDictionary<string, SlotHardwareSnapshot>> ReadAllWiredSnapshotsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, SlotHardwareSnapshot>>(Data);
        public Task RefreshHealthAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
    }
}
