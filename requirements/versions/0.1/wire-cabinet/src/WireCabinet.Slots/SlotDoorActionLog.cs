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
/// MH 取出旧焊丝 / 维护试开的记录（0.2 需求范围 §3 覆盖矩阵第 5/6/7/9/10 项）。
///
/// 粒度（2026-10-09 口径收敛，按需求方确认）：**一个格口 = 一条记录**，
/// 开门 / 关门 / 按账面清空都是这条上的字段。批量开门不合并——打开 53 格就是 53 条，
/// 因为一次批量在语义上就是"逐格取走"，而"取出一个一定记一条"。
///
/// <list type="bullet">
/// <item>单格 / 批量取旧：一条（<c>mh_take_wire</c> + 格口 + 被取走的批号 + 门开/门关）；</item>
/// <item>维护试开：一条（<c>maint_trial_open</c>），同样带门时刻；</item>
/// <item>开锁失败也留一条（§ 矩阵第 9 项：失败不单独成动作类型，而是同一动作的 result=失败）；</item>
/// <item>无指令开门是异常，仍按「开门一条 + 关门一条」配对保留。</item>
/// </list>
///
/// **为什么放在 Slots 而不是界面层**：界面层是 net8.0-windows，测试工程引用不了，
/// 而这里最容易错的恰好是条数与配对。放在这里就能用真 sink + 真库把条数钉住。
/// </summary>
public sealed class SlotDoorActionLog
{
    /// <summary>MH 取出旧焊丝（打开一格、取走一格）。</summary>
    public const string MhTakeWire = OperationLogActionTypes.MhTakeWire;

    /// <summary>维护试开（留痕）。</summary>
    public const string MaintTrialOpen = OperationLogActionTypes.MaintTrialOpen;

    private readonly IOperationLogSink? _sink;

    public SlotDoorActionLog(IOperationLogSink? sink) => _sink = sink;

    /// <summary>
    /// 写一条"打开一格并取走/试开"的记录。返回是否写入成功。
    ///
    /// <paramref name="openedAtUtc"/> 同时用作记录的 <c>ts</c> 与 <c>door_open_ts</c>——
    /// 关门回填要靠它定位那条记录，所以调用方必须原样传回同一个值。
    /// <paramref name="clearedLotNo"/> 是开门时该格账面已有的批号（空格口传 null）。
    /// </summary>
    public bool RecordOpened(string actionType, string slotNo, DoorOperator op,
        string? openedAtUtc = null, string? clearedLotNo = null)
    {
        if (_sink is null) return false;

        var openTime = openedAtUtc ?? OperationLogTime.UtcNowText();
        var cleared = string.IsNullOrWhiteSpace(clearedLotNo) ? null : clearedLotNo.Trim();

        var entry = NewEntry(actionType, op, slotNo, "成功", OpenedMessage(actionType, slotNo, cleared), null,
            lotNo: cleared, isCleared: cleared is not null)
            with { Ts = openTime, DoorOpenTs = openTime };

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

        var what = string.Equals(actionType, MaintTrialOpen, StringComparison.Ordinal) ? "试开" : "取出";
        return _sink.TryWrite(NewEntry(actionType, op, slotNo, "失败", $"{what}失败", error)).Success;
    }

    /// <summary>
    /// 无指令开门（§ 矩阵第 10 项）：库内没有开门指令，而锁 DI 显示释锁。
    /// 口径收敛后有自己的动作类型（不再复用开门类型）；<c>is_uncommanded = 1</c> 仍保留，
    /// 便于和历史记录（那时复用 mh_open_slot）按标记一起查。非计划开门没有操作人。
    /// </summary>
    public bool RecordUncommandedOpen(string slotNo, string? openedAtUtc = null)
    {
        if (_sink is null) return false;

        var openTime = openedAtUtc ?? OperationLogTime.UtcNowText();
        var entry = NewEntry(
            OperationLogActionTypes.UncommandedOpen, new DoorOperator(""), slotNo, "成功",
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

    /// <summary>
    /// 说明文案按动作类型区分——这条记录的动作是"取出旧焊丝"，不再是中性的"打开格口"。
    /// 空格口也会记一条（系统分不清"取走了"还是"本来就空"，按账面清空统一这么记）。
    /// </summary>
    private static string OpenedMessage(string actionType, string slotNo, string? clearedLot) =>
        string.Equals(actionType, MaintTrialOpen, StringComparison.Ordinal)
            ? $"维护试开格口 {slotNo}"
            : clearedLot is null
                ? $"格口 {slotNo} 取出旧焊丝（当时账面无料）"
                : $"格口 {slotNo} 取出旧焊丝（原批号 {clearedLot}）";

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
