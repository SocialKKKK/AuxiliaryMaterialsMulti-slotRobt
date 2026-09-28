using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 记录表时间的时区换算（存 UTC、看本地）。
///
/// 这组用例存在的理由：只把**显示**转成本地、而筛选条件仍按 UTC 解释的话，
/// 操作员按墙上钟 17:00–18:00 去查，会一条都查不到（库里那条是 09:19），
/// 而这种错不会报错、只会「查不到」——最容易被当成「没记录」。
/// 所以两个方向各测一遍。
///
/// 时区用固定 +08:00（自定义时区），不依赖操作系统时区库：
/// 否则换个机器结果就变，而现场与开发机也未必同区。
/// </summary>
public class OperationLogTimeTests : IDisposable
{
    private static readonly TimeZoneInfo Plus8 =
        TimeZoneInfo.CreateCustomTimeZone("test+08", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-tz-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    // ---------- 换算本身 ----------

    [Fact]
    public void ToLocalText_ShiftsUtcForward()
    {
        // 库里 09:19:58 是 UTC，北京时间应为 17:19:58
        Assert.Equal("2026-09-22 17:19:58", OperationLogTime.ToLocalText("2026-09-22 09:19:58", Plus8));
    }

    [Fact]
    public void ToUtcText_ShiftsLocalBack()
    {
        Assert.Equal("2026-09-22 09:19:58", OperationLogTime.ToUtcText("2026-09-22 17:19:58", Plus8));
    }

    [Fact]
    public void Conversion_RoundTrips()
    {
        const string utc = "2026-09-22 23:45:01";

        var local = OperationLogTime.ToLocalText(utc, Plus8);
        var back = OperationLogTime.ToUtcText(local, Plus8);

        Assert.Equal(utc, back);
    }

    /// <summary>跨日跨界：UTC 的 9/21 21:00 在 +8 是 9/22 05:00。</summary>
    [Fact]
    public void ToLocalText_HandlesDateRollover()
    {
        Assert.Equal("2026-09-22 05:00:00", OperationLogTime.ToLocalText("2026-09-21 21:00:00", Plus8));
        Assert.Equal("2026-09-21 21:00:00", OperationLogTime.ToUtcText("2026-09-22 05:00:00", Plus8));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_StaysNull(string? text)
    {
        Assert.Null(OperationLogTime.ToLocalText(text, Plus8));
        Assert.Null(OperationLogTime.ToUtcText(text, Plus8));
    }

    /// <summary>
    /// 认不出的格式原样返回：展示层宁可显示原始值，也不该因为一条脏数据抛异常；
    /// 筛选条件原样下传则只是查不准，不会让查询报错。
    /// </summary>
    [Theory]
    [InlineData("not-a-time")]
    [InlineData("2026/09/22 09:19")]
    public void UnrecognizedFormat_PassesThrough(string text)
    {
        Assert.Equal(text, OperationLogTime.ToLocalText(text, Plus8));
        Assert.Equal(text, OperationLogTime.ToUtcText(text, Plus8));
    }

    [Fact]
    public void LegacyIsoUtcDoorTime_StillDisplaysInLocalTime()
    {
        Assert.Equal("2026-09-22 17:19:58",
            OperationLogTime.ToLocalText("2026-09-22T09:19:58.0000000Z", Plus8));
    }

    // ---------- 查询服务：两个方向都要转 ----------

    private OperationLogQueryService NewService(SqliteDb db) =>
        new(SqlCatalog.Load(), new SqliteAppDb(db), Plus8);

    private SqliteDb NewDb()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        OperationLogSchema.Ensure(db);
        return db;
    }

    /// <summary>库里存 UTC，读出来应是本地时间（含门开关时刻）。</summary>
    [Fact]
    public void Search_ReturnsLocalTimes()
    {
        var db = NewDb();
        var insert = db.Execute(
            """
            INSERT INTO app_operation_log (ts, action_type, lot_no, door_open_ts, door_close_ts, result)
            VALUES ('2026-09-22 09:19:58', 'op_take_wire', 'L1', '2026-09-22 09:18:00', '2026-09-22 09:19:50', '成功');
            """,
            new Dictionary<string, object?>());
        Assert.False(insert.HasError, insert.Error);

        var record = Assert.Single(NewService(db).Search().Records);

        Assert.Equal("2026-09-22 17:19:58", record.Ts);
        Assert.Equal("2026-09-22 17:18:00", record.DoorOpenTs);
        Assert.Equal("2026-09-22 17:19:50", record.DoorCloseTs);
    }

    /// <summary>
    /// 关键用例：按**本地**时刻筛选要能查到库里的 UTC 记录。
    /// 这正是「只转显示」会漏掉的那条路径。
    /// </summary>
    [Fact]
    public void Search_FiltersByLocalTime()
    {
        var db = NewDb();
        var insert = db.Execute(
            """
            INSERT INTO app_operation_log (ts, action_type, result)
            VALUES ('2026-09-22 09:19:58', 'mh_load_available_wire', '成功');
            """,
            new Dictionary<string, object?>());
        Assert.False(insert.HasError, insert.Error);

        var query = NewService(db);

        // 本地 17:00–18:00 应命中（记录发生在本地 17:19:58）
        Assert.Single(query.Search(new OperationLogFilter
        {
            TsFrom = "2026-09-22 17:00:00",
            TsTo = "2026-09-22 18:00:00",
        }).Records);

        // 本地 16:00–17:00 不应命中
        Assert.Empty(query.Search(new OperationLogFilter
        {
            TsFrom = "2026-09-22 16:00:00",
            TsTo = "2026-09-22 17:00:00",
        }).Records);

        // 本地 09:00–10:00（= UTC 的那一小时）同样不应命中——库里的值不是本地值
        Assert.Empty(query.Search(new OperationLogFilter
        {
            TsFrom = "2026-09-22 09:00:00",
            TsTo = "2026-09-22 10:00:00",
        }).Records);
    }

    /// <summary>按日期筛选代表的是一整个**本地**日。</summary>
    [Fact]
    public void Search_DateOnlyRange_CoversWholeLocalDay()
    {
        var db = NewDb();
        // 本地 9/22 00:30 = UTC 9/21 16:30；本地 9/22 23:30 = UTC 9/22 15:30
        foreach (var utc in new[] { "2026-09-21 16:30:00", "2026-09-22 15:30:00" })
        {
            var insert = db.Execute(
                "INSERT INTO app_operation_log (ts, action_type, result) VALUES (:ts, 'x', '成功');",
                new Dictionary<string, object?> { ["ts"] = utc });
            Assert.False(insert.HasError, insert.Error);
        }

        // UTC 9/22 16:30 = 本地 9/23 00:30，不在 9/22 这一天内
        var outside = db.Execute(
            "INSERT INTO app_operation_log (ts, action_type, result) VALUES ('2026-09-22 16:30:00', 'x', '成功');",
            new Dictionary<string, object?>());
        Assert.False(outside.HasError, outside.Error);

        var found = NewService(db).Search(new OperationLogFilter
        {
            TsFrom = "2026-09-22",
            TsTo = "2026-09-22",
        }).Records;

        Assert.Equal(2, found.Count);
    }

    /// <summary>导出与列表用同一套换算，否则导出的时间与界面看到的会差一个时区。</summary>
    [Fact]
    public void Export_UsesTheSameLocalTimes()
    {
        var db = NewDb();
        var insert = db.Execute(
            "INSERT INTO app_operation_log (ts, action_type, result) VALUES ('2026-09-22 09:19:58', 'x', '成功');",
            new Dictionary<string, object?>());
        Assert.False(insert.HasError, insert.Error);

        var exported = Assert.Single(NewService(db).ListForExport().Records);

        Assert.Equal("2026-09-22 17:19:58", exported.Ts);
    }
}
