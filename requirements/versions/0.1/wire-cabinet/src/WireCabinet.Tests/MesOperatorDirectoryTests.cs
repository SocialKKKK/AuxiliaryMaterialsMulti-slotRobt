using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 按工号取操作人姓名（0.2 §3.3 的 operator_name 来源）。
///
/// 这组用例存在的理由：这段查询原先写在 MH 界面里，而界面层是 net8.0-windows、测试引用不了，
/// 于是一个「参数名传错」的错误一路溜到实机——界面上显示「未查到该工号」，而工号其实存在。
/// 移到 Data 层后这里能把参数名、模式解析与容错都钉住。
/// </summary>
public class MesOperatorDirectoryTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-opdir-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    /// <summary>mock MES：真正的 catalog + 真库 + SqliteMockMesGateway（与运行期同一套）。</summary>
    private (SqliteDb Db, MesOperatorDirectory Directory) NewDirectory()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        WireMesDiscoSchema.EnsureAll(db);
        WireMesMockSchema.Ensure(db);

        return (db, new MesOperatorDirectory(SqlCatalog.Load(), new SqliteMockMesGateway(db, new MesFunctionSettings())));
    }

    [Fact]
    public void ResolvesName_ForKnownOperator()
    {
        var (_, directory) = NewDirectory();

        Assert.Equal("张三", directory.TryGetName("1001"));
        Assert.Equal("李四", directory.TryGetName("1002"));
    }

    [Fact]
    public void ReturnsNull_ForUnknownOperator()
    {
        var (_, directory) = NewDirectory();

        Assert.Null(directory.TryGetName("9999"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNull_ForBlankOperatorId(string? operatorId)
    {
        var (_, directory) = NewDirectory();

        Assert.Null(directory.TryGetName(operatorId));
    }

    /// <summary>工号两侧的空白要容忍——扫码枪常常带空白。</summary>
    [Fact]
    public void TrimsOperatorId()
    {
        var (_, directory) = NewDirectory();

        Assert.Equal("张三", directory.TryGetName("  1001  "));
    }

    /// <summary>
    /// **参数名必须与 catalog 条目里的占位符一致**。传错了不报错、只是静默查不到，
    /// 所以在这里对着两个条目都核一遍（mock 与正式互为镜像，两边的名字都得对）。
    /// </summary>
    [Fact]
    public void ParameterAndNameFields_MatchCatalogItems()
    {
        var catalog = SqlCatalog.Load();

        var mock = catalog.Find("mes_mock.operator.get_by_id");
        Assert.NotNull(mock);
        Assert.Contains(":" + MesOperatorDirectory.ParameterName, mock!.Sql, StringComparison.Ordinal);
        Assert.Contains(MesOperatorDirectory.NameField, mock.Sql, StringComparison.Ordinal);

        var formal = catalog.Find(MesOperatorDirectory.SqlId);
        Assert.NotNull(formal);
        Assert.Contains(":" + MesOperatorDirectory.ParameterName, formal!.Sql, StringComparison.Ordinal);
    }

    /// <summary>mock 模式下引用正式 id 也要能解析到 mock 镜像条目（否则会拿 Oracle SQL 去跑 SQLite）。</summary>
    [Fact]
    public void MockMode_ResolvesToMockMirrorItem()
    {
        var catalog = SqlCatalog.Load();

        var item = catalog.FindForMode(MesOperatorDirectory.SqlId, MesMode.SqliteMock);

        Assert.NotNull(item);
        Assert.Equal("mes_mock.operator.get_by_id", item!.Id);
        Assert.DoesNotContain("NVL", item.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OracleMode_PrefersFormalItem()
    {
        var catalog = SqlCatalog.Load();

        Assert.Equal("mes.operator.get_by_id",
            catalog.FindForMode("mes_mock.operator.get_by_id", MesMode.Oracle)!.Id);
        Assert.Equal("mes.operator.get_by_id",
            catalog.FindForMode(MesOperatorDirectory.SqlId, MesMode.Oracle)!.Id);
    }

    /// <summary>MES 不可达时返回 null 而不是抛异常（界面据此只提示、不阻断存料）。</summary>
    [Fact]
    public void ReturnsNull_WhenMesUnavailable()
    {
        var (db, _) = NewDirectory();
        db.Execute("DROP TABLE mv_fw_username;", new Dictionary<string, object?>());

        var directory = new MesOperatorDirectory(SqlCatalog.Load(), new SqliteMockMesGateway(db, new MesFunctionSettings()));

        Assert.Null(directory.TryGetName("1001"));
    }
}
