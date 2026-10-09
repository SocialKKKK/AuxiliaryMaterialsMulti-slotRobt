using System.Globalization;
using WireCabinet.Data;
using WireCabinet.Slots;

namespace WireCabinet.Hmi.Services;

/// <summary>
/// MH 纯开门：不跑 material_handler_open_* 全流程；开门仅更新 door_state/lock_state。
/// 关门后对本会话已打开格口执行 assume_empty（需求范围 §3.3）。
/// </summary>
public sealed class MhDoorOnlyService
{
    public const string SessionFlowId = "mh_door_only";

    private readonly ISlotControlService _slots;
    private readonly IWireOperationSession _session;
    private readonly WireCabinet.Data.IAppDb _appDb;
    private readonly WireCabinet.Data.SqlCatalog _catalog;
    private readonly WireCabinet.Data.IWireDiscoEqpSync? _discoSync;
    private readonly HashSet<long> _openedInSession = new();
    private readonly Dictionary<long, OpenedDoorRecord> _doorOpenLogs = new();
    private readonly Dictionary<string, string> _uncommandedOpenTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _trackGate = new();

    /// <summary>
    /// 一个格口一次开门对应的一条记录（2026-10-09 口径收敛）：**一个格口一条**，
    /// 批量开门也是逐格一条（打开 53 格 = 53 条）。关门时按它回填 door_close_ts。
    /// </summary>
    private sealed class OpenedDoorRecord
    {
        public required long SlotId { get; init; }
        public required string SlotNo { get; init; }
        public required string OpenedAtUtc { get; init; }
        public required string ActionType { get; init; }

        /// <summary>开门时该格账面已有的批号（取出时被取走的那批）。</summary>
        public string? ClearedLot { get; init; }
    }

    public event EventHandler<string>? SessionAutoEnded;

    /// <summary>本会话已打开、尚未确认关门的格口。</summary>
    public event EventHandler? OperationLockChanged;

    public MhDoorOnlyService(
        ISlotControlService slots,
        IWireOperationSession session,
        WireCabinet.Data.IAppDb appDb,
        WireCabinet.Data.SqlCatalog catalog,
        WireCabinet.Data.IWireDiscoEqpSync? discoSync = null,
        WireCabinet.Data.IOperationLogSink? operationLog = null)
    {
        _slots = slots;
        _session = session;
        _appDb = appDb;
        _catalog = catalog;
        _discoSync = discoSync;
        _doorLog = new SlotDoorActionLog(operationLog);
    }

    private readonly SlotDoorActionLog _doorLog;

    /// <summary>
    /// 当前操作人（0.2 §3.3）。由界面在调用前设置——MH 界面收工号/班组，维护界面没有工号故只记角色。
    /// 未设置时按 MH 记（界面的默认角色）。
    /// </summary>
    public DoorOperator CurrentOperator { get; set; } = DoorOperator.Mh();

    public bool HasActiveSession => _session.HasActiveSession;

    /// <summary>仅 MH 纯开门（批量/单格）会话；不含存料流程会话。</summary>
    public bool HasActiveDoorOnlySession =>
        _session.HasActiveSession
        && string.Equals(_session.ActiveFlowId, SessionFlowId, StringComparison.OrdinalIgnoreCase);

    public bool HasOpenedSlotsAwaitingClose
    {
        get
        {
            lock (_trackGate)
                return _openedInSession.Count > 0;
        }
    }

    public bool TryBeginSession(out string message) =>
        _session.TryBegin(SessionFlowId, out message);

    public void EndSession()
    {
        lock (_trackGate) _openedInSession.Clear();
        _session.End();
    }

    public void TrackOpenedSlot(long slotId)
    {
        if (slotId <= 0) return;
        lock (_trackGate) _openedInSession.Add(slotId);
        NotifyOperationLockChanged();
    }

    public void UntrackSlot(long slotId)
    {
        lock (_trackGate) _openedInSession.Remove(slotId);
    }

