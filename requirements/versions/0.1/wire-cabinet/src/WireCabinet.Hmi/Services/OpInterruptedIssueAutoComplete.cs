using WireCabinet.Flows;
using WireCabinet.Slots;
using WireCabinet.Slots.Hardware;

namespace WireCabinet.Hmi.Services;

/// <summary>
/// 中断领用的自动收尾（2026-10-22 R-C01）：程序在「MES 已提交、关门确认之前」中断时，
/// 不再要求物料员专门去开那个格口——门已关且超过宽限期，就按已取料自动清库。
/// 判据见 <see cref="OpIssueAutoCompletePolicy"/>。
/// </summary>
public static class OpInterruptedIssueAutoComplete
{
    public static IReadOnlyList<string> TryRun(TimeSpan? grace = null)
    {
        var completed = new List<string>();

        // 流程正在跑（或有人正在开门）时不要插手，让正常流程自己收尾。
        if (App.Flows.IsFlowActive || App.DoorOps.IsBusy)
            return completed;

        var now = DateTime.UtcNow;
        var effectiveGrace = grace ?? OpIssueAutoCompletePolicy.DefaultGrace;
        var hardwareConfigured = App.Bootstrap.Hardware.IsConfigured;

        foreach (var pending in App.Bootstrap.ListIssuePendingSlots())
        {
            var slot = App.Bootstrap.FlowSlots.Slots.FirstOrDefault(s => s.SlotId == pending.SlotId);
            if (slot is null)
                continue;

            App.SlotHardwarePoll.Snapshots.TryGetValue(slot.SlotNo, out var hw);
            var doorOpen = SlotDoorDisplay.IsDisplayOpen(slot, hw, hardwareConfigured);

            if (!OpIssueAutoCompletePolicy.ShouldAutoComplete(
                    pending.WireState, pending.UpdatedAt, doorOpen, now, effectiveGrace))
                continue;

            if (App.Bootstrap.CompleteInterruptedIssuePickup(pending, now))
                completed.Add(pending.SlotNo);
        }

        if (completed.Count > 0)
            App.Bootstrap.FlowSlots.Reload();

        return completed;
    }
}
