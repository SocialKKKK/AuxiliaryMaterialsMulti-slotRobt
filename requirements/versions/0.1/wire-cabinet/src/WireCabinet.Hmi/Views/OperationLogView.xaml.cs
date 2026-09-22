using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WireCabinet.Data;

namespace WireCabinet.Hmi.Views;

/// <summary>
/// 物料存取记录页（0.2 需求范围 §3.4 列表 / §3.5 导出 / §3.6 按批号追溯）。
///
/// 只做界面与编排：条件、SQL、分页算术都在 Data 层（OperationLogQueryService /
/// OperationLogExcelExporter），这里不拼 SQL。
/// 查看与导出行为本身也留痕（§3.7），留痕失败只提示、不影响当前操作。
/// </summary>
public partial class OperationLogView : UserControl
{
    private readonly OperationLogQueryService _query;
    private readonly OperationLogExcelExporter _exporter = new();

    private OperationLogFilter _filter = OperationLogFilter.None;
    private int _page = 1;
    private bool _traceMode;
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
        // 进入页面即留痕一次（§3.7）；重复切换到本页不重复记，避免刷出噪声
        if (!_viewLogged)
        {
            _viewLogged = true;
            AppendAccessLog(OperationLogActionTypes.ViewLog, "查看存取记录");
        }
        Search_Click(sender, e);
    }

    // ---------- 列表 ----------

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        _traceMode = false;
        _traceLotNo = null;
        BackToListBtn.Visibility = Visibility.Collapsed;
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

    private void BackToList_Click(object sender, RoutedEventArgs e) => Search_Click(sender, e);

    private void LoadList()
    {
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
    }

    // ---------- 按批号追溯 ----------

    private void Trace_Click(object sender, RoutedEventArgs e)
    {
        var lotNo = LotNoBox.Text.Trim();
        var result = _query.Timeline(lotNo);
        if (!ShowFailure(result, "追溯全程"))
            return;

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

        var data = _query.ListForExport(_filter);
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
        SlotNo = r.SlotNo ?? "",
        LotNo = r.LotNo ?? "",
        EqpNo = r.EqpNo ?? "",
        ResultText = r.Result ?? "",
        DetailText = string.IsNullOrWhiteSpace(r.ErrorInfo) ? r.Message ?? "" : r.ErrorInfo!,
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
            StatusText.Text = $"{message}（留痕失败，已转待补偿：{write.Error}）";
    }

    /// <summary>列表行（列绑定用，只放界面需要的显示值）。</summary>
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
}
