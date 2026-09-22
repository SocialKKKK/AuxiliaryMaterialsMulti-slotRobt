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
/// 待补偿记录的补偿通道（0.2 需求范围 §3.8：自动重试 + 待补偿列表可人工重试）。
///
/// 与 <see cref="IOperationLogSink"/> 分开：写入通道的契约是「不向业务抛异常、
/// 不改变业务 outcome」，而重试跑在后台或人工路径上，两者的失败语义不同，不该混在一个接口里。
/// </summary>
public interface IOperationLogCompensation
{
    /// <summary>待补偿列表（含已达最大重试次数的），供界面展示与人工重试。</summary>
    IReadOnlyList<OperationLogPendingRow> ListPending();

    /// <summary>后台批量重试到期的待补偿，返回补写成功的条数。</summary>
    int RetryPendingBatch();

    /// <summary>重试单条。manual=true 时允许对已达上限的行再试。</summary>
    OperationLogWriteResult RetryPendingRow(long id, bool manual = false);
}

/// <summary>
/// 写入应用库 app_operation_log 的默认实现，并负责失败补偿。
/// 业务 DML 走 SQL catalog 条目 <c>app.operation_log.insert</c>（与工程约定一致，不内联 SQL）；
/// 写失败时把整条记录转存 operation_log_pending 供后台/人工重试；两种情况都不抛异常。
/// </summary>
public sealed class SqliteOperationLogSink : IOperationLogSink, IOperationLogCompensation
{
    /// <summary>写入记录所用的 catalog 条目。</summary>
    public const string InsertSqlId = "app.operation_log.insert";

    /// <summary>最大自动重试次数，之后转 failed 等人工处理。</summary>
    private const int MaxRetries = 5;

    /// <summary>重试退避，量级与 MES DISCOEQPNO 同步一致。</summary>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(5)
    ];

    private readonly SqlCatalog _catalog;
    private readonly IAppDb _appDb;
    private readonly OperationLogPendingStore _pending;

    /// <summary>写失败时触发（记录, 错误）。供界面告警与故障日志挂接。</summary>
    public event Action<OperationLogEntry, string>? WriteFailed;

    public SqliteOperationLogSink(SqlCatalog catalog, IAppDb appDb)
    {
        _catalog = catalog;
        _appDb = appDb;
        _pending = new OperationLogPendingStore(appDb);
    }

    public OperationLogWriteResult TryWrite(OperationLogEntry entry)
    {
        var result = WriteRecord(entry);
        if (result.Success)
            return result;

        // 业务路径写失败才入队；重试路径不能再入队，否则每重试一次队列就长一条。
        QueuePending(entry, result.Error!);
        WriteFailed?.Invoke(entry, result.Error!);
        return result;
    }

    public IReadOnlyList<OperationLogPendingRow> ListPending() => _pending.ListActive();

    public int RetryPendingBatch()
    {
        var succeeded = 0;
        foreach (var row in _pending.ListDueForRetry(DateTime.UtcNow))
        {
            if (RetryPendingRow(row.Id).Success)
                succeeded++;
        }
        return succeeded;
    }

    public OperationLogWriteResult RetryPendingRow(long id, bool manual = false)
    {
        var row = _pending.TryGet(id);
        if (row is null)
            return OperationLogWriteResult.Fail("待补偿记录不存在。");
        if (!row.IsActive)
            return OperationLogWriteResult.Fail("该待补偿记录已补写成功。");

        // failed 表示自动重试已到上限，只能由人工放行。否则它会被重新排回 pending，
        // 又被后台捡走，等于无限自动重试。
        if (string.Equals(row.Status, OperationLogPendingStore.StatusFailed, StringComparison.OrdinalIgnoreCase) && !manual)
            return OperationLogWriteResult.Fail("已达最大重试次数，请在待补偿列表中人工重试。");

        var result = WriteRecord(row.Entry);
        if (result.Success)
        {
            _pending.MarkSuccess(id);
            return result;
        }

        var nextCount = row.RetryCount + 1;

        // 人工重试失败后仍留在 failed，不回到后台队列——否则又会被自动重试捡走。
        if (manual || nextCount >= MaxRetries)
        {
            _pending.MarkFailed(id, result.Error, nextCount);
        }
        else
        {
            _pending.ScheduleRetry(
                id, nextCount,
                DateTime.UtcNow.Add(Backoff[Math.Min(nextCount - 1, Backoff.Length - 1)]),
                result.Error);
        }

        WriteFailed?.Invoke(row.Entry, result.Error!);
        return result;
    }

    /// <summary>实际写入。不涉及队列与事件，供业务路径与重试路径共用。</summary>
    private OperationLogWriteResult WriteRecord(OperationLogEntry entry)
    {
        try
        {
            var item = _catalog.Find(InsertSqlId);
            if (item is null)
                return OperationLogWriteResult.Fail($"缺少 SQL 条目 {InsertSqlId}。");

            var result = _appDb.Run(item, ToParameters(entry));

            // 只判 HasError 是不够的：影响 0 行意味着记录并没有写进去，同样必须按失败处理。
            // MES DISCOEQPNO 同步（WireDiscoEqpSyncService.MesWriteFailed）用同一判定——
            // 那两处曾因只看 HasError 把 0 行更新报成成功。
            if (result.HasError)
                return OperationLogWriteResult.Fail(result.Error!);
            if (result.RowsAffected < 1)
                return OperationLogWriteResult.Fail("记录未写入（影响 0 行）。");

            return OperationLogWriteResult.Ok();
        }
        catch (Exception ex)
        {
            // 防御：IAppDb 实现本身不抛，但替换实现或未来改动可能抛。
            // 记录写入任何情况下都不得把异常抛回业务链路。
            return OperationLogWriteResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 尽力转存待补偿；这里同样不能抛（连库都写不进去时只能放弃）。
    /// </summary>
    private void QueuePending(OperationLogEntry entry, string error)
    {
        try
        {
            _pending.Enqueue(entry, error);
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

/// <summary>
/// 空实现：用于需要显式关闭记录写入的场景（如回滚开关）。补偿通道同样为空，
/// 这样停用记录功能时连后台重试也一并停掉（0.2 需求范围 §1.4 第 6 条：关闭即可复原）。
/// </summary>
public sealed class NullOperationLogSink : IOperationLogSink, IOperationLogCompensation
{
    public OperationLogWriteResult TryWrite(OperationLogEntry entry) => OperationLogWriteResult.Ok();

    public IReadOnlyList<OperationLogPendingRow> ListPending() => [];

    public int RetryPendingBatch() => 0;

    public OperationLogWriteResult RetryPendingRow(long id, bool manual = false) => OperationLogWriteResult.Ok();
}
