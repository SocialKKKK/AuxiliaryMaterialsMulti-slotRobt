using System.Text.Json;

namespace WireCabinet.Data;

/// <summary>一条物料存取记录。字段与 app_operation_log 对齐，未赋值即写 NULL。</summary>
public sealed class OperationLogEntry
{
    // 动作与主体
    public string? ActionType { get; init; }
    public string? SlotNo { get; init; }
    public string? LotNo { get; init; }
    public string? WireSpec { get; init; }
    public string? Station { get; init; }

    // 操作人
    public string? OperatorId { get; init; }
    public string? OperatorName { get; init; }
    public string? OperatorTeam { get; init; }
    public string? OperatorRole { get; init; }

    // 业务上下文
    public string? EqpNo { get; init; }
    public string? ProductLotNo { get; init; }
    public double? Qty { get; init; }
    public double? RemainQty { get; init; }

    // 结果
    public string? Result { get; init; }
    public string? Message { get; init; }
    public string? MesResult { get; init; }
    public string? LockResult { get; init; }
    public string? ErrorInfo { get; init; }

    // 格口门时间
    public string? DoorOpenTs { get; init; }
    public string? DoorCloseTs { get; init; }

    // 标记
    public bool IsCleared { get; init; }
    public bool IsUncommanded { get; init; }

    // 排障
    public string? FlowId { get; init; }
    public string? NodeId { get; init; }
}

/// <summary>记录写入结果。失败不是异常，是返回值。</summary>
public readonly record struct OperationLogWriteResult(bool Success, string? Error)
{
    public static OperationLogWriteResult Ok() => new(true, null);
    public static OperationLogWriteResult Fail(string error) => new(false, error);
}

/// <summary>
/// 物料存取记录的写入通道。
///
/// **契约（重要）**：实现**不得向调用方抛出异常**——写入失败只返回失败结果，
/// 并自行转入待补偿通道。调用方据此仅做告警，**绝不改变业务 outcome 或路由**。
/// </summary>
public interface IOperationLogSink
{
    OperationLogWriteResult TryWrite(OperationLogEntry entry);
}

/// <summary>
/// 写入应用库 app_operation_log 的默认实现。
/// 业务 DML 走 SQL catalog 条目 <c>app.operation_log.insert</c>（与工程约定一致，不内联 SQL）；
/// 写失败时把整条记录转存 operation_log_pending 供后台/人工重试；两种情况都不抛异常。
/// </summary>
public sealed class SqliteOperationLogSink : IOperationLogSink
{
    /// <summary>写入记录所用的 catalog 条目。</summary>
    public const string InsertSqlId = "app.operation_log.insert";

    private readonly SqlCatalog _catalog;
    private readonly IAppDb _appDb;

    /// <summary>写失败时触发（记录, 错误）。供界面告警与故障日志挂接。</summary>
    public event Action<OperationLogEntry, string>? WriteFailed;

    public SqliteOperationLogSink(SqlCatalog catalog, IAppDb appDb)
    {
        _catalog = catalog;
        _appDb = appDb;
    }

    public OperationLogWriteResult TryWrite(OperationLogEntry entry)
    {
        try
        {
            var item = _catalog.Find(InsertSqlId);
            if (item is null)
                return HandleFailure(entry, $"缺少 SQL 条目 {InsertSqlId}。");

            var result = _appDb.Run(item, ToParameters(entry));

            // 只判 HasError 是不够的：影响 0 行意味着记录并没有写进去，同样必须按失败处理
            // （对照：WireDiscoEqpSyncService 只看 HasError，空表上的 UPDATE 会被判成功——那是假成功）。
            if (result.HasError)
                return HandleFailure(entry, result.Error!);
            if (result.RowsAffected < 1)
                return HandleFailure(entry, "记录未写入（影响 0 行）。");

            return OperationLogWriteResult.Ok();
        }
        catch (Exception ex)
        {
            // 防御：IAppDb 实现本身不抛，但替换实现或未来改动可能抛。
            // 记录写入任何情况下都不得把异常抛回业务链路。
            return HandleFailure(entry, ex.Message);
        }
    }

    private OperationLogWriteResult HandleFailure(OperationLogEntry entry, string error)
    {
        QueuePending(entry, error);
        WriteFailed?.Invoke(entry, error);
        return OperationLogWriteResult.Fail(error);
    }

    /// <summary>
    /// 尽力转存待补偿；这里同样不能抛（连库都写不进去时只能放弃）。
    /// 待补偿属于基础设施队列、不是业务 DML，故用 RunRaw 而不进 catalog
    /// （工程内既有同类做法：WireDiscoEqpSyncService.IsLotInCabinetAsAvailable）。
    /// </summary>
    private void QueuePending(OperationLogEntry entry, string error)
    {
        try
        {
            _appDb.RunRaw(
                "INSERT INTO operation_log_pending (entry_json, last_error) VALUES (:entry_json, :last_error);",
                new Dictionary<string, object?>
                {
                    ["entry_json"] = JsonSerializer.Serialize(entry),
                    ["last_error"] = error,
                },
                isWrite: true);
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine($"[OperationLog] 待补偿转存失败：{error}");
        }
    }

    private static Dictionary<string, object?> ToParameters(OperationLogEntry e) => new()
    {
        ["operator_id"] = e.OperatorId,
        ["operator_name"] = e.OperatorName,
        ["operator_team"] = e.OperatorTeam,
        ["operator_role"] = e.OperatorRole,
        ["action_type"] = e.ActionType,
        ["slot_no"] = e.SlotNo,
        ["lot_no"] = e.LotNo,
        ["wire_spec"] = e.WireSpec,
        ["eqp_no"] = e.EqpNo,
        ["product_lot_no"] = e.ProductLotNo,
        ["qty"] = e.Qty,
        ["remain_qty"] = e.RemainQty,
        ["station"] = e.Station,
        ["result"] = e.Result,
        ["message"] = e.Message,
        ["mes_result"] = e.MesResult,
        ["lock_result"] = e.LockResult,
        ["error_info"] = e.ErrorInfo,
        ["door_open_ts"] = e.DoorOpenTs,
        ["door_close_ts"] = e.DoorCloseTs,
        ["is_cleared"] = e.IsCleared ? 1 : 0,
        ["is_uncommanded"] = e.IsUncommanded ? 1 : 0,
        ["flow_id"] = e.FlowId,
        ["node_id"] = e.NodeId,
    };
}

/// <summary>空实现：用于需要显式关闭记录写入的场景（如回滚开关）。</summary>
public sealed class NullOperationLogSink : IOperationLogSink
{
    public OperationLogWriteResult TryWrite(OperationLogEntry entry) => OperationLogWriteResult.Ok();
}
