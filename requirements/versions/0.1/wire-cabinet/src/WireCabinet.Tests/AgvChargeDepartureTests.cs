using AgvDispatch.Sdk;
using AgvDispatch.Sdk.Models;
using AgvDispatch.Sdk.Operations;
using WireCabinet.Core.Config;
using WireCabinet.Rcs;
using Xunit;

namespace WireCabinet.Tests;

public class AgvChargeDepartureTests
{
    [Theory]
    [InlineData("IDLE")]
    [InlineData("CHARGING")]
    public void ReadyVehicleCanMoveButChargingCannotCreateAnotherCharge(string sys)
    {
        var snap = FakeAgvClient.Ready(sys);
        var gate = new AgvOrderGate(new());
        Assert.False(gate.BlocksNewOrder(snap, AgvPendingOrderKind.Move));
        Assert.Equal(sys == "CHARGING", gate.BlocksNewOrder(snap, AgvPendingOrderKind.Charge));
        Assert.True(AgvReadinessPolicy.Evaluate(snap.Vehicle, null, new() { AllDoorsClosed = true }, new(), false, false).CanAcceptOrder);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("order")]
    [InlineData("emergency")]
    [InlineData("moving")]
    [InlineData("busy")]
    [InlineData("unknown-door")]
    [InlineData("open-door")]
    [InlineData("call")]
    public void ChargingDoesNotOverrideOtherInterlocks(string condition)
    {
        var snap = FakeAgvClient.Ready("CHARGING");
        var doors = new DoorStateInput { AllDoorsClosed = true };
        switch (condition)
        {
            case "offline": snap.Vehicle.Enable = false; break;
            case "order": snap.Vehicle.OrderTaskId = "active"; break;
            case "emergency": snap.Vehicle.EmergencyState = "ESTOP"; break;
            case "moving": snap.Vehicle.ActionState = "AT_RUNNING"; break;
            case "busy": snap.Vehicle.ProcState = "BUSY"; break;
            case "unknown-door": doors = new(); break;
            case "open-door": doors = new() { AnyDoorOpen = true }; break;
        }
        Assert.False(AgvReadinessPolicy.Evaluate(snap.Vehicle, null, doors, new(), false, condition == "call").CanAcceptOrder);
    }

    [Fact]
    public async Task PendingOrderRequiresRealCompletionAndQueryFailuresAreVisible()
    {
        var client = new FakeAgvClient { Snapshot = FakeAgvClient.Ready(), DetailError = new IOException("offline") };
        var gate = new AgvOrderGate(new());
        gate.RecordPlacedOrder("local", AgvPendingOrderKind.Move);
        await gate.ReconcilePendingAsync(client, client.Snapshot);
        Assert.True(gate.BlocksNewOrder(client.Snapshot, AgvPendingOrderKind.Move));
        Assert.Contains("offline", gate.LastReconcileError);
        client.DetailError = null;
        client.Detail = new() { OrderState = 2 }; // 未知非队列状态不等于已完成。
        await gate.ReconcilePendingAsync(client, client.Snapshot);
        Assert.NotNull(gate.LocalPendingOrderId);
        client.Detail.DoneTimeSnake = "2026-10-08 08:00:00";
        await gate.ReconcilePendingAsync(client, client.Snapshot);
        Assert.Null(gate.LocalPendingOrderId);
        Assert.Null(gate.LastReconcileError);
    }

    [Fact]
    public async Task OnlyMatchingAssignmentClearsPendingAndConfiguredCompletionCodesWork()
    {
        var client = new FakeAgvClient();
        var gate = new AgvOrderGate(new() { CompletedOrderStates = [7] });
        gate.RecordPlacedOrder("local", AgvPendingOrderKind.Move);
        client.Snapshot.Vehicle.OrderTaskId = "another";
        gate.ApplySnapshot(client.Snapshot);
        Assert.Equal("local", gate.LocalPendingOrderId);
        client.Detail = new() { OrderState = 7 };
        await gate.ReconcilePendingAsync(client, client.Snapshot);
        Assert.Null(gate.LocalPendingOrderId);
    }

