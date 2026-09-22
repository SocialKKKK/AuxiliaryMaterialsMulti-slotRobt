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

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        [MhLoadAvailableWire] = "MH 存入可用焊丝",
        [OpReturnWire] = "OP 归还旧焊丝",
        [OpIssueAvailableWire] = "OP 领用新焊丝",
        [OpTakeWire] = "OP 取出焊丝",
    };

    public static string Label(string? actionType) =>
        string.IsNullOrWhiteSpace(actionType) ? ""
        : Labels.TryGetValue(actionType.Trim(), out var label) ? label
        : actionType.Trim();
}
