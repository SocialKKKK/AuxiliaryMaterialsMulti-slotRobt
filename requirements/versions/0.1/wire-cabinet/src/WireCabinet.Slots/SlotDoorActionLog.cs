using WireCabinet.Data;

namespace WireCabinet.Slots;

/// <summary>哪个角色、以什么身份做了这次开门（0.2 §3.3 的操作人字段）。</summary>
public sealed record DoorOperator(string Role, string? Id = null, string? Name = null, string? Team = null)
{
    public static DoorOperator Mh(string? id = null, string? name = null, string? team = null) => new("MH", id, name, team);

    public static DoorOperator Maint() => new("MAINT");
}

/// <summary>
/// 格口开/关门动作的记录（0.2 需求范围 §3 覆盖矩阵第 5/6/7 项、第 9 项）。
///
/// **为什么放在 Slots 而不是界面层**：界面层是 net8.0-windows，测试工程引用不了，
/// 而这里最容易错的恰好是「批量按格口逐个计条」——条数必须与实际开门数一致（验收 §3 第 6 项）。
/// 放在这里就能用真 sink + 真库把条数钉住。
///
/// 粒度按 §3.2「一次业务操作一条」：单格开门一条；批量开门按被打开的格口逐个一条；
/// 开锁失败的格口也留一条（§ 矩阵第 9 项：失败不单独成动作类型，而是同一动作的 result=失败）。
/// </summary>
public sealed class SlotDoorActionLog
{
    /// <summary>MH 打开单个格口。</summary>
    public const string MhOpenSlot = OperationLogActionTypes.MhOpenSlot;

    /// <summary>MH 批量打开格口（每个格口一条）。</summary>
    public const string MhOpenSlotsBatch = OperationLogActionTypes.MhOpenSlotsBatch;

    /// <summary>维护试开（留痕）。</summary>
    public const string MaintTrialOpen = OperationLogActionTypes.MaintTrialOpen;

    private readonly IOperationLogSink? _sink;

    public SlotDoorActionLog(IOperationLogSink? sink) => _sink = sink;

    /// <summary>
    /// 为每个**成功打开**的格口各写一条，返回写入成功的条数。
    /// 记录写入失败不影响开门结果（§1.4 第 1 条），故只吞掉失败并计数。
    /// </summary>
    public int RecordOpened(string actionType, IEnumerable<(long SlotId, string SlotNo)> openedSlots,
        DoorOperator op, string? openedAtUtc = null)
    {
        if (_sink is null) return 0;

        var written = 0;
        foreach (var (_, slotNo) in openedSlots)
        {
            var openTime = openedAtUtc ?? OperationLogTime.UtcNowText();
            var entry = NewEntry(actionType, op, slotNo, "成功", $"已打开 {slotNo}（仅门/锁状态）", null)
                with { Ts = openTime, DoorOpenTs = openTime };
            if (_sink.TryWrite(entry).Success)
                written++;
        }
        return written;
    }

    /// <summary>关门确认另记一条，关联同格的开门时刻；历史开门记录保持只增不改。</summary>
    public bool RecordClosed(string slotNo, DoorOperator op, string? openedAtUtc = null,
        bool isUncommanded = false)
    {
        if (_sink is null) return false;

        var closeTime = OperationLogTime.UtcNowText();
        var entry = NewEntry(OperationLogActionTypes.DoorClose, op, slotNo, "成功",
            $"格口 {slotNo} 已确认关门", null, isUncommanded: isUncommanded)
            with { Ts = closeTime, DoorOpenTs = openedAtUtc, DoorCloseTs = closeTime };
        return _sink.TryWrite(entry).Success;
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
    /// 开门即清空（§ 矩阵第 11 项）：格口无内部传感器，MH 开门关门后按账面无料处理。
    /// <c>is_cleared = 1</c>，并带上被清掉的焊丝批号（有的话）以便追溯。
    /// </summary>
    public bool RecordCleared(string slotNo, string? clearedLotNo, DoorOperator op)
    {
        if (_sink is null) return false;

        var message = string.IsNullOrWhiteSpace(clearedLotNo)
            ? $"格口 {slotNo} 按账面无料清空"
            : $"格口 {slotNo} 按账面无料清空（原批号 {clearedLotNo}）";

        var entry = NewEntry(
            OperationLogActionTypes.MhAssumeEmpty, op, slotNo, "成功", message, null,
            lotNo: clearedLotNo, isCleared: true);

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