    /// <summary>
    /// 一个格口一条：开门成功就落库并登记，关门时回填 door_close_ts。
    ///
    /// 登记必须**在开门回调里当场做**（不能等整批跑完）——批量是每格 400ms 逐格开的，
    /// 53 格要跑 20 秒；这期间若有格口被关（DI 确认锁闭 → 巡检调 NotifySlotsClosedAsync），
    /// 没登记就会漏掉"按账面清空"和关门时刻回填。
    /// </summary>
    private void RecordDoorOpened(string actionType, OpenSlotResult result, DoorOperator op,
        string? clearedLot = null)
    {
        if (!result.Success) return;

        var openedAt = result.OpenedAtUtc ?? OperationLogTime.UtcNowText();

        lock (_trackGate)
            _doorOpenLogs[result.SlotId] = new OpenedDoorRecord
            {
                SlotId = result.SlotId,
                SlotNo = result.SlotNo,
                OpenedAtUtc = openedAt,
                ActionType = actionType,
                ClearedLot = clearedLot,
            };

        _doorLog.RecordOpened(actionType, result.SlotNo, op, openedAt, clearedLot);
    }

    /// <summary>关门确认：把 door_close_ts 回填到该格那条开门记录（一格一条，不涉及批次）。</summary>
    private void RecordDoorClosedIfTracked(OpenedDoorRecord opened, string closedAt) =>
        _doorLog.RecordClosed(opened.ActionType, opened.OpenedAtUtc, closedAt);

    /// <summary>开门时该格账面已有的批号——「开门即清空」要清掉的那批（与 assume_empty 同口径）。</summary>
    private string? ReadClearedLotAtOpen(long slotId)
    {
        var invItem = _catalog.Find("app.slot.get_inventory");
        if (invItem is null) return null;

        var inv = _appDb.Run(invItem, new Dictionary<string, object?> { ["slot_id"] = slotId });
        var row = inv.First;
        if (row is null
            || !string.Equals(row.GetValueOrDefault("biz_state")?.ToString(), "available_wire", StringComparison.OrdinalIgnoreCase))
            return null;

        return row.GetValueOrDefault("wire_lot_no")?.ToString()?.Trim();
    }

    /// <summary>
    /// 锁 DI 确认这些格口已关闭后：把 door_close_ts 补进那次开门的那条记录，并按会话清空账面无料。
    ///
    /// 未被本服务跟踪的开门（操作员/物料员流程自己开的门）**不补记录**：流程自己那条业务记录
    /// 已经带上门时刻（口径优化 §2），这里再补会让同一次开门记出两条关门。
    /// 代价：重启后内存关联丢失、库内仍为 open 而 DI 已锁闭的场景，这里只修库、不再补记录——
    /// 留一条未配对的开门，比多写一条来源不明的关门更可接受。
    /// </summary>
    public async Task NotifySlotsClosedAsync(IReadOnlyList<long> slotIds, CancellationToken ct = default)
    {
        var closedAt = OperationLogTime.UtcNowText();
        List<(long SlotId, string? ClearedLot)> toAssumeEmpty;

        lock (_trackGate)
        {
            toAssumeEmpty = [];
            foreach (var id in slotIds)
            {
                if (_doorOpenLogs.Remove(id, out var opened))
                    RecordDoorClosedIfTracked(opened, closedAt);

                if (_openedInSession.Remove(id))
                    toAssumeEmpty.Add((id, opened?.ClearedLot));
            }
        }

        foreach (var (id, clearedLot) in toAssumeEmpty)
            AssumeEmpty(id, clearedLot);

        if (toAssumeEmpty.Count > 0)
            NotifyOperationLockChanged();

        await Task.CompletedTask;
        TryAutoEndDoorOnlySession();
    }

    /// <summary>
    /// 按账面无料处理（§ 矩阵第 11 项）。「被清掉的批号」已经作为字段记在开门那条记录上
    /// （口径优化 §2：清空降级为字段），所以这里只做业务写入与 MES 联动，不再单独记一条。
    /// </summary>
    private void AssumeEmpty(long slotId, string? clearedLot)
    {
        var item = _catalog.Find("app.slot.assume_empty");
        if (item is null)
            return;

        var result = _appDb.Run(item, new Dictionary<string, object?> { ["slot_id"] = slotId });
        if (result.HasError)
            return;

        if (!string.IsNullOrEmpty(clearedLot))
            _discoSync?.TryClearFromCabinet(clearedLot, WireCabinet.Data.WireMesDiscoFailureKind.Inline);
    }

