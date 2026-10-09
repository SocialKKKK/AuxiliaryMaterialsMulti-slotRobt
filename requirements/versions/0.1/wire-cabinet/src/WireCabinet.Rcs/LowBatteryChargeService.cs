using AgvDispatch.Sdk;
using AgvDispatch.Sdk.Models;
using WireCabinet.Core.Config;
using WireCabinet.Slots;

namespace WireCabinet.Rcs;

/// <summary>后台轮询电量，低电量下充电单，充满后回作业站或默认物料间。</summary>
public sealed class LowBatteryChargeService
{
    private readonly IAgvDispatchClient _client;
    private readonly AgvDispatchOptions _options;
    private readonly AgvThresholdOptions _thresholds;
    private readonly AgvOrderGate _orderGate;
    private readonly StationConfiguration _stations;

    private bool _postChargeReturnPending;
    private int? _returnDestination;
    private int? _manualWorkStationHint;
    private bool _wasAtCharge;
    private readonly ISlotControlService? _slots;

    /// <summary>充满后自动返航的记录（0.2 §3.1 第 8 项）；为 null 时不记。</summary>
    private readonly AgvActionLog? _agvLog;

    public LowBatteryChargeService(
        IAgvDispatchClient client,
        AgvDispatchOptions options,
        AgvThresholdOptions thresholds,
        AgvOrderGate orderGate,
        StationConfiguration stations,
        AgvActionLog? agvLog = null,
        ISlotControlService? slots = null)
    {
        _agvLog = agvLog;
        _client = client;
        _options = options;
        _thresholds = thresholds;
        _orderGate = orderGate;
        _stations = stations;
        _slots = slots;
    }

    public bool PostChargeReturnPending => _postChargeReturnPending;
    public int? ReturnDestination => _returnDestination;
    public string? LastBlockReason { get; private set; }

    public void ObserveSnapshot(VehicleSnapshot snap)
    {
        var atCharge = IsVehicleCharging(snap.Vehicle) || _stations.IsChargeRelatedPosition(snap.Vehicle.CurrentPosition);
        if (atCharge && !_wasAtCharge && !_postChargeReturnPending)
            RememberReturnDestinationOnChargeDispatch(_manualWorkStationHint ?? snap.Vehicle.CurrentPosition);
        if (!atCharge && _stations.TryGetWorkStationAt(snap.Vehicle.CurrentPosition) is not null)
            RememberWorkStation(snap.Vehicle.CurrentPosition);
        _wasAtCharge = atCharge;
    }

    public void RememberWorkStation(int destination)
    {
        if (_stations.TryGetWorkStationAt(destination) is not null)
            _manualWorkStationHint = destination;
    }

    public void ArmPostChargeReturnToDefault()
    {
        var dest = SanitizeReturnDestination(_stations.ResolveDefaultReturnStation()?.RcsDestination);
        if (dest is null or <= 0)
            return;

        _returnDestination = dest;
        _postChargeReturnPending = true;
    }

    public void ClearPostChargeReturn()
    {
        _postChargeReturnPending = false;
        _returnDestination = null;
    }

    public void RememberReturnDestinationOnChargeDispatch(int currentPosition)
    {
        var dest = SanitizeReturnDestination(_stations.ResolveReturnDestination(currentPosition));
        if (dest is null or <= 0 && _manualWorkStationHint is int hint)
            dest = SanitizeReturnDestination(hint);

        if (dest is null or <= 0)
        {
            ClearPostChargeReturn();
            return;
        }

        _returnDestination = dest;
        _postChargeReturnPending = true;
    }

    public async Task<string?> TryAutoChargeAsync(VehicleSnapshot snap, CancellationToken ct = default)
    {
        LastBlockReason = null;
        var v = snap.Vehicle;
        if (v.Battery > _thresholds.LowBatteryPercent)
            return null;

        if (IsVehicleCharging(v))
            return null;

        LastBlockReason = await GetDispatchBlockAsync(snap, AgvPendingOrderKind.Charge, ct).ConfigureAwait(false);
        if (LastBlockReason is not null)
            return null;

        if (await IsChargeStationOccupiedByOtherAsync(ct).ConfigureAwait(false))
            return null;

        try
        {
            var order = await _client.CreateChargeOrderAsync(cancellationToken: ct).ConfigureAwait(false);
            _orderGate.RecordPlacedOrder(order.OrderId, AgvPendingOrderKind.Charge);
            RememberReturnDestinationOnChargeDispatch(v.CurrentPosition);
            return $"已自动下充电单：{order.OrderId}";
        }
        catch (Exception ex)
        {
            return $"回充下单失败：{ex.Message}";
        }
    }

