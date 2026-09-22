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
    public int RecordOpened(string actionType, IEnumerable<(long SlotId, string SlotNo)> openedSlots, DoorOperator op)
    {
        if (_sink is null) return 0;

        var written = 0;
        foreach (var (_, slotNo) in openedSlots)
        {
            var entry = NewEntry(actionType, op, slotNo, "成功", $"已打开 {slotNo}（仅门/锁状态）", null);
            if (_sink.TryWrite(entry).Success)
                written++;
        }
        return written;
    }

    /// <summary>开锁失败的格口也留一条，带原因（§ 矩阵第 9 项）。</summary>
    public bool RecordFailed(string actionType, string slotNo, string error, DoorOperator op)
    {
        if (_sink is null) return false;

        return _sink.TryWrite(NewEntry(actionType, op, slotNo, "失败", "开门失败", error)).Success;
    }

    private static OperationLogEntry NewEntry(
        string actionType, DoorOperator op, string? slotNo, string result, string message, string? error) => new()
    {
        ActionType = actionType,
        SlotNo = slotNo,
        Result = result,
        Message = message,
        ErrorInfo = error,
        OperatorRole = op.Role,
        OperatorId = op.Id,
        OperatorName = op.Name,
        OperatorTeam = op.Team,
    };
}