    /// <summary>
    /// 无指令开门记录（§ 矩阵第 10 项）。由门态巡检发现「库内无开门指令而 DI 显示释锁」后调用。
    /// </summary>
    public bool RecordUncommandedOpen(string slotNo)
    {
        var openedAt = OperationLogTime.UtcNowText();
        lock (_trackGate) _uncommandedOpenTimes[slotNo] = openedAt;
        return _doorLog.RecordUncommandedOpen(slotNo, openedAt);
    }

    public bool RecordUncommandedClosed(string slotNo)
    {
        string? openedAt;
        lock (_trackGate)
        {
            _uncommandedOpenTimes.Remove(slotNo, out openedAt);
        }
        return _doorLog.RecordUncommandedClosed(slotNo, openedAt);
    }

    /// <summary>本会话打开格口均已关库后自动结束 mh_door_only 会话。</summary>
    public bool TryAutoEndDoorOnlySession()
    {
        if (!HasActiveDoorOnlySession)
            return false;

        lock (_trackGate)
        {
            if (_openedInSession.Count > 0)
                return false;
        }

        _session.End();
        SessionAutoEnded?.Invoke(this, "格口已关，开门会话已结束。");
        NotifyOperationLockChanged();
        return true;
    }

    /// <summary>根据库/DI 门态清理已过期的会话追踪（门已关但未走 NotifySlotsClosedAsync 时）。</summary>
    public void ReconcileSessionTracking(IReadOnlyList<SlotDoorState> slots,
        IReadOnlyDictionary<string, WireCabinet.Slots.Hardware.SlotHardwareSnapshot> snapshots,
        bool hardwareConfigured)
    {
        if (!HasActiveDoorOnlySession)
            return;

        var changed = false;
        lock (_trackGate)
        {
            if (_openedInSession.Count == 0)
            {
                TryAutoEndDoorOnlySession();
                return;
            }

            var stale = new List<long>();
            foreach (var id in _openedInSession)
            {
                var slot = slots.FirstOrDefault(s => s.SlotId == id);
                if (slot is null)
                {
                    stale.Add(id);
                    continue;
                }

                snapshots.TryGetValue(slot.SlotNo, out var hw);
                if (!MhOperationLock.IsSlotBlockingOpen(slot, hw, hardwareConfigured))
                    stale.Add(id);
            }

            foreach (var id in stale)
            {
                _openedInSession.Remove(id);
                changed = true;
            }
        }

        if (changed)
            NotifyOperationLockChanged();

        TryAutoEndDoorOnlySession();
    }

    /// <summary>维护视图试开锁：不占焊丝操作会话；禁用格亦可试开（需已接线）。</summary>
    public async Task<(bool Ok, string Message)> OpenSingleMaintTrialAsync(string slotNoInput, CancellationToken ct = default)
    {
        var dto = await ResolveSlotDtoAsync(slotNoInput, ct, includeDisabled: true);
        if (dto is null)
            return (false, $"未找到格口「{slotNoInput}」。");
        if (!dto.IoWired)
            return (false, $"格口「{dto.SlotNo}」未接线，不可试开。");

        var result = await _slots.OpenSlotAsync(new OpenSlotRequest
        {
            SlotNo = dto.SlotNo,
            Source = "maint_trial",
            OperationLabel = $"试开格口 {dto.SlotNo}"
        }, ct);

        // 试开也要留痕（§ 矩阵第 7 项）。维护界面没有工号输入，故只记角色。
        if (result.Success)
            RecordDoorOpened(SlotDoorActionLog.MaintTrialOpen, result, DoorOperator.Maint());
        else
            _doorLog.RecordFailed(SlotDoorActionLog.MaintTrialOpen, dto.SlotNo, result.Message, DoorOperator.Maint());

        return result.Success
            ? (true, $"试开 {result.SlotNo} 成功（仅门/锁）。")
            : (false, result.Message);
    }