    public async Task<string?> TryReturnToWorkStationAsync(VehicleSnapshot snap, CancellationToken ct = default)
    {
        LastBlockReason = null;
        if (!_postChargeReturnPending)
            return null;

        if (snap.Vehicle.Battery < _thresholds.FullBatteryPercent)
            return null;

        var dest = SanitizeReturnDestination(_returnDestination);
        if (dest is null or <= 0)
        {
            ClearPostChargeReturn();
            return null;
        }

        if (snap.Vehicle.CurrentPosition == dest)
        {
            ClearPostChargeReturn();
            return null;
        }

        var returnStation = dest.Value;
        try
        {
            LastBlockReason = await GetDispatchBlockAsync(snap, AgvPendingOrderKind.Move, ct).ConfigureAwait(false);
            if (LastBlockReason is not null) return null;
            if (_orderGate.CanEndChargingOrder(snap))
            {
                if (await _orderGate.TryEndChargingOrderAsync(_client, snap, ct).ConfigureAwait(false))
                {
                    _agvLog?.RecordCommand(AgvActionLog.AgvCharge, "返航前请求结束充电", ok: true);
                    return "已请求结束充电，等待订单释放后返航。";
                }
                LastBlockReason = "等待 RCS 释放充电订单后返航。";
                return null;
            }
            var order = await _client.CreateMoveOrderAsync(returnStation, cancellationToken: ct).ConfigureAwait(false);
            _agvLog?.EndAutoFailureStreak();
            _orderGate.RecordPlacedOrder(order.OrderId, AgvPendingOrderKind.Move);
            var name = _stations.TryGetWorkStationAt(returnStation)?.Name ?? returnStation.ToString();
            _agvLog?.RecordCommand(AgvActionLog.AgvMove, "充满电后自动返航", ok: true, detail: $"→ {name}（{order.OrderId}）");
            return $"充满后已返航 {name}（站点 {returnStation}），订单 {order.OrderId}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // 自动路径的同一原因连续失败合并成一条（口径优化 §2 口径③）：不再每轮各记一条。
            _agvLog?.RecordAutoFailure(AgvActionLog.AgvMove, "充满电后自动返航", ex.Message);
            return $"返航失败：{ex.Message}";
        }
    }

    public void BootstrapExternalChargeSession(VehicleSnapshot snap)
    {
        ObserveSnapshot(snap);
    }

    private async Task<string?> GetDispatchBlockAsync(VehicleSnapshot snap, AgvPendingOrderKind kind, CancellationToken ct)
    {
        if (_slots is null) return "门态服务未就绪，禁止自动下单。";
        if (_slots.HasUnlockInProgress()) return "存在开锁中，禁止自动下单。";
        var doors = await _slots.GetDoorStateForAgvAsync(ct).ConfigureAwait(false);
        if (!doors.AllDoorsClosed || doors.AnyDoorOpen) return "格口未全关或门态未知，禁止自动下单。";
        if (kind == AgvPendingOrderKind.Move && _orderGate.CanEndChargingOrder(snap)) return null;
        var gate = _orderGate.GetBlockReason(snap, kind);
        if (gate is not null) return gate;
        var readiness = AgvReadinessPolicy.Evaluate(snap.Vehicle, snap.Order, doors, _options.StateThresholds, false, false);
        return readiness.CanAcceptOrder ? null : readiness.Reason;
    }

    private bool IsVehicleCharging(VehicleInfoDto vehicle) =>
        _options.StateThresholds.ChargingSysStates.Contains(vehicle.SysState ?? "", StringComparer.OrdinalIgnoreCase);

    private int? SanitizeReturnDestination(int? destination)
    {
        if (destination is null or <= 0)
            return null;

        if (_stations.IsChargeRelatedPosition(destination.Value))
            return _stations.ResolveDefaultReturnStation()?.RcsDestination;

        return destination;
    }

    private async Task<bool> IsChargeStationOccupiedByOtherAsync(CancellationToken ct)
    {
        try
        {
            var vehicles = await _client.GetVehiclesAsync(cancellationToken: ct).ConfigureAwait(false);
            var ownKey = _options.DefaultDeviceKey;
            foreach (var v in vehicles)
            {
                if (!string.IsNullOrWhiteSpace(ownKey)
                    && string.Equals(v.DeviceKey, ownKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (_options.ChargeOccupiedPositions.Contains(v.CurrentPosition))
                    return true;

                if (v.EndStationName != null
                    && _options.ChargeOccupiedStationNames.Any(n =>
                        v.EndStationName.Contains(n, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
