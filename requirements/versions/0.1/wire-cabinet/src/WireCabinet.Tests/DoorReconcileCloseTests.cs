using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using WireCabinet.Slots;
using WireCabinet.Slots.Hardware;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 门态巡检回写「已关」的时序回归。
///
/// 现场（2026-10-09）实测形态：产品下令开门后，锁尚未真正释放（开锁是 300ms 脉冲、DI 采样还有延迟），
/// 巡检这一轮读到「库 open + DI 锁闭」就把它判成"已关门"并回写库；下一轮 DI 真的释锁了，
/// 于是被反向判成「库 closed 而 DI 释锁」= 无指令开门。一次标准 OP 存废取新里每个格口各误报一次。
///
/// 修复：开门后必须**真正读到过释锁**才允许回写「已关」；只有连续多轮都读锁闭、
/// 从未见释锁（重启后补偿 / 开锁失败）才按兜底阈值收口。
/// </summary>
public sealed class DoorReconcileCloseTests : IDisposable
{
    private const string SlotNo = "F-A01";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wc-door-close-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
        GC.SuppressFinalize(this);
    }

    private (SqliteDb Db, SlotControlService Slots, Hardware Hw, DoorStateReconciler Recon) NewFixture()
    {
        var db = new SqliteDb(_path);
        db.Initialize(recreate: true);
        var hw = new Hardware();
        var slots = new SlotControlService(db, hw, new DoorOperationGate());
        return (db, slots, hw, new DoorStateReconciler(slots, hw));
    }

    private static void RecordOpened(SqliteDb db) =>
        Assert.False(db.Execute($"UPDATE app_slot SET door_state='open' WHERE slot_no='{SlotNo}';",
            new Dictionary<string, object?>()).HasError);

    private static string? DoorState(SqliteDb db) =>
        db.Query($"SELECT door_state FROM app_slot WHERE slot_no='{SlotNo}';",
            new Dictionary<string, object?>()).First?["door_state"]?.ToString();

    /// <summary>开锁脉冲尚未生效：库已记 open，但 DI 仍读到锁闭。</summary>
    private static IReadOnlyDictionary<string, SlotHardwareSnapshot> Locked(Hardware hw) =>
        hw.Set(LockClosed: true);

    /// <summary>锁已释锁（门真的开了）。</summary>
    private static IReadOnlyDictionary<string, SlotHardwareSnapshot> Released(Hardware hw) =>
        hw.Set(LockClosed: false);

    [Fact]
    public async Task OpenThenLockedBeforePulseTakesEffect_IsNotClosedAndIsNotUncommanded()
    {
        var (db, _, hw, recon) = NewFixture();
        RecordOpened(db);

        // 1) 脉冲还没生效，DI 仍读锁闭 —— 不能据此判"已关"
        var closed = await recon.ReconcileAsync(Locked(hw));
        Assert.Empty(closed);
        Assert.Equal("open", DoorState(db));

        // 2) 下一轮 DI 释锁：门是真的开着，库内仍是 open，所以不是计划外开门
        Assert.Empty(await recon.ReconcileAsync(Released(hw)));
        Assert.Equal("open", DoorState(db));
        Assert.Empty(await recon.FindUncommandedAsync(Released(hw)));
    }

    [Fact]
    public async Task ReleasedThenLocked_ClosesOnFirstPass()
    {
        var (db, _, hw, recon) = NewFixture();
        RecordOpened(db);

        Assert.Empty(await recon.ReconcileAsync(Released(hw)));
        var closed = await recon.ReconcileAsync(Locked(hw));

        Assert.Single(closed);
        Assert.Equal("closed", DoorState(db));
    }

    [Fact]
    public async Task NeverReleased_ClosesOnlyAfterGracePasses()
    {
        var (db, _, hw, recon) = NewFixture();
        RecordOpened(db);

        // 一直读锁闭、从未见释锁（重启后补偿 / 开锁失败）：前两轮先不动
        Assert.Empty(await recon.ReconcileAsync(Locked(hw)));
        Assert.Empty(await recon.ReconcileAsync(Locked(hw)));
        Assert.Equal("open", DoorState(db));

        // 达到兜底轮数才收口
        Assert.Single(await recon.ReconcileAsync(Locked(hw)));
        Assert.Equal("closed", DoorState(db));
    }

    [Fact]
    public async Task ReadFailure_NeitherClosesNorReportsUncommanded()
    {
        var (db, _, hw, recon) = NewFixture();
        RecordOpened(db);

        var bad = hw.Set(ReadOk: false, LockClosed: null);
        Assert.Empty(await recon.ReconcileAsync(bad));
        Assert.Equal("open", DoorState(db));
        Assert.Empty(await recon.FindUncommandedAsync(bad));
    }

    private sealed class Hardware : ISlotHardwareService
    {
        private readonly Dictionary<string, SlotHardwareSnapshot> _data = new();
        public bool IsConfigured => true;
        public IReadOnlyDictionary<string, bool> ModuleHealth => new Dictionary<string, bool>();

        public IReadOnlyDictionary<string, SlotHardwareSnapshot> Set(bool? LockClosed, bool ReadOk = true)
        {
            _data[SlotNo] = new SlotHardwareSnapshot
            {
                SlotCode = SlotNo, Wired = true, ReadOk = ReadOk, LockClosed = LockClosed
            };
            return new Dictionary<string, SlotHardwareSnapshot>(_data);
        }

        public Task<bool> OpenLockAsync(string slotNo, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool?> ReadLockClosedAsync(string slotNo, CancellationToken ct = default) =>
            Task.FromResult(_data.GetValueOrDefault(slotNo)?.LockClosed);
        public Task<IReadOnlyDictionary<string, SlotHardwareSnapshot>> ReadAllWiredSnapshotsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, SlotHardwareSnapshot>>(_data);
        public Task RefreshHealthAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
