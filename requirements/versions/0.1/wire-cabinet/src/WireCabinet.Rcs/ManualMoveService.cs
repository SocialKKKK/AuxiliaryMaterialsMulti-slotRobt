using AgvDispatch.Sdk;
using AgvDispatch.Sdk.Operations;
using WireCabinet.Slots;

namespace WireCabinet.Rcs;

public enum MoveUiState { Idle, Sending, Moving, Arrived, Failed, Timeout }

public sealed class ManualMoveService
{
    private readonly IAgvDispatchClient _client;
    private readonly AgvDispatchOptions _options;
    private readonly ISlotControlService _slots;
    private readonly AgvControlLoop _loop;

    public MoveUiState State { get; private set; } = MoveUiState.Idle;
    public int? TargetDestination { get; private set; }
    public string? LastMessage { get; private set; }
    public bool WaitingForChargeDeparture { get; private set; }

    public ManualMoveService(IAgvDispatchClient client, AgvDispatchOptions options, ISlotControlService slots, AgvControlLoop loop)
    {
        _client = client;
        _options = options;
        _slots = slots;
        _loop = loop;
    }

    public async Task<(bool Ok, string Message)> RequestMoveAsync(int destination, CancellationToken ct = default)
    {
        if (_slots.HasUnlockInProgress())
            return (false, "存在开锁中，禁止下发移动单。");

        var openSlots = await _slots.ListOpenInterlockSlotNosAsync(ct).ConfigureAwait(false);
        if (openSlots.Count > 0)
            return (false, $"存在未关闭格口（{string.Join("、", openSlots)}），禁止下发移动单。");

        TargetDestination = destination;
        State = MoveUiState.Sending;
        AgvLoopTickResult tick;
        try { tick = await _loop.TickAsync(destination, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            State = MoveUiState.Failed;
            LastMessage = $"发车请求失败：{ex.Message}";
            return (false, LastMessage);
        }
        if (string.Equals(tick.ExecutedAction, "CreateMoveOrder", StringComparison.Ordinal))
        {
            WaitingForChargeDeparture = false;
            State = MoveUiState.Moving;
            LastMessage = string.IsNullOrWhiteSpace(tick.CreatedOrderId)
                ? "已下发移动单"
                : $"已下发移动单（{tick.CreatedOrderId}）";
            return (true, LastMessage);
        }

        if (tick.ExecutedAction is "CancelChargeOrder" or "AwaitChargeOrderRelease")
        {
            WaitingForChargeDeparture = true;
            LastMessage = "已请求结束充电，订单释放后自动前往所选作业站。";
            return (true, LastMessage);
        }

        if (tick.ExecutedAction == "AlreadyAtDestination")
        {
            WaitingForChargeDeparture = false;
            State = MoveUiState.Arrived;
            LastMessage = "已到达所选作业站。";
            return (true, LastMessage);
        }

        State = MoveUiState.Failed;
        LastMessage = tick.Evaluation.Reason;
        return (false, LastMessage);
    }

    public async Task<string?> ContinueChargeDepartureAsync(CancellationToken ct = default)
    {
        if (!WaitingForChargeDeparture || TargetDestination is not > 0) return null;
        var target = TargetDestination.Value;
        var result = await RequestMoveAsync(target, ct).ConfigureAwait(false);
        // 等待中的门态/订单/就绪失败保留请求；用户可用取消按钮撤销。
        if (!result.Ok) State = MoveUiState.Sending;
        return result.Message;
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var snap = await _client.GetVehicleSnapshotAsync(cancellationToken: ct).ConfigureAwait(false);
        ApplySnapshot(snap);
    }

    public void RefreshFromSnapshot(AgvDispatch.Sdk.Models.VehicleSnapshot snap) => ApplySnapshot(snap);

    public void ResetSession()
    {
        _loop.ResetCallState();
        TargetDestination = null;
        WaitingForChargeDeparture = false;
        State = MoveUiState.Idle;
        LastMessage = null;
    }

    private void ApplySnapshot(AgvDispatch.Sdk.Models.VehicleSnapshot snap)
    {
        var v = snap.Vehicle;
        if (string.IsNullOrWhiteSpace(v.OrderTaskId) && !_loop.DispatchPending
            && State is MoveUiState.Arrived or MoveUiState.Moving)
            ResetSession();

        var pos = v.CurrentPosition;
        if (TargetDestination is > 0 && pos == TargetDestination)
        {
            State = MoveUiState.Arrived;
            LastMessage = "已到站";
        }
        else if (!string.IsNullOrWhiteSpace(v.OrderTaskId)
                 && !string.IsNullOrEmpty(v.EffectiveMoveState)
                 && !v.EffectiveMoveState.Contains("NA", StringComparison.OrdinalIgnoreCase)
                 && !v.EffectiveMoveState.Contains("FINISHED", StringComparison.OrdinalIgnoreCase))
            State = MoveUiState.Moving;
    }
}
