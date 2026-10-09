using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WireCabinet.Data;

namespace WireCabinet.Hmi.Views;

/// <summary>
/// 物料存取记录页（0.2 需求范围 §3.4 列表 / §3.5 导出 / §3.6 按批号追溯 / §3.8 待补偿）。
///
/// 只做界面与编排：条件、SQL、分页算术、xlsx 生成都在 Data 层
/// （OperationLogQueryService / OperationLogExcelExporter / IOperationLogCompensation），这里不拼 SQL。
/// 查看与导出行为本身也留痕（§3.7），留痕失败只提示、不影响当前操作。
///
/// 三种显示形态共用同一块列表区域，切换可见性而不再塞第二个列表——1024×768 一屏放不下两个列表，
/// 而约束禁止用滚动来兜（HMI界面约束 §2）。
/// </summary>
public partial class OperationLogView : UserControl
{
    private readonly OperationLogQueryService _query;
    private readonly OperationLogExcelExporter _exporter = new();

    private OperationLogFilter _filter = OperationLogFilter.None;
    private int _page = 1;
    private bool _traceMode;
    private bool _pendingMode;
    private string? _traceLotNo;
    private bool _viewLogged;

    public OperationLogView()
    {
        InitializeComponent();
        _query = new OperationLogQueryService(App.Bootstrap.Catalog, App.Bootstrap.AppDbGateway);

        ActionTypeBox.Items.Add("全部");
        foreach (var (_, label) in OperationLogActionTypes.All())
            ActionTypeBox.Items.Add(label);
        ActionTypeBox.SelectedIndex = 0;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 进入页面即留痕一次（§3.7）；重复切回本页不重复记，避免刷出噪声
        if (!_viewLogged)
        {
            _viewLogged = true;
            AppendAccessLog(OperationLogActionTypes.ViewLog, "查看存取记录");
        }

        SetListMode();
        _filter = ReadFilter();
        _page = 1;
        LoadList();
    }

    // ---------- 显示形态 ----------

    private void SetListMode()
    {
        _pendingMode = false;
        RecordList.Visibility = Visibility.Visible;
        PendingList.Visibility = Visibility.Collapsed;

        PrevPageBtn.Visibility = Visibility.Visible;
        NextPageBtn.Visibility = Visibility.Visible;
        TraceBtn.Visibility = Visibility.Visible;
        ExportBtn.Visibility = Visibility.Visible;

        RetrySelectedBtn.Visibility = Visibility.Collapsed;
        RetryAllBtn.Visibility = Visibility.Collapsed;
        BackToListBtn.Visibility = Visibility.Collapsed;
    }

    private void SetPendingMode()
    {
        _pendingMode = true;
        RecordList.Visibility = Visibility.Collapsed;
        PendingList.Visibility = Visibility.Visible;

        PrevPageBtn.Visibility = Visibility.Collapsed;
        NextPageBtn.Visibility = Visibility.Collapsed;
        TraceBtn.Visibility = Visibility.Collapsed;
        ExportBtn.Visibility = Visibility.Collapsed;

        RetrySelectedBtn.Visibility = Visibility.Visible;
        RetryAllBtn.Visibility = Visibility.Visible;
        BackToListBtn.Visibility = Visibility.Visible;
    }

