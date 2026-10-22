using AgvDispatch.Sdk;
using AgvDispatch.Sdk.Models;

namespace WireCabinet.Rcs;

/// <summary>
/// 按站点名称解析当前地图上的站点号（2026-10-22 地图换版后 522 消失的根治）。
///
/// RCS 下单接口只收整数 destination，所以不能把名称直接发下去；这里在 HMI 侧用
/// <c>GET /api/imap/v1/mapInfo/stations/{mapId}</c> 拉当前地图站点，按名称精确匹配，
/// 把解析出的号回写到 <see cref="WorkStationConfig.RcsDestination"/> /
/// <see cref="ChargeStationConfig.RcsDestination"/>，后续发车/充电全部使用解析后的号。
///
/// 规则：名称去空格后精确匹配；找不到或重名一律视为失败（fail-closed），
/// 绝不猜“最近的站点”。
/// </summary>
public sealed class StationNameResolver
{
    private readonly IAgvDispatchClient _client;
    private readonly int _mapId;
    private Dictionary<string, int>? _byName;

    public StationNameResolver(IAgvDispatchClient client, int mapId)
    {
        _client = client;
        _mapId = mapId > 0 ? mapId : 22;
    }

    /// <summary>最近一次解析错误（为空表示全部解析成功，或没有配置名称）。</summary>
    public string? LastError { get; private set; }

    public IReadOnlyDictionary<string, int>? Resolved => _byName;

    public void Invalidate()
    {
        _byName = null;
        LastError = null;
    }

    /// <summary>解析所有配置了 <c>rcs_station_name</c> 的作业站与充电点；返回是否全部成功。</summary>
    public async Task<bool> ResolveAsync(StationConfiguration stations, CancellationToken ct = default)
    {
        LastError = null;

        var named = stations.WorkStations
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.RcsStationName))
            .ToList();
        var charge = stations.ChargeStation is { Enabled: true } c
                     && !string.IsNullOrWhiteSpace(c.RcsStationName)
            ? c
            : null;

        if (named.Count == 0 && charge is null)
            return true;

        var map = await LoadAsync(ct).ConfigureAwait(false);
        if (map is null)
            return false;

        var errors = new List<string>();
        foreach (var ws in named)
        {
            var name = ws.RcsStationName!.Trim();
            if (map.TryGetValue(name, out var id))
            {
                ws.RcsDestination = id;
            }
            else
            {
                errors.Add($"作业站「{ws.Name}」按名称「{name}」在地图 {_mapId} 上找不到（或重名），已阻止发车");
            }
        }

        if (charge is not null)
        {
            var name = charge.RcsStationName!.Trim();
            if (map.TryGetValue(name, out var id))
                charge.RcsDestination = id;
            else
                errors.Add($"充电点「{charge.Name}」按名称「{name}」在地图 {_mapId} 上找不到（或重名）");
        }

        LastError = errors.Count == 0 ? null : string.Join("；", errors);
        return LastError is null;
    }

    private async Task<Dictionary<string, int>?> LoadAsync(CancellationToken ct)
    {
        if (_byName is not null)
            return _byName;

        try
        {
            var list = await _client.GetMapStationsAsync(_mapId, ct).ConfigureAwait(false);
            _byName = BuildMap(list, out var duplicates);
            if (duplicates.Count > 0 && LastError is null)
                LastError = "地图上存在重名站点：" + string.Join("、", duplicates);
            return _byName;
        }
        catch (Exception ex)
        {
            LastError = $"读取 RCS 地图站点失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>构建“名称→站点号”字典；重名一律剔除（宁可阻断也不猜）。</summary>
    public static Dictionary<string, int> BuildMap(IEnumerable<MapStationDto> stations, out List<string> duplicates)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        duplicates = new List<string>();
        var flagged = new HashSet<string>(StringComparer.Ordinal);

        foreach (var station in stations)
        {
            var name = (station.Name ?? "").Trim();
            if (name.Length == 0 || station.Id <= 0)
                continue;

            if (!map.TryAdd(name, station.Id))
            {
                if (flagged.Add(name))
                    duplicates.Add(name);
            }
        }

        foreach (var name in duplicates)
            map.Remove(name);

        return map;
    }
}
