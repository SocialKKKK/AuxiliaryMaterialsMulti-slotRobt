using System.IO;
using AgvDispatch.Sdk.Operations;
using WireCabinet.Hmi.Services;
using WireCabinet.Tests;
using Xunit;

namespace WireCabinet.Hmi.Tests;

public class AgvServicesTests
{
    [Fact]
    public async Task IdleWithBusyProcessCannotStartCharging()
    {
        var client = new FakeAgvClient { Snapshot = FakeAgvClient.Ready("IDLE", 20, 22) };
        client.Snapshot.Vehicle.ProcState = "BUSY";
        using var service = Service(client);
        await service.ConnectAsync();
        Assert.False((await service.GoChargeAsync()).Ok);
        Assert.Equal(0, client.Charges);
    }

    private static AgvServices Service(FakeAgvClient client, FakeAgvSlots? slots = null)
    {
        slots ??= new();
        return new(client, FakeAgvClient.Options(), new() { LowBatteryPercent = 30, FullBatteryPercent = 90 }, FakeAgvClient.Stations(), slots, slots);
    }

    [Fact]
    public async Task ChargingWithoutOrderAllowsManualMoveAndKeepsChargeButtonDisabled()
    {
        var client = new FakeAgvClient(); using var service = Service(client);
        Assert.True(await service.ConnectAsync());
        Assert.True(service.CanPlaceMoveOrder); Assert.False(service.CanPlaceChargeOrder);
        Assert.True((await service.MoveToStationAsync(22)).Ok);
        Assert.Equal(22, Assert.Single(client.Moves));
        Assert.False(service.Charge!.PostChargeReturnPending);
    }

    [Fact]
    public async Task ChargingAtThresholdAutomaticallyReturnsOnce()
    {
        var client = new FakeAgvClient { Snapshot = FakeAgvClient.Ready("CHARGING", 90) };
        using var service = Service(client);
        await service.PollCycleAsync(null, null);
        await service.PollCycleAsync(null, null);
        Assert.Equal(11, Assert.Single(client.Moves));
        Assert.Equal(30, service.LowBatteryPercent); Assert.Equal(90, service.FullBatteryPercent);
    }

    [Fact]
    public async Task ManualChargingCancellationKeepsSelectedDestinationAndDoesNotSendEarly()
    {
        var client = new FakeAgvClient(); client.SetChargingOrder();
        using var service = Service(client); await service.ConnectAsync();
        Assert.True(service.CanPlaceMoveOrder); Assert.True(service.CanCancelActiveOrder);
        Assert.True((await service.MoveToStationAsync(22)).Ok);
        await service.PollCycleAsync(null, null);
        Assert.Single(client.Cancellations); Assert.Empty(client.Moves);
        client.Snapshot = FakeAgvClient.Ready("CHARGING");
        await service.PollCycleAsync(null, null);
        Assert.Equal(22, Assert.Single(client.Moves));
        await service.PollCycleAsync(null, null);
        Assert.Single(client.Moves);
    }

    [Fact]
    public async Task AutomaticReturnEndsRecognizedChargingOrderOnceThenWaitsForRelease()
    {
        var client = new FakeAgvClient(); client.SetChargingOrder();
        using var service = Service(client);
        await service.PollCycleAsync(null, null); await service.PollCycleAsync(null, null);
        Assert.Single(client.Cancellations); Assert.Empty(client.Moves);
        client.Snapshot = FakeAgvClient.Ready("CHARGING");
        await service.PollCycleAsync(null, null);
        Assert.Equal(11, Assert.Single(client.Moves));
    }

    [Fact]
    public async Task UserCanWithdrawDepartureWhileChargingOrderIsBeingReleased()
    {
        var client = new FakeAgvClient(); client.SetChargingOrder();
        using var service = Service(client); await service.ConnectAsync();
        await service.MoveToStationAsync(22);
        Assert.True((await service.CancelActiveOrderAsync()).Ok);
        client.Snapshot = FakeAgvClient.Ready("CHARGING");
        await service.PollCycleAsync(null, null);
        Assert.Empty(client.Moves);
    }

