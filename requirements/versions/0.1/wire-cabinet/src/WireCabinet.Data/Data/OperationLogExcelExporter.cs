using ClosedXML.Excel;

namespace WireCabinet.Data;

/// <summary>导出结果。失败不是异常，是返回值（与写入、查询同风格）。</summary>
public sealed record OperationLogExportResult(bool Success, string? OutputPath, int RowCount, string? Error)
{
    public static OperationLogExportResult Ok(string path, int rowCount) => new(true, path, rowCount, null);

    public static OperationLogExportResult Fail(string error) => new(false, null, 0, error);
}

/// <summary>
/// 把记录导出成 Excel（0.2 需求范围 §3.5）。
///
/// 只负责「把给定的记录写成 .xlsx」——导出范围由调用方先用
/// <see cref="OperationLogQueryService.ListForExport"/> 按当前筛选取好，
/// 选目录 / U 盘、权限门禁与「导出行为本身留痕」由界面负责。
///
/// 不抛异常：目标不可写、U 盘未插入、文件正被 Excel 打开都以返回值表达
/// （§5 异常：中文提示、不崩溃）。
/// </summary>
public sealed class OperationLogExcelExporter
{
    /// <summary>工作表名（与顶栏页签名一致）。</summary>
    public const string SheetName = "物料存取记录";

    public const string FileExtension = ".xlsx";

    /// <summary>列顺序与表头，按 §3.3 的字段顺序。</summary>
    private static readonly (string Header, Func<OperationLogRecord, object?> Value)[] Columns =
    [
        ("时间", r => r.Ts),
        ("操作人工号", r => r.OperatorId),
        ("操作人姓名", r => r.OperatorName),
        ("操作人班组", r => r.OperatorTeam),
        ("操作角色", r => r.OperatorRole),
        ("动作类型", r => OperationLogActionTypes.Label(r.ActionType)),
        ("格口编号", r => r.SlotNo),
        ("焊丝批号", r => r.LotNo),
        ("焊丝规格", r => r.WireSpec),
        ("机台号", r => r.EqpNo),
        ("产品批次号", r => r.ProductLotNo),
        ("数量", r => r.Qty),
        ("剩余数量", r => r.RemainQty),
        ("作业站点", r => r.Station),
        ("操作结果", r => r.Result),
        ("失败原因", r => r.ErrorInfo),
        ("MES 调用结果", r => r.MesResult),
        ("格口开锁结果", r => r.LockResult),
        ("格口开门时间", r => r.DoorOpenTs),
        ("格口关门时间", r => r.DoorCloseTs),
        ("开门即清空", r => YesNo(r.IsCleared)),
        ("无指令开门", r => YesNo(r.IsUncommanded)),
        ("流程", r => r.FlowId),
        ("节点", r => r.NodeId),
    ];

    /// <summary>列数，供界面预览表头或测试核对。</summary>
    public static int ColumnCount => Columns.Length;

    /// <summary>表头文字，按列顺序。</summary>
    public static IReadOnlyList<string> Headers => Columns.Select(c => c.Header).ToArray();

    /// <summary>
    /// 写出 <paramref name="filePath"/>。目录不存在时会创建；
    /// 记录为空也写出只有表头的文件（导出范围为空不算失败）。
    /// </summary>
    public OperationLogExportResult Export(IReadOnlyList<OperationLogRecord> records, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return OperationLogExportResult.Fail("未指定导出文件路径。");

        try
        {
            using var workbook = new XLWorkbook();
            var sheet = workbook.Worksheets.Add(SheetName);

            for (var c = 0; c < Columns.Length; c++)
            {
                var cell = sheet.Cell(1, c + 1);
                cell.Value = Columns[c].Header;
                cell.Style.Font.Bold = true;
            }

            for (var i = 0; i < records.Count; i++)
            {
                var row = records[i];
                for (var c = 0; c < Columns.Length; c++)
                {
                    var value = Columns[c].Value(row);
                    if (value is null or DBNull) continue;   // 空值留白，不写空串
                    SetValue(sheet.Cell(i + 2, c + 1), value);
                }
            }

            var lastRow = Math.Max(1, records.Count + 1);
            sheet.SheetView.FreezeRows(1);
            sheet.Range(1, 1, lastRow, Columns.Length).SetAutoFilter();

            // 定宽而不做 AdjustToContents：后者要逐格量文本，导出上千条时明显变慢，
            // 而宽度只是观感问题。
            sheet.Columns().Width = 18;

            var fullPath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            workbook.SaveAs(fullPath);
            return OperationLogExportResult.Ok(fullPath, records.Count);
        }
        catch (Exception ex)
        {
            return OperationLogExportResult.Fail($"导出失败：{ex.Message}");
        }
    }

    private static void SetValue(IXLCell cell, object value)
    {
        if (value is double d)
            cell.Value = d;
        else if (value is int i)
            cell.Value = i;
        else if (value is long l)
            cell.Value = l;
        else
            cell.Value = value.ToString();
    }

    private static string YesNo(bool value) => value ? "是" : "否";
}
