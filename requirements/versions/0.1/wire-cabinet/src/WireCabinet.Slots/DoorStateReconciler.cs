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

    /// <summary>对库 open 且 DI 锁闭合的格口执行 ConfirmDoorClosed。返回成功关库的 slot_id。</summary>
    public async Task<IReadOnlyList<long>> ReconcileAsync(
        IReadOnlyDictionary<string, SlotHardwareSnapshot> snapshots,
        CancellationToken ct = default)
    {
        if (!_hardware.IsConfigured)
            return [];

        var openIds = _slots.OpenSlotIds();
        if (openIds.Count == 0)
            return [];

        var slots = await _slots.ListSlotsAsync(new SlotListFilter { EnabledOnly = false }, ct);
        var closed = new List<long>();

        foreach (var id in openIds)
        {
            var row = slots.FirstOrDefault(s => s.SlotId == id);
            if (row is null || row.DoorState != "open")
                continue;

            if (!snapshots.TryGetValue(row.SlotNo, out var hw)
                || !hw.ReadOk
                || hw.LockClosed != true)
                continue;

            if (await _slots.ConfirmDoorClosedAsync(id, ct))
                closed.Add(id);
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
            _uncommanded.FilterNew([]);   // 读不到就当作都恢复正常，避免下一次误判为「首次」
            return found;
        }

        var slots = await _slots.ListSlotsAsync(new SlotListFilter { EnabledOnly = false }, ct);
        var matched = new List<(long SlotId, string SlotNo)>();
        foreach (var row in slots)
        {
            if (!snapshots.TryGetValue(row.SlotNo, out var hw)) continue;
            if (IsUncommanded(hw.Wired, hw.ReadOk, hw.LockClosed, row.DoorState))
                matched.Add((row.SlotId, row.SlotNo));
        }

        // 只返回**本轮新出现**的：持续释锁不能每轮都记一条
        var fresh = _uncommanded.FilterNew(matched.Select(m => m.SlotId).ToList());
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