    [Fact]
    public async Task SnapshotFailureDisablesDispatchAndCanReconnectWithoutLosingReturnIntent()
    {
        var client = new FakeAgvClient(); using var service = Service(client); await service.ConnectAsync();
        client.SnapshotError = new IOException("断线");
        Assert.False((await service.MoveToStationAsync(22)).Ok);
        Assert.False(service.CanPlaceMoveOrder); Assert.False(service.IsConnected);
        Assert.Empty(client.Moves);
        client.SnapshotError = null;
        await service.PollCycleAsync(null, null);
        Assert.True(service.IsConnected); Assert.Single(client.Moves);
    }

    [Fact]
    public async Task UserCancellationDoesNotPretendRcsHasAlreadyReleasedOrder()
    {
        var client = new FakeAgvClient(); client.SetChargingOrder();
        using var service = Service(client); await service.ConnectAsync();
        Assert.True((await service.CancelActiveOrderAsync()).Ok);
        Assert.True(service.HasBlockingOrder);
        Assert.Empty(client.Moves);
        client.Snapshot = FakeAgvClient.Ready("CHARGING");
        await service.RefreshStatusAsync(); Assert.False(service.HasBlockingOrder);
    }

    [Fact]
    public async Task SimultaneousManualAndAutomaticRequestsDoNotCreateTwoOrders()
    {
        var client = new FakeAgvClient(); using var service = Service(client); await service.ConnectAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeMove = async () => { entered.TrySetResult(); await release.Task; };
        var move = service.MoveToStationAsync(22);
        await entered.Task;
        var automatic = service.PollCycleAsync(null, null);
        var repeated = service.MoveToStationAsync(11);
        release.SetResult();
        await Task.WhenAll(move, automatic, repeated);
        Assert.Equal(22, Assert.Single(client.Moves));
    }

    [Fact]
    public async Task ExternalChargeStartedAfterStartupIsDetectedAndReturnsToPreviousStation()
    {
        var client = new FakeAgvClient { Snapshot = FakeAgvClient.Ready(position: 22) };
        using var service = Service(client);
        await service.PollCycleAsync(null, null);
        client.Snapshot = FakeAgvClient.Ready("CHARGING", 89);
        await service.PollCycleAsync(null, null); Assert.Empty(client.Moves);
        client.Snapshot.Vehicle.Battery = 90;
        await service.PollCycleAsync(null, null); Assert.Equal(22, Assert.Single(client.Moves));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("unknown")]
    [InlineData("unlocking")]
    public async Task DoorInterlockAppliesToBothManualAndAutomaticDeparture(string condition)
    {
        var client = new FakeAgvClient(); var slots = new FakeAgvSlots();
        if (condition == "open") slots.Doors = new() { AnyDoorOpen = true };
        if (condition == "unknown") slots.Doors = new();
        if (condition == "unlocking") slots.Unlocking = true;
        using var service = Service(client, slots); await service.ConnectAsync();
        Assert.False((await service.MoveToStationAsync(22)).Ok);
        await service.PollCycleAsync(null, null);
        Assert.Empty(client.Moves);
    }

    [Fact]
    public async Task StartupDoesNotCancelAnOrderJustBecauseVehicleReportsIdle()
    {
        var client = new FakeAgvClient { Snapshot = FakeAgvClient.Ready() };
        client.Snapshot.Vehicle.OrderTaskId = "queued";
        client.Snapshot.Order = new() { OrderState = 1 };
        using var service = Service(client); await service.PollCycleAsync(null, null);
        Assert.Empty(client.Cancellations); Assert.Empty(client.Moves);
    }

    [Fact]
    public async Task RejectedChargeCancellationLeavesOrderIntactAndDoesNotCrashHmi()
    {
        var client = new FakeAgvClient { CancelError = new IOException("拒绝取消") }; client.SetChargingOrder();
        using var service = Service(client); await service.ConnectAsync();
        var result = await service.MoveToStationAsync(22);
        Assert.False(result.Ok); Assert.Contains("拒绝取消", result.Message);
        Assert.True(service.HasBlockingOrder); Assert.Empty(client.Moves);
    }
}
