using WireCabinet.Data;
using WireCabinet.Slots;

namespace WireCabinet.Flows;

/// <summary>流程引擎依赖的服务集合。Mes 可在运行时随模式切换被替换。</summary>
public sealed class EngineServices
{
    public required SqlCatalog Catalog { get; init; }
    public required IAppDb AppDb { get; init; }
    public required IMesGateway Mes { get; set; }
    public required ISlotController Slot { get; init; }
    public string SystemAgvNo { get; set; } = "AGV-01";
    public string SystemMesWriter { get; set; } = "新厂前线物料多仓位2";
    public IWireDiscoEqpSync? DiscoSync { get; set; }

    /// <summary>
    /// 物料存取记录写入通道（0.2）。流程节点成功后挂接，
    /// **写入失败只告警、不改变 outcome 或路由**（0.2 需求范围 §1.4 第 1 条）。
    /// </summary>
    public IOperationLogSink? OperationLog { get; set; }
}
