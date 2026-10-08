using AgvDispatch.Sdk;
using AgvDispatch.Sdk.Models;
using AgvDispatch.Sdk.Operations;

namespace WireCabinet.Rcs;

/// <summary>0.1：充电中仍允许控车（放宽 Sample 对 CHARGING 的早退）。</summary>
public static class AgvReadinessPolicy
{
    public static AgvReadinessResult Evaluate(
        VehicleInfoDto vehicle,
        OrderDetailDto? order,
        DoorStateInput doors,
        AgvStateThresholds thresholds,
        bool previouslyPausedForDoor,
        bool callInProgress)
    {
        var baseResult = AgvOperationEvaluator.Evaluate(vehicle, order, doors, thresholds, previouslyPausedForDoor, callInProgress);
        if (HasEmergency(vehicle, thresholds))
            return new AgvReadinessResult { Reason = "车辆急停未解除，禁止下单。" };

        if (!thresholds.ChargingSysStates.Contains(vehicle.SysState ?? "", StringComparer.OrdinalIgnoreCase))
            return baseResult;

        if (!doors.AllDoorsClosed || doors.AnyDoorOpen || previouslyPausedForDoor
            || !string.IsNullOrWhiteSpace(vehicle.OrderTaskId) || baseResult.RecommendsCancelOrder)
            return baseResult;

        // 只放宽系统的充电态，仍由 SDK 检查在线、移动/进程就绪、门态与呼叫互斥。
        var departure = AgvOperationEvaluator.Evaluate(new VehicleInfoDto
        {
            Enable = vehicle.Enable,
            SysState = thresholds.IdleSysStates.FirstOrDefault() ?? "IDLE",
            ActionState = vehicle.ActionState,
            MovementState = vehicle.MovementState,
            ProcState = vehicle.ProcState
        }, order, doors, thresholds, callInProgress: callInProgress);
        return departure;
    }

    public static bool HasEmergency(VehicleInfoDto vehicle, AgvStateThresholds thresholds) =>
        !string.IsNullOrWhiteSpace(vehicle.EmergencyState)
        && thresholds.EmergencyActiveContains.Any(s => vehicle.EmergencyState.Contains(s, StringComparison.OrdinalIgnoreCase));
}
