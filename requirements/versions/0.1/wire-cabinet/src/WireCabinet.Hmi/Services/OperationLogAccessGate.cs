namespace WireCabinet.Hmi.Services;

/// <summary>
/// 「物料存取记录」页签的门禁（0.2 需求范围 §2）。
///
/// 需求：物料员 MH 与维护/管理员 MAINT **可查看**，操作员 OP **不可见**。
/// 两个角色的密码门禁是各自独立的（MaintAccessGate / MhAccessGate），
/// 所以这里把两个都试一遍：任一个通过即放行，并记下是哪个角色放行的——
/// 查看与导出要留痕（§3.7），留痕记录里得写对角色。
/// </summary>
public sealed class OperationLogAccessGate
{
    private readonly MaintAccessGate _maint;
    private readonly MhAccessGate _mh;

    public OperationLogAccessGate(MaintAccessGate maint, MhAccessGate mh)
    {
        _maint = maint;
        _mh = mh;
    }

    /// <summary>两个门禁都没配密码时无需验证。</summary>
    public bool IsGateEnabled => _maint.IsGateEnabled || _mh.IsGateEnabled;

    public bool IsUnlocked => _maint.IsUnlocked || _mh.IsUnlocked;

    /// <summary>放行本次访问的角色（MAINT / MH）；未验证时为 null。</summary>
    public string? UnlockedRole { get; private set; }

    public bool TryUnlock(string? input)
    {
        if (!IsGateEnabled)
            return true;   // 未配置密码：视为不启用门禁

        if (_maint.IsGateEnabled && _maint.TryUnlock(input))
        {
            UnlockedRole = "MAINT";
            return true;
        }

        if (_mh.IsGateEnabled && _mh.TryUnlock(input))
        {
            UnlockedRole = "MH";
            return true;
        }

        return false;
    }

    public void Lock()
    {
        _maint.Lock();
        _mh.Lock();
        UnlockedRole = null;
    }
}
