using Microsoft.Data.Sqlite;
using WireCabinet.Data;
using Xunit;

namespace WireCabinet.Tests;

public sealed class MesProtectionRegressionTests : IDisposable
{
    private const string Owner = "CABINET";
    private const string Lot = "AUDIT-LOT";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wc-mes-protect-{Guid.NewGuid():N}.db");
    private readonly SqliteDb _db;
    private readonly SqlCatalog _catalog = SqlCatalog.Load();

    public MesProtectionRegressionTests()
    {
        _db = new(_path); _db.Initialize(true); WireMesDiscoSchema.EnsureAll(_db);
        Assert.False(_db.Execute("UPDATE app_slot SET biz_state='idle',wire_lot_no=NULL;", new Dictionary<string, object?>()).HasError);
    }

    private void StoreLot() => Assert.False(_db.Execute(
        "UPDATE app_slot SET biz_state='available_wire',wire_lot_no=:lot WHERE slot_id=1;",
        new Dictionary<string, object?> { ["lot"] = Lot }).HasError);

    private (WireDiscoEqpSyncService Sync, MesDiscoReconciliationService Recon, Gateway Mes) Services(bool oracle = false, IAppDb? appDb = null)
    {
        var mes = new Gateway(_db, oracle);
        Assert.False(_db.Execute($"CREATE TABLE IF NOT EXISTS {mes.Table} (sublot TEXT, DISCOEQPNO TEXT);", new Dictionary<string, object?>()).HasError);
        Assert.False(_db.Execute($"INSERT INTO {mes.Table} (sublot, DISCOEQPNO) VALUES (:lot,:owner);",
            new Dictionary<string, object?> { ["lot"] = Lot, ["owner"] = Owner }).HasError);
        var db = appDb ?? new SqliteAppDb(_db);
        var sync = new WireDiscoEqpSyncService(_catalog, db, mes, new(_db), Owner);
        return (sync, new(db, mes, _catalog, sync), mes);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("mes")]
    public void FailedInventoryReadStopsEntireAutomaticReconciliation(string source)
    {
        StoreLot();
        var (sync, recon, mes) = Services(appDb: source == "app" ? new ReadFails(new SqliteAppDb(_db)) : null);
        mes.FailList = source == "mes";
        Assert.Throws<InvalidOperationException>(() => recon.ComputeDrift());
        Assert.Throws<InvalidOperationException>(() => recon.EnqueueAutoRepairForDrift());
        Assert.Equal(0, mes.Writes);
        Assert.Equal(Owner, mes.Value());
        Assert.Empty(sync.ListPending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresenceReadFailureNeverWritesMesAndRemainsPending(bool set)
    {
        StoreLot();
        var (sync, _, mes) = Services(appDb: new ReadFails(new SqliteAppDb(_db)));
        var result = set ? sync.TryMarkInCabinet(Lot) : sync.TryClearFromCabinet(Lot);
        Assert.False(result.Success); Assert.True(result.PendingRecorded);
        var row = Assert.Single(sync.ListPending());
        Assert.Contains("库存读取失败", row.LastError);
        Assert.False(sync.RetryPendingRow(row.Id).Success);
        Assert.Equal(0, mes.Writes); Assert.Equal(Owner, mes.Value());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherCabinetCannotBeClearedByInlineManualOrDelayedRetry(bool oracle)
    {
        var (sync, _, mes) = Services(oracle);
        mes.Assign("OTHER");
        Assert.False(sync.TryClearFromCabinet(Lot).Success);
        var row = Assert.Single(sync.ListPending());
        Assert.False(sync.RetryPendingRow(row.Id, true).Success);
        Assert.False(sync.ManualClear(Lot).Success);
        Assert.Equal("OTHER", mes.Value());
        Assert.Equal(0, mes.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnershipChangeBetweenReadAndWriteIsProtectedByActualSql(bool oracle)
    {
        var (sync, _, mes) = Services(oracle);
        mes.BeforeClear = () => mes.Assign("OTHER");
        var result = sync.TryClearFromCabinet(Lot);
        Assert.False(result.Success);
        Assert.Equal("OTHER", mes.Value());
        Assert.Equal(1, mes.Writes);
        Assert.Single(sync.ListPending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentClearToNullIsAnIdempotentSuccess(bool oracle)
    {
        var (sync, _, mes) = Services(oracle);
        mes.BeforeClear = () => mes.Assign(null);
        Assert.True(sync.TryClearFromCabinet(Lot).Success);
        Assert.Null(mes.Value());
        Assert.Empty(sync.ListPending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnCabinetCanClearAndRepeatedClearDoesNotCreatePending(bool oracle)
    {
        var (sync, _, mes) = Services(oracle);
        Assert.True(sync.TryClearFromCabinet(Lot).Success);
        Assert.True(sync.TryClearFromCabinet(Lot).Success);
        Assert.Null(mes.Value()); Assert.Equal(1, mes.Writes);
        Assert.Empty(sync.ListPending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedMesPreReadStillCannotEraseAnotherOwner(bool oracle)
    {
        var (sync, _, mes) = Services(oracle);
        mes.Assign("OTHER"); mes.FailQuery = true;
        Assert.False(sync.TryClearFromCabinet(Lot).Success);
        Assert.Equal("OTHER", mes.Value());
        Assert.Single(sync.ListPending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingMesLotIsNotReportedAsSuccessfullyCleared(bool oracle)
    {
        var (sync, _, mes) = Services(oracle);
        _db.Execute($"DELETE FROM {mes.Table};", new Dictionary<string, object?>());
        Assert.False(sync.TryClearFromCabinet(Lot).Success);
        Assert.Single(sync.ListPending());
    }

    private sealed class ReadFails(IAppDb inner) : IAppDb
    {
        public DbResult Run(SqlCatalogItem item, IDictionary<string, object?> p) =>
            item.Operation == SqlOperation.Read ? DbResult.Failure(item.Sql, "injected inventory read error") : inner.Run(item, p);
        public DbResult RunRaw(string sql, IDictionary<string, object?> p, bool isWrite) =>
            isWrite ? inner.RunRaw(sql, p, true) : DbResult.Failure(sql, "injected inventory read error");
    }

    // 正式 Oracle 条目的 SELECT/UPDATE 在 SQLite 临时表执行，验证实际 SQL 谓词。
    // 此替身不连接 Oracle，不替代现场 Oracle 驱动验证。
    private sealed class Gateway(SqliteDb db, bool oracle) : IMesGateway
    {
        public MesMode Mode => oracle ? MesMode.Oracle : MesMode.SqliteMock;
        public string Table => oracle ? "fw_material_indetail" : "v_fw_material_indetail";
        public bool FailList { get; set; }
        public bool FailQuery { get; set; }
        public Action? BeforeClear { get; set; }
        public int Writes { get; private set; }
        public DbResult Run(SqlCatalogItem item, IDictionary<string, object?> p)
        {
            if (FailList && item.Id.EndsWith("list_by_disco_eqp_no"))
                return DbResult.Failure(item.Sql, "injected MES list failure");
            if (FailQuery && item.Id.EndsWith("query_disco_by_lot"))
                return DbResult.Failure(item.Sql, "injected MES query failure");
            if (item.Operation == SqlOperation.Write)
            {
                Writes++;
                if (item.Id.EndsWith("clear_disco_eqp_no")) BeforeClear?.Invoke();
                return db.Execute(item.Sql, p);
            }
            return db.Query(item.Sql, p);
        }
        public void Assign(string? value) => Assert.False(db.Execute(
            $"UPDATE {Table} SET DISCOEQPNO=:owner WHERE sublot=:lot;",
            new Dictionary<string, object?> { ["owner"] = value, ["lot"] = Lot }).HasError);
        public string? Value()
        {
            var value = db.Query($"SELECT DISCOEQPNO FROM {Table} WHERE sublot=:lot;",
                new Dictionary<string, object?> { ["lot"] = Lot }).First?["DISCOEQPNO"];
            return value is null or DBNull ? null : value.ToString();
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
    }
}
