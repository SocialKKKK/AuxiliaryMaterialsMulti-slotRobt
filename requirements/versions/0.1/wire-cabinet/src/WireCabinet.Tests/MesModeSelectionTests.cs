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
/// 且共享配置（appsettings.json）不含现场凭据。
/// </summary>
public class MesModeSelectionTests
{
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
    public void LocalSettings_CanOverrideSharedConnectionStringWithBlank()
    {
        // 不依赖开发机上是否存在被 gitignore 的本地文件，也不把真实连接串写进失败输出。
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Mes:ConnectionString"] = "Data Source=plant" })
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Mes:ConnectionString"] = "" })
            .Build();

        Assert.False(new MesOptions { ConnectionString = config["Mes:ConnectionString"] ?? "" }.IsConfigured);
    }

    [Fact]
    public void SharedSettings_DoNotEmbedPlantCredentials()
    {
        // 现场连接串与 RCS 凭据只放在不提交的本地覆盖文件中。
        var sharedDir = Path.Combine(CabinetPaths.VersionRoot, "src", "WireCabinet.Hmi");
        var shared = new ConfigurationBuilder()
            .SetBasePath(sharedDir)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        Assert.True(string.IsNullOrWhiteSpace(shared["Mes:ConnectionString"]));
        Assert.True(string.IsNullOrWhiteSpace(shared["AgvDispatch:Password"]));
    }
}
