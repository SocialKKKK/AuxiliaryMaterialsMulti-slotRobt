using System.Text.Json;
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

    /// <summary>
    /// 自动路径（自动返航 / 自动回充 / 轮询）**同一原因连续失败**合并成一条（口径优化 §2 口径③）：
    /// 第一次失败立即落库（<c>detail.count = 1</c>），此后同一原因再失败只把次数与末次时刻
    /// 累加到那一条上（<c>detail</c> 是过程派生字段，库层触发器只放行这一列），不再各写一行。
    /// 原因变化或成功即结束本轮合并——见 <see cref="EndAutoFailureStreak"/>。
    ///
    /// 人工操作失败不走这里：那些要逐条留痕（<see cref="RecordCommand"/>）。
    /// </summary>
    public bool RecordAutoFailure(string actionType, string message, string error)
    {
        if (_sink is null) return false;

        lock (_gate)
        {
            var now = OperationLogTime.UtcNowText();

            if (_autoFailure is { } open
                && string.Equals(open.ActionType, actionType, StringComparison.Ordinal)
                && string.Equals(open.Message, message, StringComparison.Ordinal)
                && string.Equals(open.Error, error, StringComparison.Ordinal))
            {
                open.Count++;
                open.LastTs = now;
                if (_sink.UpdateFailureDetail(actionType, open.Ts, DetailJson(open.Count, open.FirstTs, open.LastTs)).Success)
                    return true;

                // 累加失败（例如那条记录还在待补偿队列里、还没落库）：放弃合并，另写一条，
                // 至少不把这次失败丢掉。
                _autoFailure = null;
            }
            else
            {
                _autoFailure = null;
            }

            var written = _sink.TryWrite(new OperationLogEntry
            {
                Ts = now,
                ActionType = actionType,
                Result = "失败",
                Message = message,
                ErrorInfo = error,
                Detail = DetailJson(1, now, now),
            }).Success;

            if (written)
                _autoFailure = new AutoFailureStreak
                {
                    ActionType = actionType,
                    Message = message,
                    Error = error,
                    Ts = now,
                    FirstTs = now,
                    LastTs = now,
                };

            return written;
        }
    }

    /// <summary>本轮合并窗口结束（原因变化、成功、或该自动路径不再运行）。</summary>
    public void EndAutoFailureStreak()
    {
        lock (_gate) _autoFailure = null;
    }

    private static string DetailJson(int count, string firstTs, string lastTs) =>
        JsonSerializer.Serialize(new { count, first_ts = firstTs, last_ts = lastTs });

    private sealed class AutoFailureStreak
    {
        public required string ActionType { get; init; }
        public required string Message { get; init; }
        public required string Error { get; init; }

        /// <summary>那条记录落库时用的 ts——累加时靠它定位。</summary>
        public required string Ts { get; init; }
        public required string FirstTs { get; init; }
        public string LastTs { get; set; } = "";
        public int Count { get; set; } = 1;
    }

    private readonly object _gate = new();
    private AutoFailureStreak? _autoFailure;
}
