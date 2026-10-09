namespace WireCabinet.Data;

/// <summary>一条物料存取记录。字段与 app_operation_log 对齐，未赋值即写 NULL。</summary>
public sealed record OperationLogEntry
{
    /// <summary>动作发生时刻（UTC）。补偿时沿用原值，不使用重试时刻。</summary>
    public string? Ts { get; init; }

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

    // 记录口径优化（2026-10-09）：一次人的动作一条，过程挂字段
    /// <summary>JSON 明细：批量格口清单 / 被清批号 / 重复失败统计（count、first_ts、last_ts）。</summary>
    public string? Detail { get; init; }

    /// <summary>更正记录指向被更正的那条（B 档）；查询默认取最新有效版本。</summary>
    public long? CorrectsId { get; init; }
}

/// <summary>记录写入结果。失败不是异常，是返回值。</summary>
public readonly record struct OperationLogWriteResult(bool Success, string? Error, bool Queued = false)
{
    public static OperationLogWriteResult Ok() => new(true, null);
    public static OperationLogWriteResult Fail(string error, bool queued = false) => new(false, error, queued);
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

    /// <summary>
    /// 把后到的格口门时刻补进**已经写好**的那条业务记录（记录口径优化 §4 A 档）。
    ///
    /// 归还 / 领用是先提交、后开关门的，写记录时关门时刻还不存在，只能事后补。
    /// 契约与 <see cref="TryWrite"/> 一致：只补 NULL、绝不改写已有值，失败只返回失败结果，
    /// 不抛异常、不阻断业务。定位键是 (actionType, ts)——ts 由调用方生成并原样写入，
    /// 必须恰好命中 0 或 1 行；命中 0 行按失败返回（可能已补过，或记录还在待补偿队列里）。
    /// </summary>
    OperationLogWriteResult BackfillDoorTs(string actionType, string ts, string? doorOpenTs, string? doorCloseTs);

    /// <summary>
    /// 更新某条**失败**记录的 <c>detail</c>（口径优化 §2 口径③）：自动路径同一原因连续失败时，
    /// 把次数与首末时刻累加到那一条上，而不是每条各写一行。
    ///
    /// <c>detail</c> 是过程派生字段，不改写业务事实；库层触发器只放行这一列。
    /// 定位键 (actionType, ts) 必须恰好命中 1 行，否则按失败返回（不阻断业务）。
    /// </summary>
    OperationLogWriteResult UpdateFailureDetail(string actionType, string ts, string detail);
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
    private readonly object _retryGate = new();

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
        entry = entry with { Ts = string.IsNullOrWhiteSpace(entry.Ts) ? OperationLogTime.UtcNowText() : entry.Ts };
        var result = WriteRecord(entry);
        if (result.Success)
            return result;

        // 业务路径写失败才入队；重试路径不能再入队，否则每重试一次队列就长一条。
        var queued = QueuePending(entry, result.Error!);
        var error = queued ? result.Error! : $"{result.Error}；待补偿保存失败，请立即联系维护人员。";
        NotifyWriteFailed(entry, error);
        return OperationLogWriteResult.Fail(error, queued);
    }

    /// <summary>补齐门时刻所用的 catalog 条目。</summary>
    public const string BackfillDoorTsSqlId = "app.operation_log.backfill_door_ts";

    /// <summary>更新失败记录 detail 所用的 catalog 条目。</summary>
    public const string UpdateDetailSqlId = "app.operation_log.update_detail";

    /// <summary>
    /// 更新失败记录的 detail（过程派生字段）。定位键 (actionType, ts) 必须恰好命中 1 行。
    /// </summary>
    public OperationLogWriteResult UpdateFailureDetail(string actionType, string ts, string detail)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(actionType) || string.IsNullOrWhiteSpace(ts))
                return OperationLogWriteResult.Fail("更新明细缺少定位键（action_type / ts）。");

            var item = _catalog.Find(UpdateDetailSqlId);
            if (item is null)
                return OperationLogWriteResult.Fail($"缺少 SQL 条目 {UpdateDetailSqlId}。");

            var result = _appDb.Run(item, new Dictionary<string, object?>
            {
                ["detail"] = detail,
                ["action_type"] = actionType,
                ["ts"] = ts,
            });

            if (result.HasError)
                return OperationLogWriteResult.Fail(result.Error!);
            if (result.RowsAffected != 1)
                return OperationLogWriteResult.Fail($"更新明细命中 {result.RowsAffected} 行（应恰好 1 行）。");

            return OperationLogWriteResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationLogWriteResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 只补 NULL 的门时刻，绝不改写已有值——库层 trg_app_operation_log_append_only 会兜底拒绝越界改动。
    /// 定位键 (actionType, ts) 必须恰好命中 1 行，否则按失败返回（不阻断业务）。
    /// </summary>
    public OperationLogWriteResult BackfillDoorTs(string actionType, string ts, string? doorOpenTs, string? doorCloseTs)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(actionType) || string.IsNullOrWhiteSpace(ts))
                return OperationLogWriteResult.Fail("补齐门时刻缺少定位键（action_type / ts）。");
            if (string.IsNullOrWhiteSpace(doorCloseTs))
                return OperationLogWriteResult.Fail("补齐门时刻缺少关门时刻。");

            var item = _catalog.Find(BackfillDoorTsSqlId);
            if (item is null)
                return OperationLogWriteResult.Fail($"缺少 SQL 条目 {BackfillDoorTsSqlId}。");

            var result = _appDb.Run(item, new Dictionary<string, object?>
            {
                ["door_open_ts"] = doorOpenTs,
                ["door_close_ts"] = doorCloseTs,
                ["action_type"] = actionType,
                ["ts"] = ts,
            });

            if (result.HasError)
                return OperationLogWriteResult.Fail(result.Error!);

            // 必须恰好 1 行：0 行说明记录还没落库（还在待补偿队列）或已经补过；
            // 多于 1 行说明定位键不够唯一。两种都不能算补写成功。
            if (result.RowsAffected != 1)
                return OperationLogWriteResult.Fail($"补齐门时刻命中 {result.RowsAffected} 行（应恰好 1 行）。");

            return OperationLogWriteResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationLogWriteResult.Fail(ex.Message);
        }
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
        // 后台与人工重试共享同一通道；跨实例仍由事务及状态条件保护。
        lock (_retryGate)
            return RetryPendingRowCore(id, manual);
    }

    private OperationLogWriteResult RetryPendingRowCore(long id, bool manual)
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

        // 兼容旧版队列中没有 Ts 的 JSON：用原入队时刻，不用本次重试时刻。
        var entry = row.Entry with { Ts = row.Entry.Ts ?? row.Ts ?? OperationLogTime.UtcNowText() };
        var result = WritePendingRecord(entry, id);
        if (result.Success)
            return result;

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

        NotifyWriteFailed(entry, result.Error!);
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

    private OperationLogWriteResult WritePendingRecord(OperationLogEntry entry, long pendingId)
    {
        // 真实 SQLite 通道必须原子化补写；测试替身才走普通接口。
        if (_appDb is not SqliteAppDb sqlite)
        {
            var fallback = WriteRecord(entry);
            if (!fallback.Success) return fallback;
            try { _pending.MarkSuccess(pendingId); return fallback; }
            catch (Exception ex) { return OperationLogWriteResult.Fail(ex.Message); }
        }

        var item = _catalog.Find(InsertSqlId);
        if (item is null)
            return OperationLogWriteResult.Fail($"缺少 SQL 条目 {InsertSqlId}。");

        var result = sqlite.InsertOperationLogAndCompletePending(item, ToParameters(entry), pendingId);
        return result.HasError
            ? OperationLogWriteResult.Fail(result.Error!)
            : OperationLogWriteResult.Ok();
    }

    /// <summary>
    /// 尽力转存待补偿；这里同样不能抛（连库都写不进去时只能放弃）。
    /// </summary>
    private bool QueuePending(OperationLogEntry entry, string error)
    {
        try
        {
            _pending.Enqueue(entry, error);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"[OperationLog] 待补偿转存失败：{ex}");
            return false;
        }
    }

    private void NotifyWriteFailed(OperationLogEntry entry, string error)
    {
        if (WriteFailed is null) return;
        foreach (var subscriber in WriteFailed.GetInvocationList())
        {
            try { ((Action<OperationLogEntry, string>)subscriber)(entry, error); }
            catch (Exception ex)
            {
                // 告警订阅者自身故障同样不得回流到业务链路。
                System.Diagnostics.Trace.TraceError($"[OperationLog] 告警处理失败：{ex}");
            }
        }
    }

    private static Dictionary<string, object?> ToParameters(OperationLogEntry e) => new()
    {
        ["ts"] = e.Ts,
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
        ["detail"] = e.Detail,
        ["corrects_id"] = e.CorrectsId,
    };
}

/// <summary>
/// 空实现：用于需要显式关闭记录写入的场景（如回滚开关）。补偿通道同样为空，
/// 这样停用记录功能时连后台重试也一并停掉（0.2 需求范围 §1.4 第 6 条：关闭即可复原）。
/// </summary>
public sealed class NullOperationLogSink : IOperationLogSink, IOperationLogCompensation
{
    public OperationLogWriteResult TryWrite(OperationLogEntry entry) => OperationLogWriteResult.Ok();

    /// <summary>记录功能已停用，没有可补写的行；保持静默（不产生告警）。</summary>
    public OperationLogWriteResult BackfillDoorTs(string actionType, string ts, string? doorOpenTs, string? doorCloseTs) =>
        OperationLogWriteResult.Ok();

    /// <summary>记录功能已停用，没有可更新的行；保持静默。</summary>
    public OperationLogWriteResult UpdateFailureDetail(string actionType, string ts, string detail) =>
        OperationLogWriteResult.Ok();

    public IReadOnlyList<OperationLogPendingRow> ListPending() => [];

    public int RetryPendingBatch() => 0;

    public OperationLogWriteResult RetryPendingRow(long id, bool manual = false) => OperationLogWriteResult.Ok();
}
