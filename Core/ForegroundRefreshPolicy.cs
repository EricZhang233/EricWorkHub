namespace EricWorkHub;

internal sealed class ForegroundRefreshPolicy
{
    public static TimeSpan Interval => TimeSpan.FromMilliseconds(7500);
    public bool IsForeground { get; private set; }
    public bool IsRefreshing { get; private set; }
    private bool _closed, _focusPending;
    public bool RunTimer => IsForeground && !_closed;

    public bool SetActive(bool active)
    {
        if (_closed) return false;
        bool gained = active && !IsForeground;
        IsForeground = active;
        if (!active) _focusPending = false;
        else if (gained) _focusPending = true;
        return gained;
    }

    public bool TryBegin()
    {
        if (!RunTimer || IsRefreshing) return false;
        IsRefreshing = true;
        _focusPending = false;
        return true;
    }

    public bool Complete()
    {
        IsRefreshing = false;
        return RunTimer && _focusPending;
    }

    public void Close() { _closed = true; IsForeground = false; _focusPending = false; }
}
