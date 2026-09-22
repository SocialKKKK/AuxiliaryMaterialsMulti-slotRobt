using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// SQLite 连接与并发设置（0.2 §变更说明 §5.5 标为未做项，现场前补上）。
///
/// 为什么值得有测试：这两项设错了都**不会报错**——
/// 不开 WAL 只表现为界面卡顿（现场最难排查的那类问题），
/// busy_timeout 没设则要等默认上限才失败。所以用用例把它钉住。
/// </summary>
public class SqliteDbConcurrencyTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-conc-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix);
        GC.SuppressFinalize(this);
    }

    private SqliteDb NewDb()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        return db;
    }

    /// <summary>新库默认是 delete（rollback journal），设过之后才是 wal。</summary>
    [Fact]
    public void EnsureWal_SwitchesJournalModeToWal()
    {
        var db = NewDb();

        Assert.Equal("wal", db.EnsureWal(), ignoreCase: true);
        Assert.Equal("wal", db.JournalMode(), ignoreCase: true);
    }

    /// <summary>幂等：每次启动都会调，重复设置不得报错。</summary>
    [Fact]
    public void EnsureWal_IsIdempotent()
    {
        var db = NewDb();

        Assert.Equal("wal", db.EnsureWal(), ignoreCase: true);
        Assert.Equal("wal", db.EnsureWal(), ignoreCase: true);
        Assert.Equal("wal", db.JournalMode(), ignoreCase: true);
    }

    /// <summary>**关键**：已存在的库也要能切到 WAL——建库脚本只在首次建库时执行，
    /// 现场老库靠的就是这条路径。</summary>
    [Fact]
    public void EnsureWal_WorksOnExistingDatabase()
    {
        var db = NewDb();
        Assert.Equal("delete", db.JournalMode(), ignoreCase: true);   // 建库后的默认模式

        // 模拟"老库"：换一个 SqliteDb 实例重新打开同一个文件
        var reopened = new SqliteDb(_dbPath);
        Assert.Equal("wal", reopened.EnsureWal(), ignoreCase: true);
        Assert.Equal("wal", reopened.JournalMode(), ignoreCase: true);
    }

    /// <summary>
    /// busy_timeout 走连接串的 <c>DefaultTimeout</c>。
    ///
    /// 注意**不要**用 <c>PRAGMA busy_timeout</c> 去断言：Microsoft.Data.Sqlite 是把
    /// DefaultTimeout 作为命令执行时的忙等待上限（每个命令执行期间生效），
    /// 并不会把值持久化到连接上，所以 pragma 读出来是 0——那不是没设上。
    /// </summary>
    [Fact]
    public void Connection_CarriesBusyTimeout()
    {
        var db = NewDb();

        using var conn = db.Open();
        var parsed = new SqliteConnectionStringBuilder(conn.ConnectionString);

        Assert.Equal(SqliteDb.BusyTimeoutSeconds, parsed.DefaultTimeout);
    }

    /// <summary>忙等待上限必须是正数：设成 0 等于「一遇到锁就立刻失败」。</summary>
    [Fact]
    public void BusyTimeout_IsPositive()
    {
        Assert.True(SqliteDb.BusyTimeoutSeconds > 0);
    }

    /// <summary>WAL 下写入仍正常（别为了并发把写入搞坏）。</summary>
    [Fact]
    public void WalMode_WritesStillWork()
    {
        var db = NewDb();
        db.EnsureWal();

        var result = db.Execute(
            "INSERT INTO app_operation_log (action_type, result) VALUES ('x', '成功');",
            new Dictionary<string, object?>());

        Assert.False(result.HasError, result.Error);
        Assert.Equal(1, result.RowsAffected);
    }
}
