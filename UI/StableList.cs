using System.Collections.ObjectModel;

namespace EricWorkHub;

internal static class StableList
{
    public static void Apply<TView, TModel, TKey>(ObservableCollection<TView> target, IReadOnlyList<TModel> incoming,
        Func<TView, TKey> viewKey, Func<TModel, TKey> modelKey, Func<TModel, TView> create, Action<TView, TModel> update)
        where TKey : notnull
    {
        var keys = incoming.Select(modelKey).ToHashSet();
        if (keys.Count != incoming.Count) throw new ArgumentException("A list snapshot must have unique keys", nameof(incoming));
        var existing = target.ToDictionary(viewKey);
        for (int index = target.Count - 1; index >= 0; index--)
            if (!keys.Contains(viewKey(target[index]))) target.RemoveAt(index);
        for (int index = 0; index < incoming.Count; index++)
        {
            var model = incoming[index];
            if (existing.TryGetValue(modelKey(model), out var view))
            {
                update(view, model);
                if (index < target.Count && EqualityComparer<TView>.Default.Equals(target[index], view)) continue;
                target.Move(target.IndexOf(view), index);
            }
            else target.Insert(index, create(model));
        }
    }
}
