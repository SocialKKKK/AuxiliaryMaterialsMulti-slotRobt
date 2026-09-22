using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// Excel 导出（0.2 需求范围 §3.5）。导出后用 ClosedXML 把文件读回来逐格核对，
/// 而不是只断言「文件存在」——后者在导出内容错列、错行时照样通过。
/// </summary>
public class OperationLogExcelExportTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"wctest-xlsx-{Guid.NewGuid():N}");

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-xlsx-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private static OperationLogRecord SampleRecord() => new()
    {
        Id = 7,
        Ts = "2026-09-22 10:00:00",
        OperatorId = "1001",
        OperatorName = "张三",
        OperatorTeam = "A班",
        OperatorRole = "OP",
        ActionType = OperationLogActionTypes.OpTakeWire,
        SlotNo = "F-B02",
        LotNo = "14Z1888-0888",
        WireSpec = "φ42(MAXSOFT)",
        EqpNo = "1QH029",
        ProductLotNo = "P20260602-01",
        Qty = 1,
        RemainQty = 0.8,
        Result = "成功",
        DoorOpenTs = "2026-09-22 10:00:05",
        DoorCloseTs = "2026-09-22 10:00:40",
        IsCleared = true,
        IsUncommanded = false,
        FlowId = "op_flow",
        NodeId = "updateIssueSlotAfterUnload",
    };

    private string TargetPath(string name = "out") => Path.Combine(_dir, "sub", name + OperationLogExcelExporter.FileExtension);

    [Fact]
    public void Export_WritesReadableWorkbook_WithHeadersAndRows()
    {
        var path = TargetPath();

        var result = new OperationLogExcelExporter().Export([SampleRecord()], path);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.RowCount);
        Assert.Equal(Path.GetFullPath(path), result.OutputPath);
        Assert.True(File.Exists(path));

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(OperationLogExcelExporter.SheetName);

        Assert.Equal(OperationLogExcelExporter.ColumnCount, sheet.LastColumnUsed()!.ColumnNumber());
        for (var c = 1; c <= OperationLogExcelExporter.ColumnCount; c++)
            Assert.Equal(OperationLogExcelExporter.Headers[c - 1], sheet.Cell(1, c).GetString());
    }

    [Fact]
    public void Export_MapsBusinessFieldsIntoTheRightColumns()
    {
        var path = TargetPath("fields");
        var exporter = new OperationLogExcelExporter();

        Assert.True(exporter.Export([SampleRecord()], path).Success);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(OperationLogExcelExporter.SheetName);

        string Cell(string header) => sheet.Cell(2, OperationLogExcelExporter.Headers.ToList().IndexOf(header) + 1).GetString();

        Assert.Equal("2026-09-22 10:00:00", Cell("时间"));
        Assert.Equal("1001", Cell("操作人工号"));
        Assert.Equal("张三", Cell("操作人姓名"));
        Assert.Equal("A班", Cell("操作人班组"));
        Assert.Equal("OP", Cell("操作角色"));
        Assert.Equal("OP 取出焊丝", Cell("动作类型"));      // 导出显示中文，而不是 action_type 的英文码
        Assert.Equal("F-B02", Cell("格口编号"));
        Assert.Equal("14Z1888-0888", Cell("焊丝批号"));
        Assert.Equal("φ42(MAXSOFT)", Cell("焊丝规格"));
        Assert.Equal("1QH029", Cell("机台号"));
        Assert.Equal("P20260602-01", Cell("产品批次号"));
        Assert.Equal("成功", Cell("操作结果"));
        Assert.Equal("2026-09-22 10:00:05", Cell("格口开门时间"));
        Assert.Equal("2026-09-22 10:00:40", Cell("格口关门时间"));
        Assert.Equal("是", Cell("开门即清空"));
        Assert.Equal("否", Cell("无指令开门"));
    }

    /// <summary>数量必须是数值，否则在 Excel 里没法求和、排序也不对。</summary>
    [Fact]
    public void Export_WritesNumbersAsNumbers_AndBlanksForNull()
    {
        var path = TargetPath("numbers");
        Assert.True(new OperationLogExcelExporter().Export([SampleRecord()], path).Success);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(OperationLogExcelExporter.SheetName);

        var qtyColumn = OperationLogExcelExporter.Headers.ToList().IndexOf("数量") + 1;
        Assert.Equal(XLDataType.Number, sheet.Cell(2, qtyColumn).DataType);
        Assert.Equal(1d, sheet.Cell(2, qtyColumn).GetDouble());

        // 该场景没有的值留白，不写空串
        var stationColumn = OperationLogExcelExporter.Headers.ToList().IndexOf("作业站点") + 1;
        Assert.True(sheet.Cell(2, stationColumn).IsEmpty());
    }

    [Fact]
    public void Export_CreatesMissingDirectory()
    {
        var path = TargetPath("nested");
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));

        var result = new OperationLogExcelExporter().Export([SampleRecord()], path);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(path));
    }

    /// <summary>导出范围为空不算失败：仍应产出一个只有表头的文件。</summary>
    [Fact]
    public void Export_EmptyResult_StillProducesHeaderOnlyFile()
    {
        var path = TargetPath("empty");

        var result = new OperationLogExcelExporter().Export([], path);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.RowCount);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(OperationLogExcelExporter.SheetName);
        Assert.Equal(1, sheet.LastRowUsed()!.RowNumber());
        Assert.Equal("时间", sheet.Cell(1, 1).GetString());
    }

    /// <summary>§5 异常：目标不可写要给出中文提示且不抛异常（U 盘未插入同理走这条）。</summary>
    [Fact]
    public void Export_WhenTargetNotWritable_ReturnsFailureInsteadOfThrowing()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "占位");
        var path = Path.Combine(blocker, "under-a-file", "out.xlsx"); // 父路径是文件

        var result = new OperationLogExcelExporter().Export([SampleRecord()], path);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Export_WithoutPath_ReportsWhy()
    {
        var result = new OperationLogExcelExporter().Export([SampleRecord()], "   ");

        Assert.False(result.Success);
        Assert.Contains("路径", result.Error);
    }

    /// <summary>
    /// §3.5：导出内容必须与当前筛选一致——这里走完整链路（查询取数 → 导出 → 读回核对）。
    /// </summary>
    [Fact]
    public void Export_Pipeline_MatchesTheFilter()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        OperationLogSchema.Ensure(db);

        for (var i = 1; i <= 5; i++)
        {
            var insert = db.Execute(
                """
                INSERT INTO app_operation_log (id, ts, lot_no, action_type, operator_id, result)
                VALUES (:id, :ts, :lot, 'mh_load_available_wire', '1001', '成功');
                """,
                new Dictionary<string, object?>
                {
                    ["id"] = i,
                    ["ts"] = $"2026-09-0{i} 08:00:00",
                    ["lot"] = i <= 2 ? "L1" : "L2",
                });
            Assert.False(insert.HasError, insert.Error);
        }

        var query = new OperationLogQueryService(SqlCatalog.Load(), new SqliteAppDb(db), TimeZoneInfo.Utc);
        var filter = new OperationLogFilter { LotNo = "L2", ActionType = "mh_load_available_wire" };

        var data = query.ListForExport(filter);
        Assert.True(data.Success, data.Error);

        var path = TargetPath("pipeline");
        var result = new OperationLogExcelExporter().Export(data.Records, path);
        Assert.True(result.Success, result.Error);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(OperationLogExcelExporter.SheetName);
        var lotColumn = OperationLogExcelExporter.Headers.ToList().IndexOf("焊丝批号") + 1;

        Assert.Equal(3, result.RowCount);                                   // L2 的三条
        Assert.Equal(4, sheet.LastRowUsed()!.RowNumber());                  // 表头 + 3 行
        for (var row = 2; row <= 4; row++)
            Assert.Equal("L2", sheet.Cell(row, lotColumn).GetString());
    }
}
