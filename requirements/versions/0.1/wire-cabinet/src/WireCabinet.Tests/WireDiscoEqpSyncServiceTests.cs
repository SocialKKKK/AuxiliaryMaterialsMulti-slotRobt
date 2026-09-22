using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// MES DISCOEQPNO 同步的“成功”判定。
///
/// 背景：UPDATE 影响 0 行时 Oracle 与 SQLite **都不报错**，但业务上就是失败
/// （sublot 在 MES 里不存在）。因此只判 <c>HasError</c> 会把这种失败报成成功，
/// 还会把 wire_mes_disco_sync 标成 success、给 ManualSet 落一条谎报的审计记录。
/// 0.2 的存取记录正建立在这一层之上，所以这里必须诚实。
///
/// mock 侧把 MES 表建模成空的 v_fw_material_indetail（由 WireMesDiscoSchema 建），
/// 因此“更新 0 行”在本机就能真实复现。
/// </summary>
public class WireDiscoEqpSyncServiceTests : IDisposable
{
    private const string DiscoEqpNo = "DISCO-01";
    private const string MockMesTable = "v_fw_material_indetail";

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"wctest-disco-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 建库并装配服务。availableLots 会插成柜内可用焊丝（SET 的前置条件）。
    /// </summary>
    private (SqliteDb Db, WireDiscoEqpSyncService Svc) NewService(
        Func<SqliteMockMesGateway, IMesGateway>? gateway = null,
        params string[] availableLots)
    {
        var db = new SqliteDb(_dbPath);
        db.Initialize(recreate: true);
        WireMesDiscoSchema.EnsureAll(db);

        // 用 T- 前缀与 9000+ id 避开 seed 已占用的 F-Axx 格口（否则唯一键冲突，
        // 而 SqliteDb.Execute 会把异常吞成返回值，插入会静默失败）。
        for (var i = 0; i < availableLots.Length; i++)
        {
            var insert = db.Execute(
                """
                INSERT INTO app_slot (slot_id, slot_no, usage_type, biz_state, wire_lot_no)
                VALUES (:id, :no, 'available', 'available_wire', :lot);
                """,
                new Dictionary<string, object?>
                {
                    ["id"] = 9001 + i,
                    ["no"] = $"T-A{i + 1:00}",
                    ["lot"] = availableLots[i],
                });
            Assert.False(insert.HasError, insert.Error);
        }

        var mock = new SqliteMockMesGateway(db, new MesFunctionSettings());
        var svc = new WireDiscoEqpSyncService(
            SqlCatalog.Load(),
            new SqliteAppDb(db),
            gateway?.Invoke(mock) ?? mock,
            new WireMesDiscoSyncStore(db),
            DiscoEqpNo,
            new MesReconAuditLogStore(db));

        return (db, svc);
    }

    private static string? PendingStatus(SqliteDb db, string lot, string action) =>
        db.Query(
            "SELECT status FROM wire_mes_disco_sync WHERE wire_lot_no = :lot AND action = :action;",
            new Dictionary<string, object?> { ["lot"] = lot, ["action"] = action }).First?["status"]?.ToString();

    private static string? MesDiscoValue(SqliteDb db, string lot) =>
        db.Query(
            $"SELECT DISCOEQPNO FROM {MockMesTable} WHERE sublot = :lot;",
            new Dictionary<string, object?> { ["lot"] = lot }).First?["DISCOEQPNO"]?.ToString();

    [Fact]
    public void TryMarkInCabinet_WhenMesUpdateHitsNoRow_IsNotReportedAsSuccess()
    {
        // MES 里没有这个 sublot → UPDATE 影响 0 行（Oracle 与 SQLite 都不报错）
        var (db, svc) = NewService(availableLots: ["LOT-SET-0ROW"]);

        var result = svc.TryMarkInCabinet("LOT-SET-0ROW");

        Assert.False(result.Success, "MES 未更新任何行（sublot 不存在），不得报成功。");
        // 也不能被标成已同步，必须留在待补偿队列里等重试
        Assert.Equal("pending", PendingStatus(db, "LOT-SET-0ROW", "set"));
    }

    [Fact]
    public void ManualSet_WhenMesUpdateHitsNoRow_DoesNotClaimSuccessOrWriteAudit()
    {
        var (db, svc) = NewService();

        var result = svc.ManualSet("LOT-SET-0ROW");

        Assert.False(result.Success, "手工 SET 同样不得把 0 行更新报成成功。");

        // 关键：审计流水是操作痕迹，不能落一条“改过 MES”的假记录
        var audit = db.Query(
            "SELECT COUNT(*) AS c FROM mes_recon_audit_log WHERE action = 'manual_set';",
            new Dictionary<string, object?>()).First;
        Assert.Equal(0L, Convert.ToInt64(audit!["c"]));
    }

    [Fact]
    public void TryClearFromCabinet_WhenMesUnreadable_IsNotReportedAsSuccess()
    {
        var (db, svc) = NewService();
        // MES 不可达：读不到当前值，也写不进去
        db.Execute($"DROP TABLE {MockMesTable};", new Dictionary<string, object?>());

        var result = svc.TryClearFromCabinet("LOT-CLR-NOSTATE");

        Assert.False(result.Success, "读不到 MES 当前值就不得断言“已是 NULL”。");
        Assert.Equal("pending", PendingStatus(db, "LOT-CLR-NOSTATE", "clear"));
    }

    /// <summary>
    /// 前置读取失败（查询报错）不等于“MES 已是 NULL”。修好之前，CLEAR 会
    /// 直接短路报成功，MES 里的 DISCOEQPNO 原地不动 —— 典型的假成功。
    /// </summary>
    [Fact]
    public void TryClearFromCabinet_WhenPreCheckReadFails_StillActuallyClearsMes()
    {
        var (db, svc) = NewService(gateway: m => new QueryFailsGateway(m));
        db.Execute(
            $"INSERT INTO {MockMesTable} (sublot, DISCOEQPNO) VALUES ('LOT-CLR', '{DiscoEqpNo}');",
            new Dictionary<string, object?>());

        var result = svc.TryClearFromCabinet("LOT-CLR");

        Assert.True(result.Success, result.UserMessage);
        Assert.Null(MesDiscoValue(db, "LOT-CLR")); // MES 必须真的被清空，而不是被短路跳过
    }

    /// <summary>让按批号查询 MES 当前值固定失败，其余调用透传。</summary>
    private sealed class QueryFailsGateway : IMesGateway
    {
        private readonly SqliteMockMesGateway _inner;

        public QueryFailsGateway(SqliteMockMesGateway inner) => _inner = inner;

        public MesMode Mode => MesMode.SqliteMock;

        public DbResult Run(SqlCatalogItem item, IDictionary<string, object?> parameters) =>
            item.Id.Contains("query_disco", StringComparison.OrdinalIgnoreCase)
                ? DbResult.Failure(item.Sql, "ORA-12170: TNS:Connect timeout occurred")
                : _inner.Run(item, parameters);
    }
}
