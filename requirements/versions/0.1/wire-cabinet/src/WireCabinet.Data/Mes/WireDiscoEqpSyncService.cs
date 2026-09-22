namespace WireCabinet.Data;

public interface IWireDiscoEqpSync
{
    string DiscoEqpNo { get; }

    WireDiscoSyncResult TryMarkInCabinet(string wireLotNo, WireMesDiscoFailureKind kind = WireMesDiscoFailureKind.Inline);

    WireDiscoSyncResult TryClearFromCabinet(string wireLotNo, WireMesDiscoFailureKind kind = WireMesDiscoFailureKind.Inline);

    string? TryGetAvailableWireLot(object? slotId);

    int RetryPendingBatch();

    WireDiscoSyncResult RetryPendingRow(long id, bool manual = false);
}

public sealed class WireDiscoEqpSyncService : IWireDiscoEqpSync
{
    private const int MaxRetries = 5;
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
    private readonly IMesGateway _mes;
    private readonly WireMesDiscoSyncStore _pending;
    private readonly MesReconAuditLogStore? _audit;
    private readonly IWireMesInterruptGuard? _interruptGuard;

    public string DiscoEqpNo { get; }

    public WireDiscoEqpSyncService(
        SqlCatalog catalog,
        IAppDb appDb,
        IMesGateway mes,
        WireMesDiscoSyncStore pending,
        string discoEqpNo,
        MesReconAuditLogStore? audit = null,
        IWireMesInterruptGuard? interruptGuard = null)
    {
        _catalog = catalog;
        _appDb = appDb;
        _mes = mes;
        _pending = pending;
        DiscoEqpNo = discoEqpNo;
        _audit = audit;
        _interruptGuard = interruptGuard;
    }

    public WireDiscoSyncResult TryMarkInCabinet(string wireLotNo, WireMesDiscoFailureKind kind = WireMesDiscoFailureKind.Inline)
    {
        var lot = wireLotNo.Trim();
        if (string.IsNullOrEmpty(lot))
            return new WireDiscoSyncResult(false, false, "批号为空。");

        if (!IsLotInCabinetAsAvailable(lot))
            return new WireDiscoSyncResult(false, false, $"批号 {lot} 不在柜内可用焊丝状态，跳过 SET。");

        if (IsAlreadySetInMes(lot))
        {
            _pending.MarkSuccess(lot, WireMesDiscoAction.Set);
            return new WireDiscoSyncResult(true, false, "MES 已是目标值。");
        }

        var item = ResolveMesItem("mes.wire.set_disco_eqp_no");
        if (item is null)
            return FailInline(lot, WireMesDiscoAction.Set, kind, "未找到 mes.wire.set_disco_eqp_no SQL 条目。");

        var result = _mes.Run(item, new Dictionary<string, object?>
        {
            ["sublot"] = lot,
            ["disco_eqp_no"] = DiscoEqpNo
        });

        if (MesWriteFailed(result))
            return FailInline(lot, WireMesDiscoAction.Set, kind, result.Error ?? "MES SET 未生效（影响 0 行）。");

        _pending.MarkSuccess(lot, WireMesDiscoAction.Set);
        return new WireDiscoSyncResult(true, false, "MES SET 成功。");
    }

    public WireDiscoSyncResult TryClearFromCabinet(string wireLotNo, WireMesDiscoFailureKind kind = WireMesDiscoFailureKind.Inline)
    {
        var lot = wireLotNo.Trim();
        if (string.IsNullOrEmpty(lot))
            return new WireDiscoSyncResult(false, false, "批号为空。");

        if (IsLotStillInCabinetAsAvailable(lot))
            return new WireDiscoSyncResult(false, false, $"批号 {lot} 仍在柜内，跳过 CLEAR。");

        if (IsAlreadyClearInMes(lot))
        {
            _pending.MarkSuccess(lot, WireMesDiscoAction.Clear);
            return new WireDiscoSyncResult(true, false, "MES 已是 NULL。");
        }

        var item = ResolveMesItem("mes.wire.clear_disco_eqp_no");
        if (item is null)
            return FailInline(lot, WireMesDiscoAction.Clear, kind, "未找到 mes.wire.clear_disco_eqp_no SQL 条目。");

        var result = _mes.Run(item, new Dictionary<string, object?> { ["sublot"] = lot });
        if (MesWriteFailed(result))
            return FailInline(lot, WireMesDiscoAction.Clear, kind, result.Error ?? "MES CLEAR 未生效（影响 0 行）。");

        _pending.MarkSuccess(lot, WireMesDiscoAction.Clear);
        return new WireDiscoSyncResult(true, false, "MES CLEAR 成功。");
    }

