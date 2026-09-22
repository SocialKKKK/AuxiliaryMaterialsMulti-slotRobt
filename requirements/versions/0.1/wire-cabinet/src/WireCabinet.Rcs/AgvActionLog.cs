using WireCabinet.Data;

namespace WireCabinet.Rcs;

/// <summary>
/// 控车与回充的记录（0.2 需求范围 §3.1 第 8 项：下单 / 暂停 / 继续 / 取消 / 回充 / 返航）。
///
/// 按 §3.1 设计说明 2，这些动作与存取动作**写同一张记录表**，用
/// <c>agv_move</c> / <c>agv_charge</c> 区分，具体动作写在 message 里；
/// 既有的 agv_move_log 仍保留作控车过程明细，两者不冲突。
///
/// **最关键的一条（验收 C5）**：记录**只在状态迁移时**产生，不能每轮询 tick 记一条，
/// 否则静置 5 分钟就会刷出一片记录。轮询侧 AgvControlLoop 已经只在真的执行了动作时
/// 才返回 ExecutedAction（暂停/继续还各带边沿守卫），所以这里只要做到
/// 「ExecutedAction 为空或未登记 → 什么都不记」即可；Describe 就是这条守则的落点，
/// 也是为什么它和对它的测试如此重要。
/// </summary>
public sealed class AgvActionLog
{
    /// <summary>控车：下单 / 暂停 / 继续 / 取消 / 返航。</summary>
    public const string AgvMove = OperationLogActionTypes.AgvMove;

    /// <summary>自动回充。</summary>
    public const string AgvCharge = OperationLogActionTypes.AgvCharge;

    private readonly IOperationLogSink? _sink;

    public AgvActionLog(IOperationLogSink? sink) => _sink = sink;

    /// <summary>
    /// 控车循环执行的动作 → 记录内容；**未执行任何动作（静置）或未登记的动作返回 null**。
    /// 这个 null 就是「不按 tick 记录」的实现。
    /// </summary>
    public static (string ActionType, string Message)? Describe(string? executedAction) => executedAction switch
    {
        "CreateMoveOrder" => (AgvMove, "下达移动单"),
        "PauseMovement" => (AgvMove, "检测到格口打开，任务已暂停"),
        "ContinueMovement" => (AgvMove, "格口已关闭，任务已继续"),
        "CancelOrder" => (AgvMove, "订单被取消"),
        _ => null,
    };

    /// <summary>
    /// 记录控车循环本轮执行的动作。静置（<paramref name="executedAction"/> 为 null 或未登记）
    /// 时**不记**，返回 false。detail 用于补充目的地 / 订单号等信息。
    /// </summary>
    public bool RecordTickAction(string? executedAction, string? detail = null)
    {
        if (_sink is null) return false;
        if (Describe(executedAction) is not { } described) return false;

        return _sink.TryWrite(new OperationLogEntry
        {
            ActionType = described.ActionType,
            Result = "成功",
            Message = Join(described.Message, detail),
        }).Success;
    }

    /// <summary>记录用户下达的指令（下单 / 取消 / 回充 / 返航）；成功与失败都留一条。</summary>
    public bool RecordCommand(string actionType, string message, bool ok, string? error = null, string? detail = null)
    {
        if (_sink is null) return false;

        return _sink.TryWrite(new OperationLogEntry
        {
            ActionType = actionType,
            Result = ok ? "成功" : "失败",
            Message = Join(message, detail),
            ErrorInfo = ok ? null : error,
        }).Success;
    }

    private static string Join(string message, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? message : $"{message}（{detail}）";
}
