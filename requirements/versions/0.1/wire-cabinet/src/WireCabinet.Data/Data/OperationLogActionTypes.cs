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
