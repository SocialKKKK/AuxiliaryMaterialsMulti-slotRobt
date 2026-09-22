using Microsoft.Extensions.Configuration;
using WireCabinet.Core;
using WireCabinet.Core.Config;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 本机开发环境开关的机器化验证。
///
/// 机制：AppBootstrap 根据 MesOptions.IsConfigured 选择网关——
///   连接串非空 → OracleMesGateway（内网真库）
///   连接串为空 → SqliteMockMesGateway（本机可离线跑通业务流程）
/// 所以把连接串清空就能在本机脱离内网开发；这两个测试保证该开关确实生效，
/// 且共享配置（appsettings.json）没有被改动。
/// </summary>
public class MesModeSelectionTests
{
    private static IConfiguration BuildConfig()
    {
        // 还原 AppBootstrap 的配置加载顺序：先共享、后本地覆盖
        var hmiDir = Path.Combine(CabinetPaths.VersionRoot, "src", "WireCabinet.Hmi");
        return new ConfigurationBuilder()
            .SetBasePath(hmiDir)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .Build();
    }

    [Fact]
    public void IsConfigured_IsFalse_WhenConnectionStringBlank()
    {
        Assert.False(new MesOptions { ConnectionString = "" }.IsConfigured);
        Assert.False(new MesOptions { ConnectionString = "   " }.IsConfigured);
    }

    [Fact]
    public void IsConfigured_IsTrue_WhenConnectionStringPresent()
    {
        Assert.True(new MesOptions { ConnectionString = "Data Source=x" }.IsConfigured);
    }

    [Fact]
    public void LocalSettings_BlankTheConnectionString_SoThisMachineUsesMockMes()
    {
        var localFile = Path.Combine(CabinetPaths.VersionRoot, "src", "WireCabinet.Hmi", "appsettings.Local.json");
        var config = BuildConfig();
        var effective = config["Mes:ConnectionString"];

        // appsettings.Local.json 是本机文件（已 gitignore），且按 optional 加载——
        // 新克隆的机器与**现场机器上它不存在**。那时「本机走 mock」这场景不适用，
        // 读到共享配置里的现场连接串才是预期；原实现一律断言为空，会在这些机器上假失败。
        if (!File.Exists(localFile))
        {
            Assert.False(
                string.IsNullOrWhiteSpace(effective),
                "没有本机配置时应读到共享配置里的现场连接串。");
            return;
        }

        Assert.True(
            string.IsNullOrWhiteSpace(effective),
            $"本机应以 mock MES 运行（连接串为空），实际读到：{effective}");
        Assert.False(new MesOptions { ConnectionString = effective ?? "" }.IsConfigured);
    }

    [Fact]
    public void SharedSettings_KeepTheOracleConnectionString_ForPlantMachines()
    {
        // 共享配置必须保持现场真库连接串，不能因为本机开发而被改掉
        var sharedDir = Path.Combine(CabinetPaths.VersionRoot, "src", "WireCabinet.Hmi");
        var shared = new ConfigurationBuilder()
            .SetBasePath(sharedDir)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        Assert.False(string.IsNullOrWhiteSpace(shared["Mes:ConnectionString"]));
    }
}
