namespace WireCabinet.Slots;

/// <summary>
/// 计划外开门记录的去重（0.2 § 覆盖矩阵第 10 项）。
///
/// 为什么需要它：门态巡检每 2 秒一轮，而「库内无开门指令 + DI 释锁」是一个**持续状态**，
/// 不是瞬时事件。不去重的话每一轮都会命中并落一条记录——一把锁卡在释放位置就会刷出
/// 成百上千条同样的记录，把记录表变成噪声，验收 §10「不产生记录洪水」也会不过。
///
/// 语义：同一格口的持续释锁只记一次；等它回到正常（DI 锁闭，或系统真的下令开了门）
/// 才忘掉，之后再释锁算新的一次。
/// </summary>
public sealed class UncommandedOpenTracker
{
    private readonly HashSet<long> _reported = new();

    /// <summary>
    /// 传入本轮处于「计划外释锁」的格口集合，返回**首次**出现的那些（需要写记录的）。
    /// 同时把已恢复正常的格口忘掉——集合里没有的即视为已恢复。
    /// </summary>
    public IReadOnlyList<long> FilterNew(IReadOnlyCollection<long> currentlyUncommanded)
    {
        _reported.IntersectWith(currentlyUncommanded);

        var fresh = new List<long>();
        foreach (var slotId in currentlyUncommanded)
        {
            if (_reported.Add(slotId))
                fresh.Add(slotId);
        }
        return fresh;
    }

    /// <summary>在跟踪中的格口数，供测试与排障。</summary>
    public int TrackedCount => _reported.Count;
}
