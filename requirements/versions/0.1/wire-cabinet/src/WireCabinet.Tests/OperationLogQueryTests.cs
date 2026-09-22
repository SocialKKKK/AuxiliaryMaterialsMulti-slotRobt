using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 记录查询与追溯（0.2 需求范围 §3.4 列表 / §3.6 按批号追溯 / §3.5 导出范围）。
///
/// 数据用直接 INSERT 造（本组用例测的是读，需要可控的 ts 与字段值，
/// 走 sink 写入只能拿到 datetime('now')，排不出确定的先后）。
/// </summary>
public class OperationLogQueryTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-query-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private static OperationLogQueryService NewService(SqliteDb db) =>
        new(SqlCatalog.Load(), new SqliteAppDb(db), TimeZoneInfo.Utc);

    private (SqliteDb Db, OperationLogQueryService Query) NewQuery()
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        OperationLogSchema.Ensure(db);
        // 注入 UTC：本组用例测的是筛选与分页，注入固定时区后断言不随开发机时区变化。
        // 本地/UTC 的换算另有 OperationLogTimeTests 与 ZoneConversion 专项用例覆盖。
        return (db, NewService(db));
    }

    private static void Add(SqliteDb db, long id, string ts, string lot, string slot, string op, string eqp, string action)
    {
        var result = db.Execute(
            """
            INSERT INTO app_operation_log (id, ts, lot_no, slot_no, operator_id, eqp_no, action_type, result, wire_spec)
            VALUES (:id, :ts, :lot, :slot, :op, :eqp, :action, '成功', 'φ42(MAXSOFT)');
            """,
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["ts"] = ts,
                ["lot"] = lot,
                ["slot"] = slot,
                ["op"] = op,
                ["eqp"] = eqp,
                ["action"] = action,
            });
        Assert.False(result.HasError, result.Error);
    }

    /// <summary>四条覆盖不同批号 / 格口 / 操作人 / 机台 / 动作 / 时间的记录。</summary>
    private static void SeedFour(SqliteDb db)
    {
        Add(db, 1, "2026-09-01 08:00:00", "L1", "F-A01", "1001", "1QH029", "mh_load_available_wire");
        Add(db, 2, "2026-09-02 09:00:00", "L1", "F-A01", "1001", "1QH029", "op_take_wire");
        Add(db, 3, "2026-09-03 10:00:00", "L2", "F-B02", "1002", "1QH030", "op_return_wire");
        Add(db, 4, "2026-09-04 11:00:00", "L2", "F-B02", "1002", "1QH030", "op_issue_available_wire");
    }

    private static long[] Ids(OperationLogQueryResult r)
    {
        Assert.True(r.Success, r.Error);
        return r.Records.Select(x => x.Id).ToArray();
    }

    [Fact]
    public void ReadSqlItems_ResolveInCatalog()
    {
        var catalog = SqlCatalog.Load();

        Assert.NotNull(catalog.Find(OperationLogQueryService.SearchSqlId));
        Assert.NotNull(catalog.Find(OperationLogQueryService.ExportSqlId));
        Assert.NotNull(catalog.Find(OperationLogQueryService.TimelineSqlId));
    }

    [Fact]
    public void NoFilter_ReturnsAll_NewestFirst()
    {
        var (db, query) = NewQuery();
        SeedFour(db);

        Assert.Equal(new long[] { 4, 3, 2, 1 }, Ids(query.Search()));
    }

    /// <summary>§3.4：六项条件必须**各自单独生效**。</summary>
    [Theory]
    [InlineData("ts_from", "2026-09-03", new long[] { 4, 3 })]
    [InlineData("ts_to", "2026-09-02", new long[] { 2, 1 })]
    [InlineData("lot_no", "L2", new long[] { 4, 3 })]
    [InlineData("slot_no", "F-A01", new long[] { 2, 1 })]
    [InlineData("operator_id", "1002", new long[] { 4, 3 })]
    [InlineData("eqp_no", "1QH030", new long[] { 4, 3 })]
    [InlineData("action_type", "op_take_wire", new long[] { 2 })]
    public void EachFilter_WorksOnItsOwn(string field, string value, long[] expected)
    {
        var (db, query) = NewQuery();
        SeedFour(db);

        var filter = field switch
        {
            "ts_from" => new OperationLogFilter { TsFrom = value },
            "ts_to" => new OperationLogFilter { TsTo = value },
            "lot_no" => new OperationLogFilter { LotNo = value },
            "slot_no" => new OperationLogFilter { SlotNo = value },
            "operator_id" => new OperationLogFilter { OperatorId = value },
            "eqp_no" => new OperationLogFilter { EqpNo = value },
            _ => new OperationLogFilter { ActionType = value },
        };

        Assert.Equal(expected, Ids(query.Search(filter)));
    }

    [Fact]
    public void Filters_Combine()
    {
        var (db, query) = NewQuery();
        SeedFour(db);

        Assert.Equal(new long[] { 3 }, Ids(query.Search(new OperationLogFilter
        {
            LotNo = "L2",
            ActionType = "op_return_wire",
        })));

        // 组合条件互相冲突时应为空，而不是退化成任一条件
        Assert.Empty(Ids(query.Search(new OperationLogFilter
        {
            LotNo = "L1",
            EqpNo = "1QH030",
        })));
    }

    /// <summary>界面上清空输入框应视为「不限」，不能反而查不到数据。</summary>
    [Fact]
    public void BlankFilterValues_MeanUnrestricted()
    {
        var (db, query) = NewQuery();
        SeedFour(db);

        Assert.Equal(4, Ids(query.Search(new OperationLogFilter
        {
            LotNo = "   ",
            SlotNo = "",
            ActionType = null,
        })).Length);
    }

    [Fact]
    public void Search_PagesAt20PerPage()
    {
        var (db, query) = NewQuery();
        for (var i = 1; i <= 25; i++)
            Add(db, i, $"2026-09-01 00:00:{i:00}", "L1", "F-A01", "1001", "1QH029", "mh_load_available_wire");

        var page1 = query.Search();
        var page2 = query.Search(page: 2);

        Assert.Equal(OperationLogQueryService.DefaultPageSize, page1.Records.Count);
        Assert.Equal(5, page2.Records.Count);
        Assert.Equal(25, page1.Records[0].Id);   // 第 1 页从最新开始
        Assert.Equal(5, page2.Records[0].Id);    // 第 2 页接在后面，不重不漏
        Assert.Empty(page1.Records.Select(r => r.Id).Intersect(page2.Records.Select(r => r.Id)));
    }

    /// <summary>§3.6：按批号追溯，时间正序；批号不存在返回 0 行而不是错误。</summary>
    [Fact]
    public void Timeline_IsAscendingAndEmptyForUnknownLot()
    {
        var (db, query) = NewQuery();
        SeedFour(db);

        var timeline = query.Timeline("L1");
        Assert.Equal(new long[] { 1, 2 }, Ids(timeline));

        var unknown = query.Timeline("LOT-NOT-EXIST");
        Assert.True(unknown.Success);
        Assert.Empty(unknown.Records);
    }

    [Fact]
    public void Timeline_WithoutLotNo_ReportsWhy()
    {
        var (_, query) = NewQuery();

        var result = query.Timeline("  ");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    /// <summary>
    /// 界面上选的是「日期」，而 ts 精确到秒：结束侧必须含当天，否则
    /// 「结束于 2026-09-02」会把当天 09:00 的那条漏掉（字符串比较下更长的排在后）。
    /// </summary>
    [Theory]
    [InlineData("2026-09-02", new long[] { 2, 1 })]          // 日期精度：含当天全天
    [InlineData("2026-09-02 09:00:00", new long[] { 2, 1 })]
    [InlineData("2026-09-02 08:59:59", new long[] { 1 })]
    [InlineData("2026-09-02 09:00", new long[] { 2, 1 })]     // 分钟精度：补到该分钟末
    public void TsTo_IsInclusive(string tsTo, long[] expected)
    {
        var (db, query) = NewQuery();
        SeedFour(db);

        Assert.Equal(expected, Ids(query.Search(new OperationLogFilter { TsTo = tsTo })));
    }

    [Fact]
    public void TsFrom_IsInclusiveOfThatDay()
    {
        var (db, query) = NewQuery();
        SeedFour(db);

        Assert.Equal(new long[] { 4, 3 }, Ids(query.Search(new OperationLogFilter { TsFrom = "2026-09-03" })));
    }

    /// <summary>§5 异常：起止颠倒要给中文提示，而不是静默返回 0 行。</summary>
    [Fact]
    public void ReversedTimeRange_ReportsWhy()
    {
        var (db, query) = NewQuery();
        SeedFour(db);

        var filter = new OperationLogFilter { TsFrom = "2026-09-10", TsTo = "2026-09-01" };

        var searched = query.Search(filter);
        Assert.False(searched.Success);
        Assert.Contains("起始晚于结束", searched.Error);

        Assert.False(query.ListForExport(filter).Success);
    }

    /// <summary>§3.5：导出与列表同一套条件，但不受分页限制。</summary>
    [Fact]
    public void Export_UsesSameFilterWithoutPaging()
    {
        var (db, query) = NewQuery();
        for (var i = 1; i <= 25; i++)
            Add(db, i, $"2026-09-01 00:00:{i:00}", i <= 5 ? "L1" : "L2", "F-A01", "1001", "1QH029", "mh_load_available_wire");

        var export = query.ListForExport(new OperationLogFilter { LotNo = "L1" });

        Assert.True(export.Success, export.Error);
        Assert.Equal(5, export.Records.Count);                  // 超过一页也全给
        Assert.All(export.Records, r => Assert.Equal("L1", r.LotNo));
    }

    [Fact]
    public void Record_FieldsAreMapped()
    {
        var (db, query) = NewQuery();
        SeedFour(db);

        var record = query.Search(new OperationLogFilter { ActionType = "op_take_wire" }).Records.Single();

        Assert.Equal(2, record.Id);
        Assert.Equal("2026-09-02 09:00:00", record.Ts);
        Assert.Equal("L1", record.LotNo);
        Assert.Equal("F-A01", record.SlotNo);
        Assert.Equal("1001", record.OperatorId);
        Assert.Equal("1QH029", record.EqpNo);
        Assert.Equal("φ42(MAXSOFT)", record.WireSpec);
        Assert.Equal("成功", record.Result);
        Assert.False(record.IsCleared);
        Assert.False(record.IsUncommanded);
    }

    /// <summary>§5 异常：查询失败要能给出中文提示且不抛异常。</summary>
    [Fact]
    public void Query_WhenDatabaseFails_ReturnsFailureInsteadOfThrowing()
    {
        var (db, query) = NewQuery();
        db.Execute("ALTER TABLE app_operation_log RENAME TO app_operation_log_broken;", new Dictionary<string, object?>());

        var result = query.Search();

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(result.Records);
    }

    /// <summary>catalog 缺条目属于部署问题，不能静默当成「查不到数据」。</summary>
    [Fact]
    public void Query_WhenCatalogItemMissing_ReportsWhy()
    {
        var (db, _) = NewQuery();
        var query = new OperationLogQueryService(new SqlCatalog(), new SqliteAppDb(db), TimeZoneInfo.Utc);

        var result = query.Search();

        Assert.False(result.Success);
        Assert.Contains("SQL 条目", result.Error);
    }
}
