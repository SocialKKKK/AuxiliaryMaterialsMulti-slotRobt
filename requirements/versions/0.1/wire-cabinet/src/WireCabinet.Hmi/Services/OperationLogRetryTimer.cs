using System.Windows.Threading;
using WireCabinet.Data;

namespace WireCabinet.Hmi.Services;

/// <summary>
/// 启动时与定时扫描 operation_log_pending 并补写（0.2 需求范围 §3.8 第 3 条：后台自动重试）。
///
/// 与 <see cref="WireMesDiscoRetryTimer"/> 同形：写入失败不能阻断业务，但必须能自愈，
/// 所以补写放在后台线程里跑，失败只留在待补偿表里等下一次或人工处理。
/// </summary>
public sealed class OperationLogRetryTimer : IDisposable
{
    private readonly IOperationLogCompensation _compensation;
    private readonly DispatcherTimer _timer;

    public OperationLogRetryTimer(IOperationLogCompensation compensation, TimeSpan interval)
    {
        _compensation = compensation;
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        // 启动时先补一次，避免上次退出时留下的待补偿要等一个周期
        _ = Task.Run(() => { try { _compensation.RetryPendingBatch(); } catch { /* 后台重试不阻塞 UI */ } });
        _timer.Start();
    }

    private void Tick()
    {
        _ = Task.Run(() =>
        {
            try { _compensation.RetryPendingBatch(); }
            catch { /* 忽略定时重试异常 */ }
        });
    }

    public void Dispose() => _timer.Stop();
}
