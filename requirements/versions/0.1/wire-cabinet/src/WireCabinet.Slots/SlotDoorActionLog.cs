using System.Text.Json;
using WireCabinet.Data;

namespace WireCabinet.Slots;

/// <summary>哪个角色、以什么身份做了这次开门（0.2 §3.3 的操作人字段）。</summary>
public sealed record DoorOperator(string Role, string? Id = null, string? Name = null, string? Team = null)
{
    public static DoorOperator Mh(string? id = null, string? name = null, string? team = null) => new("MH", id, name, team);

    public static DoorOperator Maint() => new("MAINT");
}

/// <summary>
/// 格口开/关门动作的记录（0.2 需求范围 §3 覆盖矩阵第 5/6/7/9/10/11 项）。
///
/// 粒度（记录口径优化 2026-10-09 §2）：**一次开门 = 一条记录**，关门时刻是这条上的字段。
/// <list type="bullet">
/// <item>单格开门 / 维护试开：一条（<c>action_type</c> + <c>slot_no</c> + <c>door_open_ts</c>），
/// 关门确认后回填 <c>door_close_ts</c>；</item>
/// <item>批量开门：一条（<c>slot_count</c> + <c>detail.slots</c>），同样回填 <c>door_close_ts</c>；</item>
/// <item>「开门即清空」降级为字段（<c>is_cleared</c> + <c>detail.cleared_lots</c>），不再单独成条；</item>
/// <item>无指令开门是异常，仍按「开门一条 + 关门一条」配对保留（草案 §3.4）。</item>
/// </list>
/// 开锁失败的格口也留一条（§ 矩阵第 9 项：失败不单独成动作类型，而是同一动作的 result=失败）。
///
/// **为什么放在 Slots 而不是界面层**：界面层是 net8.0-windows，测试工程引用不了，
/// 而这里最容易错的恰好是条数与配对。放在这里就能用真 sink + 真库把条数钉住。
/// </summary>
public sealed class SlotDoorActionLog
{
    /// <summary>MH 打开单个格口。</summary>
    public const string MhOpenSlot = OperationLogActionTypes.MhOpenSlot;

    /// <summary>MH 批量打开格口（一次批量一条）。</summary>
    public const string MhOpenSlotsBatch = OperationLogActionTypes.MhOpenSlotsBatch;

    /// <summary>维护试开（留痕）。</summary>
    public const string MaintTrialOpen = OperationLogActionTypes.MaintTrialOpen;

    private readonly IOperationLogSink? _sink;

    public SlotDoorActionLog(IOperationLogSink? sink) => _sink = sink;

    /// <summary>
    /// 写一条单格开门记录（MH 单格 / 维护试开）。返回是否写入成功。
    ///
    /// <paramref name="openedAtUtc"/> 同时用作记录的 <c>ts</c> 与 <c>door_open_ts</c>——
    /// 关门回填要靠它定位那条记录，所以调用方必须原样传回同一个值。
    /// <paramref name="clearedLotNo"/> 是开门时该格账面已有的批号（有则记 <c>is_cleared</c>）。
    /// </summary>
    public bool RecordOpened(string actionType, string slotNo, DoorOperator op,
        string? openedAtUtc = null, string? clearedLotNo = null)
    {
        if (_sink is null) return false;

        var openTime = openedAtUtc ?? OperationLogTime.UtcNowText();
        var cleared = string.IsNullOrWhiteSpace(clearedLotNo) ? null : clearedLotNo.Trim();
        var message = cleared is null
            ? $"已打开 {slotNo}（仅门/锁状态）"
            : $"已打开 {slotNo} 并按账面清空（原批号 {cleared}）";

        var entry = NewEntry(actionType, op, slotNo, "成功", message, null,
            lotNo: cleared, isCleared: cleared is not null)
            with
        {
            Ts = openTime,
            DoorOpenTs = openTime,
            Detail = cleared is null ? null : JsonSerializer.Serialize(new { cleared_lots = new[] { cleared } }),
        };
        return _sink.TryWrite(entry).Success;
    }

