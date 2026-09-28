namespace WireCabinet.Data;

/// <summary>
/// 动作类型的显示名（0.2 需求范围 §3.1 记录范围）。
///
/// 库里的 action_type 是稳定标识（英文码），界面列表与导出都显示中文——
/// 集中在这里是为了两处不会各写一份而对不上。
/// 未登记的类型原样返回标识，便于发现遗漏。
/// </summary>
public static class OperationLogActionTypes
{
    public const string MhLoadAvailableWire = "mh_load_available_wire";
    public const string OpReturnWire = "op_return_wire";
    public const string OpIssueAvailableWire = "op_issue_available_wire";
    public const string OpTakeWire = "op_take_wire";

    /// <summary>存取流程中的格口开门事件；业务动作仍单独记录。</summary>
    public const string DoorOpen = "door_open";

    /// <summary>MH 打开单个格口（§3 矩阵第 5 项）。</summary>
    public const string MhOpenSlot = "mh_open_slot";

    /// <summary>MH 批量打开格口（第 6 项）：**每个被打开的格口各记一条**。</summary>
    public const string MhOpenSlotsBatch = "mh_open_slots_batch";

    /// <summary>维护人员试开（第 7 项，留痕）。</summary>
    public const string MaintTrialOpen = "maint_trial_open";

    /// <summary>已打开的格口确认关门；与开门各留一条不可修改的事件。</summary>
    public const string DoorClose = "door_close";

    /// <summary>
    /// 开门即清空（第 11 项）：格口无内部传感器，MH 开门关门后按账面无料处理。
    /// 单独成类型而不是给开门记录补标记——记录只增不改，开门时还不知道会不会被清空。
    /// </summary>
    public const string MhAssumeEmpty = "mh_assume_empty";

    /// <summary>AGV 控车（第 8 项）：下单 / 暂停 / 继续 / 取消 / 返航，具体动作写在 message 里。</summary>
    public const string AgvMove = "agv_move";

    /// <summary>AGV 自动回充（第 8 项）。</summary>
    public const string AgvCharge = "agv_charge";

    /// <summary>查看记录页本身也留痕（§3.7）。</summary>
    public const string ViewLog = "view_operation_log";

    /// <summary>导出记录也留痕（§3.5、§3.7）。</summary>
    public const string ExportLog = "export_operation_log";

    /// <summary>
    /// 已登记的动作类型，**按录入顺序**（界面下拉项的顺序要稳定，故不用 Dictionary 的枚举顺序）。
    /// </summary>
    private static readonly (string Code, string Label)[] Known =
    [
        (MhLoadAvailableWire, "MH 存入可用焊丝"),
        (OpReturnWire, "OP 归还旧焊丝"),
        (OpIssueAvailableWire, "OP 领用新焊丝"),
        (OpTakeWire, "OP 取出焊丝"),
        (DoorOpen, "格口开门"),
        (MhOpenSlot, "MH 打开单个格口"),
        (MhOpenSlotsBatch, "MH 批量打开格口"),
        (MaintTrialOpen, "维护试开格口"),
        (DoorClose, "格口关门"),
        (MhAssumeEmpty, "开门即清空"),
        (AgvMove, "AGV 控车"),
        (AgvCharge, "AGV 自动回充"),
        (ViewLog, "查看存取记录"),
        (ExportLog, "导出存取记录"),
    ];

    private static readonly Dictionary<string, string> Labels =
        Known.ToDictionary(x => x.Code, x => x.Label, StringComparer.OrdinalIgnoreCase);

    /// <summary>界面筛选下拉用（不含「全部」，由调用方补）。</summary>
    public static IReadOnlyList<(string Code, string Label)> All() => Known;

    public static string Label(string? actionType) =>
        string.IsNullOrWhiteSpace(actionType) ? ""
        : Labels.TryGetValue(actionType.Trim(), out var label) ? label
        : actionType.Trim();
}
