namespace WireCabinet.Flows;

/// <summary>
/// 中断领用自动收尾的判据（2026-10-22 R-C01）。
/// 放在 Flow 层是为了能在不启动 WPF 的单元测试里覆盖；HMI 只负责取门态并调用。
///
/// 判据刻意保守：
/// 1. 只处理「领用待取」（MES 已成功；领用中还可能重复提交，不动）；
/// 2. 门必须已关（门开着说明人还在操作，不能替人做主）；
/// 3. 标记时间必须早于宽限期（避免程序重启落在“提交后、开门前”的窗口里误清）。
/// </summary>
public static class OpIssueAutoCompletePolicy
{
    public const string PendingWireState = "领用待取";

    /// <summary>宽限期：程序重启落在提交流程中间时，先给人留出操作时间。</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(10);

    public static bool ShouldAutoComplete(
        string wireState,
        DateTime? updatedAt,
        bool doorOpen,
        DateTime nowUtc,
        TimeSpan grace)
    {
        if (!string.Equals(wireState, PendingWireState, StringComparison.Ordinal))
            return false;
        if (doorOpen)
            return false;
        if (updatedAt is null)
            return false;
        return nowUtc - updatedAt.Value >= grace;
    }
}
