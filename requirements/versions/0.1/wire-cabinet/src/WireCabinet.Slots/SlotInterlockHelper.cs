using WireCabinet.Slots.Hardware;

namespace WireCabinet.Slots;

/// <summary>格口联锁判定；没有已知开门不等于已确认全关。</summary>
public static class SlotInterlockHelper
{
    public static bool IsSlotOpenForInterlock(
        SlotStatusDto slot,
        IReadOnlyDictionary<string, SlotHardwareSnapshot> snapshots,
        bool hardwareConfigured)
    {
        if (slot.DoorState == "open")
            return true;

        return hardwareConfigured
               && snapshots.TryGetValue(slot.SlotNo, out var hw)
               && hw is { ReadOk: true, LockClosed: false };
    }

    public static bool IsSlotClosedForInterlock(
        SlotStatusDto slot,
        IReadOnlyDictionary<string, SlotHardwareSnapshot> snapshots,
        bool hardwareConfigured)
    {
        if (slot.DoorState != "closed")
            return false;

        // 未接线的空位不要求 DI；已禁用但仍接线的格口也必须确认锁闭。
        if (!hardwareConfigured)
            return true;
        snapshots.TryGetValue(slot.SlotNo, out var hw);
        return !slot.IoWired && hw?.Wired != true
               || hw is { Wired: true, ReadOk: true, LockClosed: true };
    }
}
