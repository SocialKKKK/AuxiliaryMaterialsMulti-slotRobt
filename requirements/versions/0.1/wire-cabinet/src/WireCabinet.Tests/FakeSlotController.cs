using WireCabinet.Slots;

namespace WireCabinet.Tests;

/// <summary>
/// 测试用格口控制器：只记账，不做真实硬件动作。
///
/// 注意 <see cref="CloseNextOpenDoor"/> **必须**把该格口从「已打开」集合中移除。
/// 「开门 → 逐个关门直到全关」类流程靠 are_all_doors_closed 判定收敛，
/// 若这一步不移除，引擎会一直读到「还有门开着」而无限循环。
/// </summary>
internal sealed class FakeSlotController : ISlotController
{
    private readonly HashSet<long> _open = new();

    /// <summary>
    /// 复现现场前提：门在流程推进到「关门」节点**之前**就被现场/硬件关掉了
    /// （真实实现里 <c>IsOpen</c> 取自 <c>app_slot.door_state</c>，此时已是 closed，
    /// 于是 <see cref="CloseNextOpenDoor"/> 返回 null）。开门本身仍成功，
    /// 所以流程照样往下走——差别只在关门节点拿不到格口。
    /// </summary>
    public bool DoorClosesItselfAfterOpen { get; set; }

    // 显式空 add/remove：测试不关心 Changed，也避免未使用事件的编译告警。
    public event EventHandler? Changed { add { } remove { } }

    public IReadOnlyList<SlotDoorState> Slots => Array.Empty<SlotDoorState>();

    public void Reload() { }

    public Task<bool> OpenDoorAsync(long slotId, CancellationToken ct = default)
    {
        if (!DoorClosesItselfAfterOpen)
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

    public long? CloseNextOpenDoor()
    {
        if (_open.Count == 0) return null;

        var slotId = _open.Min();
        _open.Remove(slotId);
        return slotId;
    }

    public bool AreAllDoorsClosed() => _open.Count == 0;

    public IReadOnlyList<long> OpenDoorIds() => _open.OrderBy(x => x).ToList();
}
