using AgvDispatch.Sdk;
using AgvDispatch.Sdk.Models;
using AgvDispatch.Sdk.Operations;
using WireCabinet.Rcs;
using WireCabinet.Slots;

namespace WireCabinet.Tests;

internal sealed class FakeAgvClient : IAgvDispatchClient
{
    public VehicleSnapshot Snapshot { get; set; } = Ready("CHARGING");
    public OrderDetailDto Detail { get; set; } = new() { OrderState = 1 };
    public Exception? SnapshotError { get; set; }
    public Exception? DetailError { get; set; }
    public Exception? CancelError { get; set; }
    public Func<Task>? BeforeMove { get; set; }
    public List<int> Moves { get; } = [];
    public List<string> Cancellations { get; } = [];
    public int Charges { get; private set; }
    public DateTime? TokenObtainedAtUtc => DateTime.UtcNow;
    public Task LoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ForceReLoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<VehicleInfoDto> GetVehicleInfoAsync(string? deviceKey = null, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot.Vehicle);
    public Task<VehicleSnapshot> GetVehicleSnapshotAsync(string? deviceKey = null, CancellationToken cancellationToken = default) => SnapshotError is null ? Task.FromResult(Snapshot) : Task.FromException<VehicleSnapshot>(SnapshotError);
    public Task<OrderDetailDto> GetOrderDetailAsync(string orderId, CancellationToken cancellationToken = default) => DetailError is null ? Task.FromResult(Detail) : Task.FromException<OrderDetailDto>(DetailError);
    public Task<IReadOnlyList<VehicleListItemDto>> GetVehiclesAsync(int[]? deviceIds = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VehicleListItemDto>>([]);
    public async Task<CreateOrderResult> CreateMoveOrderAsync(int destination, int? mapId = null, string? deviceKey = null, string? orderName = null, CancellationToken cancellationToken = default)
    {
        if (BeforeMove is not null) await BeforeMove();
        Moves.Add(destination);
        return new() { OrderId = "move-" + Moves.Count };
    }
    public Task<CreateOrderResult> CreateChargeOrderAsync(CancellationToken cancellationToken = default)
    {
        Charges++;
        return Task.FromResult(new CreateOrderResult { OrderId = "charge-" + Charges });
    }
    public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        if (CancelError is not null) return Task.FromException(CancelError);
        Cancellations.Add(orderId);
        return Task.CompletedTask;
    }
    public Task PauseMovementAsync(string? deviceKey = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ContinueMovementAsync(string? deviceKey = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CancelEmergencyAsync(string? deviceKey = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public static VehicleSnapshot Ready(string sys = "IDLE", double battery = 100, int position = 99) => new()
    {
        Vehicle = new() { Enable = true, SysState = sys, Battery = battery, CurrentPosition = position, ActionState = "AT_FINISHED", ProcState = "IDLE" }
    };
    public static AgvDispatchOptions Options() => new() { ChargeDestination = 99, ChargeActionId = 78 };
    public static StationConfiguration Stations() => new()
    {
        DefaultReturnStationCode = "MH", ChargeStation = new() { RcsDestination = 99 },
        WorkStations = [new() { Code = "MH", Name = "物料间", RcsDestination = 11, AllowedRoles = ["MH"] }, new() { Code = "OP", Name = "作业站", RcsDestination = 22, AllowedRoles = ["OP"] }]
    };
    public void SetChargingOrder()
    {
        Snapshot = Ready("CHARGING");
        Snapshot.Vehicle.OrderTaskId = "charging-order";
        Snapshot.Order = new() { OrderState = 2, Mission = [new() { Type = "move", Destination = 99 }, new() { Type = "act", ActionId = 78 }] };
    }
}

internal sealed class FakeAgvSlots : ISlotControlService, IDoorStateProvider
{
    public bool Unlocking { get; set; }
    public DoorStateInput Doors { get; set; } = new() { AllDoorsClosed = true };
    public Task<DoorStateInput> GetDoorStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Doors);
    public Task<DoorStateInput> GetDoorStateForAgvAsync(CancellationToken ct = default) => Task.FromResult(Doors);
    public Task<IReadOnlyList<string>> ListOpenInterlockSlotNosAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Doors.AnyDoorOpen ? ["F-A01"] : []);
    public bool HasUnlockInProgress() => Unlocking;
    public bool AreAllDoorsClosed() => Doors.AllDoorsClosed;
    public IReadOnlyList<long> OpenSlotIds() => [];
    public Task<SlotStatusDto?> GetSlotStatusAsync(long slotId, CancellationToken ct = default) => Task.FromResult<SlotStatusDto?>(null);
    public Task<IReadOnlyList<SlotStatusDto>> ListSlotsAsync(SlotListFilter filter, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SlotStatusDto>>([]);
    public Task<OpenSlotResult> OpenSlotAsync(OpenSlotRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<BatchOpenResult> OpenSlotsBatchAsync(IReadOnlyList<string> slotCodes, BatchOpenOptions options, OpenSlotRequest template, CancellationToken ct = default, Action<OpenSlotResult>? onResult = null) => throw new NotSupportedException();
    public Task<bool> ConfirmDoorClosedAsync(long slotId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<(bool Success, string Message)> SetSlotEnabledAsync(long slotId, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
}
