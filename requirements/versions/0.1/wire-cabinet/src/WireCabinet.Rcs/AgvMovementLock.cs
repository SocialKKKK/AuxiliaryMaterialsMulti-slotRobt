namespace WireCabinet.Rcs;

/// <summary>下单成功立即锁按钮；以待下单会话和订单门禁为准，不要求 UI 必须观察过执行中的订单。</summary>
public sealed class AgvMovementLock
{
    public bool IsLocked { get; private set; }
    public void Set(bool locked) => IsLocked = locked;
    public void Refresh(bool hasBlockingOrder, bool requestPending, bool canPlaceMove)
    {
        if (IsLocked && !hasBlockingOrder && !requestPending && canPlaceMove)
            IsLocked = false;
    }
}
