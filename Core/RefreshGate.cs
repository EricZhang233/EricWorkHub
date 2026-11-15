namespace EricWorkHub;

internal sealed class RefreshGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastSuccess = long.MinValue / 2;

    public async Task<bool> RunAsync(bool force, TimeSpan minimumInterval, Func<CancellationToken, Task> refresh, CancellationToken token)
    {
        if (force) await _gate.WaitAsync(token);
        else if (!await _gate.WaitAsync(0, token)) return false;
        try
        {
            if (!force && Environment.TickCount64 - _lastSuccess < minimumInterval.TotalMilliseconds) return false;
            await refresh(token);
            _lastSuccess = Environment.TickCount64;
            return true;
        }
        finally { _gate.Release(); }
    }
}
