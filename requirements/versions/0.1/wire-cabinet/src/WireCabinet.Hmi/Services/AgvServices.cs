using AgvDispatch.Sdk;
using AgvDispatch.Sdk.Models;
using Microsoft.Extensions.Configuration;
using WireCabinet.Core.Config;
using WireCabinet.Rcs;
using WireCabinet.Slots;

namespace WireCabinet.Hmi.Services;

public sealed class AgvServices : IDisposable
{
    public const string AlreadyAtStationMessage = "发货柜已经在站点";
    public const string AlreadyAtChargeStationMessage = "发货柜已在充电点";

    private readonly AgvDispatchOptions _options;
    private readonly AgvThresholdOptions _thresholds;
    private readonly AgvOrderGate _orderGate;
    private readonly ISlotControlService _slots;

    /// <summary>控车与回充的记录（0.2 §3.1 第 8 项）。</summary>
    private readonly AgvActionLog _agvLog;
    private IAgvDispatchClient? _client;
    private VehicleSnapshot _lastSnapshot = new();
    private bool _startupCleanupDone;
    private readonly SemaphoreSlim _commands = new(1, 1);

    public StationConfiguration Stations { get; }
    public AgvOrderGate OrderGate => _orderGate;
    public bool IsConfigured { get; }
    public string ConfigHint { get; } = "";
    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }

    public ManualMoveService? Move { get; private set; }
    public LowBatteryChargeService? Charge { get; private set; }
    public AgvControlLoop? ControlLoop { get; private set; }

    public double LastBatteryPercent { get; private set; }
    public int LastPosition { get; private set; }
    public string LastPositionDisplay { get; private set; } = "—";
    public string LastSysState { get; private set; } = "—";
    public string LastMoveState { get; private set; } = "—";
    public int LastProgress { get; private set; }
    public string? LastOrderTaskId { get; private set; }
    public string? LastOrderName { get; private set; }
    public string LastTaskTargetDisplay { get; private set; } = "—";
    public string? LastEmergencyState { get; private set; }
    public string LastEmergencyDisplay { get; private set; } = "—";
    public string? LastLocationState { get; private set; }
    public string LastLocationDisplay { get; private set; } = "—";
    public bool HasActiveOrder { get; private set; }
    public bool HasBlockingOrder { get; private set; }
    public string LastOrderStateDisplay { get; private set; } = "—";
    public bool CanPlaceMoveOrder { get; private set; }
    public bool CanPlaceChargeOrder { get; private set; }
    public bool CanPlaceMoveOrChargeOrder => CanPlaceMoveOrder || CanPlaceChargeOrder;
    public string? DispatchBlockReason { get; private set; }
    public bool CanCancelActiveOrder { get; private set; }
    public bool CanReleaseEmergency { get; private set; }
    public int? LastWorkStationBeforeCharge { get; set; }

    public int LowBatteryPercent => _thresholds.LowBatteryPercent;
    public int FullBatteryPercent => _thresholds.FullBatteryPercent;
    public int RecommendedPollIntervalMs => _options.RecommendedPollIntervalMs;
    public DoorInterlockTickResult? LastDoorInterlockTick { get; private set; }

    public AgvServices(IConfiguration config, ISlotControlService slots, IDoorStateProvider doors,
        WireCabinet.Data.IOperationLogSink? operationLog = null)
    {
        _slots = slots;
        _agvLog = new AgvActionLog(operationLog);
        Stations = StationConfiguration.Load();
        _thresholds = new AgvThresholdOptions
        {
            LowBatteryPercent = config.GetValue("AgvThresholds:LowBatteryPercent", Stations.LowBatteryPercent),
            FullBatteryPercent = config.GetValue("AgvThresholds:FullBatteryPercent", Stations.FullBatteryPercent)
        };

        _options = new AgvDispatchOptions();
        config.GetSection(AgvDispatchOptions.SectionName).Bind(_options);

        if (_options.ChargeDestination <= 0 && Stations.ChargeStation is { RcsDestination: > 0 } chg)
            _options.ChargeDestination = chg.RcsDestination;

        if (_options.VehicleQueryDeviceIds.Length == 0)
            _options.VehicleQueryDeviceIds = [0];

        _orderGate = new AgvOrderGate(_options.StateThresholds, () => ControlLoop?.CallInProgress ?? false, _options);

        IsConfigured = IsAgvConfigComplete(config);
        if (!IsConfigured)
        {
            ConfigHint = "请在车载电脑的 appsettings.Local.json 填写 AgvDispatch（BaseUrl、Username、Password、DefaultDeviceKey）。";
            return;
        }

        try
        {
            _client = AgvDispatchClient.Create(_options);
            ControlLoop = new AgvControlLoop(_client, _options, doors, _orderGate);
            Move = new ManualMoveService(_client, _options, slots, ControlLoop);
            Charge = new LowBatteryChargeService(_client, _options, _thresholds, _orderGate, Stations, _agvLog, slots);
        }
        catch (Exception ex)
        {
            ConfigHint = $"RCS 初始化失败：{ex.Message}";
            IsConfigured = false;
        }
    }

    /// <summary>注入客户端与站点，供离线集成验证使用；不加载现场凭据，也不自动连接。</summary>
    public AgvServices(IAgvDispatchClient client, AgvDispatchOptions options, AgvThresholdOptions thresholds,
        StationConfiguration stations, ISlotControlService slots, IDoorStateProvider doors,
        WireCabinet.Data.IOperationLogSink? operationLog = null)
    {
        _client = client;
        _options = options;
        _thresholds = thresholds;
        _slots = slots;
        Stations = stations;
        _agvLog = new AgvActionLog(operationLog);
        _orderGate = new AgvOrderGate(options.StateThresholds, () => ControlLoop?.CallInProgress ?? false, options);
        ControlLoop = new AgvControlLoop(client, options, doors, _orderGate);
        Move = new ManualMoveService(client, options, slots, ControlLoop);
        Charge = new LowBatteryChargeService(client, options, thresholds, _orderGate, stations, _agvLog, slots);
        IsConfigured = true;
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try { return await ConnectCoreAsync(ct).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    public async Task RefreshStatusAsync(CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try { await RefreshStatusCoreAsync(ct).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    private async Task<bool> ConnectCoreAsync(CancellationToken ct)
    {
        if (_client is null) return false;
        try
        {
            await _client.LoginAsync(ct).ConfigureAwait(false);
            IsConnected = true;
            LastError = null;
            await RefreshStatusCoreAsync(ct).ConfigureAwait(false);
            return IsConnected;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            LastError = ex.Message;
            return false;
        }
    }

    public async Task EnsureTokenFreshAsync(CancellationToken ct = default)
    {
        if (_client is null || !IsConnected) return;

        var interval = TimeSpan.FromHours(_options.TokenRefreshIntervalHours);
        var obtained = _client.TokenObtainedAtUtc;
        if (obtained is null || DateTime.UtcNow - obtained.Value >= interval)
        {
            await _client.ForceReLoginAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshStatusCoreAsync(CancellationToken ct)
    {
        if (_client is null || !IsConnected) return;
        try
        {
            await EnsureTokenFreshAsync(ct).ConfigureAwait(false);
            var snap = await _client.GetVehicleSnapshotAsync(cancellationToken: ct).ConfigureAwait(false);
            ApplySnapshot(snap);
            await _orderGate.ReconcilePendingAsync(_client, snap, ct).ConfigureAwait(false);
            ApplySnapshot(snap);
            Move?.RefreshFromSnapshot(snap);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsConnected = false;
            CanPlaceMoveOrder = CanPlaceChargeOrder = CanCancelActiveOrder = false;
        }
    }

    public async Task<(bool Ok, string Message)> MoveToStationAsync(int destination, CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try { return await MoveToStationCoreAsync(destination, ct).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    private async Task<(bool Ok, string Message)> MoveToStationCoreAsync(int destination, CancellationToken ct)
    {
        if (Move is null) return (false, "RCS 未就绪");
        if (!Stations.EnabledWorkStations.Any(s => s.RcsDestination == destination))
            return (false, "请选择已启用的作业站。");
        if (!IsConnected && !await ConnectCoreAsync(ct).ConfigureAwait(false))
            return (false, LastError ?? "RCS 登录失败");

        await RefreshStatusCoreAsync(ct).ConfigureAwait(false);
        if (!IsConnected) return (false, LastError ?? "RCS 状态刷新失败。");
        if (Move.WaitingForChargeDeparture) return (false, "已有等待充电结束的移动请求，请等待或取消。");
        if (!CanPlaceMoveOrder) return (false, DispatchBlockReason ?? "当前不可发车。");

        if (destination > 0 && LastPosition == destination)
            return (false, AlreadyAtStationMessage);

        var doorBlock = await TryBlockIfDoorsOpenForDispatchAsync(ct).ConfigureAwait(false);
        if (doorBlock is not null)
            return doorBlock.Value;

        var ws = Stations.EnabledWorkStations.FirstOrDefault(s => s.RcsDestination == destination);
        var result = await Move.RequestMoveAsync(destination, ct).ConfigureAwait(false);
        if (result.Ok)
        {
            SetLastWorkStationBeforeCharge(destination);
            Charge?.ClearPostChargeReturn(); // 手动所选站点优先，避免默认返航抢先下单。
        }
        await RefreshStatusCoreAsync(ct).ConfigureAwait(false);
        var label = ws?.Name ?? destination.ToString();
        _agvLog.RecordCommand(AgvActionLog.AgvMove,
            Move.WaitingForChargeDeparture ? "已接受结束充电后的移动请求" : "下达移动单",
            result.Ok, result.Message, $"→ {label}（站点 {destination}）");
        return result.Ok
            ? (true, $"{result.Message} → {label}（站点 {destination}）")
            : (false, result.Message);
    }

    public async Task<(bool Ok, string Message)> GoChargeAsync(CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try { return await GoChargeCoreAsync(ct).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    private async Task<(bool Ok, string Message)> GoChargeCoreAsync(CancellationToken ct)
    {
        if (Charge is null || _options.ChargeDestination <= 0)
            return (false, "未配置充电站点号（stations.yaml / AgvDispatch:ChargeDestination）。");
        if (!IsConnected && !await ConnectCoreAsync(ct).ConfigureAwait(false))
            return (false, LastError ?? "RCS 登录失败");

        await RefreshStatusCoreAsync(ct).ConfigureAwait(false);
        if (!IsConnected) return (false, LastError ?? "RCS 状态刷新失败。");
        if (!CanPlaceChargeOrder || Move?.WaitingForChargeDeparture == true)
            return (false, _orderGate.GetBlockReason(_lastSnapshot, AgvPendingOrderKind.Charge) ?? "已有移动请求，请等待或取消。");

        if (_options.ChargeDestination > 0
            && LastPosition == _options.ChargeDestination
            && !_options.StateThresholds.IdleSysStates.Contains(LastSysState, StringComparer.OrdinalIgnoreCase))
            return (false, AlreadyAtChargeStationMessage);

        var doorBlock = await TryBlockIfDoorsOpenForDispatchAsync(ct).ConfigureAwait(false);
        if (doorBlock is not null)
            return doorBlock.Value;

        try
        {
            var doors = await _slots.GetDoorStateForAgvAsync(ct).ConfigureAwait(false);
            var ready = AgvReadinessPolicy.Evaluate(_lastSnapshot.Vehicle, _lastSnapshot.Order, doors,
                _options.StateThresholds, false, ControlLoop?.CallInProgress == true);
            if (!ready.CanAcceptOrder) return (false, ready.Reason);
            var order = await _client!.CreateChargeOrderAsync(ct).ConfigureAwait(false);
            _orderGate.RecordPlacedOrder(order.OrderId, AgvPendingOrderKind.Charge);
            Charge.RememberReturnDestinationOnChargeDispatch(LastPosition);
            await RefreshStatusCoreAsync(ct).ConfigureAwait(false);
            var name = Stations.ChargeStation?.Name ?? "充电点";
            _agvLog.RecordCommand(AgvActionLog.AgvCharge, "下充电单（自动回充）", ok: true, detail: $"→ {name}（{order.OrderId}）");
            return (true, $"已下充电单 → {name}（{order.OrderId}）");
        }
        catch (Exception ex)
        {
            _agvLog.RecordCommand(AgvActionLog.AgvCharge, "下充电单（自动回充）", ok: false, error: ex.Message);
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> CancelActiveOrderAsync(CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try { return await CancelActiveOrderCoreAsync(ct).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    private async Task<(bool Ok, string Message)> CancelActiveOrderCoreAsync(CancellationToken ct)
    {
        if (_client is null)
            return (false, "RCS 未就绪");

        if (Move?.WaitingForChargeDeparture == true)
        {
            Move.ResetSession();
            Charge?.ClearPostChargeReturn();
            EvaluateUiGates(_lastSnapshot);
            _agvLog.RecordCommand(AgvActionLog.AgvMove, "撤销等待充电结束的移动请求", ok: true);
            return (true, "已撤销等待充电结束的移动请求。充电订单若仍在取消中，将继续等待其释放。");
        }
        await RefreshStatusCoreAsync(ct).ConfigureAwait(false);
        if (!IsConnected) return (false, LastError ?? "RCS 状态刷新失败。");

        var orderId = _orderGate.ResolveCancelOrderId(_lastSnapshot);
        if (string.IsNullOrWhiteSpace(orderId))
            return (false, "当前无进行中订单。");
        if (!CanCancelActiveOrder)
            return (false, "仅暂停、队列中的任务或已识别的充电任务可取消。");

        try
        {
            await _client.CancelOrderAsync(orderId, ct).ConfigureAwait(false);
            _orderGate.RecordCancellationRequested(orderId, wasAssigned: !string.IsNullOrWhiteSpace(_lastSnapshot.Vehicle.OrderTaskId));
            Move?.ResetSession();
            Charge?.ClearPostChargeReturn();
            await RefreshStatusCoreAsync(ct).ConfigureAwait(false);
            _agvLog.RecordCommand(AgvActionLog.AgvMove, "取消当前订单", ok: true, detail: orderId);
            return (true, "已请求取消当前订单，等待 RCS 确认释放。");
        }
        catch (Exception ex)
        {
            _agvLog.RecordCommand(AgvActionLog.AgvMove, "取消当前订单", ok: false, error: ex.Message, detail: orderId);
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> ReleaseEmergencyAsync(CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReleaseEmergencyCoreAsync(ct).ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    private async Task<(bool Ok, string Message)> ReleaseEmergencyCoreAsync(CancellationToken ct)
    {
        if (_client is null) return (false, "RCS 未就绪");
        if (!CanReleaseEmergency)
            return (false, "当前无需解除急停。");

        try
        {
            await _client.CancelEmergencyAsync(cancellationToken: ct).ConfigureAwait(false);
            await RefreshStatusCoreAsync(ct).ConfigureAwait(false);
            return (true, "已下发解除急停。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>后台轮询：刷新状态、自动回充；到站时回调。</summary>
    public async Task<string?> PollCycleAsync(
        AgvArrivalNavigator? navigator,
        Action<WorkStationConfig>? onArrivedAtStation,
        CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try { return await PollCycleCoreAsync(navigator, onArrivedAtStation, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            IsConnected = false;
            CanPlaceMoveOrder = CanPlaceChargeOrder = CanCancelActiveOrder = false;
            LastError = ex.Message;
            return $"RCS 状态刷新失败，已暂停下单：{ex.Message}";
        }
        finally { _commands.Release(); }
    }

    private async Task<string?> PollCycleCoreAsync(AgvArrivalNavigator? navigator,
        Action<WorkStationConfig>? onArrivedAtStation, CancellationToken ct)
    {
        if (!IsConfigured) return null;

        if (!IsConnected && !await ConnectCoreAsync(ct).ConfigureAwait(false))
            return LastError ?? "RCS 连接失败，已暂停下单。";

        var doorTick = await RunDoorInterlockTickAsync(ct).ConfigureAwait(false);
        LastDoorInterlockTick = doorTick;

        string? startupMsg = null;
        if (IsConnected)
            startupMsg = await EnsureStartupOrderCleanupAsync(ct).ConfigureAwait(false);

        var policyMsg = doorTick?.StatusMessage;
        if (policyMsg is null)
        {
            if (Move?.WaitingForChargeDeparture == true)
            {
                policyMsg = await Move.ContinueChargeDepartureAsync(ct).ConfigureAwait(false);
                await RefreshStatusCoreAsync(ct).ConfigureAwait(false);
                if (Move.WaitingForChargeDeparture == false && Move.State == MoveUiState.Moving)
                    _agvLog.RecordCommand(AgvActionLog.AgvMove, "充电结束后前往所选作业站", ok: true);
            }
            else
                policyMsg = await RunAutoChargePolicyAsync(ct).ConfigureAwait(false);
        }
        if (startupMsg is not null && policyMsg is null)
            policyMsg = startupMsg;

        if (navigator is not null && onArrivedAtStation is not null)
        {
            navigator.ResetIfLeftStation(LastPosition);
            var station = navigator.TryGetArrivedStation(this);
            if (station is not null)
                onArrivedAtStation(station);
        }

        return policyMsg;
    }

    /// <summary>后台轮询：格口联锁 — 移动/充电执行中若格口打开则暂停，关闭后继续。</summary>
    public async Task<DoorInterlockTickResult?> RunDoorInterlockTickAsync(CancellationToken ct = default)
    {
        if (ControlLoop is null || !IsConnected)
        {
            await RefreshStatusCoreAsync(ct).ConfigureAwait(false);
            return null;
        }

        var tick = await ControlLoop.TickAsync(ct: ct).ConfigureAwait(false);
        ApplySnapshot(tick.Snapshot);
        Move?.RefreshFromSnapshot(tick.Snapshot);

        var openSlots = await _slots.ListOpenInterlockSlotNosAsync(ct).ConfigureAwait(false);

        // 状态迁移才记一条（验收 C5）：静置时 ExecutedAction 为 null，RecordTickAction 不写任何东西
        _agvLog.RecordTickAction(tick.ExecutedAction);

        var statusMessage = tick.ExecutedAction switch
        {
            "PauseMovement" => "检测到格口打开，已暂停 AGV 任务。",
            "ContinueMovement" => "格口已关闭，AGV 任务已继续。",
            _ => null
        };

        if (statusMessage is null && tick.ExecutedAction is null)
            return null;

        return new DoorInterlockTickResult(statusMessage, tick.ExecutedAction, openSlots);
    }

    private async Task<(bool Ok, string Message)?> TryBlockIfDoorsOpenForDispatchAsync(CancellationToken ct)
    {
        if (_slots.HasUnlockInProgress())
            return (false, "存在开锁中，禁止下发移动/充电单。");

        var doors = await _slots.GetDoorStateForAgvAsync(ct).ConfigureAwait(false);
        if (!doors.AllDoorsClosed && !doors.AnyDoorOpen)
            return (false, "门态未知，禁止下发移动/充电单。");

        var open = await _slots.ListOpenInterlockSlotNosAsync(ct).ConfigureAwait(false);
        if (open.Count == 0 && doors.AllDoorsClosed && !doors.AnyDoorOpen)
            return null;

        var labels = AgvDoorInterlockAlert.FormatSlotLabels(open);
        return (false, AgvDoorInterlockAlert.BuildDispatchBlockedMessage(labels));
    }

    private async Task<string?> RunAutoChargePolicyAsync(CancellationToken ct = default)
    {
        if (Charge is null || !IsConnected || _client is null) return null;

        var snap = await _client.GetVehicleSnapshotAsync(cancellationToken: ct).ConfigureAwait(false);
        ApplySnapshot(snap);
        await _orderGate.ReconcilePendingAsync(_client, snap, ct).ConfigureAwait(false);
        ApplySnapshot(snap);

        var low = await Charge.TryAutoChargeAsync(snap, ct).ConfigureAwait(false);
        if (low is not null)
        {
            ApplySnapshot(snap);
            return low;
        }

        if (Charge.PostChargeReturnPending)
        {
            var ret = await Charge.TryReturnToWorkStationAsync(snap, ct).ConfigureAwait(false);
            if (ret is not null)
                ApplySnapshot(snap);
            return ret ?? (Charge.LastBlockReason is { } reason ? $"返航等待：{reason}" : null);
        }

        return Charge.LastBlockReason is { } block ? $"回充等待：{block}" : null;
    }

    private async Task<string?> EnsureStartupOrderCleanupAsync(CancellationToken ct)
    {
        if (_startupCleanupDone || _client is null)
            return null;

        var snap = await _client.GetVehicleSnapshotAsync(cancellationToken: ct).ConfigureAwait(false);
        ApplySnapshot(snap);
        Charge?.BootstrapExternalChargeSession(snap);
        _startupCleanupDone = true;
        if (!string.IsNullOrWhiteSpace(snap.Vehicle.OrderTaskId))
            return "车辆任务执行中，保留当前订单。";

        if (Charge?.PostChargeReturnPending == true)
        {
            var mh = Stations.ResolveDefaultReturnStation();
            if (mh is not null)
                return $"检测到已在充电，充满后将返航 {mh.Name}。";
        }

        return null;
    }

    private void SetLastWorkStationBeforeCharge(int destination)
    {
        LastWorkStationBeforeCharge = destination;
        Charge?.RememberWorkStation(destination);
    }

    public void Dispose()
    {
        if (_client is IDisposable d) d.Dispose();
        _client = null;
    }

    private void ApplySnapshot(VehicleSnapshot snap)
    {
        _lastSnapshot = snap;
        _orderGate.ApplySnapshot(snap);
        Charge?.ObserveSnapshot(snap);

        var v = snap.Vehicle;
        LastBatteryPercent = v.Battery;
        LastPosition = v.CurrentPosition;
        LastPositionDisplay = FormatPosition(v.CurrentPosition);
        LastSysState = v.SysState ?? "—";
        LastMoveState = v.EffectiveMoveState ?? "—";
        LastProgress = v.Progress;
        LastOrderTaskId = v.OrderTaskId;
        LastOrderName = v.OrderName;
        LastEmergencyState = v.EmergencyState;
        LastLocationState = v.LocationState;
        LastLocationDisplay = LocationStateDisplay.Format(v.LocationState, _options.StateThresholds);
        LastTaskTargetDisplay = _orderGate.FormatTaskSummary(snap, FormatPosition);
        LastOrderStateDisplay = _orderGate.FormatOrderStateDisplay(snap);
        HasActiveOrder = !string.IsNullOrWhiteSpace(v.OrderTaskId);
        HasBlockingOrder = _orderGate.HasBlockingOrder(snap);
        EvaluateUiGates(snap);
        LastEmergencyDisplay = FormatEmergency(v);
        LastWorkStationBeforeCharge = Charge?.ReturnDestination;
    }

    private void EvaluateUiGates(VehicleSnapshot snap)
    {
        var v = snap.Vehicle;
        var thresholds = _options.StateThresholds;

        DispatchBlockReason = _orderGate.GetBlockReason(snap, AgvPendingOrderKind.Move);
        CanPlaceMoveOrder = IsConnected && Move?.WaitingForChargeDeparture != true
            && (DispatchBlockReason is null || _orderGate.CanEndChargingOrder(snap));
        CanPlaceChargeOrder = IsConnected && Move?.WaitingForChargeDeparture != true
            && !_orderGate.BlocksNewOrder(snap, AgvPendingOrderKind.Charge);
        CanCancelActiveOrder = IsConnected && (Move?.WaitingForChargeDeparture == true || _orderGate.CanCancelOrder(snap));

        var sysError = thresholds.ErrorSysStates.Contains(v.SysState ?? "", StringComparer.OrdinalIgnoreCase);
        var emgActive = !string.IsNullOrWhiteSpace(v.EmergencyState)
                        && thresholds.EmergencyActiveContains.Any(e =>
                            v.EmergencyState!.Contains(e, StringComparison.OrdinalIgnoreCase));
        CanReleaseEmergency = sysError || emgActive;
    }

    private string FormatPosition(int position)
    {
        if (position <= 0) return "—";
        var ws = Stations.EnabledWorkStations.FirstOrDefault(s => s.RcsDestination == position);
        if (ws is not null) return $"{ws.Name}（{position}）";
        if (Stations.ChargeStation is { RcsDestination: var chg } && chg == position)
            return $"{Stations.ChargeStation.Name}（{position}）";
        return position.ToString();
    }

    private string FormatEmergency(VehicleInfoDto v)
    {
        if (!CanReleaseEmergency && string.IsNullOrWhiteSpace(v.EmergencyState))
            return "正常";
        if (!string.IsNullOrWhiteSpace(v.EmergencyState))
            return v.EmergencyState;
        if (_options.StateThresholds.ErrorSysStates.Contains(v.SysState ?? "", StringComparer.OrdinalIgnoreCase))
            return "系统 ERROR";
        return "—";
    }

    private static bool IsAgvConfigComplete(IConfiguration config)
    {
        var section = config.GetSection(AgvDispatchOptions.SectionName);
        var url = section["BaseUrl"];
        var user = section["Username"];
        var pass = section["Password"];
        var key = section["DefaultDeviceKey"];
        return !string.IsNullOrWhiteSpace(url)
               && !string.IsNullOrWhiteSpace(user)
               && !string.IsNullOrWhiteSpace(pass)
               && !string.IsNullOrWhiteSpace(key);
    }
}