    /// <summary>
    /// 写一条批量开门记录（一次批量一条，口径优化 §2）。格口清单与数量挂在 detail / slot_count 上。
    /// <paramref name="clearedLots"/> 是开门时这些格里账面有料的批号。
    /// </summary>
    public bool RecordBatchOpened(string actionType, IReadOnlyList<(long SlotId, string SlotNo)> slots,
        DoorOperator op, string? openedAtUtc = null, IReadOnlyList<string>? clearedLots = null)
    {
        if (_sink is null || slots.Count == 0) return false;

        var openTime = openedAtUtc ?? OperationLogTime.UtcNowText();
        var slotNos = slots.Select(s => s.SlotNo).ToList();
        var lots = clearedLots?.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).Distinct().ToList() ?? [];
        var message = $"批量打开 {slotNos.Count} 个格口（仅门/锁状态）"
            + (lots.Count > 0 ? $"，其中 {lots.Count} 格有料按账面清空" : "");

        var entry = NewEntry(actionType, op, slotNos.Count == 1 ? slotNos[0] : null, "成功", message, null,
            isCleared: lots.Count > 0)
            with
        {
            Ts = openTime,
            DoorOpenTs = openTime,
            SlotCount = slotNos.Count,
            Detail = JsonSerializer.Serialize(new { slots = slotNos, cleared_lots = lots }),
        };
        return _sink.TryWrite(entry).Success;
    }

    /// <summary>
    /// 关门确认：把 <c>door_close_ts</c> 补进那次开门的那条记录（只补 NULL，口径优化 §4 A 档）。
    /// 定位用 (actionType, openedAtUtc)——开门记录的 ts 就是它。
    /// </summary>
    public bool RecordClosed(string actionType, string openedAtUtc, string? closedAtUtc = null)
    {
        if (_sink is null || string.IsNullOrWhiteSpace(openedAtUtc)) return false;

        var closeTime = closedAtUtc ?? OperationLogTime.UtcNowText();
        return _sink.BackfillDoorTs(actionType, openedAtUtc, openedAtUtc, closeTime).Success;
    }

    /// <summary>开锁失败的格口也留一条，带原因（§ 矩阵第 9 项）。</summary>
    public bool RecordFailed(string actionType, string slotNo, string error, DoorOperator op)
    {
        if (_sink is null) return false;

        return _sink.TryWrite(NewEntry(actionType, op, slotNo, "失败", "开门失败", error)).Success;
    }

    /// <summary>
    /// 无指令开门（§ 矩阵第 10 项）：库内没有开门指令，而锁 DI 显示释锁。
    ///
    /// 按 §3.1 设计说明「第 9、10 项并入对应的开门记录…不新增动作类型」，
    /// 这里复用单格开门的类型并置 <c>is_uncommanded = 1</c>；
    /// 既然是非计划开门，没有操作人，故只留空角色与说明文案。
    /// </summary>
    public bool RecordUncommandedOpen(string slotNo, string? openedAtUtc = null)
    {
        if (_sink is null) return false;

        var openTime = openedAtUtc ?? OperationLogTime.UtcNowText();
        var entry = NewEntry(
            MhOpenSlot, new DoorOperator(""), slotNo, "成功",
            "库内无开门指令但锁 DI 显示释锁（计划外开门）", null, isUncommanded: true)
            with { Ts = openTime, DoorOpenTs = openTime };

        return _sink.TryWrite(entry).Success;
    }

    /// <summary>
    /// 无指令开门对应的关门（异常，仍独立成条并与开门配对——草案 §3.4）。
    /// 注意与正常关门不同：这里不并入开门记录，而是另写一条带 <c>is_uncommanded</c> 的关门。
    /// </summary>
    public bool RecordUncommandedClosed(string slotNo, string? openedAtUtc, string? closedAtUtc = null)
    {
        if (_sink is null) return false;

        var closeTime = closedAtUtc ?? OperationLogTime.UtcNowText();
        var entry = NewEntry(OperationLogActionTypes.DoorClose, new DoorOperator(""), slotNo, "成功",
            $"格口 {slotNo} 已确认关门", null, isUncommanded: true)
            with { Ts = closeTime, DoorOpenTs = openedAtUtc, DoorCloseTs = closeTime };

        return _sink.TryWrite(entry).Success;
    }

    private static OperationLogEntry NewEntry(
        string actionType, DoorOperator op, string? slotNo, string result, string message, string? error,
        string? lotNo = null, bool isCleared = false, bool isUncommanded = false) => new()
    {
        ActionType = actionType,
        SlotNo = slotNo,
        LotNo = lotNo,
        Result = result,
        Message = message,
        ErrorInfo = error,
        IsCleared = isCleared,
        IsUncommanded = isUncommanded,
        OperatorRole = op.Role,
        OperatorId = op.Id,
        OperatorName = op.Name,
        OperatorTeam = op.Team,
    };
}
