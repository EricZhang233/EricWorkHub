namespace EricWorkHub;

internal sealed class BoundedCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _entries = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
    public bool TryGet(TKey key, out TValue value)
    {
        if (!_entries.TryGetValue(key, out var node)) { value = default!; return false; }
        _order.Remove(node);
        _order.AddLast(node);
        value = node.Value.Value;
        return true;
    }
    public void Set(TKey key, TValue value)
    {
        if (_entries.Remove(key, out var previous)) _order.Remove(previous);
        var node = _order.AddLast((key, value));
        _entries[key] = node;
        while (_entries.Count > Math.Max(1, capacity))
        {
            var first = _order.First!;
            _entries.Remove(first.Value.Key);
            _order.RemoveFirst();
        }
    }
}
