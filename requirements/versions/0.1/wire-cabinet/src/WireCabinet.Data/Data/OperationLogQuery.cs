namespace WireCabinet.Data;

/// <summary>
/// 记录查询的筛选条件（0.2 需求范围 §3.4 的六项 + 时间范围，各项**可选、可任意组合**）。
/// 全部为 null 表示不加条件。
/// </summary>
public sealed record OperationLogFilter
{
    /// <summary>起始时间（含）。与写入时的 ts 同为 UTC 的 'YYYY-MM-DD HH:MM:SS'。</summary>
    public string? TsFrom { get; init; }

    /// <summary>结束时间（含）。</summary>
    public string? TsTo { get; init; }

    public string? LotNo { get; init; }
    public string? SlotNo { get; init; }
    public string? OperatorId { get; init; }
    public string? EqpNo { get; init; }
    public string? ActionType { get; init; }

    public static OperationLogFilter None { get; } = new();

    /// <summary>
    /// 转成 SQL 参数。**七项条件必须全部传**（不用的传 null）：catalog 里的条件是
    /// <c>:x IS NULL OR col = :x</c>，而绑定只绑定 SQL 中出现的参数，
    /// 少传一个就等于把「不限」的语义交给了缺省值。
    ///
    /// 时间先经 <see cref="Pad"/> 补齐再换算：界面给的是**本地**日期/分钟精度，
    /// 而库里是 UTC，故需 <see cref="OperationLogTime.ToUtcText"/> 转到 UTC 才能比较
    /// （只把显示转成本地、不转筛选条件的话，按本地时刻查询会整体偏一个时区）。
    /// </summary>
    internal Dictionary<string, object?> ToParameters(TimeZoneInfo zone) => new()
    {
        ["ts_from"] = ToUtc(Pad(TsFrom, endOfRange: false), zone),
        ["ts_to"] = ToUtc(Pad(TsTo, endOfRange: true), zone),
        ["lot_no"] = Blank(LotNo),
        ["slot_no"] = Blank(SlotNo),
        ["operator_id"] = Blank(OperatorId),
        ["eqp_no"] = Blank(EqpNo),
        ["action_type"] = Blank(ActionType),
    };

    private static string? ToUtc(string? localText, TimeZoneInfo zone) =>
        OperationLogTime.ToUtcText(localText, zone);

    /// <summary>起始晚于结束时返回中文提示，否则 null（§5 异常：时间范围起止颠倒）。</summary>
    public string? TimeRangeError()
    {
        var from = Pad(TsFrom, endOfRange: false);
        var to = Pad(TsTo, endOfRange: true);

        if (from is null || to is null) return null;

        return string.CompareOrdinal(from, to) > 0 ? "时间范围的起始晚于结束，请检查后重试。" : null;
    }

    /// <summary>
    /// 把日期 / 分钟精度的时间补成与 ts 同格式（'YYYY-MM-DD HH:MM:SS'）的完整时间戳。
    /// 结束侧补 23:59:59：字符串比较下 "2026-09-02 09:00:00" &gt; "2026-09-02"，
    /// 不补就会把应属「结束于该日」的记录排除掉。其它长度原样返回。
    /// </summary>
    internal static string? Pad(string? value, bool endOfRange)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim();
        return text.Length switch
        {
            10 => text + (endOfRange ? " 23:59:59" : " 00:00:00"),
            16 => text + (endOfRange ? ":59" : ":00"),
            _ => text,
        };
    }

    /// <summary>空白条件按「不限」处理，避免界面上清空输入框反而查不到任何记录。</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>一条记录（读模型）。列与 app_operation_log 对齐，与写入用的 OperationLogEntry 分开。</summary>
public sealed record OperationLogRecord
{
    public long Id { get; init; }
    public string? Ts { get; init; }

    public string? OperatorId { get; init; }
    public string? OperatorName { get; init; }
    public string? OperatorTeam { get; init; }
    public string? OperatorRole { get; init; }

    public string? ActionType { get; init; }
    public string? SlotNo { get; init; }
    public string? LotNo { get; init; }
    public string? WireSpec { get; init; }
    public string? EqpNo { get; init; }
    public string? ProductLotNo { get; init; }

    public double? Qty { get; init; }
    public double? RemainQty { get; init; }
    public string? Station { get; init; }

    public string? Result { get; init; }
    public string? Message { get; init; }
    public string? MesResult { get; init; }
    public string? LockResult { get; init; }
    public string? ErrorInfo { get; init; }

    public string? DoorOpenTs { get; init; }
    public string? DoorCloseTs { get; init; }
    public bool IsCleared { get; init; }
    public bool IsUncommanded { get; init; }

    public string? FlowId { get; init; }
    public string? NodeId { get; init; }

