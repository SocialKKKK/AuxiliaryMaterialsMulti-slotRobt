using AgvDispatch.Sdk;
using AgvDispatch.Sdk.Models;
namespace WireCabinet.Rcs;

public enum AgvPendingOrderKind
{
    Charge,
    Move
}

/// <summary>统一订单门禁：本地 pending + RCS 快照，防止队列等待期间重复下单。</summary>
public sealed class AgvOrderGate
{
    private readonly AgvStateThresholds _thresholds;
    private readonly Func<bool> _callInProgress;
    private readonly AgvDispatchOptions? _options;
    private string? _cancelRequestedOrderId;
    private bool _cancelWasAssigned;
    private bool _unconfirmedDispatch;

    private string? _localPendingOrderId;
    private AgvPendingOrderKind? _localPendingKind;
    private OrderDetailDto? _cachedPendingDetail;

    public AgvOrderGate(AgvStateThresholds thresholds, Func<bool>? callInProgress = null, AgvDispatchOptions? options = null)
    {
        _thresholds = thresholds;
        _callInProgress = callInProgress ?? (() => false);
        _options = options;
    }

    public string? LocalPendingOrderId => _localPendingOrderId;
    public AgvPendingOrderKind? LocalPendingKind => _localPendingKind;
    public string? LastReconcileError { get; private set; }
    public bool HasUnconfirmedDispatch => _unconfirmedDispatch;

