using System.IO;
using Microsoft.Extensions.Configuration;
using WireCabinet.Core;
using WireCabinet.Core.Config;
using WireCabinet.Data;
using WireCabinet.Flows;
using WireCabinet.Slots;
using WireCabinet.Slots.Hardware;

namespace WireCabinet.Hmi.Services;

public sealed class AppBootstrap : IDisposable
{
    public SqliteDb AppDb { get; }
    public SqlCatalog Catalog { get; }
    public IMesGateway Mes { get; }
    public IAppDb AppDbGateway { get; }
    /// <summary>物料存取记录写入通道与失败补偿（0.2）。</summary>
    public SqliteOperationLogSink OperationLog { get; }
    public SlotIoConfig SlotIo { get; }
    public ISlotHardwareService Hardware { get; }
    public DoorOperationGate DoorOps { get; }
    public ISlotControlService SlotControl { get; }
    public ISlotController FlowSlots { get; }
    public IWireOperationSession WireSession { get; } = new WireOperationSession();
    public MhInterruptedLoadStore InterruptedLoad { get; }
    public OpInterruptedIssueStore OpInterruptedIssue { get; }
    public WireMesDiscoSyncStore MesDiscoPending { get; }
    public MesReconAuditLogStore MesReconAudit { get; }
    public WireDiscoEqpSyncService DiscoSync { get; }
    public MesDiscoReconciliationService MesReconciliation { get; }
    public WireMesInterruptGuard InterruptGuard { get; }
    public EngineServices EngineServices { get; }
    public IReadOnlyList<FlowDefinition> Flows { get; }
    public MesOptions MesOptions { get; }
    public FlowTraceWindowOptions FlowTraceOptions { get; }
    public bool MesReady { get; }
    public bool FlowTraceEnabled => FlowTraceOptions.Enabled;

    public AppBootstrap(IConfiguration config)
    {
        var dbPath = config["ApplicationDatabase:Path"];
        if (string.IsNullOrWhiteSpace(dbPath))
            dbPath = CabinetPaths.DefaultAppDbPath;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        var needsInit = !SqliteDb.HasAppSlotTable(dbPath);
        if (needsInit && File.Exists(dbPath) && new FileInfo(dbPath).Length == 0)
            File.Delete(dbPath);
        AppDb = new SqliteDb(dbPath);
        if (needsInit)
            AppDb.Initialize(recreate: false);

        WireMesDiscoSchema.EnsureAll(AppDb);

        // WAL 是库文件的持久属性，对已存在的库必须显式设置一次（建库脚本只在首次建库时执行）。
        // 库被硬件轮询、AGV 轮询与记录写入共用，不开 WAL 时争用会表现为界面卡顿而非报错。
        AppDb.EnsureWal();

        // MES mock 表与演示数据。**必须在这里而不是建库脚本里**：Initialize 只在首次建库时执行，
        // 而已存在的 app.db（开发机上的常态）永远拿不到这些表，操作员流程在本机就跑不起来。
        WireMesMockSchema.Ensure(AppDb);

        // 物料存取记录（0.2）：schema 脚本用的是 CREATE TABLE IF NOT EXISTS，
        // 对已存在的库不会加列，必须在这里幂等补列，否则现场老库会 no such column。
        OperationLogSchema.Ensure(AppDb);

        InterruptedLoad = new MhInterruptedLoadStore(AppDb);
        InterruptedLoad.EnsureSchema();

        OpInterruptedIssue = new OpInterruptedIssueStore(AppDb);
        OpInterruptedIssue.EnsureSchema();

        MesDiscoPending = new WireMesDiscoSyncStore(AppDb);
        MesDiscoPending.EnsureSchema();

        MesReconAudit = new MesReconAuditLogStore(AppDb);
        MesReconAudit.EnsureSchema();

        Catalog = SqlCatalog.Load();
        MesOptions = new MesOptions
        {
            ConnectionString = NormalizeMesConnectionString(config["Mes:ConnectionString"]),
            MatTransWriter = config["Mes:MatTransWriter"] ?? "新厂前线物料多仓位2"
        };
        MesReady = MesOptions.IsConfigured;
        Mes = MesReady
            ? new OracleMesGateway(MesOptions.ConnectionString)
            : new SqliteMockMesGateway(AppDb, new MesFunctionSettings());

        AppDbGateway = new SqliteAppDb(AppDb);

        // 物料存取记录（0.2）写入通道。业务动作成功后就地调用：
        // 失败只告警 + 转待补偿，**不得改变业务 outcome**——调用方须遵守该契约。
        OperationLog = new SqliteOperationLogSink(Catalog, AppDbGateway);

        SlotIo = SlotIoConfig.Load();
        Hardware = new ModbusSlotHardwareService(SlotIo);
        DoorOps = new DoorOperationGate();
        SlotControl = new SlotControlService(AppDb, Hardware, DoorOps);
        FlowSlots = new FlowSlotController(SlotControl, AppDb);

        InterruptGuard = new WireMesInterruptGuard(InterruptedLoad, OpInterruptedIssue);

        DiscoSync = new WireDiscoEqpSyncService(
            Catalog,
            AppDbGateway,
            Mes,
            MesDiscoPending,
            MesOptions.MatTransWriter,
            MesReconAudit,
            InterruptGuard);

        MesReconciliation = new MesDiscoReconciliationService(
            AppDbGateway,
            Mes,
            Catalog,
            DiscoSync,
            InterruptGuard);

        EngineServices = new EngineServices
        {
            Catalog = Catalog,
            AppDb = AppDbGateway,
            Mes = Mes,
            Slot = FlowSlots,
            SystemAgvNo = config["AgvDispatch:DefaultDeviceKey"] ?? "AGV-01",
            SystemMesWriter = MesOptions.MatTransWriter,
            DiscoSync = DiscoSync,
            OperationLog = OperationLog
        };

        Flows = FlowLoader.LoadAll();

        FlowTraceOptions = new FlowTraceWindowOptions();
        config.GetSection("Debug:FlowTraceWindow").Bind(FlowTraceOptions);
    }