    /// <summary>
    /// 从库行构造读模型。时间列（ts / 门开 / 关门）在库里是 UTC，这里换算成
    /// <paramref name="zone"/> 的本地时间——界面与导出的都是给人看的值。
    /// </summary>
    internal static OperationLogRecord FromRow(Dictionary<string, object?> row, TimeZoneInfo zone) => new()
    {
        Id = Convert.ToInt64(row.GetValueOrDefault("id") ?? 0L),
        Ts = OperationLogTime.ToLocalText(Text(row, "ts"), zone),
        OperatorId = Text(row, "operator_id"),
        OperatorName = Text(row, "operator_name"),
        OperatorTeam = Text(row, "operator_team"),
        OperatorRole = Text(row, "operator_role"),
        ActionType = Text(row, "action_type"),
        SlotNo = Text(row, "slot_no"),
        LotNo = Text(row, "lot_no"),
        WireSpec = Text(row, "wire_spec"),
        EqpNo = Text(row, "eqp_no"),
        ProductLotNo = Text(row, "product_lot_no"),
        Qty = Number(row, "qty"),
        RemainQty = Number(row, "remain_qty"),
        Station = Text(row, "station"),
        Result = Text(row, "result"),
        Message = Text(row, "message"),
        MesResult = Text(row, "mes_result"),
        LockResult = Text(row, "lock_result"),
        ErrorInfo = Text(row, "error_info"),
        DoorOpenTs = OperationLogTime.ToLocalText(Text(row, "door_open_ts"), zone),
        DoorCloseTs = OperationLogTime.ToLocalText(Text(row, "door_close_ts"), zone),
        IsCleared = Flag(row, "is_cleared"),
        IsUncommanded = Flag(row, "is_uncommanded"),
        FlowId = Text(row, "flow_id"),
        NodeId = Text(row, "node_id"),
    };

    private static string? Text(Dictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column)?.ToString();

    private static double? Number(Dictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) is { } v && v is not DBNull
        && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static bool Flag(Dictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) is { } v && v is not DBNull && Convert.ToInt64(v) == 1;
}

/// <summary>查询结果。查询失败不是异常而是返回值（与 OperationLogWriteResult 同风格）。</summary>
public sealed record OperationLogQueryResult(IReadOnlyList<OperationLogRecord> Records, string? Error)
{
    public bool Success => Error is null;

    public static OperationLogQueryResult Ok(IReadOnlyList<OperationLogRecord> records) => new(records, null);

    public static OperationLogQueryResult Fail(string error) => new([], error);
}

/// <summary>
/// 记录的查询与追溯（0.2 需求范围 §3.4 列表 / §3.6 按批号追溯 / §3.5 导出范围）。
///
/// 条件与 SQL 全在 catalog 条目里（app.operation_log.list_by_filter / list_for_export /
/// timeline_by_lot），本类只负责绑定参数、映射结果与分页算术——不拼 SQL。
/// 只读，无副作用；不抛异常，失败以返回值表达，供界面显示中文提示。
/// </summary>
public sealed class OperationLogQueryService
{
    /// <summary>列表每页条数（§3.4）。</summary>
    public const int DefaultPageSize = 20;

    public const string SearchSqlId = "app.operation_log.list_by_filter";
    public const string ExportSqlId = "app.operation_log.list_for_export";
    public const string TimelineSqlId = "app.operation_log.timeline_by_lot";

    private readonly SqlCatalog _catalog;
    private readonly IAppDb _appDb;
    private readonly TimeZoneInfo _displayZone;

    /// <summary>
    /// <paramref name="displayZone"/> 是「给谁看」的时区，默认本机时区。
    /// 注入它是为了测试能用固定时区断言换算结果，不随开发机时区变化。
    /// </summary>
    public OperationLogQueryService(SqlCatalog catalog, IAppDb appDb, TimeZoneInfo? displayZone = null)
    {
        _catalog = catalog;
        _appDb = appDb;
        _displayZone = displayZone ?? TimeZoneInfo.Local;
    }

    /// <summary>分页查询，默认时间倒序（页码从 1 开始）。</summary>
    public OperationLogQueryResult Search(OperationLogFilter? filter = null, int page = 1, int pageSize = DefaultPageSize)
    {
        if (filter?.TimeRangeError() is { } rangeError)
            return OperationLogQueryResult.Fail(rangeError);

        var size = Math.Max(1, pageSize);
        var parameters = (filter ?? OperationLogFilter.None).ToParameters(_displayZone);
        parameters["page_size"] = size;
        parameters["offset"] = (Math.Max(1, page) - 1) * size;
        return Run(SearchSqlId, parameters);
    }

    /// <summary>导出用：与列表同一套条件，但**不分页**（§3.5 导出内容与当前筛选一致）。</summary>
    public OperationLogQueryResult ListForExport(OperationLogFilter? filter = null)
    {
        if (filter?.TimeRangeError() is { } rangeError)
            return OperationLogQueryResult.Fail(rangeError);

        return Run(ExportSqlId, (filter ?? OperationLogFilter.None).ToParameters(_displayZone));
    }

    /// <summary>按批号追溯全程，时间正序；批号不存在返回 0 行（不是错误）。</summary>
    public OperationLogQueryResult Timeline(string? lotNo)
    {
        if (string.IsNullOrWhiteSpace(lotNo))
            return OperationLogQueryResult.Fail("请先输入物料批号。");

        return Run(TimelineSqlId, new Dictionary<string, object?> { ["lot_no"] = lotNo.Trim() });
    }

    private OperationLogQueryResult Run(string sqlId, Dictionary<string, object?> parameters)
    {
        var item = _catalog.Find(sqlId);
        if (item is null)
        {
            // catalog 缺条目属于部署/版本问题，不能静默当成「查不到数据」。
            System.Diagnostics.Debug.WriteLine($"[OperationLog] catalog 缺少查询条目 {sqlId}");
            return OperationLogQueryResult.Fail("记录查询不可用：缺少 SQL 条目，请检查 catalog 部署。");
        }

        var result = _appDb.Run(item, parameters);
        if (result.HasError)
            return OperationLogQueryResult.Fail($"记录查询失败：{result.Error}");

        return OperationLogQueryResult.Ok(
            result.Rows.Select(r => OperationLogRecord.FromRow(r, _displayZone)).ToList());
    }
}