    public async Task<(bool Ok, string Message)> OpenSingleAsync(string slotNoInput, CancellationToken ct = default)
    {
        if (!EnsureNoOpenDoors(out var doorMsg))
            return (false, doorMsg);

        if (!EnsureSession(out var sessionMsg))
            return (false, sessionMsg);

        var dto = await ResolveSlotDtoAsync(slotNoInput, ct, includeDisabled: true);
        if (dto is null)
            return (false, $"未找到格口「{slotNoInput}」。");
        if (!dto.IoWired)
            return (false, $"格口「{dto.SlotNo}」未接线，不可打开。");
        if (!CanOpenForMhDoor(dto))
            return (false, $"格口「{dto.SlotNo}」已禁用且无料，不可打开。");

        var result = await _slots.OpenSlotAsync(new OpenSlotRequest
        {
            SlotNo = dto.SlotNo,
            Source = SessionFlowId,
            OperationLabel = $"打开指定格口 {dto.SlotNo}"
        }, ct);

        if (result.Success)
        {
            // 开一格、取走一格 = 一条「MH 取出旧焊丝」；被取走的批号与门时刻都在这条上。
            RecordDoorOpened(SlotDoorActionLog.MhTakeWire, result, CurrentOperator,
                clearedLot: ReadClearedLotAtOpen(result.SlotId));
            TrackOpenedSlot(result.SlotId);
            NotifySlotStateChanged();
        }
        else
        {
            // 开锁失败也留一条（§ 矩阵第 9 项）
            _doorLog.RecordFailed(SlotDoorActionLog.MhTakeWire, dto.SlotNo, result.Message, CurrentOperator);
        }

        return result.Success
            ? (true, $"已打开格口 {result.SlotNo}（仅门/锁状态）。")
            : (false, result.Message);
    }

    public Task<(bool Ok, string Message)> OpenAllSlotsAsync(CancellationToken ct = default) =>
        OpenFilteredBatchAsync("all_slots", "打开所有非禁用和有料格口", ct);

    public Task<(bool Ok, string Message)> OpenAvailableWireSlotsAsync(CancellationToken ct = default) =>
        OpenFilteredBatchAsync("available_wire_slots", "打开可用焊丝格口", ct);

    public Task<(bool Ok, string Message)> OpenReturnedWireSlotsAsync(CancellationToken ct = default) =>
        OpenFilteredBatchAsync("returned_wire_slots", "打开所有归还焊丝格口", ct);

    /// <summary>维护视图批量开门：不占焊丝操作会话，不改 biz_state。</summary>
    public Task<(bool Ok, string Message)> OpenAllMaintTrialAsync(CancellationToken ct = default) =>
        OpenFilteredBatchMaintAsync("all_slots", MaintSideFilter.All, "打开所有格口", ct);

    public Task<(bool Ok, string Message)> OpenFrontMaintTrialAsync(CancellationToken ct = default) =>
        OpenFilteredBatchMaintAsync("all_slots", MaintSideFilter.Front, "打开前柜所有格口", ct);

    public Task<(bool Ok, string Message)> OpenRearMaintTrialAsync(CancellationToken ct = default) =>
        OpenFilteredBatchMaintAsync("all_slots", MaintSideFilter.Rear, "打开后柜所有格口", ct);

    private enum MaintSideFilter { All, Front, Rear }

    public async Task<(bool Ok, string Message)> CloseSingleAsync(long slotId, CancellationToken ct = default)
    {
        var ok = await _slots.ConfirmDoorClosedAsync(slotId, ct);
        if (ok)
            await NotifySlotsClosedAsync([slotId], ct);
        return ok
            ? (true, $"格口 {slotId} 已确认关门（仅门/锁状态）。")
            : (false, "关门确认失败：锁反馈未闭合或格口不存在。");
    }

