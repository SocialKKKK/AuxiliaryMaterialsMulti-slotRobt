namespace WireCabinet.Slots;

/// <summary>
/// 「开门那一刻格子里是哪批料」的判定 —— 也就是这次开门取走的批号（记录口径 2026-10-09）。
/// </summary>
public static class SlotInventoryLot
{
    /// <summary>
    /// 从 <c>app.slot.get_inventory</c> 的结果行里取账面批号。
    ///
    /// **只看有没有批号，不看 <c>biz_state</c>**：只要账面记着批号，格子里就有料，
    /// 开门 + 关门就等于取走。这里曾经只认 <c>available_wire</c>，于是
    /// MH 点「打开所有归还焊丝格口」取走 <c>returned_wire</c> 时批号漏记 ——
    /// 而 <c>app.slot.assume_empty</c> 是不分状态一律清账面的，结果是
    /// **账面上的批号被抹掉、记录里又没有**，那批料在追溯链上直接消失（2026-10-09 现场发现）。
    /// </summary>
    public static string? Of(IReadOnlyDictionary<string, object?>? inventoryRow)
    {
        var lot = inventoryRow?.GetValueOrDefault("wire_lot_no")?.ToString()?.Trim();
        return string.IsNullOrEmpty(lot) ? null : lot;
    }
}
