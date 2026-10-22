using AgvDispatch.Sdk.Models;
using WireCabinet.Rcs;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 按站点名称解析站点号（2026-10-22 地图换版后 522 消失的根治）。
/// 现场确认的名称：MH = 物料多仓位2停靠点、OP = 站点597、充电 = 物料多仓位充电点。
/// </summary>
public class StationNameResolverTests
{
    [Fact]
    public void BuildMap_DropsDuplicateNames()
    {
        var map = StationNameResolver.BuildMap([
            new MapStationDto { Id = 643, Name = "物料多仓位2停靠点" },
            new MapStationDto { Id = 644, Name = "物料多仓位2停靠点" },
            new MapStationDto { Id = 597, Name = "站点597" },
            new MapStationDto { Id = 0, Name = "无效零号" },
            new MapStationDto { Id = 1, Name = "  " },
        ], out var duplicates);

        Assert.False(map.ContainsKey("物料多仓位2停靠点"));
        Assert.Equal(597, map["站点597"]);
        Assert.Single(duplicates);
    }

    [Fact]
    public async Task Resolve_ByNames_UpdatesConfiguredIds()
    {
        var client = new FakeAgvClient();
        client.MapStations.AddRange([
            new MapStationDto { Id = 643, Name = "物料多仓位2停靠点" },
            new MapStationDto { Id = 597, Name = "站点597" },
            new MapStationDto { Id = 520, Name = "物料多仓位充电点" },
        ]);

        var stations = new StationConfiguration
        {
            WorkStations =
            [
                new WorkStationConfig
                {
                    Code = "WS_MH_STORE", Name = "物料间焊丝柜（MH）",
                    RcsDestination = 522, RcsStationName = "物料多仓位2停靠点"
                },
                new WorkStationConfig
                {
                    Code = "WS_OP_ISSUE", Name = "线边焊丝柜（OP）",
                    RcsDestination = 597, RcsStationName = "站点597"
                },
            ],
            ChargeStation = new ChargeStationConfig
            {
                Code = "CHG_WIRE_01", Name = "物料多仓位充电点",
                RcsDestination = 520, RcsStationName = "物料多仓位充电点"
            }
        };

        var resolver = new StationNameResolver(client, 22);
        Assert.True(await resolver.ResolveAsync(stations));
        Assert.Null(resolver.LastError);

        Assert.Equal(643, stations.WorkStations[0].RcsDestination);
        Assert.Equal(597, stations.WorkStations[1].RcsDestination);
        Assert.Equal(520, stations.ChargeStation!.RcsDestination);
    }

    [Fact]
    public async Task Resolve_MissingName_FailsClosedAndKeepsConfiguredId()
    {
        var client = new FakeAgvClient();
        client.MapStations.Add(new MapStationDto { Id = 643, Name = "物料多仓位2停靠点" });

        var stations = new StationConfiguration
        {
            WorkStations =
            [
                new WorkStationConfig
                {
                    Code = "WS_MH_STORE", Name = "物料间焊丝柜（MH）",
                    RcsDestination = 522, RcsStationName = "物料多仓位2停靠点"
                },
                new WorkStationConfig
                {
                    Code = "WS_OP_ISSUE", Name = "线边焊丝柜（OP）",
                    RcsDestination = 597, RcsStationName = "站点597"
                },
            ]
        };

        var resolver = new StationNameResolver(client, 22);
        Assert.False(await resolver.ResolveAsync(stations));

        Assert.Contains("站点597", resolver.LastError);
        Assert.Equal(643, stations.WorkStations[0].RcsDestination); // 能解析的仍更新
        Assert.Equal(597, stations.WorkStations[1].RcsDestination); // 找不到的保持原值（由调用方阻断）
    }

    [Fact]
    public async Task Resolve_DuplicateNameInMap_FailsClosed()
    {
        var client = new FakeAgvClient();
        client.MapStations.AddRange([
            new MapStationDto { Id = 643, Name = "物料多仓位2停靠点" },
            new MapStationDto { Id = 645, Name = "物料多仓位2停靠点" },
        ]);

        var stations = new StationConfiguration
        {
            WorkStations =
            [
                new WorkStationConfig
                {
                    Code = "WS_MH_STORE", Name = "物料间焊丝柜（MH）",
                    RcsDestination = 522, RcsStationName = "物料多仓位2停靠点"
                },
            ]
        };

        var resolver = new StationNameResolver(client, 22);
        Assert.False(await resolver.ResolveAsync(stations));
        Assert.Contains("找不到（或重名）", resolver.LastError);
        Assert.Equal(522, stations.WorkStations[0].RcsDestination);
    }

    [Fact]
    public async Task Resolve_NoNamesConfigured_IsNoOp()
    {
        var client = new FakeAgvClient();
        var stations = new StationConfiguration
        {
            WorkStations =
            [
                new WorkStationConfig { Code = "WS_MH_STORE", Name = "MH", RcsDestination = 643 }
            ]
        };

        var resolver = new StationNameResolver(client, 22);
        Assert.True(await resolver.ResolveAsync(stations));
        Assert.Equal(643, stations.WorkStations[0].RcsDestination);
    }
}
