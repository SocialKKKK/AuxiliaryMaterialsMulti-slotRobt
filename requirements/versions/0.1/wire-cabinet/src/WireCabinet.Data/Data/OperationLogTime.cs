namespace WireCabinet.Data;

/// <summary>
/// 记录表时间列的时区换算。
///
/// **存 UTC、看本地**：`app_operation_log.ts` 的默认值是 SQLite 的 `datetime('now')`，那是 UTC；
/// 而操作员对着的是墙上的钟。所以两个方向都要转，**只转一边会让筛选整体偏一个时区**：
/// 界面显示成 17:19、但按 17:00–18:00 去查却查不到（因为库里那条是 09:19）。
///
/// 库里时间统一是 'YYYY-MM-DD HH:MM:SS' 的 UTC 文本，不带偏移量；
/// 换算时显式按 <paramref name="zone"/> 解释，转换后仍写回同样格式。
///
/// 时区作为参数传入而不是写死 <c>TimeZoneInfo.Local</c>，是为了让调用方能注入固定时区——
/// 否则测试结果会随开发者机器的时区变，现场与开发机也可能不一致。
/// </summary>
public static class OperationLogTime
{
    /// <summary>库里时间列的格式（无偏移量）。</summary>
    public const string Format = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// 库里的 UTC 文本 → 该时区的本地文本，供界面显示与导出。
    /// 空值返回 null；格式不认识时**原样返回**（宁可显示原始值，也不要在展示层抛异常）。
    /// </summary>
    public static string? ToLocalText(string? utcText, TimeZoneInfo zone)
    {
        // 空白统一当作「没有值」——与 ToUtcText 一致，避免空串与 null 两种"空"混用
        if (string.IsNullOrWhiteSpace(utcText)) return null;

        var parsed = TryParseAsUtc(utcText);
        return parsed is null
            ? utcText.Trim()
            : TimeZoneInfo.ConvertTimeFromUtc(parsed.Value, zone).ToString(Format);
    }

    /// <summary>
    /// 界面上输入的本地时间 → 库里的 UTC 文本，供筛选条件比较。
    /// 空值返回 null；格式不认识时原样返回（让 SQL 按原值比较，查询不会因此报错）。
    /// </summary>
    public static string? ToUtcText(string? localText, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(localText)) return null;

        var text = localText.Trim();
        if (!DateTime.TryParseExact(text, Format, null,
                System.Globalization.DateTimeStyles.None, out var local))
            return text;

        // 说明：不指定 DateTimeKind 时按 Unspecified 处理，这里显式按传入时区解释
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone)
            .ToString(Format);
    }

    /// <summary>本地时刻按同格式渲染（供写入侧复用，保证两处格式一致）。</summary>
    public static string NowLocalText(TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).ToString(Format);

    private static DateTime? TryParseAsUtc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        return DateTime.TryParseExact(text.Trim(), Format, null,
            System.Globalization.DateTimeStyles.None, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }
}
