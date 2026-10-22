using AgvDispatch.Sdk.Models;

namespace AgvDispatch.Sdk;

/// <summary>
/// AGV 调度 REST 客户端（.NET 8+）。
/// </summary>
public interface IAgvDispatchClient
{
    Task LoginAsync(CancellationToken cancellationToken = default);
    Task ForceReLoginAsync(CancellationToken cancellationToken = default);
    DateTime? TokenObtainedAtUtc { get; }
    Task<VehicleInfoDto> GetVehicleInfoAsync(string? deviceKey = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VehicleListItemDto>> GetVehiclesAsync(int[]? deviceIds = null, CancellationToken cancellationToken = default);
    /// <summary>读取地图站点（id + name），供“按名称解析站点号”使用。</summary>
    Task<IReadOnlyList<MapStationDto>> GetMapStationsAsync(int mapId, CancellationToken cancellationToken = default);
    Task<OrderDetailDto> GetOrderDetailAsync(string orderId, CancellationToken cancellationToken = default);
    Task<VehicleSnapshot> GetVehicleSnapshotAsync(string? deviceKey = null, CancellationToken cancellationToken = default);
    Task<CreateOrderResult> CreateMoveOrderAsync(int destination, int? mapId = null, string? deviceKey = null, string? orderName = null, CancellationToken cancellationToken = default);
    Task<CreateOrderResult> CreateChargeOrderAsync(CancellationToken cancellationToken = default);
    Task<CreateOrderResult> CreateChargeOrderAsync(int destination, CancellationToken cancellationToken = default);
    Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default);
    Task PauseMovementAsync(string? deviceKey = null, CancellationToken cancellationToken = default);
    Task ContinueMovementAsync(string? deviceKey = null, CancellationToken cancellationToken = default);
    Task CancelEmergencyAsync(string? deviceKey = null, CancellationToken cancellationToken = default);
}
