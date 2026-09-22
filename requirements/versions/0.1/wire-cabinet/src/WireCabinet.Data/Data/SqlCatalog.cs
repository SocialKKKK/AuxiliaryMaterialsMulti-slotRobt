using System.IO;
using WireCabinet.Core;
using WireCabinet.Core.Infrastructure;

namespace WireCabinet.Data;

public enum SqlOperation { Read, Write, Function }

public enum SqlParamDirection { In, Out }

public sealed class SqlParamSpec
{
    public string Name { get; init; } = "";
    public string LogicalType { get; init; } = "string";
    public SqlParamDirection Direction { get; init; } = SqlParamDirection.In;
}

public sealed class SqlCatalogItem
{
    public string Id { get; init; } = "";
    public string Dialect { get; init; } = "sqlite";
    public string DataSource { get; init; } = "";
    public SqlOperation Operation { get; init; } = SqlOperation.Read;
    public string Sql { get; init; } = "";
    public string? OracleSql { get; init; }
    public string? MockKind { get; init; }
    public string? MockResultField { get; init; }
    public string BusinessMeaning { get; init; } = "";
    public IReadOnlyList<SqlParamSpec> Params { get; init; } = Array.Empty<SqlParamSpec>();

    public bool IsMes => Id.StartsWith("mes", StringComparison.OrdinalIgnoreCase);
    public bool IsApp => Id.StartsWith("app", StringComparison.OrdinalIgnoreCase);
}

/// <summary>加载 sql-catalog/items/*.yaml 形成 id -> item 的索引。</summary>
public sealed class SqlCatalog
{
    private readonly Dictionary<string, SqlCatalogItem> _items = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, SqlCatalogItem> Items => _items;

    public SqlCatalogItem? Find(string id) => _items.TryGetValue(id, out var it) ? it : null;

    /// <summary>
    /// 按 MES 模式解析条目。流程定义与代码里的 sql_id 是**逻辑**引用：mock 条目与正式条目
    /// 互为镜像（catalog 用 <c>mirrors</c> 标注），由这里按模式决定实际执行哪一份。
    ///
    /// 两个方向都要重定向，缺镜像时回退到原 id。此前 FlowEngine 与
    /// WireDiscoEqpSyncService 各写了一份，第三个调用方（界面解析操作人姓名）出现时
    /// 已无处可抄，故收敛到这里。
    /// </summary>
    public SqlCatalogItem? FindForMode(string sqlId, MesMode mode)
    {
        if (string.IsNullOrWhiteSpace(sqlId)) return null;

        var mirrored = MirrorId(sqlId, mode);
        return (mirrored is null ? null : Find(mirrored)) ?? Find(sqlId);
    }

    /// <summary>镜像 id：Oracle 模式把 mes_mock.* 指向正式 mes.*，其余模式反向。</summary>
    private static string? MirrorId(string sqlId, MesMode mode)
    {
        if (mode == MesMode.Oracle && sqlId.StartsWith("mes_mock.", StringComparison.OrdinalIgnoreCase))
            return "mes." + sqlId["mes_mock.".Length..];

        // 注意 "mes_mock." 不以 "mes." 开头（第 4 个字符是下划线），不会误判
        if (mode != MesMode.Oracle && sqlId.StartsWith("mes.", StringComparison.OrdinalIgnoreCase))
            return "mes_mock." + sqlId["mes.".Length..];

        return null;
    }

    public static SqlCatalog Load()
    {
        var catalog = new SqlCatalog();
        foreach (var itemsDir in new[] { CabinetPaths.LabSqlCatalogDir, CabinetPaths.FormalSqlCatalogDir })
        {
            if (!Directory.Exists(itemsDir)) continue;
            LoadDirectory(catalog, itemsDir);
        }
        return catalog;
    }

    private static void LoadDirectory(SqlCatalog catalog, string itemsDir)
    {
        foreach (var file in Directory.GetFiles(itemsDir, "*.yaml"))
        {
            var node = Yaml.Parse(File.ReadAllText(file));
            var id = Yaml.Str(node, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            var op = (Yaml.Str(node, "operation") ?? "read").ToLowerInvariant() switch
            {
                "write" => SqlOperation.Write,
                "function" => SqlOperation.Function,
                _ => SqlOperation.Read
            };

            var mockNode = Yaml.Get(node, "mock");
            var sql = (Yaml.Str(node, "sql") ?? "").Trim();

            catalog._items[id] = new SqlCatalogItem
            {
                Id = id,
                Dialect = Yaml.Str(node, "dialect") ?? "sqlite",
                DataSource = Yaml.Str(node, "datasource") ?? "",
                Operation = op,
                Sql = sql,
                OracleSql = Yaml.Str(node, "oracle_sql")?.Trim(),
                MockKind = Yaml.Str(mockNode, "kind"),
                MockResultField = Yaml.Str(mockNode, "result_field"),
                BusinessMeaning = Yaml.Str(node, "business_meaning") ?? "",
                Params = LoadParams(node!, op, sql)
            };
        }
    }

    private static List<SqlParamSpec> LoadParams(object node, SqlOperation op, string sql)
    {
        var list = new List<SqlParamSpec>();

        var inputs = Yaml.AsList(Yaml.Get(node, "inputs"));
        if (inputs is not null)
        {
            foreach (var im in inputs)
            {
                var name = Yaml.Str(im, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new SqlParamSpec
                {
                    Name = name,
                    LogicalType = Yaml.Str(im, "type") ?? "string",
                    Direction = SqlParamDirection.In
                });
            }
        }

        var paramsList = Yaml.AsList(Yaml.Get(node, "params"));
        if (paramsList is not null)
        {
            foreach (var pm in paramsList)
            {
                var name = Yaml.Str(pm, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (list.Exists(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add(new SqlParamSpec { Name = name, Direction = SqlParamDirection.In });
            }
        }

        if (op == SqlOperation.Function || sql.Contains(":result", StringComparison.Ordinal))
        {
            var outputs = Yaml.AsList(Yaml.Get(node, "outputs"));
            var addedOut = false;
            if (outputs is not null)
            {
                foreach (var om in outputs)
                {
                    var name = Yaml.Str(om, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!string.Equals(name, "result", StringComparison.OrdinalIgnoreCase)) continue;
                    if (list.Exists(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    list.Add(new SqlParamSpec
                    {
                        Name = name,
                        LogicalType = Yaml.Str(om, "type") ?? "string",
                        Direction = SqlParamDirection.Out
                    });
                    addedOut = true;
                }
            }

            if (!addedOut && !list.Exists(p => string.Equals(p.Name, "result", StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new SqlParamSpec
                {
                    Name = "result",
                    LogicalType = "string",
                    Direction = SqlParamDirection.Out
                });
            }
        }

        return list;
    }
}
