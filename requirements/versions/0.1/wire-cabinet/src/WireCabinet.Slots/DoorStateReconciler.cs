using WireCabinet.Slots.Hardware;

namespace WireCabinet.Slots;

/// <summary>根据锁 DI 将仍为 open 的格口回写为 closed（设计：DI 先 Locked，再更新库）。</summary>
public sealed class DoorStateReconciler
{
    private readonly ISlotControlService _slots;
    private readonly ISlotHardwareService _hardware;

    public DoorStateReconciler(ISlotControlService slots, ISlotHardwareService hardware)
    {
        _slots = slots;
        _hardware = hardware;
    }

    /// <summary>计划外开门的去重：持续释锁只记一次，否则每次轮询都会刷一条。</summary>
    private readonly UncommandedOpenTracker _uncommanded = new();
    public IReadOnlyList<(long SlotId, string SlotNo)> RecoveredUncommanded { get; private set; } = [];

    /// <summary>
    /// 开门是硬件脉冲（默认 300ms），DI 采样也有延迟；巡检约 2 秒一轮，可能正撞上
    /// 「库已记为 open、锁还没释放」的窗口。只有连续这么多轮都读到锁闭、且期间从未读到释锁
    /// （重启后补偿或开锁失败），才认定它其实已关上。正常开门下一轮就读到释锁，累计不到该阈值。
    /// </summary>
    private const int LockedPassesBeforeAssumingClosed = 3;

    /// <summary>库内 open 且已真正读到过释锁的格口——判「已关」的前提。</summary>
    private readonly HashSet<long> _seenReleased = [];

    /// <summary>库内 open、连续读到锁闭但尚未见释锁的轮数。</summary>
    private readonly Dictionary<long, int> _lockedPasses = [];

    /// <summary>
    /// 对库 open 且 DI 确认锁闭的格口执行 ConfirmDoorClosed。返回成功关库的 slot_id。
    /// 参见 <see cref="LockedPassesBeforeAssumingClosed"/>：没见过释锁时不能立刻判「已关」。
    /// </summary>
    public async Task<IReadOnlyList<long>> ReconcileAsync(
        IReadOnlyDictionary<string, SlotHardwareSnapshot> snapshots,
        CancellationToken ct = default)
    {
        if (!_hardware.IsConfigured)
            return [];

        var openIds = _slots.OpenSlotIds();
        if (openIds.Count == 0)
        {
            _seenReleased.Clear();
            _lockedPasses.Clear();
            return [];
        }

        // 只保留仍在库内 open 的格口，避免追踪集合无界增长
        _seenReleased.RemoveWhere(id => !openIds.Contains(id));
        foreach (var stale in _lockedPasses.Keys.Where(id => !openIds.Contains(id)).ToList())
            _lockedPasses.Remove(stale);

        var slots = await _slots.ListSlotsAsync(new SlotListFilter { EnabledOnly = false }, ct);
        var closed = new List<long>();

        foreach (var id in openIds)
        {
            var row = slots.FirstOrDefault(s => s.SlotId == id);
            if (row is null || row.DoorState != "open")
                continue;

            if (!snapshots.TryGetValue(row.SlotNo, out var hw) || !hw.ReadOk || hw.LockClosed is null)
                continue;

            if (hw.LockClosed == false)
            {
                // 真正读到释锁：门确实开过。这是后面判「已关」的前提。
                _seenReleased.Add(id);
                _lockedPasses.Remove(id);
                continue;
            }

            if (!_seenReleased.Contains(id))
            {
                var passes = (_lockedPasses.TryGetValue(id, out var previous) ? previous : 0) + 1;
                _lockedPasses[id] = passes;
                if (passes < LockedPassesBeforeAssumingClosed)
                    continue;
            }

            if (await _slots.ConfirmDoorClosedAsync(id, ct))
            {
                closed.Add(id);
                _seenReleased.Remove(id);
                _lockedPasses.Remove(id);
            }
        }

        return closed;
    }

    /// <summary>
    /// 无指令开门（0.2 § 覆盖矩阵第 10 项）：库内**没有**开门指令，而锁 DI 读到**释锁**。
    /// 与 ReconcileAsync 是相反方向——那边是库 open 而锁已闭合，这边是库 closed 而锁已释放。
    ///
    /// 判定直接由库状态推得：系统下令开门时会把 door_state 写成 open
    /// （见 app.slot.open / SlotControlService），所以 door_state 仍为 closed
    /// 却读到释锁，就说明这次开门不是系统下的令。
    /// </summary>
    public async Task<IReadOnlyList<(long SlotId, string SlotNo)>> FindUncommandedAsync(
        IReadOnlyDictionary<string, SlotHardwareSnapshot> snapshots,
        CancellationToken ct = default)
    {
        var found = new List<(long, string)>();
        if (!_hardware.IsConfigured || snapshots.Count == 0)
        {
            _uncommanded.SuspendObservation(); // 无 DI 读数，不能判断恢复，也不能再次计为首次
            RecoveredUncommanded = [];     // 没读到 DI 不能据此断言已关门
            return found;
        }

        var slots = await _slots.ListSlotsAsync(new SlotListFilter { EnabledOnly = false }, ct);
        var matched = new List<(long SlotId, string SlotNo)>();
        var unknown = new List<long>();
        foreach (var row in slots)
        {
            if (!snapshots.TryGetValue(row.SlotNo, out var hw) || !hw.ReadOk || hw.LockClosed is null)
            {
                unknown.Add(row.SlotId);
                continue;
            }
            if (IsUncommanded(hw.Wired, hw.ReadOk, hw.LockClosed, row.DoorState))
                matched.Add((row.SlotId, row.SlotNo));
        }

        // 只返回**本轮新出现**的：持续释锁不能每轮都记一条
        var fresh = _uncommanded.FilterNew(matched.Select(m => m.SlotId).ToList(), unknown);
        RecoveredUncommanded = _uncommanded.LastRecovered
            .Select(id => slots.FirstOrDefault(s => s.SlotId == id))
            .Where(s => s is not null
                && snapshots.TryGetValue(s.SlotNo, out var hw)
                && hw.ReadOk && hw.LockClosed == true)
            .Select(s => (s!.SlotId, s.SlotNo))
            .ToList();
        foreach (var slotId in fresh)
            found.Add(matched.First(m => m.SlotId == slotId));

        return found;
    }

    /// <summary>
    /// 纯判定，便于逐项测：已接线、读到锁、锁为释锁，而库内门态仍是 closed。
    /// 未读到（ReadOk=false / LockClosed=null）一律不算——读失败不能当成"计划外开门"，
    /// 否则硬件抖动会刷出一片假记录。
    /// </summary>
    public static bool IsUncommanded(bool wired, bool readOk, bool? lockClosed, string? dbDoorState) =>
        wired
        && readOk
        && lockClosed == false
        && string.Equals(dbDoorState, "closed", StringComparison.OrdinalIgnoreCase);
}