    [Fact]
    public async Task ChargingCancellationIsIssuedOnceAndDoesNotReleaseGateEarly()
    {
        var client = new FakeAgvClient(); client.SetChargingOrder();
        var options = FakeAgvClient.Options();
        var gate = new AgvOrderGate(options.StateThresholds, options: options);
        Assert.True(gate.CanCancelOrder(client.Snapshot));
        Assert.True(await gate.TryEndChargingOrderAsync(client, client.Snapshot, default));
        Assert.False(await gate.TryEndChargingOrderAsync(client, client.Snapshot, default));
        Assert.True(gate.BlocksNewOrder(client.Snapshot, AgvPendingOrderKind.Move));
        Assert.Single(client.Cancellations);
        client.Snapshot = FakeAgvClient.Ready("CHARGING");
        gate.ApplySnapshot(client.Snapshot);
        Assert.False(gate.BlocksNewOrder(client.Snapshot, AgvPendingOrderKind.Move));
    }

    [Fact]
    public async Task WrongMissionAndRejectedCancellationDoNotAuthorizeDeparture()
    {
        var client = new FakeAgvClient(); client.SetChargingOrder();
        var options = FakeAgvClient.Options();
        var gate = new AgvOrderGate(options.StateThresholds, options: options);
        client.Snapshot.Order!.Mission![1].ActionId = 999;
        Assert.False(gate.CanEndChargingOrder(client.Snapshot));
        Assert.False(await gate.TryEndChargingOrderAsync(client, client.Snapshot, default));
        client.SetChargingOrder(); client.CancelError = new IOException("拒绝取消");
        await Assert.ThrowsAsync<IOException>(() => gate.TryEndChargingOrderAsync(client, client.Snapshot, default));
        Assert.True(gate.CanCancelOrder(client.Snapshot));
        Assert.Empty(client.Moves);
    }

    [Fact]
    public async Task ManualChargeDepartureRemembersTargetAndWaitsForRelease()
    {
        var client = new FakeAgvClient(); client.SetChargingOrder();
        var slots = new FakeAgvSlots(); var options = FakeAgvClient.Options();
        var gate = new AgvOrderGate(options.StateThresholds, options: options);
        var loop = new AgvControlLoop(client, options, slots, gate);
        var move = new ManualMoveService(client, options, slots, loop);
        Assert.True((await move.RequestMoveAsync(22)).Ok);
        await move.ContinueChargeDepartureAsync();
        Assert.Single(client.Cancellations); Assert.Empty(client.Moves);
        client.Snapshot = FakeAgvClient.Ready("CHARGING");
        await move.ContinueChargeDepartureAsync();
        Assert.Equal(22, Assert.Single(client.Moves));
        Assert.False(move.WaitingForChargeDeparture);
        await loop.TickAsync(22);
        Assert.Single(client.Moves); // RCS 尚未显示新订单也不能重发。
    }

    [Fact]
    public async Task ManualDepartureRechecksDoorAndCanBeWithdrawnWhileWaiting()
    {
        var client = new FakeAgvClient(); client.SetChargingOrder();
        var slots = new FakeAgvSlots(); var options = FakeAgvClient.Options();
        var gate = new AgvOrderGate(options.StateThresholds, options: options);
        var move = new ManualMoveService(client, options, slots, new(client, options, slots, gate));
        await move.RequestMoveAsync(22);
        client.Snapshot = FakeAgvClient.Ready("CHARGING"); slots.Doors = new() { AnyDoorOpen = true };
        await move.ContinueChargeDepartureAsync();
        Assert.Empty(client.Moves);
        move.ResetSession(); slots.Doors = new() { AllDoorsClosed = true };
        await move.ContinueChargeDepartureAsync();
        Assert.Empty(client.Moves);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("unknown")]
    [InlineData("unlocking")]
    public async Task AutomaticReturnAndChargeRequireKnownClosedDoors(string condition)
    {
        var client = new FakeAgvClient(); var slots = new FakeAgvSlots();
        if (condition == "open") slots.Doors = new() { AnyDoorOpen = true };
        if (condition == "unknown") slots.Doors = new();
        if (condition == "unlocking") slots.Unlocking = true;
        var options = FakeAgvClient.Options(); var gate = new AgvOrderGate(options.StateThresholds, options: options);
        var charge = new LowBatteryChargeService(client, options, new() { LowBatteryPercent = 30, FullBatteryPercent = 90 }, gate, FakeAgvClient.Stations(), slots: slots);
        charge.ObserveSnapshot(client.Snapshot);
        await charge.TryReturnToWorkStationAsync(client.Snapshot);
        client.Snapshot = FakeAgvClient.Ready("IDLE", 10);
        await charge.TryAutoChargeAsync(client.Snapshot);
        Assert.Empty(client.Moves); Assert.Equal(0, client.Charges); Assert.NotNull(charge.LastBlockReason);
    }