    private async Task<(bool Ok, string Message)> OpenFilteredBatchAsync(string filter, string operationLabel, CancellationToken ct)
    {
        if (!EnsureNoOpenDoors(out var doorMsg))
            return (false, doorMsg);

        if (!EnsureSession(out var sessionMsg))
            return (false, sessionMsg);

        var codes = await ListOpenableSlotCodesAsync(filter, ct);
        if (codes.Count == 0)
            return (true, "没有符合条件的已接线格口（禁用空格口不可打开）。");

        var opened = 0;

        // 逐格一条「MH 取出旧焊丝」：打开 53 格就是 53 条（口径收敛 2026-10-09）。
        // 记录与登记都在回调里当场做，见 RecordDoorOpened 的说明。
        var batch = await _slots.OpenSlotsBatchAsync(
            codes,
            new BatchOpenOptions { IntervalMs = 400, StopOnFailure = true },
            new OpenSlotRequest { Source = SessionFlowId, OperationLabel = operationLabel },
            ct,
            r =>
            {
                if (r.Success)
                {
                    RecordDoorOpened(SlotDoorActionLog.MhTakeWire, r, CurrentOperator,
                        clearedLot: ReadClearedLotAtOpen(r.SlotId));
                    TrackOpenedSlot(r.SlotId);
                    opened++;
                }
                else if (!string.IsNullOrWhiteSpace(r.SlotNo))
                    _doorLog.RecordFailed(SlotDoorActionLog.MhTakeWire, r.SlotNo, r.Message, CurrentOperator);
            });

        var failed = batch.Results.FirstOrDefault(r => !r.Success);
        if (failed is not null)
            return (false, $"批量开门中止：{failed.Message}（已成功 {opened}/{codes.Count}）");

        if (opened > 0)
            NotifySlotStateChanged();

        return (true, $"已打开 {opened} 个格口（仅门/锁状态）。");
    }

    private async Task<(bool Ok, string Message)> OpenFilteredBatchMaintAsync(
        string filter, MaintSideFilter side, string operationLabel, CancellationToken ct)
    {
        var codes = await ListMaintTrialSlotCodesAsync(filter, ct);
        if (side != MaintSideFilter.All)
        {
            var filtered = new List<string>();
            foreach (var code in codes)
            {
                var dto = await FindBySlotNoAsync(code, ct, includeDisabled: true);
                if (dto is null) continue;
                if (side == MaintSideFilter.Front && CabinetSlotGridController.IsFrontSlot(dto.SlotId, dto.SlotNo))
                    filtered.Add(code);
                else if (side == MaintSideFilter.Rear && CabinetSlotGridController.IsRearSlot(dto.SlotId, dto.SlotNo))
                    filtered.Add(code);
            }
            codes = filtered;
        }

        if (codes.Count == 0)
        {
            var emptyMsg = side switch
            {
                MaintSideFilter.Front => "没有符合条件的前柜已接线格口。",
                MaintSideFilter.Rear => "没有符合条件的后柜已接线格口。",
                _ => "没有符合条件的已接线格口。"
            };
            return (true, emptyMsg);
        }

        var opened = 0;

        // 逐格一条「维护试开格口」；维护不改库存，故不纳入「待关门清空」的会话跟踪，
        // 但仍逐格登记，以便关门时回填门时刻。
        var batch = await _slots.OpenSlotsBatchAsync(
            codes,
            new BatchOpenOptions { IntervalMs = 400, StopOnFailure = true },
            new OpenSlotRequest { Source = "maint_trial", OperationLabel = operationLabel },
            ct,
            r =>
            {
                if (r.Success)
                {
                    RecordDoorOpened(SlotDoorActionLog.MaintTrialOpen, r, DoorOperator.Maint());
                    opened++;
                }
                else if (!string.IsNullOrWhiteSpace(r.SlotNo))
                    _doorLog.RecordFailed(SlotDoorActionLog.MaintTrialOpen, r.SlotNo, r.Message, DoorOperator.Maint());
            });

        var failed = batch.Results.FirstOrDefault(r => !r.Success);
        if (failed is not null)
            return (false, $"批量开门中止：{failed.Message}（已成功 {opened}/{codes.Count}）");

        return (true, $"已打开 {opened} 个格口（仅门/锁，未改库存）。");
    }

