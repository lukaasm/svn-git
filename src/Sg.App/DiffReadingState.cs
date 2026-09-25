using System.Text.Json;

namespace Sg.App;

/// <summary>Small, bounded reading positions owned by one navigation entry, never editors or file contents.</summary>
internal sealed class DiffReadingState
{
    readonly Dictionary<string, JsonElement> _positions = new(StringComparer.Ordinal);
    readonly LinkedList<string> _recent = new();
    public int Visit { get; private set; }
    public void BeginVisit() => Visit++;
    public JsonElement? Get(string key)
    {
        if (!_positions.TryGetValue(key, out var position)) return null;
        _recent.Remove(key); _recent.AddLast(key);
        return position;
    }
    public void Put(int visit, string key, JsonElement position)
    {
        // A closing WebView may finish reporting after the replacement page has already loaded.
        if (visit != Visit || key.Length > 4096 || position.ValueKind != JsonValueKind.Object || position.GetRawText().Length > 16384) return;
        _positions[key] = position.Clone();
        _recent.Remove(key); _recent.AddLast(key);
        while (_recent.Count > 32)
        {
            _positions.Remove(_recent.First!.Value);
            _recent.RemoveFirst();
        }
    }
}