    [Fact]
    public async Task ExternalChargeTransitionPreservesPreviousStationAndDoesNotRearmEveryTick()
    {
        var client = new FakeAgvClient { Snapshot = FakeAgvClient.Ready(position: 22) }; var options = FakeAgvClient.Options();
        var gate = new AgvOrderGate(options.StateThresholds, options: options);
        var charge = new LowBatteryChargeService(client, options, new() { FullBatteryPercent = 90 }, gate, FakeAgvClient.Stations(), slots: new FakeAgvSlots());
        charge.ObserveSnapshot(client.Snapshot);
        client.Snapshot = FakeAgvClient.Ready("CHARGING", 89);
        charge.ObserveSnapshot(client.Snapshot);
        Assert.Equal(22, charge.ReturnDestination);
        await charge.TryReturnToWorkStationAsync(client.Snapshot); Assert.Empty(client.Moves);
        client.Snapshot.Vehicle.Battery = 90;
        await charge.TryReturnToWorkStationAsync(client.Snapshot);
        Assert.Equal(22, Assert.Single(client.Moves));
        await charge.TryReturnToWorkStationAsync(client.Snapshot); Assert.Single(client.Moves);
        charge.ClearPostChargeReturn(); charge.ObserveSnapshot(client.Snapshot);
        Assert.False(charge.PostChargeReturnPending);
    }

    [Fact]
    public void MovementLockReleasesEvenWhenActiveOrderWasMissedButKeepsPendingRequestsLocked()
    {
        var uiLock = new AgvMovementLock(); uiLock.Set(true);
        uiLock.Refresh(false, true, true); Assert.True(uiLock.IsLocked);
        uiLock.Refresh(true, false, true); Assert.True(uiLock.IsLocked);
        uiLock.Refresh(false, false, false); Assert.True(uiLock.IsLocked);
        uiLock.Refresh(false, false, true); Assert.False(uiLock.IsLocked);
    }

    [Fact]
    public async Task CancellingUnassignedQueueRequiresOrderDetailConfirmation()
    {
        var client = new FakeAgvClient { Snapshot = FakeAgvClient.Ready() };
        var gate = new AgvOrderGate(new());
        gate.RecordPlacedOrder("queued", AgvPendingOrderKind.Move);
        gate.RecordCancellationRequested("queued", wasAssigned: false);
        gate.ApplySnapshot(client.Snapshot);
        await gate.ReconcilePendingAsync(client, client.Snapshot);
        Assert.True(gate.HasBlockingOrder(client.Snapshot));
        client.Detail.OrderState = 9;
        await gate.ReconcilePendingAsync(client, client.Snapshot);
        Assert.False(gate.HasBlockingOrder(client.Snapshot));
    }

    [Fact]
    public void MissingCreatedOrderIdKeepsDispatchBlockedUntilRcsShowsAssignment()
    {
        var gate = new AgvOrderGate(new()); var snap = FakeAgvClient.Ready();
        gate.RecordPlacedOrder(null, AgvPendingOrderKind.Move);
        gate.ApplySnapshot(snap);
        Assert.True(gate.BlocksNewOrder(snap, AgvPendingOrderKind.Move));
        Assert.Contains("未返回订单号", gate.LastReconcileError);
        snap.Vehicle.OrderTaskId = "observed"; gate.ApplySnapshot(snap);
        Assert.False(gate.HasUnconfirmedDispatch);
        Assert.True(gate.HasBlockingOrder(snap));
    }

    [Fact]
    public async Task PendingDepartureAlreadyMovedBySchedulerDoesNotDispatchAnotherOrder()
    {
        var client = new FakeAgvClient(); client.SetChargingOrder();
        var options = FakeAgvClient.Options(); var slots = new FakeAgvSlots();
        var gate = new AgvOrderGate(options.StateThresholds, options: options);
        var move = new ManualMoveService(client, options, slots, new(client, options, slots, gate));
        await move.RequestMoveAsync(22);
        client.Snapshot = FakeAgvClient.Ready(position: 22);
        await move.ContinueChargeDepartureAsync();
        Assert.False(move.WaitingForChargeDeparture); Assert.Empty(client.Moves);
    }
}
