namespace WireCabinet.Data;

/// <summary>
/// 动作类型的显示名（0.2 需求范围 §3.1 记录范围）。
///
/// 库里的 action_type 是稳定标识（英文码），界面列表与导出都显示中文——
/// 集中在这里是为了两处不会各写一份而对不上。
/// 未登记的类型原样返回标识，便于发现遗漏。
///
/// **口径收敛（2026-10-09，按需求方确认）**：这个功能是"存取记录"，所以新写入只用
/// 2×2 的存取动作 + 维护试开 + 无指令开门：
/// <list type="bullet">
/// <item>MH 存入焊丝 / MH 取出旧焊丝；</item>
/// <item>OP 归还旧焊丝 / OP 领用新焊丝（归还＝存放，领用＝取出）；</item>
/// <item>维护试开格口（MAINT，留痕）；无指令开门（异常）。</item>
/// </list>
/// 规则只有一条：**取出一个一定记一条，存放一个一定记一条**，不看账面有没有料
/// （柜子没有传感器，"取走"只能按"开门 + 关门"推导）。
///
/// 控车 / 回充 / 查看与导出留痕仍在同一张表里（§3.1 设计说明 2、§3.7），但它们是
/// 设备动作与合规留痕，不属于上面那 2×2。
///
/// <see cref="Legacy"/> 里的码**只用于读历史数据**（口径优化之前的记录），
/// 不再产生新记录，也不进界面筛选下拉——否则下拉会越用越长。
/// </summary>
public static class OperationLogActionTypes
{
    // ---------- 存取动作：2×2（角色 × 取/存） ----------

    /// <summary>MH 存入焊丝（放一个新的）。</summary>
    public const string MhLoadWire = "mh_load_available_wire";

    /// <summary>MH 取出旧焊丝（取走一个）。</summary>
    public const string MhTakeWire = "mh_take_wire";

    /// <summary>OP 归还旧焊丝（存放）。</summary>
    public const string OpReturnWire = "op_return_wire";

    /// <summary>OP 领用新焊丝（取出，含"取出"这一步）。</summary>
    public const string OpIssueWire = "op_issue_available_wire";

    // ---------- 维护与异常 ----------

    /// <summary>维护人员试开（留痕）。</summary>
    public const string MaintTrialOpen = "maint_trial_open";

    /// <summary>
    /// 无指令开门（计划外释锁）：库内没有开门指令，而锁 DI 显示释锁。
    /// 口径收敛后不再"复用开门类型"，改为独立动作类型；<c>is_uncommanded = 1</c> 仍保留，
    /// 便于与历史记录（那时复用 mh_open_slot）按标记一起查。
    /// </summary>
    public const string UncommandedOpen = "uncommanded_open";

    // ---------- 设备动作与留痕（同一张表，但不是"人的存取动作"） ----------

    /// <summary>AGV 控车：下单 / 暂停 / 继续 / 取消 / 返航，具体动作写在 message 里。</summary>
    public const string AgvMove = "agv_move";

    /// <summary>AGV 自动回充。</summary>
    public const string AgvCharge = "agv_charge";

    /// <summary>查看记录页本身也留痕（§3.7）。</summary>
    public const string ViewLog = "view_operation_log";

    /// <summary>导出记录也留痕（§3.5、§3.7）。</summary>
    public const string ExportLog = "export_operation_log";

    // ---------- 历史码（只读旧数据用） ----------

    /// <summary>口径优化前：OP 取出单独成条；现在与领用合并，只在"取出后库存更新失败"时出现。</summary>
    public const string OpTakeWire = "op_take_wire";

    /// <summary>口径优化前：MH 单格开门。</summary>
    public const string MhOpenSlot = "mh_open_slot";

    /// <summary>口径优化前：MH 批量开门（每格一条）。</summary>
    public const string MhOpenSlotsBatch = "mh_open_slots_batch";

    /// <summary>口径优化前：开门即清空单独成条；现在降级为开门记录上的字段。</summary>
    public const string MhAssumeEmpty = "mh_assume_empty";

    /// <summary>口径优化前：流程中的开门事件单独成条；现在挂成业务记录上的字段，无人再产生。</summary>
    public const string DoorOpen = "door_open";

    /// <summary>无指令开门异常的配对关门记录（这一条仍会产生）。</summary>
    public const string DoorClose = "door_close";

    /// <summary>
    /// 已登记的动作类型，**按录入顺序**（界面筛选下拉的顺序要稳定，故不用 Dictionary 的枚举顺序）。
    /// 只放"新记录会出现"的类型；历史码见 <see cref="Legacy"/>。
    /// </summary>
    private static readonly (string Code, string Label)[] Known =
    [
        (MhLoadWire, "MH 存入焊丝"),
        (MhTakeWire, "MH 取出旧焊丝"),
        (OpReturnWire, "OP 归还旧焊丝"),
        (OpIssueWire, "OP 领用新焊丝"),
        (MaintTrialOpen, "维护试开格口"),
        (UncommandedOpen, "无指令开门"),
        (DoorClose, "格口关门"),
        (AgvMove, "AGV 控车"),
        (AgvCharge, "AGV 自动回充"),
        (ViewLog, "查看存取记录"),
        (ExportLog, "导出存取记录"),
    ];

    /// <summary>
    /// 口径优化之前的动作类型。**只用于给历史记录显示中文名**，不进筛选下拉、
    /// 也不该再被任何写入路径使用。
    /// </summary>
    private static readonly (string Code, string Label)[] Legacy =
    [
        (OpTakeWire, "OP 取出焊丝（旧）"),
        (MhOpenSlot, "MH 打开格口（旧）"),
        (MhOpenSlotsBatch, "MH 批量打开格口（旧）"),
        (MhAssumeEmpty, "开门即清空（旧）"),
        (DoorOpen, "格口开门（旧）"),
    ];

    private static readonly Dictionary<string, string> Labels =
        Known.Concat(Legacy).ToDictionary(x => x.Code, x => x.Label, StringComparer.OrdinalIgnoreCase);

    /// <summary>界面筛选下拉用（不含「全部」，由调用方补）。</summary>
    public static IReadOnlyList<(string Code, string Label)> All() => Known;

    public static string Label(string? actionType) =>
        string.IsNullOrWhiteSpace(actionType) ? ""
        : Labels.TryGetValue(actionType.Trim(), out var label) ? label
        : actionType.Trim();
}