    public void RecordPlacedOrder(string? orderId, AgvPendingOrderKind kind)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            _unconfirmedDispatch = true;
            LastReconcileError = "RCS 未返回订单号，等待车辆确认订单；禁止重复下单。";
            return;
        }

        _localPendingOrderId = orderId;
        _localPendingKind = kind;
        _cachedPendingDetail = null;
        LastReconcileError = null;
        _unconfirmedDispatch = false;
    }

    public void ClearPending()
    {
        _localPendingOrderId = null;
        _localPendingKind = null;
        _cachedPendingDetail = null;
        LastReconcileError = null;
    }

    public bool HasBlockingOrder(VehicleSnapshot snap)
    {
        if (_unconfirmedDispatch) return true;
        if (!string.IsNullOrWhiteSpace(snap.Vehicle.OrderTaskId))
            return true;

        if (!string.IsNullOrWhiteSpace(_localPendingOrderId))
            return true;

        return snap.Order is not null && !IsTerminal(snap.Order);
    }

    public bool BlocksNewOrder(VehicleSnapshot snap, AgvPendingOrderKind? placingKind = null)
        => GetBlockReason(snap, placingKind) is not null;

    public string? GetBlockReason(VehicleSnapshot snap, AgvPendingOrderKind? placingKind = null)
    {
        if (_callInProgress())
            return "下单请求正在执行，请稍候。";

        if (!snap.Vehicle.Enable)
            return "车辆未在线或不可调度。";
        if (AgvReadinessPolicy.HasEmergency(snap.Vehicle, _thresholds))
            return "车辆急停未解除。";

        if (HasBlockingOrder(snap))
            return LastReconcileError ?? (_cancelRequestedOrderId is not null
                ? "充电任务取消中，等待 RCS 释放订单。"
                : "已有进行中或队列中的订单，请先取消或等待完成。");

        var sys = snap.Vehicle.SysState ?? "";
        var chargingDeparture = placingKind == AgvPendingOrderKind.Move
            && _thresholds.ChargingSysStates.Contains(sys, StringComparer.OrdinalIgnoreCase);
        if (!_thresholds.IdleSysStates.Contains(sys, StringComparer.OrdinalIgnoreCase) && !chargingDeparture)
            return $"车辆状态 {sys} 不允许下单。";

        return null;
    }

    public bool CanEndChargingOrder(VehicleSnapshot snap) =>
        _options is not null && snap.Vehicle.Enable
        && !AgvReadinessPolicy.HasEmergency(snap.Vehicle, _thresholds)
        && _thresholds.ChargingSysStates.Contains(snap.Vehicle.SysState ?? "", StringComparer.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(snap.Vehicle.OrderTaskId)
        && !_unconfirmedDispatch
        && (_localPendingOrderId is null || _localPendingOrderId == snap.Vehicle.OrderTaskId)
        && snap.Order?.Mission is { } mission
        && mission.Any(m => m.Type == "act" && m.ActionId == _options.ChargeActionId)
        && mission.Any(m => m.Type == "move" && m.Destination == _options.ChargeDestination)
        && !IsTerminal(snap.Order);

    // 取消是异步命令：成功响应后仍保留订单门禁，直到下一份快照确认订单已释放。
    public async Task<bool> TryEndChargingOrderAsync(IAgvDispatchClient client, VehicleSnapshot snap, CancellationToken ct)
    {
        if (!CanEndChargingOrder(snap) || _callInProgress())
            return false;
        var id = snap.Vehicle.OrderTaskId!;
        if (_cancelRequestedOrderId == id)
            return false;
        await client.CancelOrderAsync(id, ct).ConfigureAwait(false);
        RecordCancellationRequested(id, wasAssigned: true);
        return true;
    }

    public void RecordCancellationRequested(string id, bool wasAssigned = true)
    {
        _cancelRequestedOrderId = id;
        _cancelWasAssigned = wasAssigned;
    }
    public bool CancellationRequested(string id) => _cancelRequestedOrderId == id;

    public void ApplySnapshot(VehicleSnapshot snap)
    {
        var taskId = snap.Vehicle.OrderTaskId;
        if (_unconfirmedDispatch && !string.IsNullOrWhiteSpace(taskId))
        {
            _unconfirmedDispatch = false;
            LastReconcileError = null;
        }
        if (string.IsNullOrWhiteSpace(taskId) && _cancelRequestedOrderId is not null && _cancelWasAssigned)
        {
            if (_localPendingOrderId == _cancelRequestedOrderId)
                ClearPending();
            _cancelRequestedOrderId = null;
        }
        if (!string.IsNullOrWhiteSpace(taskId)
            && string.Equals(taskId, _localPendingOrderId, StringComparison.OrdinalIgnoreCase))
            ClearPending();
    }

    public async Task ReconcilePendingAsync(IAgvDispatchClient client, VehicleSnapshot snap, CancellationToken ct = default)
    {
        ApplySnapshot(snap);

        if (string.IsNullOrWhiteSpace(_localPendingOrderId))
            return;

        var pendingId = _localPendingOrderId;
        try
        {
            var detail = await client.GetOrderDetailAsync(pendingId, ct).ConfigureAwait(false);
            if (_localPendingOrderId != pendingId) return;
            _cachedPendingDetail = detail;
            LastReconcileError = null;
            if (IsTerminal(detail))
            {
                ClearPending();
                if (_cancelRequestedOrderId == pendingId) _cancelRequestedOrderId = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (_localPendingOrderId == pendingId)
                LastReconcileError = $"订单 {pendingId} 状态查询失败，保留发车联锁：{ex.Message}";
        }
    }

    public string FormatOrderStateDisplay(VehicleSnapshot snap)
    {
        if (_unconfirmedDispatch) return "等待 RCS 确认订单";
        if (snap.Order is not null)
        {
            var state = snap.Order.EffectiveOrderState;
            if (state == _thresholds.OrderStatePending)
                return "队列中";
            if (state == _thresholds.OrderStateCanceled)
                return "已取消（待清理）";

            var move = snap.Vehicle.EffectiveMoveState ?? "";
            if (move.Contains("PAUSED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(snap.Vehicle.SysState, "PAUSE", StringComparison.OrdinalIgnoreCase))
                return "已暂停";

            return "执行中";
        }

        if (!string.IsNullOrWhiteSpace(_localPendingOrderId))
            return LastReconcileError is not null ? "订单状态查询失败" : "等待订单确认";

        return "—";
    }

    public string FormatTaskSummary(VehicleSnapshot snap, Func<int, string> formatPosition)
    {
        if (_unconfirmedDispatch) return LastReconcileError ?? "等待 RCS 确认订单";
        var v = snap.Vehicle;
        if (!string.IsNullOrWhiteSpace(v.OrderTaskId))
            return FormatRcsTask(v, snap.Order, formatPosition, _thresholds.OrderStatePending);

        if (!string.IsNullOrWhiteSpace(_localPendingOrderId))
        {
            var kind = _localPendingKind == AgvPendingOrderKind.Charge ? "充电单" : "移动单";
            return $"{kind} {_localPendingOrderId}（队列中，等待执行）";
        }

        return "无进行中任务";
    }

    public bool CanCancelOrder(VehicleSnapshot snap)
    {
        if (_cancelRequestedOrderId is not null) return false;
        if (CanEndChargingOrder(snap)) return true;
        if (!string.IsNullOrWhiteSpace(snap.Vehicle.OrderTaskId))
        {
            var v = snap.Vehicle;
            if (v.EffectiveMoveState?.Contains("PAUSED", StringComparison.OrdinalIgnoreCase) ?? false)
                return true;
            if (string.Equals(v.SysState, "PAUSE", StringComparison.OrdinalIgnoreCase))
                return true;
            if (snap.Order is not null && snap.Order.EffectiveOrderState == _thresholds.OrderStatePending)
                return true;
            return false;
        }

        return !string.IsNullOrWhiteSpace(_localPendingOrderId);
    }

    public string? ResolveCancelOrderId(VehicleSnapshot snap) =>
        !string.IsNullOrWhiteSpace(snap.Vehicle.OrderTaskId)
            ? snap.Vehicle.OrderTaskId
            : _localPendingOrderId;

    private static string FormatRcsTask(VehicleInfoDto v, OrderDetailDto? order, Func<int, string> formatPosition, int orderStatePending)
    {
        var dest = order?.Mission?.FirstOrDefault(m =>
            string.Equals(m.Type, "move", StringComparison.OrdinalIgnoreCase))?.Destination;
        if (dest is null or 0)
            dest = v.EndStationNo > 0 ? v.EndStationNo : null;

        var name = !string.IsNullOrWhiteSpace(v.EndStationName)
            ? v.EndStationName
            : dest is int d ? formatPosition(d) : "—";

        var orderLabel = string.IsNullOrWhiteSpace(v.OrderName) ? v.OrderTaskId : $"{v.OrderName}（{v.OrderTaskId}）";
        var queueHint = order?.EffectiveOrderState == orderStatePending ? "（队列中）" : "";

        return dest is int station
            ? $"{orderLabel}{queueHint} → {name}（站点 {station}）"
            : $"{orderLabel}{queueHint} → {name}";
    }

    private bool IsTerminal(OrderDetailDto detail) =>
        !string.IsNullOrWhiteSpace(detail.EffectiveDoneTime)
        || detail.EffectiveOrderState == _thresholds.OrderStateCanceled
        || _thresholds.CompletedOrderStates.Contains(detail.EffectiveOrderState);
}