    public string? TryGetAvailableWireLot(object? slotId)
    {
        var id = ToLong(slotId);
        if (id <= 0) return null;

        var item = _catalog.Find("app.slot.get_inventory");
        if (item is null) return null;

        var result = _appDb.Run(item, new Dictionary<string, object?> { ["slot_id"] = id });
        var row = result.First;
        if (row is null) return null;

        var state = row.GetValueOrDefault("biz_state")?.ToString();
        if (!string.Equals(state, "available_wire", StringComparison.OrdinalIgnoreCase))
            return null;

        return row.GetValueOrDefault("wire_lot_no")?.ToString()?.Trim();
    }

    public int RetryPendingBatch()
    {
        var due = _pending.ListDueForRetry(DateTime.UtcNow);
        var count = 0;
        foreach (var row in due)
        {
            if (RetryPendingRow(row.Id).Success)
                count++;
        }
        return count;
    }

    public WireDiscoSyncResult RetryPendingRow(long id, bool manual = false)
    {
        var row = _pending.TryGet(id);
        if (row is null)
            return new WireDiscoSyncResult(false, false, "记录不存在。");

        if (row.Status == WireMesDiscoSyncStatus.ManualResolved)
            return new WireDiscoSyncResult(false, false, "已人工处理。");

        if (manual && row.Status == WireMesDiscoSyncStatus.Failed)
            _pending.ScheduleRetry(id, row.RetryCount, DateTime.UtcNow, row.LastError);

        if (_interruptGuard?.IsLotBlocked(row.WireLotNo) == true)
            return new WireDiscoSyncResult(false, false, "存在流程中断快照，禁止自动重试。");

        WireDiscoSyncResult result = row.Action switch
        {
            WireMesDiscoAction.Set when !IsLotInCabinetAsAvailable(row.WireLotNo) =>
                new WireDiscoSyncResult(false, false, "柜内状态与 SET 不一致，跳过重试。"),
            WireMesDiscoAction.Clear when IsLotStillInCabinetAsAvailable(row.WireLotNo) =>
                new WireDiscoSyncResult(false, false, "柜内仍有该批号，跳过 CLEAR 重试。"),
            WireMesDiscoAction.Set => TryMarkInCabinet(row.WireLotNo, row.FailureKind),
            _ => TryClearFromCabinet(row.WireLotNo, row.FailureKind)
        };

        if (result.Success)
        {
            if (manual)
                _audit?.Append("manual_retry_success", row.WireLotNo, $"action={row.Action}");
            return result;
        }

        if (manual)
        {
            var nextCount = row.RetryCount + 1;
            if (nextCount >= MaxRetries)
                _pending.MarkFailed(row.WireLotNo, row.Action, result.UserMessage, nextCount, null);
            else
                _pending.ScheduleRetry(id, nextCount, DateTime.UtcNow.Add(Backoff[Math.Min(nextCount, Backoff.Length - 1)]), result.UserMessage);
            _audit?.Append("manual_retry_fail", row.WireLotNo, result.UserMessage);
            return result;
        }

        var retryCount = row.RetryCount + 1;
        if (retryCount >= MaxRetries)
        {
            _pending.MarkFailed(row.WireLotNo, row.Action, result.UserMessage, retryCount, null);
            return result;
        }

        _pending.ScheduleRetry(id, retryCount, DateTime.UtcNow.Add(Backoff[Math.Min(retryCount - 1, Backoff.Length - 1)]), result.UserMessage);
        return result;
    }

    public WireDiscoSyncResult ManualSet(string wireLotNo)
    {
        var lot = wireLotNo.Trim();
        var item = ResolveMesItem("mes.wire.set_disco_eqp_no");
        if (item is null)
            return new WireDiscoSyncResult(false, false, "SQL 条目缺失。");

        var result = _mes.Run(item, new Dictionary<string, object?> { ["sublot"] = lot, ["disco_eqp_no"] = DiscoEqpNo });
        if (MesWriteFailed(result))
            return new WireDiscoSyncResult(false, false, result.Error ?? "SET 未生效（影响 0 行）。");

        _pending.MarkSuccess(lot, WireMesDiscoAction.Set);
        _audit?.Append("manual_set", lot, $"DISCOEQPNO={DiscoEqpNo}");
        return new WireDiscoSyncResult(true, false, "手工 SET 成功。");
    }

