using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.Configuration;
using WireCabinet.Hmi.Services;
using WireCabinet.Hmi.Views;
using WireCabinet.Slots;

namespace WireCabinet.Hmi;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private bool _servicesInitialized;

    public static AppBootstrap Bootstrap { get; private set; } = null!;
    public static FlowCoordinator Flows { get; private set; } = null!;
    public static WireCabinet.Rcs.StationArrivalGate? StationGate { get; private set; }
    public static AgvServices Agv { get; private set; } = null!;
    public static IoModuleHealthMonitor IoHealth { get; private set; } = null!;
    public static SlotHardwarePollService SlotHardwarePoll { get; private set; } = null!;
    public static MhDoorOnlyService MhDoors { get; private set; } = null!;
    public static DoorOperationGate DoorOps { get; private set; } = null!;
    public static MaintAccessGate MaintAccess { get; private set; } = null!;
    public static MhAccessGate MhAccess { get; private set; } = null!;

    /// <summary>「物料存取记录」页签门禁：物料员或维护密码均可放行，操作员不可见（0.2 §2）。</summary>
    public static OperationLogAccessGate OpLogAccess { get; private set; } = null!;
    public static HmiUiGateService UiGate { get; private set; } = null!;
    public static AgvDoorInterlockNotifier AgvDoorNotifier { get; private set; } = new();
    public static FlowTraceHub? FlowTrace { get; private set; }

    public static WireMesDiscoRetryTimer? MesDiscoRetry { get; private set; }
    public static OperationLogRetryTimer? OperationLogRetry { get; private set; }
    public static KioskOptions Kiosk { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, @"Global\WireCabinet.Hmi", out var createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"启动失败：{args.Exception.Message}", "WireCabinet", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(-1);
        };

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .Build();

        Kiosk = config.GetSection("Ui:Kiosk").Get<KioskOptions>() ?? new KioskOptions();

        Bootstrap = new AppBootstrap(config);
        DoorOps = Bootstrap.DoorOps;
        MaintAccess = new MaintAccessGate(config["MaintAccess:Password"]);
        MhAccess = new MhAccessGate(config["MhAccess:Password"]);
        OpLogAccess = new OperationLogAccessGate(MaintAccess, MhAccess);
        if (Bootstrap.FlowTraceEnabled)
        {
            FlowTrace = new FlowTraceHub(Bootstrap.FlowTraceOptions, Dispatcher);
            Flows = new FlowCoordinator(Bootstrap, FlowTrace);
        }
        else
            Flows = new FlowCoordinator(Bootstrap);
        var stations = WireCabinet.Rcs.StationConfiguration.Load();
        StationGate = new WireCabinet.Rcs.StationArrivalGate(stations);
        var doors = new WireCabinet.Slots.CabinetDoorStateProvider(Bootstrap.SlotControl);
        Agv = new AgvServices(config, Bootstrap.SlotControl, doors, Bootstrap.OperationLog);
        UiGate = new HmiUiGateService();
        IoHealth = new IoModuleHealthMonitor(Bootstrap.Hardware, Bootstrap.SlotIo);
        SlotHardwarePoll = new SlotHardwarePollService(Bootstrap.Hardware, Bootstrap.SlotIo.HealthCheckIntervalMs);
        MhDoors = new MhDoorOnlyService(
            Bootstrap.SlotControl,
            Bootstrap.WireSession,
            Bootstrap.AppDbGateway,
            Bootstrap.Catalog,
            Bootstrap.DiscoSync,
            Bootstrap.OperationLog);

        MesDiscoRetry = new WireMesDiscoRetryTimer(
            Bootstrap.DiscoSync,
            Bootstrap.MesReconciliation,
            TimeSpan.FromMinutes(2));
        MesDiscoRetry.Start();

        OperationLogRetry = new OperationLogRetryTimer(
            Bootstrap.OperationLog,
            TimeSpan.FromMinutes(2));
        OperationLogRetry.Start();

        var doorReconciler = new DoorStateReconciler(Bootstrap.SlotControl, Bootstrap.Hardware);
        SlotHardwarePoll.SetAfterPoll(async snapshots =>
        {
            var closed = await doorReconciler.ReconcileAsync(snapshots);
            if (closed.Count > 0)
            {
                await MhDoors.NotifySlotsClosedAsync(closed);
                Bootstrap.FlowSlots.Reload();
                // 中断领用：门已关且过了宽限期就自动清库，不再要求物料员专门开这一格。
                OpInterruptedIssueAutoComplete.TryRun();
            }
            else
                MhDoors.TryAutoEndDoorOnlySession();

            // 计划外开门（0.2 § 矩阵第 10 项）：库内没有开门指令，而 DI 读到释锁。
            // 只返回本轮新出现的——持续释锁由 DoorStateReconciler 内部去重，
            // 否则 2 秒一轮会把同一把卡住的锁刷成记录洪水。
            foreach (var (_, slotNo) in await doorReconciler.FindUncommandedAsync(snapshots))
                MhDoors.RecordUncommandedOpen(slotNo);
            foreach (var (_, slotNo) in doorReconciler.RecoveredUncommanded)
                MhDoors.RecordUncommandedClosed(slotNo);
        });

        base.OnStartup(e);
        _servicesInitialized = true;

        if (FlowTrace is not null)
            new FlowTraceWindow(FlowTrace, Bootstrap.Flows).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        if (_servicesInitialized)
        {
            Agv.Dispose();
            IoHealth.Dispose();
            SlotHardwarePoll.Dispose();
            MesDiscoRetry?.Dispose();
            Bootstrap.Dispose();
        }
        base.OnExit(e);
    }
}
