namespace WireCabinet.Data;

/// <summary>
/// 按工号取操作人姓名（0.2 需求范围 §3.3 指定的来源：<c>mes.operator.get_by_id</c>）。
///
/// **为什么单独成类**：界面层是 net8.0-windows，测试工程引用不了，而这个查询有一处很容易错——
/// catalog 条目的参数名是 <c>user_id</c>（流程节点也用这个名字），传成 <c>operator_id</c> 不会报错，
/// 只会静默查不到：<see cref="SqliteDb.Bind"/> 只绑定 SQL 中出现的参数，多传的被忽略、
/// 少传的留成 NULL，于是 <c>WHERE usercode = NULL</c> 命中 0 行。这坑真踩过一次
/// （界面里显示「未查到该工号」而工号其实是存在的）。放在 Data 层就能用一条用例把它钉住。
///
/// **契约**：不抛异常、查不到返回 null。调用方据此只做提示，绝不因姓名缺失阻断业务（§1.4）。
/// </summary>
public sealed class MesOperatorDirectory
{
    /// <summary>查询所用条目：正式 id，实际执行时按 MES 模式解析镜像条目（mes_mock.* / mes.*）。</summary>
    public const string SqlId = "mes.operator.get_by_id";

    /// <summary>catalog 条目的参数名。**不是** operator_id——见类注释。</summary>
    public const string ParameterName = "user_id";

    /// <summary>条目输出的姓名字段。</summary>
    public const string NameField = "operator_name";

    private readonly SqlCatalog _catalog;
    private readonly IMesGateway _mes;

    public MesOperatorDirectory(SqlCatalog catalog, IMesGateway mes)
    {
        _catalog = catalog;
        _mes = mes;
    }

    /// <summary>按工号取姓名；工号为空、条目缺失、MES 报错或查不到时一律返回 null。</summary>
    public string? TryGetName(string? operatorId)
    {
        if (string.IsNullOrWhiteSpace(operatorId))
            return null;

        try
        {
            var item = _catalog.FindForMode(SqlId, _mes.Mode);
            if (item is null)
                return null;

            var result = _mes.Run(
                item, new Dictionary<string, object?> { [ParameterName] = operatorId.Trim() });

            return result.HasError ? null : result.First?.GetValueOrDefault(NameField)?.ToString();
        }
        catch
        {
            // 姓名只是记录的锦上添花，任何时候都不该把异常抛给界面/业务
            return null;
        }
    }
}