    // ---------- 列表 ----------

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        SetListMode();
        _filter = ReadFilter();
        _page = 1;
        LoadList();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        TsFromBox.Text = "";
        TsToBox.Text = "";
        LotNoBox.Text = "";
        SlotNoBox.Text = "";
        OperatorBox.Text = "";
        EqpNoBox.Text = "";
        ActionTypeBox.SelectedIndex = 0;
        Search_Click(sender, e);
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page <= 1) return;
        _page--;
        LoadList();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        _page++;
        LoadList();
    }

    /// <summary>从追溯或待补偿回到列表。</summary>
    private void BackToList_Click(object sender, RoutedEventArgs e) => Search_Click(sender, e);

    private void LoadList()
    {
        _traceMode = false;
        var result = _query.Search(_filter, _page);
        if (!ShowFailure(result, "查询记录"))
            return;

        Render(result.Records);

        var pageSize = OperationLogQueryService.DefaultPageSize;
        SummaryText.Text = result.Records.Count == 0
            ? "无匹配记录"
            : $"第 {_page} 页 · 每页 {pageSize} 条";

        // 没有总数接口，用「本页是否满页」判断还有没有下一页
        PrevPageBtn.IsEnabled = _page > 1;
        NextPageBtn.IsEnabled = result.Records.Count == pageSize;

        RefreshPendingBadge();
    }

    // ---------- 按批号追溯 ----------

    private void Trace_Click(object sender, RoutedEventArgs e)
    {
        var lotNo = LotNoBox.Text.Trim();
        var result = _query.Timeline(lotNo);
        if (!ShowFailure(result, "追溯全程"))
            return;

        SetListMode();
        _traceMode = true;
        _traceLotNo = lotNo;
        Render(result.Records);

        SummaryText.Text = result.Records.Count == 0
            ? $"批号 {lotNo} 在记录中不存在"
            : $"批号 {lotNo} 全程 · 共 {result.Records.Count} 条（时间正序）";

        PrevPageBtn.IsEnabled = false;
        NextPageBtn.IsEnabled = false;
        BackToListBtn.Visibility = Visibility.Visible;
    }

    // ---------- 待补偿（§3.8 第 3、4 项；自动重试由 OperationLogRetryTimer 负责） ----------

    private void Pending_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingMode)
        {
            BackToList_Click(sender, e);
            return;
        }

        SetPendingMode();
        LoadPending();
    }

    private void LoadPending()
    {
        var rows = App.Bootstrap.OperationLog.ListPending();

        PendingList.ItemsSource = rows.Select(ToPendingRow).ToList();
        SummaryText.Text = rows.Count == 0 ? "没有待补偿记录" : $"待补偿 {rows.Count} 条";
        StatusText.Text = "";
        RefreshPendingBadge();
    }

    private void RetrySelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = PendingList.SelectedItems.Cast<OperationLogPendingRowVm>().ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "请先选中要重试的记录。";
            return;
        }

        Retry(selected.Select(r => r.Id).ToList());
    }

    private void RetryAll_Click(object sender, RoutedEventArgs e)
    {
        var ids = App.Bootstrap.OperationLog.ListPending().Select(r => r.Id).ToList();
        if (ids.Count == 0)
        {
            StatusText.Text = "没有待补偿记录。";
            return;
        }

        Retry(ids);
    }

    /// <summary>
    /// 人工重试。用 manual: true——达到自动重试上限而转 failed 的记录只有人工能再试
    /// （见 SqliteOperationLogSink.RetryPendingRow）。
    /// </summary>
    private void Retry(IReadOnlyList<long> ids)
    {
        var succeeded = 0;
        string? lastError = null;

        foreach (var id in ids)
        {
            var result = App.Bootstrap.OperationLog.RetryPendingRow(id, manual: true);
            if (result.Success)
                succeeded++;
            else
                lastError = result.Error;
        }

        LoadPending();
        StatusText.Text = succeeded == ids.Count
            ? $"已补写 {succeeded} 条"
            : $"补写成功 {succeeded}/{ids.Count} 条{(string.IsNullOrWhiteSpace(lastError) ? "" : $"：{lastError}")}";
    }

    /// <summary>待补偿条数带在按钮上，便于发现写入失败（§3.8 第 2 项的告警入口）。</summary>
    private void RefreshPendingBadge()
    {
        var count = App.Bootstrap.OperationLog.ListPending().Count;
        PendingBtn.Content = count == 0 ? "待补偿" : $"待补偿 ({count})";
    }

    // ---------- 导出 ----------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出物料存取记录",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            FileName = $"物料存取记录_{DateTime.Now:yyyyMMdd_HHmmss}{OperationLogExcelExporter.FileExtension}",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return;

        // 追溯视图展示的是全批号时间线；导出必须与眼前列表一致。
        var data = _traceMode ? _query.Timeline(_traceLotNo) : _query.ListForExport(_filter);
        if (!ShowFailure(data, "导出"))
            return;

        var exported = _exporter.Export(data.Records, dialog.FileName);
        if (!exported.Success)
        {
            StatusText.Text = exported.Error!;
            MessageBox.Show(exported.Error!, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StatusText.Text = $"已导出 {exported.RowCount} 条到 {exported.OutputPath}";
        // 导出行为留痕（§3.5、§3.7）
        AppendAccessLog(OperationLogActionTypes.ExportLog, $"导出 {exported.RowCount} 条 → {exported.OutputPath}");
    }

    // ---------- 内部 ----------

    private OperationLogFilter ReadFilter() => new()
    {
        TsFrom = Text(TsFromBox),
        TsTo = Text(TsToBox),
        LotNo = Text(LotNoBox),
        SlotNo = Text(SlotNoBox),
        OperatorId = Text(OperatorBox),
        EqpNo = Text(EqpNoBox),
        ActionType = ActionTypeBox.SelectedIndex <= 0
            ? null
            : OperationLogActionTypes.All()[ActionTypeBox.SelectedIndex - 1].Code,
    };

    private static string? Text(TextBox box) => box.Text.Trim();

    private bool ShowFailure(OperationLogQueryResult result, string what)
    {
        if (result.Success)
            return true;

        SummaryText.Text = what + "失败";
        StatusText.Text = result.Error!;
        return false;
    }

    private void Render(IReadOnlyList<OperationLogRecord> records)
    {
        RecordList.ItemsSource = records.Select(ToRow).ToList();
        StatusText.Text = _traceMode ? $"批号 {_traceLotNo} 的全程时间线" : "";
    }

    private static OperationLogRow ToRow(OperationLogRecord r) => new()
    {
        TsText = r.Ts ?? "",
        ActionText = OperationLogActionTypes.Label(r.ActionType),
        OperatorText = string.IsNullOrWhiteSpace(r.OperatorName)
            ? r.OperatorId ?? ""
            : $"{r.OperatorName}({r.OperatorId})",
        SlotNo = SlotText(r),
        LotNo = r.LotNo ?? "",
        EqpNo = r.EqpNo ?? "",
        ResultText = r.Result ?? "",
        DetailText = ExplainText(r),
    };

    /// <summary>
    /// 一次批量开门只有一条记录、没有单个格口号（口径优化 §2）。直接留空会看不出发生了什么，
    /// 所以显示「N 格」；单格动作仍显示格口号。
    /// </summary>
    private static string SlotText(OperationLogRecord r) =>
        !string.IsNullOrWhiteSpace(r.SlotNo) ? r.SlotNo!
        : r.SlotCount is > 1 ? $"{r.SlotCount} 格"
        : "";

    /// <summary>
    /// 说明列：失败原因优先，否则用记录的说明文案。
    ///
    /// 「开门即清空」的被清批号**不**往这里拼：说明列只有 200px（约 20 个汉字），
    /// 批量开门那条的说明本身就写明了「其中 N 格有料按账面清空」，再拼批号只会被裁掉。
    /// 完整批号在导出的 Excel（「明细」列）与 `detail.cleared_lots` 里。
    /// </summary>
    private static string ExplainText(OperationLogRecord r) =>
        string.IsNullOrWhiteSpace(r.ErrorInfo) ? r.Message ?? "" : r.ErrorInfo!;

    private static OperationLogPendingRowVm ToPendingRow(OperationLogPendingRow row) => new()
    {
        Id = row.Id,
        // 入队时间是库里的 UTC 文本，要换算成本地与「下次重试」保持一致
        // （两者同在一张列表里，一个 UTC 一个本地会让人看不懂）
        TsText = OperationLogTime.ToLocalText(row.Ts, TimeZoneInfo.Local) ?? "",
        ActionText = OperationLogActionTypes.Label(row.Entry.ActionType),
        LotNo = row.Entry.LotNo ?? "",
        StatusText = row.Status switch
        {
            OperationLogPendingStore.StatusFailed => "需人工",
            OperationLogPendingStore.StatusSuccess => "已补写",
            _ => "待重试",
        },
        RetryCount = row.RetryCount.ToString(),
        NextRetryText = row.Status == OperationLogPendingStore.StatusFailed
            ? "—"
            : row.NextRetryAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "立即",
        ErrorText = row.LastError ?? "",
    };

    /// <summary>
    /// 查看 / 导出留痕。**契约**：失败只提示，不改变当前操作结果（§1.4 第 1 条）。
    /// </summary>
    private void AppendAccessLog(string actionType, string message)
    {
        var entry = new OperationLogEntry
        {
            ActionType = actionType,
            OperatorRole = App.OpLogAccess.UnlockedRole,
            Message = message,
        };

        var write = App.Bootstrap.OperationLog.TryWrite(entry);
        if (!write.Success)
            StatusText.Text = $"{message}（留痕失败{(write.Queued ? "，已转待补偿" : "")}：{write.Error}）";
    }

    /// <summary>记录行（列绑定用，只放界面需要的显示值）。</summary>
    private sealed class OperationLogRow
    {
        public string TsText { get; init; } = "";
        public string ActionText { get; init; } = "";
        public string OperatorText { get; init; } = "";
        public string SlotNo { get; init; } = "";
        public string LotNo { get; init; } = "";
        public string EqpNo { get; init; } = "";
        public string ResultText { get; init; } = "";
        public string DetailText { get; init; } = "";
    }

    /// <summary>待补偿行。</summary>
    private sealed class OperationLogPendingRowVm
    {
        public long Id { get; init; }
        public string TsText { get; init; } = "";
        public string ActionText { get; init; } = "";
        public string LotNo { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string RetryCount { get; init; } = "";
        public string NextRetryText { get; init; } = "";
        public string ErrorText { get; init; } = "";
    }
}
