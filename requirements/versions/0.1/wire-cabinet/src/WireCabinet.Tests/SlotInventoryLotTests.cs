using WireCabinet.Slots;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 「开门取走的是哪批料」的判定（2026-10-09 现场发现）。
///
/// 现场形态：MH 点「打开所有归还焊丝格口」，把归还焊丝取走，记录里批号却是空的。
/// 根因是判定时只认 <c>biz_state = available_wire</c>，而归还焊丝格口的状态是
/// <c>returned_wire</c>；偏偏 <c>app.slot.assume_empty</c> 不分状态一律清账面，
/// 于是**账面批号被抹掉、记录里又没有**，那批料在追溯链上直接消失。
/// </summary>
public class SlotInventoryLotTests
{
    private static Dictionary<string, object?> Row(string? bizState, string? lot) => new()
    {
        ["biz_state"] = bizState,
        ["wire_lot_no"] = lot,
    };

    [Theory]
    [InlineData("available_wire")]
    [InlineData("returned_wire")]   // ← 修复前这一档取不到批号
    [InlineData("whatever_new_state")]
    public void Of_TakesTheLotWheneverTheLedgerHasOne(string bizState)
    {
        Assert.Equal("MC268142-02-0111", SlotInventoryLot.Of(Row(bizState, "MC268142-02-0111")));
    }

    [Theory]
    [InlineData("idle", null)]
    [InlineData("idle", "")]
    [InlineData("idle", "   ")]
    public void Of_NoLot_ReturnsNull(string bizState, string? lot)
    {
        Assert.Null(SlotInventoryLot.Of(Row(bizState, lot)));
    }

    [Fact]
    public void Of_NoRow_ReturnsNull()
    {
        Assert.Null(SlotInventoryLot.Of(null));
    }
}