    private Task<List<string>> ListOpenableSlotCodesAsync(string filter, CancellationToken ct) =>
        ListSlotCodesAsync(filter, ct, SlotListPolicy.MhDoor);

    /// <summary>维护批量试开：含禁用格，仅要求已接线。</summary>
    private Task<List<string>> ListMaintTrialSlotCodesAsync(string filter, CancellationToken ct) =>
        ListSlotCodesAsync(filter, ct, SlotListPolicy.MaintTrial);

    private enum SlotListPolicy
    {
        /// <summary>MH 开门：已启用，或已禁用但有可取焊丝。</summary>
        MhDoor,
        /// <summary>维护试开：所有已接线格口（含禁用空格）。</summary>
        MaintTrial
    }

    private async Task<List<string>> ListSlotCodesAsync(string filter, CancellationToken ct, SlotListPolicy policy)
    {
        var item = _catalog.Find("app.slot.list_by_filter");
        if (item is null)
            return [];

        var result = _appDb.Run(item, new Dictionary<string, object?> { ["filter"] = filter });
        var codes = new List<string>();
        foreach (var row in result.Rows)
        {
            var id = ToLong(row.GetValueOrDefault("slot_id"));
            if (id <= 0) continue;
            var dto = await _slots.GetSlotStatusAsync(id, ct);
            if (dto is null || !dto.IoWired)
                continue;
            if (policy == SlotListPolicy.MhDoor)
            {
                if (!CanOpenForMhDoor(dto))
                    continue;
            }
            codes.Add(dto.SlotNo);
        }
        return codes;
    }

    private static bool CanOpenForMhDoor(SlotStatusDto dto) =>
        dto.IsEnabled || HasRetrievableWire(dto.BizState);

    private static bool HasRetrievableWire(string bizState) =>
        bizState is "available_wire" or "returned_wire";

    private async Task<SlotStatusDto?> ResolveSlotDtoAsync(string input, CancellationToken ct, bool includeDisabled = false)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        var candidates = new List<string> { trimmed };
        if (!trimmed.Contains('-', StringComparison.Ordinal))
        {
            candidates.Add($"F-{trimmed}");
            candidates.Add($"R-{trimmed}");
        }

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dto = await FindBySlotNoAsync(c, ct, includeDisabled);
            if (dto is not null) return dto;
        }
        return null;
    }

    private async Task<SlotStatusDto?> FindBySlotNoAsync(string slotNo, CancellationToken ct, bool includeDisabled = false)
    {
        var all = await _slots.ListSlotsAsync(new SlotListFilter { EnabledOnly = !includeDisabled }, ct);
        return all.FirstOrDefault(s => string.Equals(s.SlotNo, slotNo, StringComparison.OrdinalIgnoreCase));
    }

    private static bool EnsureNoOpenDoors(out string message)
    {
        var block = MhOperationLock.Evaluate(reloadDb: true);
        message = block.Message;
        return !block.IsBlocked;
    }

    private void NotifySlotStateChanged()
    {
        try
        {
            App.Bootstrap.FlowSlots.Reload();
        }
        catch
        {
            // ignore
        }

        NotifyOperationLockChanged();
    }

    private void NotifyOperationLockChanged() =>
        OperationLockChanged?.Invoke(this, EventArgs.Empty);

    private bool EnsureSession(out string message)
    {
        if (_session.HasActiveSession &&
            !string.Equals(_session.ActiveFlowId, SessionFlowId, StringComparison.OrdinalIgnoreCase))
        {
            message = "请先完成或退出当前操作。";
            return false;
        }

        if (_session.HasActiveSession)
        {
            message = "";
            return true;
        }

        return _session.TryBegin(SessionFlowId, out message);
    }

    private static long ToLong(object? value) =>
        value switch
        {
            long l => l,
            int i => i,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) => x,
            _ => 0
        };
}