    public void Dispose() => (Hardware as IDisposable)?.Dispose();

    /// <summary>
    /// 处于 OP 领用占用/隔离状态的格口。启动恢复与 MES 对账页用它提示人工处理；
    /// 这些格口已被 app.wire_inventory.find_matched_available 排除，不会发给操作员。
    /// </summary>
    public IReadOnlyList<IssuePendingSlot> ListIssuePendingSlots()
    {
        var item = Catalog.Find("app.slot.list_issue_pending");
        if (item is null)
            return Array.Empty<IssuePendingSlot>();

        var result = AppDbGateway.Run(item, new Dictionary<string, object?>());
        if (result.HasError)
            return Array.Empty<IssuePendingSlot>();

        var list = new List<IssuePendingSlot>();
        foreach (var row in result.Rows)
        {
            if (!long.TryParse(row.GetValueOrDefault("slot_id")?.ToString(), out var slotId) || slotId <= 0)
                continue;
            list.Add(new IssuePendingSlot(
                slotId,
                row.GetValueOrDefault("slot_no")?.ToString() ?? "",
                row.GetValueOrDefault("wire_lot_no")?.ToString(),
                row.GetValueOrDefault("wire_state")?.ToString() ?? ""));
        }
        return list;
    }


    /// <summary>去掉从旧版配置整段粘贴时误带的 OracleConnection= 前缀。</summary>
    private static string NormalizeMesConnectionString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var s = raw.Trim();
        const string legacyPrefix = "OracleConnection=";
        if (s.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
            s = s[legacyPrefix.Length..].TrimStart();

        return s;
    }
}

public sealed record IssuePendingSlot(long SlotId, string SlotNo, string? WireLotNo, string WireState);

public sealed class UnconfiguredMesGateway : IMesGateway
{
    public MesMode Mode => MesMode.Oracle;
    public DbResult Run(SqlCatalogItem item, IDictionary<string, object?> parameters) =>
        DbResult.Failure(item.OracleSql ?? item.Sql, "MES 未配置：请在车载电脑的 appsettings.Local.json 填写 Mes:ConnectionString。");
}
