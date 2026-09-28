namespace WireCabinet.Data;

/// <summary>应用数据库访问接口（真实 SQLite 执行）。</summary>
public interface IAppDb
{
    DbResult Run(SqlCatalogItem item, IDictionary<string, object?> parameters);
    DbResult RunRaw(string sql, IDictionary<string, object?> parameters, bool isWrite);
}

public sealed class SqliteAppDb : IAppDb
{
    private readonly SqliteDb _db;

    public SqliteAppDb(SqliteDb db) => _db = db;

    public DbResult Run(SqlCatalogItem item, IDictionary<string, object?> parameters)
        => item.Operation == SqlOperation.Write
            ? _db.Execute(item.Sql, parameters)
            : _db.Query(item.Sql, parameters);

    public DbResult RunRaw(string sql, IDictionary<string, object?> parameters, bool isWrite)
        => isWrite ? _db.Execute(sql, parameters) : _db.Query(sql, parameters);

    /// <summary>
    /// 补偿记录写入与待补偿状态更新必须同生同灭；否则插入成功、状态更新失败后
    /// 下一轮会重复插入，破坏「一次操作一条」。此事务只含记录基础设施，不含业务 DML。
    /// </summary>
    public DbResult InsertOperationLogAndCompletePending(
        SqlCatalogItem insert, IDictionary<string, object?> parameters, long pendingId)
    {
        var result = new DbResult();
        try
        {
            using var conn = _db.Open();
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = insert.Sql;
                SqliteDb.Bind(cmd, insert.Sql, parameters);
                if (cmd.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("记录未写入（影响行数不是 1）。");
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE operation_log_pending
                    SET status = 'success', next_retry_at = NULL, last_try_ts = datetime('now')
                    WHERE id = :id AND status IN ('pending', 'failed');
                    """;
                cmd.Parameters.AddWithValue(":id", pendingId);
                if (cmd.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("待补偿状态未更新（影响行数不是 1）。");
            }

            tx.Commit();
            result.RowsAffected = 1;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }
}