    public WireDiscoSyncResult ManualClear(string wireLotNo)
    {
        var lot = wireLotNo.Trim();
        var item = ResolveMesItem("mes.wire.clear_disco_eqp_no");
        if (item is null)
            return new WireDiscoSyncResult(false, false, "SQL 条目缺失。");

        var result = _mes.Run(item, new Dictionary<string, object?> { ["sublot"] = lot });
        if (MesWriteFailed(result))
            return new WireDiscoSyncResult(false, false, result.Error ?? "CLEAR 未生效（影响 0 行）。");

        _pending.MarkSuccess(lot, WireMesDiscoAction.Clear);
        _audit?.Append("manual_clear", lot, "DISCOEQPNO=NULL");
        return new WireDiscoSyncResult(true, false, "手工 NULL 成功。");
    }

    public void MarkManualResolved(long id)
    {
        _pending.MarkManualResolved(id);
        var row = _pending.TryGet(id);
        _audit?.Append("manual_resolved", row?.WireLotNo, $"id={id}");
    }

    public IReadOnlyList<WireMesDiscoPendingRow> ListPending() => _pending.ListActive();

    private WireDiscoSyncResult FailInline(string lot, WireMesDiscoAction action, WireMesDiscoFailureKind kind, string error)
    {
        _pending.UpsertPending(lot, action, kind, error);
        return new WireDiscoSyncResult(false, true, WireMesDiscoMessages.InlineFailUser);
    }

    /// <summary>
    /// MES 写入是否未生效。UPDATE 影响 0 行时 Oracle 与 SQLite 都不报错，
    /// 但 sublot 在 fw_material_indetail 中不存在本身就是失败——只判 HasError
    /// 会把它报成成功，并把待补偿状态标成已同步。判定与
    /// <see cref="SqliteOperationLogSink.TryWrite"/> 保持一致。
    /// </summary>
    private static bool MesWriteFailed(DbResult result) =>
        result.HasError || result.RowsAffected < 1;

    private SqlCatalogItem? ResolveMesItem(string formalId)
    {
        // 与 FlowEngine 共用同一套模式解析（此前各写一份）
        return _catalog.FindForMode(formalId, _mes.Mode);
    }

    private bool IsLotInCabinetAsAvailable(string lot)
    {
        var result = _appDb.RunRaw(
            "SELECT 1 FROM app_slot WHERE biz_state = 'available_wire' AND wire_lot_no = :lot LIMIT 1;",
            new Dictionary<string, object?> { ["lot"] = lot },
            isWrite: false);
        return result.Rows.Count > 0;
    }

    private bool IsLotStillInCabinetAsAvailable(string lot) => IsLotInCabinetAsAvailable(lot);

    private bool IsAlreadySetInMes(string lot)
    {
        // 读失败时不跳过：交给后面的 SET 由影响行数定夺，避免把“不知道”当成“已同步”。
        if (!TryQueryMesDisco(lot, out var current))
            return false;

        return !string.IsNullOrWhiteSpace(current)
               && string.Equals(current, DiscoEqpNo, StringComparison.Ordinal);
    }

    private bool IsAlreadyClearInMes(string lot)
    {
        // 读失败不能当成“已是 NULL”——否则 CLEAR 会在完全没写 MES 的情况下报成功。
        if (!TryQueryMesDisco(lot, out var current))
            return false;

        return string.IsNullOrWhiteSpace(current);
    }

    /// <summary>
    /// 读取 MES 当前 DISCOEQPNO。返回 false 表示**读取失败**，
    /// 与“读到了 NULL”（返回 true 且 value 为 null）是两回事，调用方必须区分。
    /// </summary>
    private bool TryQueryMesDisco(string lot, out string? value)
    {
        value = null;

        var item = ResolveMesItem("mes.wire.query_disco_by_lot");
        if (item is null) return false;

        var result = _mes.Run(item, new Dictionary<string, object?> { ["sublot"] = lot });
        if (result.HasError) return false;

        value = result.First?.GetValueOrDefault("DISCOEQPNO")?.ToString();
        return true;
    }

    private static long ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        string s when long.TryParse(s, out var x) => x,
        _ => 0
    };
}
