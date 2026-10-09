using System.Text.RegularExpressions;
using WireCabinet.Core;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 记录的保留约束（0.2 需求范围 §3.7、验收 §6、§10 把「记录可被修改或删除」列为未通过条件）。
///
/// 2026-10-09 记录口径优化把这条例外收窄为「**业务事实不可改写**」：唯一放行的写法是
/// 「只把 NULL 的门时刻补齐」（§4 A 档），因为归还/领用是先提交、后开关门的，
/// 关门时刻在写记录时还不存在。除此之外的任何 UPDATE / DELETE 仍然禁止。
/// 库层另有 trg_app_operation_log_append_only 触发器兜底。
/// </summary>
public class OperationLogRetentionTests
{
    /// <summary>表名后紧跟下划线的是待补偿表（operation_log_pending），它本就该被更新。</summary>
    private static readonly Regex UpdateRecordTable = new(
        @"\bupdate\s+app_operation_log\b(?!_)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DeleteFromRecordTable = new(
        @"\bdelete\s+from\s+app_operation_log\b(?!_)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 放行的写法之一（口径优化 §4 A 档）：只补空的门时刻，且 WHERE 上有 IS NULL 守卫。
    /// 参数名写死为 :door_open_ts / :door_close_ts / :action_type / :ts，与 catalog 条目一致。
    /// </summary>
    private static readonly Regex AllowedDoorTsBackfill = new(
        @"^update\s+app_operation_log\s+set\s+door_open_ts\s*=\s*:door_open_ts\s*,\s*door_close_ts\s*=\s*:door_close_ts"
        + @"\s+where\s+id\s*=\s*:id\s+and\s+door_close_ts\s+is\s+null\s*;$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 放行的写法之二：更新**失败**记录的 detail（过程派生字段，承载重复失败统计）。
    /// 业务事实列一律不在放行范围内。
    /// </summary>
    private static readonly Regex AllowedDetailUpdate = new(
        @"^update\s+app_operation_log\s+set\s+detail\s*=\s*:detail\s+where\s+action_type\s*=\s*:action_type"
        + @"\s+and\s+ts\s*=\s*:ts\s+and\s+result\s*=\s*'失败'\s*;$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void RecordTable_IsNeverUpdatedOrDeleted()
    {
        var offenders = new List<string>();

        foreach (var file in SqlAndSourceFiles())
        {
            // 折叠换行后再匹配：UPDATE 与表名可能不在同一行
            var text = Whitespace().Replace(File.ReadAllText(file), " ");
            if (HasDisallowedWrite(text))
                offenders.Add(Relative(file));
        }

        Assert.True(
            offenders.Count == 0,
            "记录表出现越界的 UPDATE / DELETE（§3.7 业务事实不可改写），涉及：" + string.Join("、", offenders));
    }

    /// <summary>DELETE 一律禁止；UPDATE 只允许「补齐 NULL 门时刻」与「更新失败记录 detail」两种写法。</summary>
    private static bool HasDisallowedWrite(string collapsed)
    {
        if (DeleteFromRecordTable.IsMatch(collapsed))
            return true;

        foreach (Match match in UpdateRecordTable.Matches(collapsed))
        {
            var end = collapsed.IndexOf(';', match.Index);
            var statement = (end < 0 ? collapsed[match.Index..] : collapsed[match.Index..(end + 1)]).Trim();
            if (!AllowedDoorTsBackfill.IsMatch(statement) && !AllowedDetailUpdate.IsMatch(statement))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 防空转：扫描范围必须真的覆盖到 SQL 与源码，否则上面那条断言可以「因为什么都没扫」
    /// 而永远通过；测试工程自己必须排除，否则会被自己的断言字面串误伤。
    /// </summary>
    [Fact]
    public void Scan_ReallyCoversSqlAndSources()
    {
        var files = SqlAndSourceFiles().ToList();

        Assert.Contains(files, f => f.EndsWith("app.operation_log.insert.yaml", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.EndsWith("app.operation_log.backfill_door_ts.yaml", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.EndsWith("OperationLogSink.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Contains("WireCabinet.Tests", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 自检：既要抓到违规写法，也要放行唯一那条白名单写法——否则「正则太松」会让上面那条断言空转，
    /// 或者「白名单太严」会把 backfill 误报成违规。
    /// </summary>
    [Theory]
    [InlineData("UPDATE app_operation_log SET result = '成功';", true)]
    [InlineData("UPDATE app_operation_log SET door_open_ts = :door_open_ts, door_close_ts = :door_close_ts;", true)]
    [InlineData("UPDATE app_operation_log SET message = :message WHERE id = :id;", true)]
    [InlineData("delete from app_operation_log where id = 1;", true)]
    [InlineData("DELETE FROM app_operation_log\nWHERE id = 1;", true)]
    [InlineData(
        "UPDATE app_operation_log SET door_open_ts = :door_open_ts, door_close_ts = :door_close_ts"
        + " WHERE id = :id AND door_close_ts IS NULL;",
        false)]
    [InlineData(
        "UPDATE app_operation_log SET detail = :detail WHERE action_type = :action_type AND ts = :ts AND result = '失败';",
        false)]
    [InlineData("UPDATE app_operation_log SET detail = :detail WHERE id = 1;", true)]
    [InlineData("INSERT INTO app_operation_log (id) VALUES (1);", false)]
    [InlineData("SELECT * FROM app_operation_log;", false)]
    [InlineData("ALTER TABLE app_operation_log ADD COLUMN station TEXT;", false)]
    [InlineData("UPDATE operation_log_pending SET status = 'success';", false)]
    [InlineData("UPDATE app_slot SET door_state = 'open';", false)]
    public void GuardRegex_DetectsOnlyRecordTableWrites(string sql, bool expected)
    {
        var collapsed = Whitespace().Replace(sql, " ");

        Assert.Equal(expected, HasDisallowedWrite(collapsed));
    }

    /// <summary>
    /// 要检查的范围：全部 SQL（catalog 是工程约定的唯一 SQL 存放处）与产品源码。
    /// 测试工程自己排除在外——它必然含有用于断言的字面串。
    /// </summary>
    private static IEnumerable<string> SqlAndSourceFiles()
    {
        foreach (var dir in new[] { CabinetPaths.FormalSqlCatalogDir, CabinetPaths.LabSqlCatalogDir })
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories))
                    yield return f;

        var srcDir = Path.Combine(CabinetPaths.VersionRoot, "src");
        if (!Directory.Exists(srcDir)) yield break;

        foreach (var f in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            if (f.Contains($"{Path.DirectorySeparatorChar}WireCabinet.Tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            if (f.Contains($"{Path.DirectorySeparatorChar}WireCabinet.Hmi.Tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            yield return f;
        }
    }

    private static Regex Whitespace() => new(@"\s+", RegexOptions.Compiled);

    private static string Relative(string path) =>
        Path.GetRelativePath(CabinetPaths.RepoRoot, path).Replace('\\', '/');
}
