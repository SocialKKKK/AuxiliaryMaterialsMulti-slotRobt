using System.Text.Json;

namespace WireCabinet.Data;

/// <summary>一条待补偿的记录：写入 app_operation_log 失败后转存，等待后台或人工重试。</summary>
public sealed record OperationLogPendingRow(
    long Id,
    OperationLogEntry Entry,
    string Status,
    int RetryCount,
    DateTime? NextRetryAt,
    string? LastError,
    string? LastTryTs)
{
    /// <summary>是否还欠一次补偿（success 表示已补写成功，仅作留痕）。</summary>
    public bool IsActive => !string.Equals(Status, OperationLogPendingStore.StatusSuccess, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// operation_log_pending 的读写（0.2 §3.8 失败补偿）。
///
/// 队列是基础设施、不是业务 DML，故走 RunRaw 而不进 catalog——与
/// <see cref="SqliteOperationLogSink"/> 入队时的做法一致。
///
/// 时间统一用 UTC 的往返格式（"o"）存字符串：同为零时区偏移时字典序即时间序，
/// 可直接在 SQL 里比较（沿用 wire_mes_disco_sync 的做法）。
/// </summary>
public sealed class OperationLogPendingStore
{
    public const string StatusPending = "pending";
    public const string StatusFailed = "failed";
    public const string StatusSuccess = "success";

    private const string SelectColumns =
        "id, entry_json, status, retry_count, next_retry_at, last_error, last_try_ts";

    private readonly IAppDb _appDb;

    public OperationLogPendingStore(IAppDb appDb) => _appDb = appDb;

    /// <summary>入队一条待补偿。retry_count=0 且 next_retry_at 为空 ⇒ 立即可被重试。</summary>
    public void Enqueue(OperationLogEntry entry, string? error) =>
        _appDb.RunRaw(
            """
            INSERT INTO operation_log_pending (entry_json, status, retry_count, last_error)
            VALUES (:entry_json, 'pending', 0, :last_error);
            """,
            new Dictionary<string, object?>
            {
                ["entry_json"] = JsonSerializer.Serialize(entry),
                ["last_error"] = error,
            },
            isWrite: true);

    public OperationLogPendingRow? TryGet(long id) =>
        Read("WHERE id = :id;", new Dictionary<string, object?> { ["id"] = id }).FirstOrDefault();

    /// <summary>待补偿列表（含已达最大重试次数的 failed），供界面展示与人工重试。</summary>
    public IReadOnlyList<OperationLogPendingRow> ListActive() =>
        Read(
            $"WHERE status IN ('{StatusPending}', '{StatusFailed}') ORDER BY ts;",
            new Dictionary<string, object?>());

    /// <summary>到期待重试的行（仅 pending；failed 需人工触发）。</summary>
    public IReadOnlyList<OperationLogPendingRow> ListDueForRetry(DateTime nowUtc) =>
        Read(
            $"""
             WHERE status = '{StatusPending}'
               AND (next_retry_at IS NULL OR next_retry_at <= :now)
             ORDER BY next_retry_at;
             """,
            new Dictionary<string, object?> { ["now"] = Format(nowUtc) });

    /// <summary>安排下次重试（回到 pending 状态）。</summary>
    public void ScheduleRetry(long id, int retryCount, DateTime nextRetryUtc, string? error) =>
        Write(
            """
            UPDATE operation_log_pending
            SET status = 'pending', retry_count = :retry_count, next_retry_at = :next_retry_at,
                last_error = :last_error, last_try_ts = datetime('now')
            WHERE id = :id;
            """,
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["retry_count"] = retryCount,
                ["next_retry_at"] = Format(nextRetryUtc),
                ["last_error"] = error,
            });

    /// <summary>达到最大重试次数，转为 failed 等待人工处理。</summary>
    public void MarkFailed(long id, string? error, int retryCount) =>
        Write(
            """
            UPDATE operation_log_pending
            SET status = 'failed', retry_count = :retry_count, next_retry_at = NULL,
                last_error = :last_error, last_try_ts = datetime('now')
            WHERE id = :id;
            """,
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["retry_count"] = retryCount,
                ["last_error"] = error,
            });

    /// <summary>补写成功。保留行并置为 success 作留痕，不再参与重试。</summary>
    public void MarkSuccess(long id) =>
        Write(
            """
            UPDATE operation_log_pending
            SET status = 'success', next_retry_at = NULL, last_try_ts = datetime('now')
            WHERE id = :id;
            """,
            new Dictionary<string, object?> { ["id"] = id });

    private List<OperationLogPendingRow> Read(string tail, Dictionary<string, object?> parameters)
    {
        var result = _appDb.RunRaw($"SELECT {SelectColumns} FROM operation_log_pending {tail}", parameters, isWrite: false);

        var rows = new List<OperationLogPendingRow>();
        foreach (var row in result.Rows)
        {
            // entry_json 解析失败说明该行不可补偿：跳过而不是让整条重试链路崩掉。
            var json = row.GetValueOrDefault("entry_json")?.ToString();
            if (string.IsNullOrWhiteSpace(json)) continue;

            OperationLogEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<OperationLogEntry>(json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (entry is null) continue;

            rows.Add(new OperationLogPendingRow(
                Convert.ToInt64(row.GetValueOrDefault("id") ?? 0L),
                entry,
                row.GetValueOrDefault("status")?.ToString() ?? StatusPending,
                Convert.ToInt32(row.GetValueOrDefault("retry_count") ?? 0),
                ParseDate(row.GetValueOrDefault("next_retry_at")?.ToString()),
                row.GetValueOrDefault("last_error")?.ToString(),
                row.GetValueOrDefault("last_try_ts")?.ToString()));
        }
        return rows;
    }

    private void Write(string sql, Dictionary<string, object?> parameters) =>
        _appDb.RunRaw(sql, parameters, isWrite: true);

    private static string Format(DateTime utc) =>
        utc.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(string? text) =>
        DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
}
