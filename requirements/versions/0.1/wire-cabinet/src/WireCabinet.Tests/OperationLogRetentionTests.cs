using System.Text.RegularExpressions;
using WireCabinet.Core;
using Xunit;

namespace WireCabinet.Tests;

/// <summary>
/// 记录的保留约束（0.2 需求范围 §3.7「只增不删」、验收 §6「DB 层无 UPDATE / DELETE 记录表」、
/// §10 把「记录可被修改或删除」列为未通过条件）。
///
/// 这条不是形式主义：一个「顺手清理旧记录」的改动、或一次「修正错别字」的 UPDATE
/// 就会踩上，而且事后无法从记录本身看出被改过。所以在这里钉死。
/// </summary>
public class OperationLogRetentionTests
{
    /// <summary>表名后紧跟下划线的是待补偿表（operation_log_pending），它本就该被更新。</summary>
    private static readonly Regex WriteToRecordTable = new(
        @"\b(update|delete\s+from)\s+app_operation_log\b(?!_)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void RecordTable_IsNeverUpdatedOrDeleted()
    {
        var offenders = new List<string>();

        foreach (var file in SqlAndSourceFiles())
        {
            // 折叠换行后再匹配：UPDATE 与表名可能不在同一行
            var text = Whitespace().Replace(File.ReadAllText(file), " ");
            if (WriteToRecordTable.IsMatch(text))
                offenders.Add(Relative(file));
        }

        Assert.True(
            offenders.Count == 0,
            "记录表出现 UPDATE / DELETE（§3.7 只增不删），涉及：" + string.Join("、", offenders));
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
        Assert.Contains(files, f => f.EndsWith("OperationLogSink.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f.Contains("WireCabinet.Tests", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>同时作为自检：正则确实能抓到违规写法，否则上面那条断言可能是空转。</summary>
    [Theory]
    [InlineData("UPDATE app_operation_log SET result = '成功';", true)]
    [InlineData("delete from app_operation_log where id = 1;", true)]
    [InlineData("DELETE FROM app_operation_log\nWHERE id = 1;", true)]
    [InlineData("INSERT INTO app_operation_log (id) VALUES (1);", false)]
    [InlineData("SELECT * FROM app_operation_log;", false)]
    [InlineData("ALTER TABLE app_operation_log ADD COLUMN station TEXT;", false)]
    [InlineData("UPDATE operation_log_pending SET status = 'success';", false)]
    [InlineData("UPDATE app_slot SET door_state = 'open';", false)]
    public void GuardRegex_DetectsOnlyRecordTableWrites(string sql, bool expected)
    {
        var collapsed = Whitespace().Replace(sql, " ");

        Assert.Equal(expected, WriteToRecordTable.IsMatch(collapsed));
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
