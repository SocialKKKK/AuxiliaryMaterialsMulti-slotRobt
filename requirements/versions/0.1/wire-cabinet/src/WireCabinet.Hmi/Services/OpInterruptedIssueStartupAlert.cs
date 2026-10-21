using System.Text;
using System.Windows;
using WireCabinet.Slots;
using WireCabinet.Slots.Hardware;

using WireCabinet.Data;

namespace WireCabinet.Hmi.Services;

/// <summary>启动时展示 OP 领用中断提醒弹窗。</summary>
public static class OpInterruptedIssueStartupAlert
{
    public static bool TryShow(Window owner)
    {
        App.Bootstrap.FlowSlots.Reload();

        var record = App.Bootstrap.OpInterruptedIssue.TryGet();
        var pending = App.Bootstrap.ListIssuePendingSlots();

        // 老版本只清快照、不锁格口；启动时见到「MES 已提交、未取料」的中断记录，
        // 先补一个领用待取标记，保证这个格口不会再被匹配到。
        if (record is not null && record.SubmitIssueDone && !record.PickupDone)
        {
            EnsureIssueSlotBlocked(record);
            pending = App.Bootstrap.ListIssuePendingSlots();
        }

        if (record is null && pending.Count == 0)
            return false;

        var sb = new StringBuilder();
        if (pending.Count > 0)
        {
            sb.AppendLine("以下格口处于领用未完成/隔离状态，已从可领列表排除：");
            foreach (var slot in pending)
                sb.AppendLine($"· {MhOpenDoorGuard.FormatCabinetSlotLabel(slot.SlotNo)}，批号 {slot.WireLotNo ?? "—"}，状态 {slot.WireState}");
            sb.AppendLine();
            sb.AppendLine("请到物料员 MH 页选中对应格口，打开核对/取出实物并关门；清空后隔离自动解除。");
            sb.AppendLine("清除「上次领用未完成」快照不会清空格口，也不会解除隔离。");
            sb.AppendLine();
        }

        if (record is not null)
        {
            var doorStillOpen = IsSlotDoorOpen(record, out _);
            var slotLabel = MhOpenDoorGuard.FormatCabinetSlotLabel(record.SlotNo);
            sb.AppendLine(BuildMessage(record, slotLabel, doorStillOpen));
        }

        MessageBox.Show(owner, sb.ToString().TrimEnd(), "上次领用未完成", MessageBoxButton.OK, MessageBoxImage.Warning);

        // MES 未提交，或已确认取料完成，才自动清快照；
        // 「已提交但未取料」必须保留，直到人工在 MES 对账页清除或 MH 清空后人工确认。
        if (record is not null && (record.PickupDone || !record.SubmitIssueDone))
            App.Bootstrap.OpInterruptedIssue.Clear();

        return true;
    }

    /// <summary>给中断记录对应的格口补「领用待取」标记；失败不阻断启动。</summary>
    private static void EnsureIssueSlotBlocked(OpInterruptedIssue record)
    {
        foreach (var pending in App.Bootstrap.ListIssuePendingSlots())
        {
            if (pending.SlotId == record.SlotId)
                return;
        }

        // 快照可能是旧的：格口已被清空并重新存了别的批号。只有当前格口仍绑着
        // 快照里的那盘料时才补标记，避免把新存的料误锁。
        var slot = App.Bootstrap.FlowSlots.Slots.FirstOrDefault(s => s.SlotId == record.SlotId);
        if (slot is null
            || !string.Equals(slot.BizState, "available_wire", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(slot.WireLotNo?.Trim(), record.WireLotNo.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        var item = App.Bootstrap.Catalog.Find("app.slot.mark_issue_state");
        if (item is null)
            return;

        App.Bootstrap.AppDbGateway.Run(item, new Dictionary<string, object?>
        {
            ["slot_id"] = record.SlotId,
            ["wire_state"] = "领用待取"
        });
    }

    private static bool IsSlotDoorOpen(OpInterruptedIssue record, out SlotDoorState? slot)
    {
        slot = App.Bootstrap.FlowSlots.Slots.FirstOrDefault(s => s.SlotId == record.SlotId);
        var hwConfigured = App.Bootstrap.Hardware.IsConfigured;
        App.SlotHardwarePoll.Snapshots.TryGetValue(record.SlotNo, out var hw);
        return slot is not null && SlotDoorDisplay.IsDisplayOpen(slot, hw, hwConfigured);
    }

    private static string BuildMessage(OpInterruptedIssue record, string slotLabel, bool doorStillOpen)
    {
        var sb = new StringBuilder();
        sb.AppendLine("上次领用焊丝时程序异常关闭，领用流程未完成。");
        sb.AppendLine();
        sb.AppendLine($"格口：{slotLabel}");
        sb.AppendLine($"批号：{record.WireLotNo}");
        sb.AppendLine($"MES 领用提交：{(record.SubmitIssueDone ? "已完成" : "未完成")}");
        sb.AppendLine($"本地清库：{(record.PickupDone ? "已完成" : "未完成")}");
        sb.AppendLine($"MES DISCOEQPNO 清除：{(record.MesDiscoDone ? "已完成" : "未完成")}");
        sb.AppendLine();

        if (doorStillOpen)
        {
            sb.AppendLine("该格口目前仍未关闭。");
            sb.AppendLine("请确认是否已取丝，关好格口后重新走 OP 领用流程。");
            sb.AppendLine("程序不会自动完成清库或 MES 清除。");
        }
        else
        {
            sb.AppendLine("该格口已关闭，但系统未记录领用完成。");
            sb.AppendLine("请到物料员 MH 页打开该格口核对/取出实物并关门清空；清空后隔离自动解除。");
            if (record.SubmitIssueDone && !record.PickupDone)
                sb.AppendLine("注意：MES 领用已提交但本地未清库；重走 OP 可能冲突，不要直接再领用。");
        }

        return sb.ToString().TrimEnd();
    }
}
